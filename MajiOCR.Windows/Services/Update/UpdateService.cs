using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MajiOCR_Windows.Services.Update;

public sealed class UpdateService
{
    public const string SettingsFileName = "UpdateSettings.json";
    public const string DefaultSettingsFileName = "DefaultUpdateSettings.json";
    public const string AppliedUpdateStateFileName = "AppliedUpdateState.json";
    private const string DownloadsDirectoryName = "updates";
    private readonly string _basePath;

    public UpdateService(string? basePath = null)
    {
        _basePath = !string.IsNullOrWhiteSpace(basePath)
            ? basePath
            : (!string.IsNullOrWhiteSpace(Environment.ProcessPath) ? Path.GetDirectoryName(Environment.ProcessPath) : null)
              ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public string BasePath => _basePath;
    public string SettingsFilePath => Path.Combine(_basePath, SettingsFileName);
    public string DefaultSettingsFilePath => Path.Combine(_basePath, DefaultSettingsFileName);
    public string DownloadsDirectoryPath => Path.Combine(_basePath, DownloadsDirectoryName);
    public string AppliedUpdateStateFilePath => Path.Combine(DownloadsDirectoryPath, AppliedUpdateStateFileName);

    public string CurrentVersion
    {
        get
        {
            var asm = typeof(UpdateService).Assembly.GetName().Version;
            if (asm != null)
            {
                return $"{asm.Major}.{asm.Minor}.{asm.Build}.{asm.Revision}";
            }
            return "0.1.0.23";
        }
    }

    public string DefaultReleaseManifestUrl => "https://api.github.com/repos/Ryan-tw-Hans/MajiStudio/releases/latest";

    public UpdateSettings LoadSettings()
    {
        var settings = LoadSettingsFile(DefaultSettingsFilePath, "內建更新設定")
            ?? new UpdateSettings(null, null, false);
        var userSettings = LoadSettingsFile(SettingsFilePath, "使用者更新設定");
        if (userSettings is not null)
        {
            var relUrl = userSettings.ReleaseManifestUrl;
            // 平滑遷移：若使用者的設定檔仍為舊版 Google Drive 更新網址，自動升級為最新的 GitHub Releases API 網址
            if (!string.IsNullOrWhiteSpace(relUrl) && relUrl.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase))
            {
                relUrl = null;
            }

            settings = settings with
            {
                ReleaseManifestUrl = string.IsNullOrWhiteSpace(relUrl)
                    ? settings.ReleaseManifestUrl
                    : relUrl,
                TestManifestUrl = string.IsNullOrWhiteSpace(userSettings.TestManifestUrl)
                    ? settings.TestManifestUrl
                    : userSettings.TestManifestUrl,
                IncludeTestVersions = userSettings.IncludeTestVersions,
                CustomManifestUrl = userSettings.CustomManifestUrl
            };
        }

        var releaseUrl = Environment.GetEnvironmentVariable("MAJIOCR_RELEASE_UPDATE_MANIFEST_URL")
            ?? Environment.GetEnvironmentVariable("MAJIOCR_UPDATE_MANIFEST_URL");
        var testUrl = Environment.GetEnvironmentVariable("MAJIOCR_TEST_UPDATE_MANIFEST_URL");
        var includeTest = ReadBooleanEnvironmentVariable("MAJIOCR_UPDATE_INCLUDE_TEST");

        if (!string.IsNullOrWhiteSpace(releaseUrl))
        {
            settings = settings with { ReleaseManifestUrl = releaseUrl.Trim() };
        }

        if (!string.IsNullOrWhiteSpace(testUrl))
        {
            settings = settings with { TestManifestUrl = testUrl.Trim() };
        }

        if (includeTest.HasValue)
        {
            settings = settings with { IncludeTestVersions = includeTest.Value };
        }

        return settings.Normalized();
    }

