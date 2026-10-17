using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public class Issue0041TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0041TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0041TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.20";
    public string IssueId { get; set; } = "ISSUE-0041";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0041TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0041AutomatedTestSuite
{
    public static async Task<Issue0041TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0041TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0041TestCaseResult
            {
                TestId = id,
                Description = desc,
                Passed = passed,
                Details = details,
                LatencyMs = ms
            });
            if (passed) report.PassCount++;
            else report.FailCount++;
            report.TotalTests++;
        }

        string baseTemp = Path.Combine(Path.GetTempPath(), $"issue0041_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseTemp);
        Directory.CreateDirectory(evidenceDir);

        var driveService = new GoogleDriveBackupService();

        try
        {
            // Helper for draft snapshots
            ContactDraftSnapshot CreateDraft(string name, string company, string phone)
            {
                byte[] bmp = CreateSyntheticBmp(50, 50, 0x11, 0x22, 0x33);
                return new ContactDraftSnapshot
                {
                    ContactId = Guid.NewGuid().ToString("N"),
                    SaveOperationId = Guid.NewGuid().ToString("N"),
                    Revision = 1,
                    ImageBytes = bmp,
                    ImageSha256 = Convert.ToHexString(SHA256.HashData(bmp)),
                    CaptureSource = "ImagePickerCopy",
                    MimeType = "image/bmp",
                    Width = 50,
                    Height = 50,
                    Values = new List<DraftValueSnapshot>
                    {
                        new() { ValueKind = CanonicalValueKind.Name, RawValue = name, Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow },
                        new() { ValueKind = CanonicalValueKind.Company, RawValue = company, Label = "公司", Ordinal = 1, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow },
                        new() { ValueKind = CanonicalValueKind.Phone, RawValue = phone, Label = "電話", Ordinal = 2, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                    }
                };
            }

            // ==========================================
            // T01: 未加密資料庫備份 Manifest 與屬性驗證
            // ==========================================
            var t01Sw = Stopwatch.StartNew();
            string plainStorageDir = Path.Combine(baseTemp, "PlainStorage");
            var plainStorage = new StorageService(plainStorageDir, autoInit: true);
            var draft1 = CreateDraft("張小明", "測試科技", "0911-222-333");
            var saveRes1 = await plainStorage.SaveDraftAsync(draft1);
            plainStorage.Dispose();
            SqliteConnection.ClearAllPools();

            var (plainZip, plainManifest) = await driveService.CreateLocalBackupPackageAsync(plainStorageDir, "windows");
            bool t01Passed = saveRes1.Succeeded &&
                             File.Exists(plainZip) &&
                             !plainManifest.IsPasswordProtected &&
                             plainManifest.EncryptionAlgorithm == null &&
                             plainManifest.ContactCount == 1;
            t01Sw.Stop();
            Record("T01", "未加密資料庫備份 Manifest 與屬性驗證 (IsPasswordProtected=false, ContactCount=1)",
                t01Passed,
                $"ZipExists={File.Exists(plainZip)}, IsProtected={plainManifest.IsPasswordProtected}, Algo={plainManifest.EncryptionAlgorithm}, Contacts={plainManifest.ContactCount}, SaveSuccess={saveRes1.Succeeded}",
                t01Sw.ElapsedMilliseconds);

            // Delay 1.1s for unique backup timestamp
            await Task.Delay(1100);

            // ==========================================
            // T02: 加密資料庫備份 Manifest 與屬性驗證
            // ==========================================
            var t02Sw = Stopwatch.StartNew();
            string encStorageDir = Path.Combine(baseTemp, "EncryptedStorage");
            string masterPass = "MasterSecret@2026!";
            var encStorage = new StorageService(encStorageDir, autoInit: true);
            var saveRes2A = await encStorage.SaveDraftAsync(CreateDraft("李大華", "加密資訊", "0922-333-444"));
            var saveRes2B = await encStorage.SaveDraftAsync(CreateDraft("王美麗", "金鑰管理", "0933-444-555"));
            // Enable SQLCipher encryption
            bool encEnabled = await encStorage.EnableEncryptionAsync(masterPass);
            encStorage.Dispose();
            SqliteConnection.ClearAllPools();

            var (encZip, encManifest) = await driveService.CreateLocalBackupPackageAsync(encStorageDir, "windows", masterPass);
            bool t02Passed = saveRes2A.Succeeded && saveRes2B.Succeeded && encEnabled &&
                             File.Exists(encZip) &&
                             encManifest.IsPasswordProtected &&
                             encManifest.EncryptionAlgorithm == "SQLCipher-AES256" &&
                             encManifest.ContactCount == 2;
            t02Sw.Stop();
            Record("T02", "加密資料庫備份 Manifest 與屬性驗證 (IsPasswordProtected=true, ContactCount=2)",
                t02Passed,
                $"ZipExists={File.Exists(encZip)}, IsProtected={encManifest.IsPasswordProtected}, Algo={encManifest.EncryptionAlgorithm}, Contacts={encManifest.ContactCount}",
                t02Sw.ElapsedMilliseconds);

            // ==========================================
            // T03: Google Drive 備份清單解析與加密標記解析 (List Parser)
            // ==========================================
            var t03Sw = Stopwatch.StartNew();
            var mockDriveList = await driveService.ListDriveBackupsAsync("mock_token_for_test");
            bool t03Passed = mockDriveList.Count > 0 &&
                             mockDriveList[0].ContactCount == 6 &&
                             mockDriveList[0].IsPasswordProtected == false;
            t03Sw.Stop();
            Record("T03", "Google Drive 備份清單解析與加密標記解析 (DriveBackupVersionItem)",
                t03Passed,
                $"ListCount={mockDriveList.Count}, Item0_Protected={mockDriveList[0].IsPasswordProtected}, Contacts={mockDriveList[0].ContactCount}",
                t03Sw.ElapsedMilliseconds);

            // ==========================================
            // T04: 加密備份正確密碼還原測試 (Correct Password Restore)
            // ==========================================
            var t04Sw = Stopwatch.StartNew();
            string restoreTargetDir1 = Path.Combine(baseTemp, "RestoreTarget1");
            var restoreRes1 = await driveService.RestoreLocalBackupPackageAsync(encZip, restoreTargetDir1, masterPass);
            string restoredDbPath1 = Path.Combine(restoreTargetDir1, StoragePathHelper.DatabaseFileName);
            bool restoredIsEnc1 = StorageService.IsDatabaseEncrypted(restoredDbPath1);
            bool verifyPass1 = StorageService.TryVerifyPassphrase(restoredDbPath1, masterPass, out _);

            bool t04Passed = restoreRes1.Success && restoredIsEnc1 && verifyPass1;
            t04Sw.Stop();
            Record("T04", "加密備份以正確密碼還原測試 (驗證通過且資料庫完整性 ok)",
                t04Passed,
                $"Success={restoreRes1.Success}, Encrypted={restoredIsEnc1}, VerifyPass={verifyPass1}, Msg={restoreRes1.Message}",
                t04Sw.ElapsedMilliseconds);

            // ==========================================
            // T05: 加密備份錯誤密碼還原測試 (Wrong Password Protection)
            // ==========================================
            var t05Sw = Stopwatch.StartNew();
            string restoreTargetDir2 = Path.Combine(baseTemp, "RestoreTarget2");
            var dummyStorage2 = new StorageService(restoreTargetDir2, autoInit: true);
            dummyStorage2.Dispose();
            SqliteConnection.ClearAllPools();
            string existingDummyDb = Path.Combine(restoreTargetDir2, StoragePathHelper.DatabaseFileName);
            await File.WriteAllTextAsync(existingDummyDb, "PRE_EXISTING_DATA_SHOULD_NOT_BE_DESTROYED");

            var restoreRes2 = await driveService.RestoreLocalBackupPackageAsync(encZip, restoreTargetDir2, "WrongPassword#999");
            string afterDbContent2 = await File.ReadAllTextAsync(existingDummyDb);
            bool t05Passed = !restoreRes2.Success &&
                             restoreRes2.Message.Contains("密碼不正確") &&
                             afterDbContent2 == "PRE_EXISTING_DATA_SHOULD_NOT_BE_DESTROYED";
            t05Sw.Stop();
            Record("T05", "加密備份以錯誤密碼還原測試 (攔截失敗且保留原資料庫不被破壞)",
                t05Passed,
                $"Success={restoreRes2.Success}, ErrorMsg={restoreRes2.Message}, OriginalPreserved={afterDbContent2 == "PRE_EXISTING_DATA_SHOULD_NOT_BE_DESTROYED"}",
                t05Sw.ElapsedMilliseconds);

            // ==========================================
            // T06: 加密備份未輸入密碼還原測試 (No Password Interception)
            // ==========================================
            var t06Sw = Stopwatch.StartNew();
            string restoreTargetDir3 = Path.Combine(baseTemp, "RestoreTarget3");
            var restoreRes3 = await driveService.RestoreLocalBackupPackageAsync(encZip, restoreTargetDir3, null);
            bool t06Passed = !restoreRes3.Success && restoreRes3.Message.Contains("請輸入主密碼");
            t06Sw.Stop();
            Record("T06", "加密備份未提供密碼還原測試 (自動攔截並提示需輸入主密碼)",
                t06Passed,
                $"Success={restoreRes3.Success}, Msg={restoreRes3.Message}",
                t06Sw.ElapsedMilliseconds);

            // ==========================================
            // T07: 未加密備份還原至已加密環境 (Plain Restore Over Encrypted)
            // ==========================================
            var t07Sw = Stopwatch.StartNew();
            string restoreTargetDir4 = Path.Combine(baseTemp, "RestoreTarget4");
            // Pre-create encrypted target
            var preEncStorage = new StorageService(restoreTargetDir4, autoInit: true);
            await preEncStorage.EnableEncryptionAsync("OldSecret123!");
            preEncStorage.Dispose();
            SqliteConnection.ClearAllPools();

            var restoreRes4 = await driveService.RestoreLocalBackupPackageAsync(plainZip, restoreTargetDir4);
            string restoredDbPath4 = Path.Combine(restoreTargetDir4, StoragePathHelper.DatabaseFileName);
            bool isPlainNow4 = !StorageService.IsDatabaseEncrypted(restoredDbPath4);

            var postStorage4 = new StorageService(restoreTargetDir4, autoInit: true);
            var contacts4 = await postStorage4.GetContactsPagedAsync(null, 0, 10);
            postStorage4.Dispose();
            SqliteConnection.ClearAllPools();

            bool t07Passed = restoreRes4.Success && isPlainNow4 && contacts4.TotalCount == 1;
            t07Sw.Stop();
            Record("T07", "未加密備份還原至加密環境 (成功轉為標準 SQLite 並可直接讀取名片)",
                t07Passed,
                $"Success={restoreRes4.Success}, IsPlain={isPlainNow4}, RestoredCount={contacts4.TotalCount}",
                t07Sw.ElapsedMilliseconds);

            // ==========================================
            // T08: 加密備份還原至未加密環境 (Encrypted Restore Over Plain)
            // ==========================================
            var t08Sw = Stopwatch.StartNew();
            string restoreTargetDir5 = Path.Combine(baseTemp, "RestoreTarget5");
            // Pre-create plain target
            var prePlainStorage = new StorageService(restoreTargetDir5, autoInit: true);
            prePlainStorage.Dispose();
            SqliteConnection.ClearAllPools();

            var restoreRes5 = await driveService.RestoreLocalBackupPackageAsync(encZip, restoreTargetDir5, masterPass);
            string restoredDbPath5 = Path.Combine(restoreTargetDir5, StoragePathHelper.DatabaseFileName);
            bool isEncNow5 = StorageService.IsDatabaseEncrypted(restoredDbPath5);

            var postStorage5 = new StorageService(restoreTargetDir5, autoInit: true, passphrase: masterPass);
            var contacts5 = await postStorage5.GetContactsPagedAsync(null, 0, 10);
            postStorage5.Dispose();
            SqliteConnection.ClearAllPools();

            bool t08Passed = restoreRes5.Success && isEncNow5 && contacts5.TotalCount == 2;
            t08Sw.Stop();
            Record("T08", "加密備份還原至未加密環境 (以密碼還原成功，資料庫轉為加密並正確載入 2 筆名片)",
                t08Passed,
                $"Success={restoreRes5.Success}, IsEncrypted={isEncNow5}, RestoredCount={contacts5.TotalCount}",
                t08Sw.ElapsedMilliseconds);

            // ==========================================
            // T09: 模擬主程式目錄環境還原 (Application Directory Mode & Inherited ACL Protection)
            // ==========================================
            var t09Sw = Stopwatch.StartNew();
            string restoreTargetDir6 = Path.Combine(baseTemp, "AppDirSimulatedTarget");
            Directory.CreateDirectory(restoreTargetDir6);

            var restoreRes6 = await driveService.RestoreLocalBackupPackageAsync(
                encZip,
                restoreTargetDir6,
                masterPass,
                isApplicationDirectory: true);

            string restoredDbPath6 = Path.Combine(restoreTargetDir6, StoragePathHelper.DatabaseFileName);
            bool isEncNow6 = StorageService.IsDatabaseEncrypted(restoredDbPath6);

            var postStorage6 = new StorageService(restoreTargetDir6, autoInit: true, passphrase: masterPass);
            var contacts6 = await postStorage6.GetContactsPagedAsync(null, 0, 10);
            postStorage6.Dispose();
            SqliteConnection.ClearAllPools();

            bool t09Passed = restoreRes6.Success && isEncNow6 && contacts6.TotalCount == 2;
            t09Sw.Stop();
            Record("T09", "模擬主程式目錄環境還原 (isApplicationDirectory=true，未受保護繼承目錄還原成功，資料庫加密且完整載入 2 筆名片)",
                t09Passed,
                $"Success={restoreRes6.Success}, IsEncrypted={isEncNow6}, RestoredCount={contacts6.TotalCount}",
                t09Sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("FATAL", $"測試套件執行發生例外：{ex.Message}", false, ex.ToString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(baseTemp)) Directory.Delete(baseTemp, true); } catch { }
        }

        sw.Stop();

        // Write Evidence Report
        string reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        string reportFile = Path.Combine(evidenceDir, "Issue0041_Automated_Test_Report.json");
        await File.WriteAllTextAsync(reportFile, reportJson);

        return report;
    }

    private static byte[] CreateSyntheticBmp(int width, int height, byte r = 255, byte g = 255, byte b = 255)
    {
        int rowBytes = ((width * 3 + 3) / 4) * 4;
        int imageSize = rowBytes * height;
        int fileSize = 54 + imageSize;
        byte[] bmp = new byte[fileSize];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);

        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(0).CopyTo(bmp, 30);
        BitConverter.GetBytes(imageSize).CopyTo(bmp, 34);

        for (int y = 0; y < height; y++)
        {
            int rowStart = 54 + y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int pixelStart = rowStart + x * 3;
                bmp[pixelStart] = b;
                bmp[pixelStart + 1] = g;
                bmp[pixelStart + 2] = r;
            }
        }
        return bmp;
    }
}
