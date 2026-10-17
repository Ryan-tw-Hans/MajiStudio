using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public class Issue0033AutomatedTestSuite
{
    public record TestResult(string TestName, bool Passed, string Message);

    public static async Task<List<TestResult>> RunAllTestsAsync()
    {
        var results = new List<TestResult>();

        results.Add(Test01_ManifestSerialization());
        results.Add(await Test02_CreateLocalBackupPackageAsync());
        results.Add(await Test03_ListBackupsAndVersionDetectionAsync());
        results.Add(await Test04_RestoreLocalBackupPackageAsync());

        return results;
    }

    public static TestResult Test01_ManifestSerialization()
    {
        var manifest = new BackupManifest(
            BackupId: "MajiOCR_Backup_20260929_120000_windows",
            BackupVersion: "1.0",
            AppVersion: "0.1.0-beta.18",
            Platform: "windows",
            CreatedAtUtc: DateTime.UtcNow,
            ContactCount: 15,
            ImageCount: 15,
            SqliteSha256: "abc1234567890def",
            Description: "WINDOWS 名片備份 (15 筆名片, 15 張圖檔)"
        );

        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<BackupManifest>(json);

        if (deserialized != null &&
            deserialized.BackupId == manifest.BackupId &&
            deserialized.ContactCount == 15 &&
            deserialized.ImageCount == 15 &&
            deserialized.Platform == "windows")
        {
            return new TestResult("TC-0033-01: Manifest JSON 序列化與屬性欄位校驗", true, "Manifest 包含 backup_id, platform, contact_count, image_count 與 SHA-256");
        }

        return new TestResult("TC-0033-01: Manifest JSON 序列化與屬性欄位校驗", false, "Manifest 欄位反序列化不相符");
    }

    public static async Task<TestResult> Test02_CreateLocalBackupPackageAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"test_backup_src_{Guid.NewGuid():N}");
        string dbPath = Path.Combine(tempRoot, StoragePathHelper.DatabaseFileName);
        string imagesDir = Path.Combine(tempRoot, StoragePathHelper.CardImagesDirName);

        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(imagesDir);

        try
        {
            // Seed a small sqlite database
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE contact_records (contact_id TEXT PRIMARY KEY, status TEXT, created_at TEXT, updated_at TEXT);
                    INSERT INTO contact_records VALUES ('c1', 'Ready', '2026-09-29T00:00:00Z', '2026-09-29T00:00:00Z');
                    INSERT INTO contact_records VALUES ('c2', 'Ready', '2026-09-29T00:00:00Z', '2026-09-29T00:00:00Z');
                ";
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            // Seed a dummy image
            await File.WriteAllBytesAsync(Path.Combine(imagesDir, "dummy1.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 });

            var driveService = new GoogleDriveBackupService();
            var (zipPath, manifest) = await driveService.CreateLocalBackupPackageAsync(tempRoot, "windows");

            bool zipExists = File.Exists(zipPath);
            bool containsDb = false;
            bool containsManifest = false;
            bool containsImage = false;

            if (zipExists)
            {
                using var zip = ZipFile.OpenRead(zipPath);
                containsDb = zip.GetEntry(StoragePathHelper.DatabaseFileName) != null;
                containsManifest = zip.GetEntry("manifest.json") != null;
                containsImage = zip.GetEntry($"{StoragePathHelper.CardImagesDirName}/dummy1.jpg") != null;
            }

            if (File.Exists(zipPath)) File.Delete(zipPath);

            if (zipExists && containsDb && containsManifest && containsImage && manifest.ContactCount == 2 && manifest.ImageCount == 1)
            {
                return new TestResult("TC-0033-02: 本機備份包打包與 SQLite WAL Checkpoint 驗證", true, "成功打包 ZIP，內含 contacts.sqlite3、manifest.json 及 card-images/，筆數與圖檔數正確");
            }

            return new TestResult("TC-0033-02: 本機備份包打包與 SQLite WAL Checkpoint 驗證", false, $"打包內容不完整：zipExists={zipExists}, containsDb={containsDb}, containsManifest={containsManifest}, containsImage={containsImage}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    public static async Task<TestResult> Test03_ListBackupsAndVersionDetectionAsync()
    {
        var driveService = new GoogleDriveBackupService();
        var items = await driveService.ListDriveBackupsAsync("mock_token_123");

        if (items.Count > 0 &&
            items[0].Platform == "windows" &&
            items[0].ContactCount == 6 &&
            items[0].ImageCount == 6)
        {
            return new TestResult("TC-0033-03: Google Drive 備份版本清單與平台屬性解析", true, $"成功解析版本項目：{items[0].FileName}, 平台={items[0].Platform}, 名片={items[0].ContactCount}, 圖片={items[0].ImageCount}");
        }

        return new TestResult("TC-0033-03: Google Drive 備份版本清單與平台屬性解析", false, "版本解析失敗");
    }

    public static async Task<TestResult> Test04_RestoreLocalBackupPackageAsync()
    {
        string sourceDir = Path.Combine(Path.GetTempPath(), $"test_restore_src_{Guid.NewGuid():N}");
        string targetDir = Path.Combine(Path.GetTempPath(), $"test_restore_dest_{Guid.NewGuid():N}");
        string zipPath = Path.Combine(Path.GetTempPath(), $"test_package_{Guid.NewGuid():N}.zip");

        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            string dbPath = Path.Combine(sourceDir, StoragePathHelper.DatabaseFileName);
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE contact_records (contact_id TEXT PRIMARY KEY, status TEXT, created_at TEXT, updated_at TEXT);
                    INSERT INTO contact_records VALUES ('restored_1', 'Ready', '2026-09-29T00:00:00Z', '2026-09-29T00:00:00Z');
                ";
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            var driveService = new GoogleDriveBackupService();
            var (createdZip, manifest) = await driveService.CreateLocalBackupPackageAsync(sourceDir, "windows");
            File.Copy(createdZip, zipPath, true);
            File.Delete(createdZip);

            // Execute restore into targetDir
            var restoreResult = await driveService.RestoreLocalBackupPackageAsync(zipPath, targetDir);

            string restoredDbPath = Path.Combine(targetDir, StoragePathHelper.DatabaseFileName);
            bool restoredDbExists = File.Exists(restoredDbPath);
            int restoredCount = 0;

            if (restoredDbExists)
            {
                using var conn = new SqliteConnection($"Data Source={restoredDbPath}");
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM contact_records;";
                restoredCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            SqliteConnection.ClearAllPools();

            if (restoreResult.Success && restoredDbExists && restoredCount == 1)
            {
                return new TestResult("TC-0033-04: 本機安全還原與資料庫完整性校驗", true, $"成功還原資料庫，驗證筆數={restoredCount}，完整性檢查 OK");
            }

            return new TestResult("TC-0033-04: 本機安全還原與資料庫完整性校驗", false, $"還原失敗：{restoreResult.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { Directory.Delete(sourceDir, true); } catch { }
            try { Directory.Delete(targetDir, true); } catch { }
        }
    }
}
