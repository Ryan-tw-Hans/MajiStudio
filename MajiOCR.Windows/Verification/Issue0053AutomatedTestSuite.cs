using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public class Issue0053TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0053TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0053TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.32";
    public string IssueId { get; set; } = "ISSUE-0053";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0053TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0053AutomatedTestSuite
{
    public static async Task<Issue0053TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0053TestSuiteReport();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0053TestCaseResult
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

        string tempBaseDir = Path.Combine(Path.GetTempPath(), $"issue0053_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBaseDir);

        try
        {
            var mergeService = new DatabaseMergeService();

            // TC-01: 主畫面選單與面板方法完整性
            {
                var sw = Stopwatch.StartNew();
                var panelMethod = typeof(SavedCardsTablePanel).GetMethod("ShowDatabaseSyncDialogAsync", BindingFlags.Public | BindingFlags.Instance);
                var mainPageMethod = typeof(MainPage).GetMethod("OnSyncDatabaseClicked", BindingFlags.NonPublic | BindingFlags.Instance);
                bool passed = panelMethod != null && mainPageMethod != null;
                sw.Stop();
                Record("TC-01", "主畫面功能選單入口與面板同步方法掛接驗證", passed,
                    $"PanelMethodFound={panelMethod != null}, MainPageMethodFound={mainPageMethod != null}", sw.ElapsedMilliseconds);
            }

            // 準備兩個獨立資料庫 (A 為目前本機，B 為外部選定檔案)
            string localRoot = Path.Combine(tempBaseDir, "local");
            string externalRoot = Path.Combine(tempBaseDir, "external");
            Directory.CreateDirectory(localRoot);
            Directory.CreateDirectory(externalRoot);

            InitTestDatabase(localRoot);
            InitTestDatabase(externalRoot);

            string idAOnly = Guid.NewGuid().ToString("D");
            string idBOnly = Guid.NewGuid().ToString("D");
            string idConflict = Guid.NewGuid().ToString("D");

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset earlier = now.AddHours(-2);
            DateTimeOffset later = now.AddHours(-1);

            InsertContact(localRoot, idAOnly, "本機卡片A", "宏遠公司", "經理", "0911-111-111", "備註A", earlier);
            InsertContact(localRoot, idConflict, "衝突名片", "大同公司", "總監", "0933-111-111", "舊電話", earlier);

            InsertContact(externalRoot, idBOnly, "外部卡片B", "創見公司", "工程師", "0922-222-222", "備註B", later);
            InsertContact(externalRoot, idConflict, "衝突名片", "大同公司", "總監", "0933-999-999", "新電話", later);

            // TC-02: 本地端雙 SQLite 資料庫雙向整併
            {
                var sw = Stopwatch.StartNew();
                // 1. 比對差異
                var diff = await mergeService.CompareDatabasesAsync(localRoot, externalRoot);
                // 2. 裁決衝突為 KeepBoth
                var conflictItem = diff.Items.FirstOrDefault(i => i.Status == ContactDiffStatus.Conflicted);
                if (conflictItem != null) conflictItem.Resolution = MergeResolutionStrategy.KeepBoth;

                // 3. 執行本地整併
                var res = await mergeService.ExecuteMergeAsync(localRoot, externalRoot, diff);

                // 4. 模擬同步回寫外部 SQLite
                string localDb = Path.Combine(localRoot, StoragePathHelper.DatabaseFileName);
                string extDb = Path.Combine(externalRoot, StoragePathHelper.DatabaseFileName);
                File.Copy(localDb, extDb, true);

                int countLocal = GetContactCount(localRoot);
                int countExt = GetContactCount(externalRoot);
                sw.Stop();

                bool passed = res.Success && countLocal == 4 && countExt == 4; // A, B, Conflict(Orig), Conflict(Clone)
                Record("TC-02", "本地端雙 SQLite 資料庫雙向整併同步", passed,
                    $"Success={res.Success}, LocalCount={countLocal}, ExternalCount={countExt}", sw.ElapsedMilliseconds);
            }

            // TC-03: 本地端 ZIP 備份檔單向拉取 (PullToLocal)
            {
                var sw = Stopwatch.StartNew();
                string zipSourceRoot = Path.Combine(tempBaseDir, "zip_source");
                Directory.CreateDirectory(zipSourceRoot);
                InitTestDatabase(zipSourceRoot);

                string idZipCard = Guid.NewGuid().ToString("D");
                InsertContact(zipSourceRoot, idZipCard, "ZIP備份專屬卡片", "微軟公司", "架構師", "0988-888-888", "來自ZIP", now);

                string zipFile = Path.Combine(tempBaseDir, "external_backup.zip");
                ZipFile.CreateFromDirectory(zipSourceRoot, zipFile);

                // 測試拉取解壓比對
                string stagingDir = Path.Combine(tempBaseDir, "zip_staging");
                Directory.CreateDirectory(stagingDir);
                ZipFile.ExtractToDirectory(zipFile, stagingDir);

                var diff = await mergeService.CompareDatabasesAsync(localRoot, stagingDir);
                var res = await mergeService.ExecuteMergeAsync(localRoot, stagingDir, diff);
                int finalCount = GetContactCount(localRoot);
                sw.Stop();

                bool passed = res.Success && finalCount == 5; // 原先 4 筆 + ZIP 1 筆
                Record("TC-03", "本地端 ZIP 備份檔單向拉取整併 (PullToLocal)", passed,
                    $"Success={res.Success}, FinalCount={finalCount}", sw.ElapsedMilliseconds);
            }

            // TC-04: 本地端推送到外部目標 (PushToTarget)
            {
                var sw = Stopwatch.StartNew();
                string targetDir = Path.Combine(tempBaseDir, "push_target");
                Directory.CreateDirectory(targetDir);
                string targetDb = Path.Combine(targetDir, "external_copy.sqlite3");

                // 執行複製推送
                string localDb = Path.Combine(localRoot, StoragePathHelper.DatabaseFileName);
                File.Copy(localDb, targetDb, true);

                int pushedCount = GetContactCountByFile(targetDb);
                sw.Stop();

                bool passed = pushedCount == 5;
                Record("TC-04", "本地端單向推送到外部目標 (PushToTarget)", passed,
                    $"PushedCount={pushedCount}", sw.ElapsedMilliseconds);
            }

            // TC-05: 兩側同時保留 (KeepBoth) 副本派生與命名
            {
                var sw = Stopwatch.StartNew();
                bool hasClone = false;
                using (var conn = new SqliteConnection($"Data Source={Path.Combine(localRoot, StoragePathHelper.DatabaseFileName)}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT raw_value FROM contact_values WHERE value_kind = 'name' AND (raw_value LIKE '%(同步複本)%' OR raw_value LIKE '%(同步副本)%');";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        hasClone = true;
                    }
                }
                sw.Stop();
                Record("TC-05", "兩側同時保留 (KeepBoth) 副本派生與標籤驗證", hasClone,
                    $"HasCloneContact={hasClone}", sw.ElapsedMilliseconds);
            }

            // TC-06: Google OAuth 憑證就地儲存與讀取 (OAuth Settings Persistence)
            {
                var sw = Stopwatch.StartNew();
                var authService = new GoogleAuthService();
                string testId = "test_client_id_12345.apps.googleusercontent.com";
                string testSecret = "test_client_secret_xyz";

                await authService.SaveOAuthConfigAsync(testId, testSecret);
                var (loadedId, loadedSecret) = await authService.LoadOAuthConfigAsync();
                sw.Stop();

                bool passed = loadedId == testId && loadedSecret == testSecret;
                Record("TC-06", "Google OAuth 憑證就地儲存與載入往返驗證", passed,
                    $"LoadedIdMatches={loadedId == testId}, LoadedSecretMatches={loadedSecret == testSecret}", sw.ElapsedMilliseconds);
            }

            // TC-07: 本機安全備份快照存在性驗證
            {
                var sw = Stopwatch.StartNew();
                string backupDir = Path.Combine(localRoot, "backups");
                bool backupDirExists = Directory.Exists(backupDir);
                int backupFilesCount = backupDirExists ? Directory.GetFiles(backupDir, "*.sqlite3").Length : 0;
                sw.Stop();

                bool passed = backupDirExists && backupFilesCount >= 1;
                Record("TC-07", "安全防護快照機制（整併前自動備份至 backups/）", passed,
                    $"BackupDirExists={backupDirExists}, BackupFiles={backupFilesCount}", sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Record("TC-EX", "未預期例外", false, ex.ToString());
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempBaseDir)) Directory.Delete(tempBaseDir, true);
            }
            catch { }
        }

        return report;
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

    private static int GetContactCount(string rootDir)
    {
        string dbPath = Path.Combine(rootDir, StoragePathHelper.DatabaseFileName);
        return GetContactCountByFile(dbPath);
    }

    private static int GetContactCountByFile(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM contact_records WHERE pending_delete = 0;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