    public void SaveSettings(UpdateSettings settings)
    {
        Directory.CreateDirectory(_basePath);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings.Normalized(), options) + Environment.NewLine, Encoding.UTF8);
    }

    public string LoadLocalChangeLogText()
    {
        // 優先從組件內嵌資源讀取，確保單檔 EXE 永遠顯示當前編譯版本的最新變更紀錄，不受舊實體檔案影響
        try
        {
            var asm = typeof(UpdateService).Assembly;
            using var stream = asm.GetManifestResourceStream("MajiOCR.CHANGELOG.txt");
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var embedded = reader.ReadToEnd().Trim();
                if (!string.IsNullOrWhiteSpace(embedded))
                {
                    return embedded;
                }
            }
        }
        catch { }

        var changeLogPath = Path.Combine(_basePath, "CHANGELOG.txt");
        if (File.Exists(changeLogPath))
        {
            var text = File.ReadAllText(changeLogPath, Encoding.UTF8).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return "找不到本機 CHANGELOG.txt。";
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestUrl,
        UpdateChannel channel = UpdateChannel.Release,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return new UpdateCheckResult(
                new UpdateManifest("", null, null, null, "", null, "", ""),
                false,
                channel,
                "",
                "未設定更新來源網址。"
            );
        }

        var directUrl = ToDirectDownloadUrl(manifestUrl);
        if (!Uri.TryCreate(directUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("更新索引連結格式不正確，請使用有效的 HTTPS 網址或 GitHub Releases API 連結。");
        }

        using var client = CreateHttpClient();
        var json = await DownloadTextWithRetriesAsync(client, manifestUrl, "更新索引", cancellationToken);
        var manifest = UpdateManifest.Parse(json, channel);

        if (!string.IsNullOrWhiteSpace(manifest.ChangeLogUrl))
        {
            manifest = await TryLoadChangeLogAsync(manifest, cancellationToken);
        }

        var availability = EvaluateUpdateAvailability(manifest);
        return new UpdateCheckResult(manifest, availability.IsAvailable, channel, manifestUrl, availability.Reason);
    }

    public async Task<UpdateDownloadResult> DownloadAndVerifyAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.PackageUrl))
        {
            throw new InvalidOperationException("更新索引未提供 packageUrl。");
        }

        var expectedSha256 = !string.IsNullOrWhiteSpace(manifest.Sha256)
            ? NormalizeSha256(manifest.Sha256)
            : string.Empty;

        Directory.CreateDirectory(DownloadsDirectoryPath);
        var fileName = SanitizeFileName(manifest.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"MajiOCR.Windows_v{manifest.Version}.zip";
        }

        var finalPath = Path.Combine(DownloadsDirectoryPath, fileName);
        var tempPath = finalPath + ".download";

        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        using var client = CreateHttpClient();
        var totalBytes = await DownloadPackageWithRetriesAsync(
            client,
            manifest.PackageUrl,
            tempPath,
            progress,
            cancellationToken);

        progress?.Report(new UpdateDownloadProgress(new FileInfo(tempPath).Length, totalBytes, Verifying: true));
        var actualSha256 = await ComputeSha256Async(tempPath, cancellationToken);
        if (expectedSha256.Length == 64)
        {
            if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                throw new InvalidOperationException($"更新包 SHA-256 驗證失敗：期望 {expectedSha256}，實際 {actualSha256}。");
            }
        }

        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }

        File.Move(tempPath, finalPath);
        return new UpdateDownloadResult(finalPath, actualSha256);
    }

    public void StartSelfUpdate(UpdateManifest manifest, UpdateDownloadResult downloadResult)
    {
        if (string.IsNullOrWhiteSpace(downloadResult.FilePath) || !File.Exists(downloadResult.FilePath))
        {
            throw new InvalidOperationException("更新包不存在，無法套用更新。");
        }

        Directory.CreateDirectory(DownloadsDirectoryPath);
        var scriptPath = Path.Combine(DownloadsDirectoryPath, "apply-update.ps1");
        File.WriteAllText(scriptPath, BuildApplyUpdateScript(), Encoding.UTF8);

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("找不到目前執行檔，無法在更新後重新啟動。");
        }

        var powershellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershellPath))
        {
            powershellPath = "powershell.exe";
        }

        var logPath = Path.Combine(_basePath, "logs", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = _basePath
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ZipPath");
        startInfo.ArgumentList.Add(downloadResult.FilePath);
        startInfo.ArgumentList.Add("-TargetDir");
        startInfo.ArgumentList.Add(_basePath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-ExecutablePath");
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);
        startInfo.ArgumentList.Add("-UpdateChannel");
        startInfo.ArgumentList.Add(manifest.Channel.ToString());
        startInfo.ArgumentList.Add("-UpdateVersion");
        startInfo.ArgumentList.Add(manifest.Version);
        startInfo.ArgumentList.Add("-UpdateSha256");
        startInfo.ArgumentList.Add(downloadResult.Sha256);
        startInfo.ArgumentList.Add("-UpdateFileName");
        startInfo.ArgumentList.Add(manifest.FileName);
        startInfo.ArgumentList.Add("-UpdatePackageUrl");
        startInfo.ArgumentList.Add(manifest.PackageUrl);

        Process.Start(startInfo);
    }

    public (bool IsAvailable, string Reason) EvaluateUpdateAvailability(UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return (false, "更新索引未包含版本號。");
        }

        var comparison = CompareVersions(manifest.Version, CurrentVersion);
        if (comparison > 0)
        {
            return (true, $"線上版本 v{manifest.Version} 高於目前版本 v{CurrentVersion}。");
        }

        if (comparison == 0)
        {
            var applied = LoadAppliedUpdateState();
            if (applied is not null &&
                !string.IsNullOrWhiteSpace(manifest.Sha256) &&
                applied.Sha256.Equals(NormalizeSha256(manifest.Sha256), StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"目前版本 v{CurrentVersion} 已是最新且已套用該更新包。");
            }

            return (false, $"目前版本 v{CurrentVersion} 已是最新。");
        }

        return (false, $"目前版本 v{CurrentVersion} 高於線上版本 v{manifest.Version}。");
    }

    public static int CompareVersions(string versionA, string versionB)
    {
        var cleanA = CleanVersionString(versionA);
        var cleanB = CleanVersionString(versionB);

        if (Version.TryParse(cleanA, out var parsedA) && Version.TryParse(cleanB, out var parsedB))
        {
            return parsedA.CompareTo(parsedB);
        }

        var partsA = cleanA.Split('.');
        var partsB = cleanB.Split('.');
        var maxLen = Math.Max(partsA.Length, partsB.Length);

        for (var i = 0; i < maxLen; i++)
        {
            var valA = i < partsA.Length && int.TryParse(partsA[i], out var va) ? va : 0;
            var valB = i < partsB.Length && int.TryParse(partsB[i], out var vb) ? vb : 0;
            if (valA != valB)
            {
                return valA.CompareTo(valB);
            }
        }

        return 0;
    }

    private static string CleanVersionString(string v)
    {
        var cleaned = v.TrimStart('v', 'V');
        var dashIdx = cleaned.IndexOf('-');
        if (dashIdx > 0)
        {
            cleaned = cleaned.Substring(0, dashIdx);
        }
        return cleaned.Trim();
    }

    public AppliedUpdateState? LoadAppliedUpdateState()
    {
        if (!File.Exists(AppliedUpdateStateFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(AppliedUpdateStateFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppliedUpdateState>(json);
        }
        catch
        {
            return null;
        }
    }

    public static string ToDirectDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        if (!uri.Host.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return url.Trim();
        }

        var fileId = ExtractGoogleDriveFileId(uri);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return url.Trim();
        }

        return "https://drive.google.com/uc?export=download&id=" + Uri.EscapeDataString(fileId);
    }

    private static string? ExtractGoogleDriveFileId(Uri uri)
    {
        var fileMatch = Regex.Match(uri.AbsolutePath, @"/file/d/([^/]+)", RegexOptions.IgnoreCase);
        if (fileMatch.Success)
        {
            return fileMatch.Groups[1].Value;
        }

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var items = pair.Split('=', 2);
            if (items.Length == 2 && items[0].Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(items[1]);
            }
        }

        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private async Task<string> DownloadTextWithRetriesAsync(
        HttpClient client,
        string url,
        string description,
        CancellationToken cancellationToken)
    {
        var candidateUrls = BuildPackageDownloadUrls(url);
        Exception? lastEx = null;

        foreach (var candidate in candidateUrls)
        {
            try
            {
                using var response = await client.GetAsync(candidate, HttpCompletionOption.ResponseContentRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastEx = ex;
            }
        }

        throw lastEx ?? new InvalidOperationException($"無法下載{description}。");
    }

    private static string[] BuildPackageDownloadUrls(string originalUrl)
    {
        var directUrl = ToDirectDownloadUrl(originalUrl);
        if (!Uri.TryCreate(originalUrl.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return [directUrl];
        }

        var fileId = ExtractGoogleDriveFileId(uri);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return [directUrl];
        }

        return
        [
            $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(fileId)}&confirm=t",
            $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(fileId)}&export=download&confirm=t",
            directUrl,
            $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(fileId)}",
            $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(fileId)}&export=download"
        ];
    }

    private async Task<long?> DownloadPackageWithRetriesAsync(
        HttpClient client,
        string packageUrl,
        string tempPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        var candidateUrls = BuildPackageDownloadUrls(packageUrl);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            foreach (var candidateUrl in candidateUrls)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }

                    using var response = await client.GetAsync(candidateUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength;
                    await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long totalRead = 0;
                        int read;

                        while ((read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            totalRead += read;
                            progress?.Report(new UpdateDownloadProgress(totalRead, totalBytes));
                        }
                    }

                    // 驗證下載檔案是否為真正的二進位 ZIP 封裝包 (避免 Google Drive 回傳 HTML 錯誤/確認頁)
                    if (File.Exists(tempPath))
                    {
                        var info = new FileInfo(tempPath);
                        if (info.Length < 1024)
                        {
                            var sampleText = await File.ReadAllTextAsync(tempPath, cancellationToken);
                            File.Delete(tempPath);
                            throw new InvalidOperationException($"伺服器回傳非更新包內容：{sampleText.Trim()}");
                        }

                        using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            byte[] magic = new byte[2];
                            int mRead = fs.Read(magic, 0, 2);
                            if (mRead < 2 || magic[0] != 0x50 || magic[1] != 0x4B)
                            {
                                fs.Close();
                                File.Delete(tempPath);
                                throw new InvalidOperationException("下載之檔案不是標準 ZIP 封裝包，可能是 Google 存取限制頁面。");
                            }
                        }
                    }

                    return totalBytes;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            }
        }

        throw lastException ?? new InvalidOperationException("無法下載更新包。");
    }

    private async Task<UpdateManifest> TryLoadChangeLogAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest.ChangeLogUrl)) return manifest;
        try
        {
            using var client = CreateHttpClient();
            var text = await DownloadTextWithRetriesAsync(client, manifest.ChangeLogUrl, "Change Log", cancellationToken);
            return manifest with { ChangeLog = text.Trim() };
        }
        catch
        {
            return manifest;
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string NormalizeSha256(string sha256)
    {
        return sha256.Trim().Replace("-", string.Empty).ToUpperInvariant();
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
        {
            if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
        }
        return sb.ToString();
    }

    private static UpdateSettings? LoadSettingsFile(string path, string desc)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<UpdateSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadBooleanEnvironmentVariable(string name)
    {
        var val = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(val)) return null;
        if (bool.TryParse(val, out var parsed)) return parsed;
        if (val.Equals("1", StringComparison.OrdinalIgnoreCase)) return true;
        if (val.Equals("0", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    public static string BuildApplyUpdateScript()
    {
        return @"#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,

    [Parameter(Mandatory = $true)]
    [string]$TargetDir,

    [Parameter(Mandatory = $true)]
    [int]$ProcessId,

    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [string]$LogPath = '',
    [string]$UpdateChannel = 'Release',
    [string]$UpdateVersion = '',
    [string]$UpdateSha256 = '',
    [string]$UpdateFileName = '',
    [string]$UpdatePackageUrl = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'
    $line = ""$stamp [$Level] $Message""
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        try {
            $dir = Split-Path -Parent $LogPath
            if (-not [string]::IsNullOrWhiteSpace($dir) -and -not (Test-Path -LiteralPath $dir)) {
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
            }
            Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value $line
        } catch { }
    }
}

try {
    Write-Log ""更新套用程序啟動：目標=$TargetDir，版本=$UpdateVersion，主程序PID=$ProcessId""

    # 等待原主程式退出
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Log ""等待主程式 (PID: $ProcessId) 正常關閉...""
        $timeout = [DateTime]::UtcNow.AddSeconds(30)
        while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $timeout) {
            Start-Sleep -Milliseconds 500
            $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if (-not $proc) { break }
        }
        if ($proc -and -not $proc.HasExited) {
            Write-Log ""主程式未能即時退出，執行終止程序..."" 'WARN'
            Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
        }
    }

    $tempExtract = Join-Path $TargetDir ""updates\temp_extract""
    Remove-Item -LiteralPath $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $tempExtract | Out-Null

    Write-Log ""解壓縮更新包：$ZipPath""
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $tempExtract -Force

    # 覆寫保護名單
    $protectedFiles = @('card_manager.db', 'card_manager.db-wal', 'card_manager.db-shm', 'appsettings.json', 'UpdateSettings.json')
    $protectedDirs = @('logs', 'updates', 'CardImages')

    Write-Log ""開始套用更新檔案至目標目錄...""
    Get-ChildItem -LiteralPath $tempExtract -Recurse | ForEach-Object {
        $relPath = $_.FullName.Substring($tempExtract.Length).TrimStart('\', '/')
        $targetFile = Join-Path $TargetDir $relPath

        if ($_.PSIsContainer) {
            if ($_.Name -notin $protectedDirs) {
                if (-not (Test-Path -LiteralPath $targetFile)) {
                    New-Item -ItemType Directory -Force -Path $targetFile | Out-Null
                }
            }
        } else {
            $parentDir = Split-Path -Parent $targetFile
            if (-not (Test-Path -LiteralPath $parentDir)) {
                New-Item -ItemType Directory -Force -Path $parentDir | Out-Null
            }
            if ($_.Name -notin $protectedFiles) {
                $copied = $false
                for ($retry = 1; $retry -le 10; $retry++) {
                    try {
                        Copy-Item -LiteralPath $_.FullName -Destination $targetFile -Force -ErrorAction Stop
                        $copied = $true
                        break
                    } catch {
                        Start-Sleep -Seconds 1
                    }
                }
                if (-not $copied) {
                    throw ""無法覆寫檔案 ($targetFile)：可能被系統或其他程序鎖定。""
                }
            }
        }
    }

    # 記錄已套用狀態
    $stateObj = [ordered]@{
        appliedAt = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
        channel = $UpdateChannel
        version = $UpdateVersion
        sha256 = $UpdateSha256
        fileName = $UpdateFileName
        packageUrl = $UpdatePackageUrl
    }
    $stateJson = $stateObj | ConvertTo-Json -Depth 5
    $statePath = Join-Path $TargetDir ""updates\AppliedUpdateState.json""
    [System.IO.File]::WriteAllText($statePath, $stateJson, [System.Text.Encoding]::UTF8)

    Remove-Item -LiteralPath $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
    Write-Log ""更新套用完成！準備重新啟動主程式：$ExecutablePath""

    Start-Process -FilePath $ExecutablePath -WorkingDirectory $TargetDir
} catch {
    Write-Log ""套用更新失敗：$_"" 'ERROR'
}
";
    }
}
