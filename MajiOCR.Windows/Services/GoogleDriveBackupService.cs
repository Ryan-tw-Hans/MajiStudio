using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Services;

public record BackupManifest(
    string BackupId,
    string BackupVersion,
    string AppVersion,
    string Platform,
    DateTime CreatedAtUtc,
    int ContactCount,
    int ImageCount,
    string SqliteSha256,
    string Description,
    bool IsPasswordProtected = false,
    string? EncryptionAlgorithm = null
);

public record DriveBackupVersionItem(
    string FileId,
    string FileName,
    long FileSizeBytes,
    DateTime CreatedAtUtc,
    string Platform,
    int ContactCount,
    int ImageCount,
    string? Description,
    string? SqliteSha256,
    bool IsPasswordProtected = false,
    string? EncryptionAlgorithm = null
);

public record BackupProgress(int Percentage, string StatusMessage);

public record DriveOperationResult(bool Success, string Message, string? FileId = null, BackupManifest? Manifest = null);

public interface IGoogleDriveBackupService
{
    Task<(string ZipPath, BackupManifest Manifest)> CreateLocalBackupPackageAsync(
        string storageRoot,
        string platform = "windows",
        string? passphrase = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default);

    Task<DriveOperationResult> UploadBackupToDriveAsync(
        string zipFilePath,
        BackupManifest manifest,
        string accessToken,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default);

    Task<List<DriveBackupVersionItem>> ListDriveBackupsAsync(
        string accessToken,
        CancellationToken ct = default);

    Task<DriveOperationResult> DownloadBackupFromDriveAsync(
        string fileId,
        string targetZipPath,
        string accessToken,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default);

    Task<DriveOperationResult> RestoreLocalBackupPackageAsync(
        string zipFilePath,
        string storageRoot,
        string? passphrase = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default,
        bool? isApplicationDirectory = null);
}

public class GoogleDriveBackupService : IGoogleDriveBackupService
{
    private static readonly HttpClient HttpClient = new();
    private const string BackupFolderName = "MajiOCR_Backups";
    private const string ManifestFileName = "manifest.json";

    public async Task<(string ZipPath, BackupManifest Manifest)> CreateLocalBackupPackageAsync(
        string storageRoot,
        string platform = "windows",
        string? passphrase = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new BackupProgress(10, "正在檢查資料庫與執行 WAL Checkpoint..."));

        string dbPath = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
        string imagesDir = Path.Combine(storageRoot, StoragePathHelper.CardImagesDirName);
        bool isEncrypted = StorageService.IsDatabaseEncrypted(dbPath);
        string? encAlgo = isEncrypted ? "SQLCipher-AES256" : null;

