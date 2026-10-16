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
using MajiOCR_Windows.Services.Update;

namespace MajiOCR_Windows.Verification;

public class Issue0043TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0043TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0043TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.23";
    public string IssueId { get; set; } = "ISSUE-0043";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0043TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0043AutomatedTestSuite
{
    public static async Task<Issue0043TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0043TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0043TestCaseResult
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

        string baseTemp = Path.Combine(Path.GetTempPath(), $"issue0043_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseTemp);
        Directory.CreateDirectory(evidenceDir);

        try
        {
            // TC-01: SQLite Schema 驗證
            {
                var ssw = Stopwatch.StartNew();
                string storageDir = Path.Combine(baseTemp, "TC01_Storage");
                using var storage = new StorageService(storageDir, autoInit: true);
                bool tableExists = false;

                using var conn = new SqliteConnection($"Data Source={storage.DatabasePath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='custom_field_definitions';";
                long count = (long)(cmd.ExecuteScalar() ?? 0L);
                tableExists = (count > 0);

                ssw.Stop();
                Record("TC-01-SCHEMA", "驗證 StorageService 初始化自動建立 custom_field_definitions 資料表",
                    tableExists, $"custom_field_definitions table exists: {tableExists}", ssw.ElapsedMilliseconds);
            }

            // TC-02: SQLite CRUD 與重複名稱阻擋驗證
            {
                var ssw = Stopwatch.StartNew();
                string storageDir = Path.Combine(baseTemp, "TC02_Storage");
                using var storage = new StorageService(storageDir, autoInit: true);

                var def1 = storage.AddCustomFieldDefinition("統編");
                var def2 = storage.AddCustomFieldDefinition("部門代號");

                bool duplicatePrevented = false;
                try
                {
                    storage.AddCustomFieldDefinition("統編");
                }
                catch (InvalidOperationException)
                {
                    duplicatePrevented = true;
                }

                storage.RenameCustomFieldDefinition(def1.Id, "統一編號");
                storage.SetCustomFieldDefinitionActive(def2.Id, false);

                var allDefs = storage.GetCustomFieldDefinitions(activeOnly: false);
                var activeDefs = storage.GetCustomFieldDefinitions(activeOnly: true);

                bool pass = allDefs.Count == 2 &&
                            activeDefs.Count == 1 &&
                            activeDefs[0].Name == "統一編號" &&
                            duplicatePrevented;

                ssw.Stop();
                Record("TC-02-CRUD", "驗證 custom_field_definitions 的 CRUD 與唯一名稱約束",
                    pass, $"Total: {allDefs.Count}, Active: {activeDefs.Count}, DuplicatePrevented: {duplicatePrevented}", ssw.ElapsedMilliseconds);
            }

            // TC-03: Legacy JSON 自動遷移驗證
            {
                var ssw = Stopwatch.StartNew();
                string storageDir = Path.Combine(baseTemp, "TC03_Storage");
                string legacyJsonDir = Path.Combine(baseTemp, "TC03_Legacy");
                Directory.CreateDirectory(legacyJsonDir);
                string legacyJsonPath = Path.Combine(legacyJsonDir, "custom-fields.json");

                var legacyData = new
                {
                    catalog_version = 1,
                    definitions = new[]
                    {
                        new { id = Guid.NewGuid().ToString("D").ToLowerInvariant(), name = "分機號碼", active = true, created_at = "2026-09-30T12:00:00+08:00" },
                        new { id = Guid.NewGuid().ToString("D").ToLowerInvariant(), name = "緊急聯絡人", active = false, created_at = "2026-09-30T12:01:00+08:00" }
                    }
                };
                File.WriteAllText(legacyJsonPath, JsonSerializer.Serialize(legacyData));

                using var storage = new StorageService(storageDir, autoInit: true, legacyJsonPathOverride: legacyJsonPath);
                var loaded = storage.GetCustomFieldDefinitions(activeOnly: false);

                bool pass = loaded.Count == 2 &&
                            loaded.Any(d => d.Name == "分機號碼" && d.Active) &&
                            loaded.Any(d => d.Name == "緊急聯絡人" && !d.Active);

                ssw.Stop();
                Record("TC-03-LEGACY-MIGRATION", "驗證歷史 custom-fields.json 無縫遷移至 SQLite 資料庫",
                    pass, $"Migrated count: {loaded.Count}", ssw.ElapsedMilliseconds);
            }

            // TC-04: CustomFieldCatalogService AttachStorage 驗證
            {
                var ssw = Stopwatch.StartNew();
                string storageDir = Path.Combine(baseTemp, "TC04_Storage");
                string dummyJsonPath = Path.Combine(baseTemp, "TC04_dummy.json");
                using var storage = new StorageService(storageDir, autoInit: true);

                var catalogService = new CustomFieldCatalogService(dummyJsonPath);
                catalogService.AttachStorage(storage);

                var added = catalogService.AddDefinition("職級代碼");
                var activeList = catalogService.GetActiveDefinitions();
                catalogService.SetDefinitionActive(added.Id, false);
                var allList = catalogService.GetAllDefinitions();

                bool jsonCreated = File.Exists(dummyJsonPath);
                bool pass = activeList.Count == 1 &&
                            allList.Count == 1 &&
                            !allList[0].Active &&
                            !jsonCreated; // 必須不寫入 JSON

                ssw.Stop();
                Record("TC-04-CATALOG-ATTACH", "驗證 CustomFieldCatalogService 綁定 StorageService 後完全直接讀寫 SQLite",
                    pass, $"Active: {activeList.Count}, All: {allList.Count}, JsonFileCreated: {jsonCreated}", ssw.ElapsedMilliseconds);
            }

            // TC-05: UpdateService 與自動檢查架構相容性驗證
            {
                var ssw = Stopwatch.StartNew();
                var updateService = new UpdateService(baseTemp);
                string curVer = updateService.CurrentVersion;

                // 測試本地設定讀取與版本字串一致性
                var settings = updateService.LoadSettings();
                bool pass = !string.IsNullOrWhiteSpace(curVer) &&
                            Version.TryParse(curVer, out var parsedVer) &&
                            parsedVer >= new Version(0, 1, 0, 23) &&
                            !string.IsNullOrWhiteSpace(updateService.DefaultReleaseManifestUrl);

                ssw.Stop();
                Record("TC-05-AUTO-UPDATE-NON-BLOCKING", "驗證 UpdateService 版本 (>= 0.1.0.23) 與自動檢查設定讀取",
                    pass, $"CurrentVersion: {curVer}, HasDefaultManifest: {!string.IsNullOrWhiteSpace(updateService.DefaultReleaseManifestUrl)}", ssw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Record("TC-ERR", "自動測試執行發生未預期異常", false, ex.ToString());
        }
        finally
        {
            try
            {
                if (Directory.Exists(baseTemp))
                {
                    Directory.Delete(baseTemp, recursive: true);
                }
            }
            catch { }
        }

        return report;
    }
}
