using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;
using MajiOCR_Windows.Services.Update;

namespace MajiOCR_Windows.Verification;

public class Issue0050TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0050TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0050TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.30";
    public string IssueId { get; set; } = "ISSUE-0050";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0 && TotalTests > 0;
    public List<Issue0050TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0050AutomatedTestSuite
{
    public static async Task<Issue0050TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0050TestSuiteReport();

        async Task AddTestAsync(string id, string desc, Func<Task<string>> action)
        {
            var sw = Stopwatch.StartNew();
            var res = new Issue0050TestCaseResult { TestId = id, Description = desc };
            try
            {
                string detail = await action();
                res.Passed = true;
                res.Details = string.IsNullOrWhiteSpace(detail) ? "PASS" : detail;
            }
            catch (Exception ex)
            {
                res.Passed = false;
                res.Details = ex.ToString();
            }
            finally
            {
                sw.Stop();
                res.LatencyMs = sw.ElapsedMilliseconds;
            }
            report.TestCases.Add(res);
        }

        // TC-0050-01: GoogleAuthService OAuth ClientSecret DPAPI encryption & decryption
        await AddTestAsync("TC-0050-01", "驗證 GoogleAuthService 儲存 google_oauth_config.json 時以 Windows DPAPI 加密 ClientSecret 且不含明文", async () =>
        {
            string configPath = Path.Combine(StoragePathHelper.GetDefaultStorageRoot(), "google_oauth_config.json");
            string backupPath = Path.Combine(Path.GetTempPath(), $"oauth_bak_{Guid.NewGuid():N}.json");
            bool hadExisting = File.Exists(configPath);
            if (hadExisting) File.Copy(configPath, backupPath, true);

            try
            {
                var authService = new GoogleAuthService();
                string sampleId = "synthetic-client-id-0050.apps.googleusercontent.com";
                string sampleSecret = "GOCSPX-SyntheticSecretForDpapiTest0050";

                await authService.SaveOAuthConfigAsync(sampleId, sampleSecret);

                if (!File.Exists(configPath))
                    throw new InvalidOperationException("google_oauth_config.json 未建立");

                string rawJson = await File.ReadAllTextAsync(configPath, Encoding.UTF8);
                if (rawJson.Contains(sampleSecret, StringComparison.Ordinal))
                    throw new InvalidOperationException("磁碟 google_oauth_config.json 仍包含明文 ClientSecret");

                var node = JsonNode.Parse(rawJson);
                string? diskSecret = node?["ClientSecret"]?.GetValue<string>();
                if (!CredentialProtectionHelper.IsProtected(diskSecret))
                    throw new InvalidOperationException($"磁碟 ClientSecret 未帶 dpapi: 前綴: {diskSecret}");

                var (loadedId, loadedSecret) = await authService.LoadOAuthConfigAsync();
                if (loadedId != sampleId || loadedSecret != sampleSecret)
                    throw new InvalidOperationException($"DPAPI 解密還原不符: Id={loadedId}, SecretMatch={loadedSecret == sampleSecret}");

                return $"DiskCipherPrefix={diskSecret?.Substring(0, Math.Min(14, diskSecret.Length))}..., PlaintextAbsentOnDisk=true, RoundtripMatch=true";
            }
            finally
            {
                if (hadExisting && File.Exists(backupPath))
                {
                    File.Copy(backupPath, configPath, true);
                    File.Delete(backupPath);
                }
                else if (!hadExisting && File.Exists(configPath))
                {
                    File.Delete(configPath);
                }
            }
        });

        // TC-0050-02: AppSettingsService VercelAccessToken DPAPI encryption & decryption
        await AddTestAsync("TC-0050-02", "驗證 AppSettingsService 儲存 settings.json 時以 Windows DPAPI 加密 VercelAccessToken 且不含明文", async () =>
        {
            await Task.CompletedTask;
            string tempSettingsPath = Path.Combine(Path.GetTempPath(), $"settings_0050_{Guid.NewGuid():N}.json");
            try
            {
                var settingsService = new AppSettingsService(tempSettingsPath);
                string sampleToken = "vcel_synthetic_token_0050_secret_value";

                settingsService.UpdateVercelSettings(sampleToken, "majiocr-test-pwa", "client-id.apps.googleusercontent.com", "https://example.vercel.app");

                string rawJson = File.ReadAllText(tempSettingsPath, Encoding.UTF8);
                if (rawJson.Contains(sampleToken, StringComparison.Ordinal))
                    throw new InvalidOperationException("磁碟 settings.json 仍包含明文 VercelAccessToken");

                var node = JsonNode.Parse(rawJson);
                string? diskToken = node?["vercelAccessToken"]?.GetValue<string>();
                if (!CredentialProtectionHelper.IsProtected(diskToken))
                    throw new InvalidOperationException($"磁碟 vercelAccessToken 未帶 dpapi: 前綴: {diskToken}");

                var loaded = settingsService.LoadSettings();
                if (loaded.VercelAccessToken != sampleToken)
                    throw new InvalidOperationException($"DPAPI 解密還原 VercelAccessToken 不符: {loaded.VercelAccessToken}");

                return $"DiskCipherPrefix={diskToken?.Substring(0, Math.Min(14, diskToken.Length))}..., PlaintextAbsentOnDisk=true, RoundtripMatch=true";
            }
            finally
            {
                if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath);
            }
        });

        // TC-0050-03: Legacy plaintext auto-migration & corrupted ciphertext resilience
        await AddTestAsync("TC-0050-03", "驗證舊版明文設定檔讀取後自動平滑升級為 DPAPI 密文，且損毀密文安全回退不崩潰", async () =>
        {
            string tempSettingsPath = Path.Combine(Path.GetTempPath(), $"settings_legacy_{Guid.NewGuid():N}.json");
            try
            {
                string legacyPlainToken = "legacy_plain_vercel_token_0050";
                string legacyJson = $$"""
                {
                  "settingsVersion": 1,
                  "fullOcrLanguageCode": "chi_tra+chi_sim+jpn+kor+eng",
                  "partialOcrLanguageCode": "chi_tra+chi_sim+jpn+kor+eng",
                  "vercelAccessToken": "{{legacyPlainToken}}"
                }
                """;
                await File.WriteAllTextAsync(tempSettingsPath, legacyJson, Encoding.UTF8);

                var svc = new AppSettingsService(tempSettingsPath);
                var loadedFromLegacy = svc.LoadSettings();
                if (loadedFromLegacy.VercelAccessToken != legacyPlainToken)
                    throw new InvalidOperationException("舊版明文 Token 首次載入應正常回傳明文");

                string upgradedDiskJson = await File.ReadAllTextAsync(tempSettingsPath, Encoding.UTF8);
                if (upgradedDiskJson.Contains(legacyPlainToken, StringComparison.Ordinal))
                    throw new InvalidOperationException("舊版明文設定載入後應自動升級為 DPAPI 密文並覆寫磁碟");

                var upgradedNode = JsonNode.Parse(upgradedDiskJson);
                string? upgradedToken = upgradedNode?["vercelAccessToken"]?.GetValue<string>();
                if (!CredentialProtectionHelper.IsProtected(upgradedToken))
                    throw new InvalidOperationException("自動升級後的 vercelAccessToken 應具備 dpapi: 前綴");

                // Test corrupted ciphertext resilience
                string corruptedJson = """
                {
                  "settingsVersion": 1,
                  "vercelAccessToken": "dpapi:NOT_VALID_BASE64_OR_DPAPI_BLOB!!!"
                }
                """;
                await File.WriteAllTextAsync(tempSettingsPath, corruptedJson, Encoding.UTF8);
                var loadedCorrupted = svc.LoadSettings();
                if (loadedCorrupted.VercelAccessToken != null)
                    throw new InvalidOperationException("損毀或跨機無法解密的 DPAPI 密文應安全回退為 null");

                return "LegacyAutoUpgraded=true, CorruptedSafeFallbackToNull=true";
            }
            finally
            {
                if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath);
            }
        });

        // TC-0050-04: Repository code & update settings sanitization check (pattern + synthetic verification)
        await AddTestAsync("TC-0050-04", "驗證儲存庫敏感資訊偵測器（合成輸入驗證）及 Issue0035、DefaultUpdateSettings.json、CHANGELOG.txt 無個人路徑與舊版雲端硬碟連結", async () =>
        {
            await Task.CompletedTask;
            string repoRoot = FindRepoRoot();

            // 1. 先以明確合成測試資料驗證偵測邏輯有效
            if (!TryDetectSensitivePattern(@"C:\Users\synthetic_user_0050\Documents\card.png", out _) ||
                !TryDetectSensitivePattern("https://drive.google.com/uc?export=download&id=1SyntheticDriveIdForUnitTest000000", out _) ||
                !TryDetectSensitivePattern("file:///z:/synthetic_workspace/Requirements/doc.md", out _))
            {
                throw new InvalidOperationException("敏感路徑與網址特徵偵測器未能攔截合成測試輸入");
            }

            // 2. 檢查 Issue0035AutomatedTestSuite.cs 已改用合成姓名「王小明」且不含任何個人使用者目錄路徑
            string issue35Path = Path.Combine(repoRoot, "MajiOCR.Windows", "Verification", "Issue0035AutomatedTestSuite.cs");
            string issue35Text = File.ReadAllText(issue35Path, Encoding.UTF8);
            bool hasIssue35Hit = TryDetectSensitivePattern(issue35Text, out string issue35Hit);
            if (!issue35Text.Contains("王小明", StringComparison.Ordinal) || hasIssue35Hit)
            {
                throw new InvalidOperationException($"Issue0035AutomatedTestSuite.cs 未通過敏感特徵檢查: {issue35Hit}");
            }

            // 3. 檢查 DefaultUpdateSettings.json 指向官方 GitHub Releases API 且不含雲端硬碟連結
            string defaultUpdatePath = Path.Combine(repoRoot, "MajiOCR.Windows", "DefaultUpdateSettings.json");
            string defaultUpdateText = File.ReadAllText(defaultUpdatePath, Encoding.UTF8);
            bool hasUpdateHit = TryDetectSensitivePattern(defaultUpdateText, out string updateHit);
            if (hasUpdateHit ||
                !defaultUpdateText.Contains("https://api.github.com/repos/Ryan-tw-Hans/MajiStudio/releases/latest", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"DefaultUpdateSettings.json 未通過敏感特徵檢查: {updateHit}");
            }

            // 4. 檢查 CHANGELOG.txt 已使用隱私泛化標記且不含舊版雲端硬碟連結
            string changelogPath = Path.Combine(repoRoot, "CHANGELOG.txt");
            string changelogText = File.ReadAllText(changelogPath, Encoding.UTF8);
            if (TryDetectSensitivePattern(changelogText, out string changelogHit))
            {
                throw new InvalidOperationException($"CHANGELOG.txt 未通過敏感特徵檢查: {changelogHit}");
            }

            var updateSvc = new UpdateService();
            var loadedUpdate = updateSvc.LoadSettings();
            if (loadedUpdate.ReleaseManifestUrl != updateSvc.DefaultReleaseManifestUrl)
            {
                throw new InvalidOperationException($"預設載入之 ReleaseManifestUrl 應為 GitHub Releases API，實際為: {loadedUpdate.ReleaseManifestUrl}");
            }

            return "SyntheticDetectorVerified=true, Issue0035Clean=true, DefaultUpdateSettingsClean=true, ChangelogClean=true";
        });

        // TC-0050-05: .gitignore, relative markdown links, and version 0.1.0.30 alignment
        await AddTestAsync("TC-0050-05", "驗證 .gitignore 排除 IssueLog.sqlite3、正式文件無本機絕對路徑連結且版本同步為 0.1.0.30", async () =>
        {
            await Task.CompletedTask;
            string repoRoot = FindRepoRoot();

            string gitignore = File.ReadAllText(Path.Combine(repoRoot, ".gitignore"), Encoding.UTF8);
            if (gitignore.Contains("!IssueLog/IssueLog.sqlite3", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(".gitignore 仍包含 !IssueLog/IssueLog.sqlite3 例外規則");
            }

            string[] docFolders = { "Architecture", "DesignPlans", "Requirements", "TestPlans", "TestReports" };
            foreach (string folder in docFolders)
            {
                string dirPath = Path.Combine(repoRoot, folder);
                if (!Directory.Exists(dirPath)) continue;
                foreach (string mdFile in Directory.GetFiles(dirPath, "*.md"))
                {
                    string content = File.ReadAllText(mdFile, Encoding.UTF8);
                    if (TryDetectSensitivePattern(content, out string docHit))
                    {
                        throw new InvalidOperationException($"文件仍包含本機敏感路徑或絕對連結 ({Path.GetFileName(mdFile)}): {docHit}");
                    }
                }
            }

            string verText = File.ReadAllText(Path.Combine(repoRoot, "VERSION.txt"), Encoding.UTF8);
            string csprojText = File.ReadAllText(Path.Combine(repoRoot, "MajiOCR.Windows", "MajiOCR.Windows.csproj"), Encoding.UTF8);
            string changelogText = File.ReadAllText(Path.Combine(repoRoot, "CHANGELOG.txt"), Encoding.UTF8);

            if (!verText.Contains("Version: 0.1.0.30") ||
                !csprojText.Contains("<Version>0.1.0-beta.30</Version>") ||
                !changelogText.Contains("## [0.1.0.30]"))
            {
                throw new InvalidOperationException("版本號尚未同步推進至 0.1.0.30 / 0.1.0-beta.30");
            }

            return "GitignoreClean=true, DocRelativeLinksVerified=true, Version=0.1.0.30";
        });

        report.TotalTests = report.TestCases.Count;
        report.PassCount = report.TestCases.Count(c => c.Passed);
        report.FailCount = report.TestCases.Count(c => !c.Passed);
        return report;
    }

    private static bool TryDetectSensitivePattern(string content, out string reason)
    {
        if (string.IsNullOrEmpty(content))
        {
            reason = string.Empty;
            return false;
        }

        // 1. 偵測個人 Windows 使用者目錄路徑 (排除泛用占位符如 <User>、Public、Default)
        var userPathMatch = System.Text.RegularExpressions.Regex.Match(
            content,
            @"(?i)[a-z]:\\Users\\(?!Public\b|Default\b|All Users\b|<)[^\\\s""'`<>|]+");
        if (userPathMatch.Success)
        {
            reason = "PersonalUserPathPattern";
            return true;
        }

        // 2. 偵測本機磁碟絕對連結 file:///c:/ 或 file:///d:/ 等
        var fileUriMatch = System.Text.RegularExpressions.Regex.Match(
            content,
            @"(?i)file:///[a-z]:/");
        if (fileUriMatch.Success)
        {
            reason = "LocalDiskFileUriPattern";
            return true;
        }

        // 3. 偵測直接硬編碼之 Google Drive 檔案下載網址
        var driveUrlMatch = System.Text.RegularExpressions.Regex.Match(
            content,
            @"(?i)drive\.(?:google|usercontent)\.com/(?:uc\?|download\?|drive/folders/)[A-Za-z0-9_?=&%-]+");
        if (driveUrlMatch.Success)
        {
            reason = "HardcodedDriveResourceUrlPattern";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string FindRepoRoot()
    {
        string cur = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            if (File.Exists(Path.Combine(cur, "VERSION.txt")) && File.Exists(Path.Combine(cur, "README.md")))
            {
                return cur;
            }
            string? parent = Directory.GetParent(cur)?.FullName;
            if (parent == null) break;
            cur = parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