        // 1. Checkpoint SQLite database to ensure WAL commits are fully in .sqlite3
        int contactCount = 0;
        if (File.Exists(dbPath))
        {
            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                };
                if (!string.IsNullOrEmpty(passphrase))
                {
                    builder.Password = passphrase;
                }
                using var conn = new SqliteConnection(builder.ConnectionString);
                await conn.OpenAsync(ct);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM contact_records WHERE pending_delete = 0;";
                    var countObj = await cmd.ExecuteScalarAsync(ct);
                    contactCount = Convert.ToInt32(countObj);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Checkpoint Error] {ex.Message}");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        // 2. Count images & calculate sqlite sha256
        progress?.Report(new BackupProgress(25, "正在計算圖檔與雜湊驗證碼..."));
        int imageCount = 0;
        if (Directory.Exists(imagesDir))
        {
            imageCount = Directory.GetFiles(imagesDir, "*.*", SearchOption.AllDirectories)
                .Count(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
        }

        string sqliteSha256 = string.Empty;
        if (File.Exists(dbPath))
        {
            using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(fs, ct);
            sqliteSha256 = Convert.ToHexStringLower(hash);
        }

        // 3. Create Manifest
        string nowUtcStr = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string backupId = $"MajiOCR_Backup_{nowUtcStr}_{platform}";
        string encTag = isEncrypted ? " [已加密]" : "";
        var manifest = new BackupManifest(
            BackupId: backupId,
            BackupVersion: "1.0",
            AppVersion: Program.GetAppVersion(),
            Platform: platform,
            CreatedAtUtc: DateTime.UtcNow,
            ContactCount: contactCount,
            ImageCount: imageCount,
            SqliteSha256: sqliteSha256,
            Description: $"{platform.ToUpperInvariant()} 名片備份 ({contactCount} 筆名片, {imageCount} 張圖檔){encTag}",
            IsPasswordProtected: isEncrypted,
            EncryptionAlgorithm: encAlgo
        );

        // 4. Create ZIP package in temp directory
        progress?.Report(new BackupProgress(50, "正在壓縮打包資料庫與名片原圖..."));
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"{backupId}.zip");
        if (File.Exists(tempZipPath)) File.Delete(tempZipPath);

        using (var zipArchive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
        {
            // Add manifest.json
            string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            var manifestEntry = zipArchive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
            {
                await writer.WriteAsync(manifestJson);
            }

            // Add contacts.sqlite3 with FileShare.ReadWrite
            if (File.Exists(dbPath))
            {
                var dbEntry = zipArchive.CreateEntry(StoragePathHelper.DatabaseFileName, CompressionLevel.Optimal);
                using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var entryStream = dbEntry.Open();
                await fs.CopyToAsync(entryStream, ct);
            }

            // Add card-images/
            if (Directory.Exists(imagesDir))
            {
                var files = Directory.GetFiles(imagesDir, "*.*", SearchOption.AllDirectories);
                int currentFile = 0;
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string rel = Path.GetRelativePath(storageRoot, file).Replace('\\', '/');
                    zipArchive.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
                    currentFile++;
                    if (files.Length > 0 && currentFile % 5 == 0)
                    {
                        int p = 50 + (int)(40.0 * currentFile / files.Length);
                        progress?.Report(new BackupProgress(p, $"正在壓縮名片圖檔 ({currentFile}/{files.Length})..."));
                    }
                }
            }
        }

        progress?.Report(new BackupProgress(100, "本機備份包建立完成！"));
        return (tempZipPath, manifest);
    }

    public async Task<string> EnsureBackupFolderAsync(string accessToken, CancellationToken ct = default)
    {
        // Search if folder exists
        string query = $"name = '{BackupFolderName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        string url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&fields=files(id,name)";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var resp = await HttpClient.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(body);
            var files = node?["files"]?.AsArray();
            if (files != null && files.Count > 0)
            {
                string? folderId = files[0]?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(folderId)) return folderId;
            }
        }

        // Create folder
        string createUrl = "https://www.googleapis.com/drive/v3/files";
        var folderMeta = new
        {
            name = BackupFolderName,
            mimeType = "application/vnd.google-apps.folder",
            description = "MajiOCR 名片雲端備份專用資料夾"
        };
        using var createReq = new HttpRequestMessage(HttpMethod.Post, createUrl);
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        createReq.Content = new StringContent(JsonSerializer.Serialize(folderMeta), Encoding.UTF8, "application/json");

        var createResp = await HttpClient.SendAsync(createReq, ct);
        createResp.EnsureSuccessStatusCode();
        string createBody = await createResp.Content.ReadAsStringAsync(ct);
        var createdNode = JsonNode.Parse(createBody);
        return createdNode?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("無法建立 Google Drive 備份資料夾。");
    }

    public async Task<DriveOperationResult> UploadBackupToDriveAsync(
        string zipFilePath,
        BackupManifest manifest,
        string accessToken,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Mock token handler for testing
        if (accessToken.StartsWith("mock_", StringComparison.OrdinalIgnoreCase) ||
            accessToken.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new BackupProgress(100, "測試模式：模擬備份上傳成功"));
            return new DriveOperationResult(true, $"測試模式：成功備份 {manifest.ContactCount} 筆名片至雲端", "mock_drive_file_id_001", manifest);
        }

        try
        {
            progress?.Report(new BackupProgress(15, "正在連線 Google Drive 取得備份目錄..."));
            string folderId = await EnsureBackupFolderAsync(accessToken, ct);

            progress?.Report(new BackupProgress(35, "正在準備上傳備份封裝..."));
            string fileName = Path.GetFileName(zipFilePath);
            var fileInfo = new FileInfo(zipFilePath);
            long fileSize = fileInfo.Exists ? fileInfo.Length : 0;

            // Prepare Resumable Upload Metadata
            var metadata = new
            {
                name = fileName,
                parents = new[] { folderId },
                description = JsonSerializer.Serialize(manifest),
                appProperties = new Dictionary<string, string>
                {
                    ["backup_id"] = manifest.BackupId,
                    ["platform"] = manifest.Platform,
                    ["contact_count"] = manifest.ContactCount.ToString(),
                    ["image_count"] = manifest.ImageCount.ToString(),
                    ["sqlite_sha256"] = manifest.SqliteSha256,
                    ["created_at"] = manifest.CreatedAtUtc.ToString("o"),
                    ["is_password_protected"] = manifest.IsPasswordProtected.ToString().ToLowerInvariant(),
                    ["encryption_algorithm"] = manifest.EncryptionAlgorithm ?? ""
                }
            };

            // Step 1: Initiate Resumable Upload Session
            string initiateUrl = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable";
            using var initReq = new HttpRequestMessage(HttpMethod.Post, initiateUrl);
            initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            initReq.Headers.Add("X-Upload-Content-Type", "application/zip");
            initReq.Headers.Add("X-Upload-Content-Length", fileSize.ToString());
            initReq.Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");

            var initResp = await HttpClient.SendAsync(initReq, ct);
            if (!initResp.IsSuccessStatusCode)
            {
                string err = await initResp.Content.ReadAsStringAsync(ct);
                return new DriveOperationResult(false, $"建立 Google Drive 上傳會話失敗 (HTTP {(int)initResp.StatusCode}): {err}");
            }

            string? sessionUri = initResp.Headers.Location?.ToString();
            if (string.IsNullOrWhiteSpace(sessionUri))
            {
                return new DriveOperationResult(false, "無法取得 Google Drive 上傳會話網址 (缺少 Location 標頭)。");
            }

            // Step 2: Upload Binary Data Stream to Resumable Session URI
            progress?.Report(new BackupProgress(50, "正在傳輸備份資料至 Google Drive..."));
            using var fileStream = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var putReq = new HttpRequestMessage(HttpMethod.Put, sessionUri);

            var streamContent = new ProgressableStreamContent(fileStream, fileSize, progress, startPercentage: 50, endPercentage: 95);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            putReq.Content = streamContent;

            var putResp = await HttpClient.SendAsync(putReq, ct);
            if (!putResp.IsSuccessStatusCode)
            {
                string err = await putResp.Content.ReadAsStringAsync(ct);
                return new DriveOperationResult(false, $"Google Drive 檔案傳輸失敗 (HTTP {(int)putResp.StatusCode}): {err}");
            }

            string respJson = await putResp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(respJson);
            string fileId = node?["id"]?.GetValue<string>() ?? "unknown_id";

            progress?.Report(new BackupProgress(100, "雲端備份成功！"));
            return new DriveOperationResult(true, $"✅ 成功備份至 Google Drive！\n備份包：{fileName}\n名片：{manifest.ContactCount} 筆、圖片：{manifest.ImageCount} 張", fileId, manifest);
        }
        catch (Exception ex)
        {
            return new DriveOperationResult(false, $"備份上傳過程發生錯誤：{ex.Message}");
        }
    }

    public async Task<List<DriveBackupVersionItem>> ListDriveBackupsAsync(
        string accessToken,
        CancellationToken ct = default)
    {
        // Mock token handler
        if (accessToken.StartsWith("mock_", StringComparison.OrdinalIgnoreCase) ||
            accessToken.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
        {
            return new List<DriveBackupVersionItem>
            {
                new(
                    FileId: "mock_file_001",
                    FileName: "MajiOCR_Backup_20260929_120000_windows.zip",
                    FileSizeBytes: 1048576,
                    CreatedAtUtc: DateTime.UtcNow.AddHours(-2),
                    Platform: "windows",
                    ContactCount: 6,
                    ImageCount: 6,
                    Description: "WINDOWS 名片備份 (6 筆名片, 6 張圖檔)",
                    SqliteSha256: "mocksha256",
                    IsPasswordProtected: false,
                    EncryptionAlgorithm: null
                )
            };
        }

        var results = new List<DriveBackupVersionItem>();
        try
        {
            string folderId = await EnsureBackupFolderAsync(accessToken, ct);
            string query = $"'{folderId}' in parents and trashed = false and mimeType = 'application/zip'";
            string url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&fields=files(id,name,size,createdTime,modifiedTime,description,appProperties)&orderBy=createdTime+desc";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var resp = await HttpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return results;

            string json = await resp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json);
            var files = node?["files"]?.AsArray();
            if (files == null) return results;

            foreach (var f in files)
            {
                string id = f?["id"]?.GetValue<string>() ?? "";
                string name = f?["name"]?.GetValue<string>() ?? "";
                long size = f?["size"] != null ? Convert.ToInt64(f["size"]!.GetValue<string>()) : 0;
                string createdStr = f?["createdTime"]?.GetValue<string>() ?? "";
                DateTime.TryParse(createdStr, out DateTime created);

                string? desc = f?["description"]?.GetValue<string>();
                string platform = "unknown";
                int contacts = 0;
                int images = 0;
                string? sqliteSha = null;
                bool isProtected = false;
                string? encAlgo = null;

                var appProps = f?["appProperties"];
                if (appProps != null)
                {
                    platform = appProps["platform"]?.GetValue<string>() ?? "unknown";
                    int.TryParse(appProps["contact_count"]?.GetValue<string>(), out contacts);
                    int.TryParse(appProps["image_count"]?.GetValue<string>(), out images);
                    sqliteSha = appProps["sqlite_sha256"]?.GetValue<string>();
                    string? protStr = appProps["is_password_protected"]?.GetValue<string>();
                    if (bool.TryParse(protStr, out bool bp)) isProtected = bp;
                    else if (protStr == "1" || protStr == "true") isProtected = true;
                    encAlgo = appProps["encryption_algorithm"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(encAlgo)) encAlgo = null;
                }
                else if (!string.IsNullOrWhiteSpace(desc))
                {
                    try
                    {
                        var manifest = JsonSerializer.Deserialize<BackupManifest>(desc);
                        if (manifest != null)
                        {
                            platform = manifest.Platform;
                            contacts = manifest.ContactCount;
                            images = manifest.ImageCount;
                            sqliteSha = manifest.SqliteSha256;
                            isProtected = manifest.IsPasswordProtected;
                            encAlgo = manifest.EncryptionAlgorithm;
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(platform) || platform == "unknown")
                {
                    if (name.Contains("windows", StringComparison.OrdinalIgnoreCase)) platform = "windows";
                    else if (name.Contains("android", StringComparison.OrdinalIgnoreCase)) platform = "android";
                }

                results.Add(new DriveBackupVersionItem(
                    FileId: id,
                    FileName: name,
                    FileSizeBytes: size,
                    CreatedAtUtc: created,
                    Platform: platform,
                    ContactCount: contacts,
                    ImageCount: images,
                    Description: desc,
                    SqliteSha256: sqliteSha,
                    IsPasswordProtected: isProtected,
                    EncryptionAlgorithm: encAlgo
                ));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ListBackups Error] {ex.Message}");
        }
        return results;
    }

    public async Task<DriveOperationResult> DownloadBackupFromDriveAsync(
        string fileId,
        string targetZipPath,
        string accessToken,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Mock handler
        if (accessToken.StartsWith("mock_", StringComparison.OrdinalIgnoreCase) ||
            accessToken.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new BackupProgress(100, "測試模式：模擬下載備份成功"));
            return new DriveOperationResult(true, "測試模式：下載完成", fileId);
        }

        try
        {
            progress?.Report(new BackupProgress(20, "正在自 Google Drive 下載備份封裝..."));
            string url = $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long? totalBytes = resp.Content.Headers.ContentLength;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(targetZipPath, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buffer = new byte[81920];
            long readBytes = 0;
            int bytes;

            while ((bytes = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytes, ct);
                readBytes += bytes;
                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    int p = 20 + (int)(70.0 * readBytes / totalBytes.Value);
                    progress?.Report(new BackupProgress(p, $"已下載 {readBytes / 1024} KB / {totalBytes.Value / 1024} KB..."));
                }
            }

            progress?.Report(new BackupProgress(100, "備份包下載完成！"));
            return new DriveOperationResult(true, "下載完成", fileId);
        }
        catch (Exception ex)
        {
            return new DriveOperationResult(false, $"下載備份失敗：{ex.Message}");
        }
    }

    public async Task<DriveOperationResult> RestoreLocalBackupPackageAsync(
        string zipFilePath,
        string storageRoot,
        string? passphrase = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default,
        bool? isApplicationDirectory = null)
    {
        if (!File.Exists(zipFilePath))
        {
            return new DriveOperationResult(false, "備份檔案不存在。");
        }

        progress?.Report(new BackupProgress(10, "正在驗證備份包完整性與中繼資料..."));
        BackupManifest? manifest = null;
        string tempStagingDir = Path.Combine(Path.GetTempPath(), $"restore_staging_{Guid.NewGuid():N}");

        try
        {
            using (var zip = ZipFile.OpenRead(zipFilePath))
            {
                var manifestEntry = zip.GetEntry(ManifestFileName);
                if (manifestEntry != null)
                {
                    using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
                    string json = await reader.ReadToEndAsync(ct);
                    manifest = JsonSerializer.Deserialize<BackupManifest>(json);
                }

                var dbEntry = zip.GetEntry(StoragePathHelper.DatabaseFileName);
                if (dbEntry == null)
                {
                    return new DriveOperationResult(false, "備份包無效：缺少 contacts.sqlite3 資料庫檔案。");
                }
            }

            // 1. Extract to temporary staging directory first
            progress?.Report(new BackupProgress(30, "正在解壓縮還原資料庫與名片原圖..."));
            Directory.CreateDirectory(tempStagingDir);
            using (var zip = ZipFile.OpenRead(zipFilePath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string target = Path.Combine(tempStagingDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    string? pDir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(pDir) && !Directory.Exists(pDir)) Directory.CreateDirectory(pDir);
                    entry.ExtractToFile(target, true);
                }
            }

            // 2. Inspect and verify staging database integrity
            progress?.Report(new BackupProgress(60, "正在校驗還原資料庫完整性與金鑰..."));
            string stagingDbPath = Path.Combine(tempStagingDir, StoragePathHelper.DatabaseFileName);
            bool isEncrypted = StorageService.IsDatabaseEncrypted(stagingDbPath);

            if (isEncrypted)
            {
                if (string.IsNullOrEmpty(passphrase))
                {
                    return new DriveOperationResult(false, "此備份資料庫受 SQLCipher 密碼保護，請輸入主密碼後再執行還原。");
                }

                if (!StorageService.TryVerifyPassphrase(stagingDbPath, passphrase, out string? verifyErr))
                {
                    return new DriveOperationResult(false, $"資料庫解密失敗：密碼不正確或資料庫毀損 ({verifyErr})。");
                }

                // Verify integrity with password
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = stagingDbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    Password = passphrase
                };
                using var conn = new SqliteConnection(builder.ConnectionString);
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                var integrityResult = (await cmd.ExecuteScalarAsync(ct))?.ToString();
                if (integrityResult != "ok")
                {
                    return new DriveOperationResult(false, $"資料庫完整性檢查未通過 (結果: {integrityResult})。");
                }
            }
            else
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = stagingDbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                };
                using var conn = new SqliteConnection(builder.ConnectionString);
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                var integrityResult = (await cmd.ExecuteScalarAsync(ct))?.ToString();
                if (integrityResult != "ok")
                {
                    return new DriveOperationResult(false, $"資料庫完整性檢查未通過 (結果: {integrityResult})。");
                }
            }

            SqliteConnection.ClearAllPools();

            // 3. Ensure target storage directories and take safety rollback snapshot of current target
            progress?.Report(new BackupProgress(80, "正在建立還原前本機安全復原快照..."));
            bool isAppDir = isApplicationDirectory ?? false;
            if (!isApplicationDirectory.HasValue)
            {
                try
                {
                    isAppDir = string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(StoragePathHelper.GetDefaultStorageRoot())),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }

            StoragePathHelper.EnsureStorageDirectories(storageRoot, isAppDir);
            string currentDbPath = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
            string currentImagesDir = Path.Combine(storageRoot, StoragePathHelper.CardImagesDirName);
            string snapshotDir = Path.Combine(storageRoot, ".backup_snapshot");

            try
            {
                Directory.CreateDirectory(snapshotDir);
                if (File.Exists(currentDbPath))
                {
                    File.Copy(currentDbPath, Path.Combine(snapshotDir, "contacts_pre_restore.sqlite3"), true);
                }
            }
            catch { }

            // 4. Overwrite storageRoot with staged files
            progress?.Report(new BackupProgress(90, "正在套用還原資料庫與圖片庫..."));

            // Copy staging database and secure file permissions
            File.Copy(stagingDbPath, currentDbPath, true);
            try
            {
                StoragePathHelper.ProtectDataFile(currentDbPath);
            }
            catch { }

            // Copy staging images
            string stagingImagesDir = Path.Combine(tempStagingDir, StoragePathHelper.CardImagesDirName);
            if (Directory.Exists(stagingImagesDir))
            {
                foreach (var imgFile in Directory.GetFiles(stagingImagesDir, "*.*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(stagingImagesDir, imgFile);
                    string dest = Path.Combine(currentImagesDir, rel);
                    string? destP = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destP) && !Directory.Exists(destP)) Directory.CreateDirectory(destP);
                    File.Copy(imgFile, dest, true);
                }
            }

            progress?.Report(new BackupProgress(100, "還原完成！"));
            string encNotice = isEncrypted ? " (🔒 已還原為加密保護狀態)" : " (🔓 已還原為未加密狀態)";
            string summary = manifest != null
                ? $"✅ 成功自備份包還原！\n來源版本：{manifest.BackupId}\n名片：{manifest.ContactCount} 筆、圖片：{manifest.ImageCount} 張{encNotice}"
                : $"✅ 成功自備份包還原！名片資料與圖片庫已更新。{encNotice}";

            return new DriveOperationResult(true, summary, null, manifest);
        }
        catch (Exception ex)
        {
            // Rollback if failed
            try
            {
                string currentDbPath = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
                string snapshotDir = Path.Combine(storageRoot, ".backup_snapshot");
                if (File.Exists(Path.Combine(snapshotDir, "contacts_pre_restore.sqlite3")))
                {
                    File.Copy(Path.Combine(snapshotDir, "contacts_pre_restore.sqlite3"), currentDbPath, true);
                }
            }
            catch { }
            return new DriveOperationResult(false, $"還原過程發生錯誤：{ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempStagingDir))
                {
                    Directory.Delete(tempStagingDir, true);
                }
            }
            catch { }
        }
    }
}

internal class ProgressableStreamContent : HttpContent
{
    private const int BufferSize = 81920;
    private readonly Stream _stream;
    private readonly long _length;
    private readonly IProgress<BackupProgress>? _progress;
    private readonly int _startPercentage;
    private readonly int _endPercentage;

    public ProgressableStreamContent(Stream stream, long length, IProgress<BackupProgress>? progress, int startPercentage = 50, int endPercentage = 95)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _length = length;
        _progress = progress;
        _startPercentage = startPercentage;
        _endPercentage = endPercentage;
    }

    protected override async Task SerializeToStreamAsync(Stream targetStream, System.Net.TransportContext? context)
    {
        var buffer = new byte[BufferSize];
        long totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await targetStream.WriteAsync(buffer, 0, bytesRead);
            totalBytesRead += bytesRead;

            if (_progress != null && _length > 0)
            {
                int range = _endPercentage - _startPercentage;
                int currentPct = _startPercentage + (int)((double)totalBytesRead / _length * range);
                long uploadedKb = totalBytesRead / 1024;
                long totalKb = _length / 1024;
                _progress.Report(new BackupProgress(currentPct, $"正在上傳備份封裝至雲端 ({uploadedKb} KB / {totalKb} KB)..."));
            }
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
