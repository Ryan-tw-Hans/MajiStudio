using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public class Issue0052TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0052TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0052TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.31";
    public string IssueId { get; set; } = "ISSUE-0052";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0052TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0052AutomatedTestSuite
{
    public static async Task<Issue0052TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0052TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0052TestCaseResult
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

        string tempBaseDir = Path.Combine(Path.GetTempPath(), $"issue0052_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBaseDir);

        try
        {
            var mergeService = new DatabaseMergeService();

            // 準備測試用本地與遠端資料庫
            string localRoot = Path.Combine(tempBaseDir, "local");
            string remoteRoot = Path.Combine(tempBaseDir, "remote");
            Directory.CreateDirectory(localRoot);
            Directory.CreateDirectory(remoteRoot);

            InitTestDatabase(localRoot);
            InitTestDatabase(remoteRoot);

            string idLocalOnly = Guid.NewGuid().ToString("D");
            string idRemoteOnly = Guid.NewGuid().ToString("D");
            string idIdentical = Guid.NewGuid().ToString("D");
            string idConflicted = Guid.NewGuid().ToString("D");

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset earlier = now.AddHours(-2);
            DateTimeOffset later = now.AddHours(-1);

            // 1. 本地建立名片
            InsertContact(localRoot, idLocalOnly, "本地獨有名片", "大本公司", "經理", "0911-000-001", "備註1", earlier);
            InsertContact(localRoot, idIdentical, "一致名片", "大中公司", "專員", "0922-000-002", "備註2", earlier);
            InsertContact(localRoot, idConflicted, "衝突名片", "大同公司", "總監", "0933-111-111", "舊備註", earlier);

            // 2. 遠端建立名片
            InsertContact(remoteRoot, idRemoteOnly, "遠端新增名片", "大雲公司", "工程師", "0944-000-003", "備註3", later);
            InsertContact(remoteRoot, idIdentical, "一致名片", "大中公司", "專員", "0922-000-002", "備註2", earlier);
            InsertContact(remoteRoot, idConflicted, "衝突名片", "大同公司", "總監", "0933-222-222", "新備註", later);

            // TC-01: 比對引擎分類精準度
            var tcSw = Stopwatch.StartNew();
            var diffReport = await mergeService.CompareDatabasesAsync(localRoot, remoteRoot);
            tcSw.Stop();

            bool tc1Passed = diffReport.OnlyInLocalCount == 1 &&
                             diffReport.OnlyInRemoteCount == 1 &&
                             diffReport.IdenticalCount == 1 &&
                             diffReport.ConflictedCount == 1;

            string tc1Details = $"LocalOnly={diffReport.OnlyInLocalCount}, RemoteOnly={diffReport.OnlyInRemoteCount}, " +
                                $"Identical={diffReport.IdenticalCount}, Conflicted={diffReport.ConflictedCount}";
            Record("TC-01", "比對引擎四態分類精準度 (OnlyLocal, OnlyRemote, Identical, Conflicted)", tc1Passed, tc1Details, tcSw.ElapsedMilliseconds);

            // TC-02: 時間戳記與較新判定
            var conflictItem = diffReport.ConflictedItems.FirstOrDefault(i => i.ContactId == idConflicted);
            bool tc2Passed = conflictItem != null &&
                             conflictItem.IsRemoteNewer &&
                             conflictItem.Differences.Any(d => d.FieldName == "電話") &&
                             conflictItem.Differences.Any(d => d.FieldName == "備註") &&
                             conflictItem.TimeDifferenceSummary.Contains("較新");

            string tc2Details = conflictItem != null
                ? $"IsRemoteNewer={conflictItem.IsRemoteNewer}, Diffs={conflictItem.Differences.Count}, Summary={conflictItem.TimeDifferenceSummary}"
                : "找不到衝突名片";
            Record("TC-02", "更新時間戳記比對與較新徽章判定", tc2Passed, tc2Details);

            // TC-03: KeepNewer 裁決整併
            // 複製一份測試資料庫來測 KeepNewer
            string test3Local = Path.Combine(tempBaseDir, "test3_local");
            CopyDir(localRoot, test3Local);
            var report3 = await mergeService.CompareDatabasesAsync(test3Local, remoteRoot);
            foreach (var item in report3.ConflictedItems) item.Resolution = MergeResolutionStrategy.KeepNewer;

            var res3 = await mergeService.ExecuteMergeAsync(test3Local, remoteRoot, report3);
            string phoneAfterMerge3 = QueryContactPhone(test3Local, idConflicted);
            bool tc3Passed = res3.Success && phoneAfterMerge3 == "0933-222-222"; // 外部較新，應採納外部
            Record("TC-03", "KeepNewer 策略整併（採納外部較新資料）", tc3Passed, $"ResultSuccess={res3.Success}, Phone={phoneAfterMerge3}");

            // TC-04: KeepLocal 裁決整併
            string test4Local = Path.Combine(tempBaseDir, "test4_local");
            CopyDir(localRoot, test4Local);
            var report4 = await mergeService.CompareDatabasesAsync(test4Local, remoteRoot);
            foreach (var item in report4.ConflictedItems) item.Resolution = MergeResolutionStrategy.KeepLocal;

            var res4 = await mergeService.ExecuteMergeAsync(test4Local, remoteRoot, report4);
            string phoneAfterMerge4 = QueryContactPhone(test4Local, idConflicted);
            bool tc4Passed = res4.Success && phoneAfterMerge4 == "0933-111-111"; // 保留本地舊電話
            Record("TC-04", "KeepLocal 策略整併（保留本機原始資料）", tc4Passed, $"ResultSuccess={res4.Success}, Phone={phoneAfterMerge4}");

            // TC-05: KeepRemote 裁決整併
            string test5Local = Path.Combine(tempBaseDir, "test5_local");
            CopyDir(localRoot, test5Local);
            var report5 = await mergeService.CompareDatabasesAsync(test5Local, remoteRoot);
            foreach (var item in report5.ConflictedItems) item.Resolution = MergeResolutionStrategy.KeepRemote;

            var res5 = await mergeService.ExecuteMergeAsync(test5Local, remoteRoot, report5);
            string phoneAfterMerge5 = QueryContactPhone(test5Local, idConflicted);
            bool tc5Passed = res5.Success && phoneAfterMerge5 == "0933-222-222";
            Record("TC-05", "KeepRemote 策略整併（強制作採納外部版本）", tc5Passed, $"ResultSuccess={res5.Success}, Phone={phoneAfterMerge5}");

            // TC-06: KeepBoth 兩側同時保留
            string test6Local = Path.Combine(tempBaseDir, "test6_local");
            CopyDir(localRoot, test6Local);
            var report6 = await mergeService.CompareDatabasesAsync(test6Local, remoteRoot);
            foreach (var item in report6.ConflictedItems) item.Resolution = MergeResolutionStrategy.KeepBoth;

            int countBefore6 = QueryContactCount(test6Local);
            var res6 = await mergeService.ExecuteMergeAsync(test6Local, remoteRoot, report6);
            int countAfter6 = QueryContactCount(test6Local);
            string origPhone6 = QueryContactPhone(test6Local, idConflicted);
            string cloneName6 = QueryContactNameByPhone(test6Local, "0933-222-222");

            bool tc6Passed = res6.Success &&
                             origPhone6 == "0933-111-111" && // 本機原卡未變
                             cloneName6.Contains("(同步複本)") && // 外部另存為新卡並加註
                             countAfter6 == countBefore6 + 2; // +1 外部新增(idRemoteOnly), +1 KeepBoth 派生新卡

            string tc6Details = $"Success={res6.Success}, OrigPhone={origPhone6}, CloneName={cloneName6}, CountBefore={countBefore6}, CountAfter={countAfter6}";
            Record("TC-06", "KeepBoth 兩側同時保留（派生新 UUID 與同步複本註記）", tc6Passed, tc6Details);

            // TC-07: 安全防護快照驗證
            string backupDir = Path.Combine(test6Local, "backups");
            bool backupExists = Directory.Exists(backupDir) &&
                                Directory.GetFiles(backupDir, "pre_merge_*.sqlite3").Length > 0;
            Record("TC-07", "安全防護快照機制（整併前自動備份至 backups/）", backupExists, $"BackupDirExists={Directory.Exists(backupDir)}, Files={Directory.GetFiles(backupDir, "pre_merge_*.sqlite3").Length}");
        }
        catch (Exception ex)
        {
            Record("TC-ERR", "測試執行過程發生非預期例外", false, ex.ToString());
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempBaseDir))
                {
                    Directory.Delete(tempBaseDir, recursive: true);
                }
            }
            catch { }
        }

        return report;
    }

    private static void CopyDir(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            CopyDir(sub, Path.Combine(destinationDir, Path.GetFileName(sub)));
        }
    }

    private static void InsertContact(
        string storageRoot,
        string contactId,
        string name,
        string company,
        string jobTitle,
        string phone,
        string notes,
        DateTimeOffset time)
    {
        string dbPath = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();

        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO contact_records (contact_id, contract_version, revision, persistence_state, completion_source, created_at, updated_at, save_operation_id, content_fingerprint, pending_delete)
                VALUES (@id, '0.3', 1, 'Ready', 'Manual', @created, @updated, 'op-1', 'fp-1', 0);
            ";
            cmd.Parameters.AddWithValue("@id", contactId);
            cmd.Parameters.AddWithValue("@created", time.ToString("o"));
            cmd.Parameters.AddWithValue("@updated", time.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        void AddVal(string kind, string val, string label, int ord)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO contact_values (value_id, contact_id, value_kind, raw_value, label, ordinal, source_mode, locked_at)
                VALUES (@vid, @cid, @kind, @raw, @label, @ord, 'Manual', @locked);
            ";
            cmd.Parameters.AddWithValue("@vid", Guid.NewGuid().ToString("D"));
            cmd.Parameters.AddWithValue("@cid", contactId);
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@raw", val);
            cmd.Parameters.AddWithValue("@label", label);
            cmd.Parameters.AddWithValue("@ord", ord);
            cmd.Parameters.AddWithValue("@locked", time.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        AddVal("name", name, "姓名", 0);
        AddVal("company", company, "公司", 1);
        AddVal("job_title", jobTitle, "職稱", 2);
        AddVal("phone", phone, "電話", 3);
        AddVal("notes", notes, "備註", 4);

        tx.Commit();
    }

    private static string QueryContactPhone(string storageRoot, string contactId)
    {
        string dbPath = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw_value FROM contact_values WHERE contact_id = @id AND value_kind = 'phone';";
        cmd.Parameters.AddWithValue("@id", contactId);
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static string QueryContactNameByPhone(string storageRoot, string phone)
    {
        string dbPath = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT v1.raw_value
            FROM contact_values v1
            INNER JOIN contact_values v2 ON v1.contact_id = v2.contact_id
            WHERE v1.value_kind = 'name' AND v2.value_kind = 'phone' AND v2.raw_value = @phone;
        ";
        cmd.Parameters.AddWithValue("@phone", phone);
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static int QueryContactCount(string storageRoot)
    {
        string dbPath = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM contact_records;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void InitTestDatabase(string storageRoot)
    {
        string dbPath = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS contact_records (
                contact_id TEXT PRIMARY KEY,
                contract_version TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK (revision >= 1),
                persistence_state TEXT NOT NULL,
                completion_source TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                completed_at TEXT,
                save_operation_id TEXT NOT NULL,
                content_fingerprint TEXT NOT NULL,
                pending_delete INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS contact_values (
                value_id TEXT PRIMARY KEY,
                contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
                value_kind TEXT NOT NULL,
                custom_field_definition_id TEXT,
                custom_field_name_snapshot TEXT,
                raw_value TEXT NOT NULL,
                normalized_value TEXT,
                label TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                source_mode TEXT NOT NULL,
                locked_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS card_image_assets (
                asset_id TEXT PRIMARY KEY,
                contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
                capture_source TEXT NOT NULL,
                storage_state TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                staging_relative_path TEXT,
                sha256 TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                byte_count INTEGER NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS contact_tag_catalog (
                tag_id TEXT PRIMARY KEY,
                tag_name TEXT NOT NULL UNIQUE,
                revision INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS contact_record_tags (
                contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
                tag_id TEXT NOT NULL REFERENCES contact_tag_catalog(tag_id) ON DELETE CASCADE,
                PRIMARY KEY (contact_id, tag_id)
            );
        ";
        cmd.ExecuteNonQuery();
    }
}
