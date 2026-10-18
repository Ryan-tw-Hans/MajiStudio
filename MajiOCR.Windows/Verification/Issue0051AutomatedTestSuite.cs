using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public class Issue0051TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0051TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0051TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.31";
    public string IssueId { get; set; } = "ISSUE-0051";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0 && TotalTests > 0;
    public List<Issue0051TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0051AutomatedTestSuite
{
    public static async Task<Issue0051TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0051TestSuiteReport();

        async Task AddTestAsync(string id, string desc, Func<Task<string>> action)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                string details = await action();
                sw.Stop();
                report.TestCases.Add(new Issue0051TestCaseResult
                {
                    TestId = id,
                    Description = desc,
                    Passed = true,
                    Details = details,
                    LatencyMs = sw.ElapsedMilliseconds
                });
                report.PassCount++;
            }
            catch (Exception ex)
            {
                sw.Stop();
                report.TestCases.Add(new Issue0051TestCaseResult
                {
                    TestId = id,
                    Description = desc,
                    Passed = false,
                    Details = $"{ex.GetType().Name}: {ex.Message}",
                    LatencyMs = sw.ElapsedMilliseconds
                });
                report.FailCount++;
            }
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), $"MajiOCR_Issue0051_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            // TC-0051-01: 設定檔與自訂欄位目錄預設路徑可攜化驗證 (AC-01)
            await AddTestAsync("TC-0051-01", "驗證 AppSettingsService 與 CustomFieldCatalogService 預設路徑跟隨主程式所在目錄而非 AppData", async () =>
            {
                await Task.CompletedTask;
                string expectedRoot = StoragePathHelper.GetDefaultStorageRoot();
                var defaultSettingsSvc = new AppSettingsService();
                var defaultCatalogSvc = new CustomFieldCatalogService();

                string expectedSettingsPath = Path.Combine(expectedRoot, "settings.json");
                string expectedCatalogPath = Path.Combine(expectedRoot, "custom-fields.json");

                if (!string.Equals(Path.GetFullPath(defaultSettingsSvc.SettingsFilePath), Path.GetFullPath(expectedSettingsPath), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"SettingsFilePath 預期為 {expectedSettingsPath}，實際為 {defaultSettingsSvc.SettingsFilePath}");
                }
                if (!string.Equals(Path.GetFullPath(defaultCatalogSvc.CatalogFilePath), Path.GetFullPath(expectedCatalogPath), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"CatalogFilePath 預期為 {expectedCatalogPath}，實際為 {defaultCatalogSvc.CatalogFilePath}");
                }
                if (defaultSettingsSvc.SettingsFilePath.Contains("AppData", StringComparison.OrdinalIgnoreCase) ||
                    defaultCatalogSvc.CatalogFilePath.Contains("AppData", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("預設設定路徑不應指向 AppData 全域目錄");
                }

                return $"DefaultRoot={ Path.GetFileName(expectedRoot) }, SettingsCoLocated=true, CatalogCoLocated=true";
            });

            // TC-0051-02: 新資料夾（無 contacts.sqlite3）完全隔離與不沿用舊設定／舊自訂欄位驗證 (AC-02)
            await AddTestAsync("TC-0051-02", "驗證主程式移至無 contacts.sqlite3 的新資料夾時，不遷移舊設定、不詢問舊密碼且不匯入跨目錄自訂欄位", async () =>
            {
                await Task.CompletedTask;
                string newPortableDir = Path.Combine(tempRoot, "NewMovedFolder_NoDb");
                string mockLegacyAppDataDir = Path.Combine(tempRoot, "MockLegacyAppData");
                StoragePathHelper.EnsurePrivateDataDirectory(tempRoot, newPortableDir);
                Directory.CreateDirectory(mockLegacyAppDataDir);

                // 在模擬的舊 %LocalAppData% 準備已啟用密碼保護的 settings.json 與 custom-fields.json
                string legacySettingsPath = Path.Combine(mockLegacyAppDataDir, "settings.json");
                var (hash, salt) = PasswordSecurityService.HashPassword("OldGlobalPassword123");
                var legacySvc = new AppSettingsService(legacySettingsPath);
                legacySvc.UpdateFullOcrLanguage(OcrLanguageCatalog.Jpn);
                legacySvc.UpdateSecuritySettings(true, hash, salt);

                string legacyCustomFieldsPath = Path.Combine(mockLegacyAppDataDir, "custom-fields.json");
                File.WriteAllText(legacyCustomFieldsPath, JsonSerializer.Serialize(new
                {
                    catalog_version = 1,
                    definitions = new[]
                    {
                        new { id = Guid.NewGuid().ToString("D").ToLowerInvariant(), name = "舊資料夾自訂欄位", active = true, created_at = "2026-10-01T00:00:00+08:00" }
                    }
                }), Encoding.UTF8);

                // 新資料夾下沒有 contacts.sqlite3
                string newSettingsPath = Path.Combine(newPortableDir, "settings.json");
                string newDbPath = Path.Combine(newPortableDir, StoragePathHelper.DatabaseFileName);

                var newSettingsSvc = new AppSettingsService(newSettingsPath, legacySettingsFilePathOverride: legacySettingsPath);
                var loadedSettings = newSettingsSvc.LoadSettings();

                if (File.Exists(newSettingsPath))
                {
                    throw new InvalidOperationException("新資料夾無 contacts.sqlite3 時，不應從舊設定目錄複製 settings.json");
                }
                if (loadedSettings.IsPasswordProtected || loadedSettings.FullOcrLanguageCode != OcrLanguageCatalog.Mixed5)
                {
                    throw new InvalidOperationException("新資料夾應使用乾淨預設設定，不應繼承舊密碼或語系");
                }

                var securityRes = StorageService.ResolveStartupSecurityState(newDbPath, newSettingsSvc);
                if (securityRes.RequiresUnlock || securityRes.IsDatabaseFilePresent || securityRes.IsDatabaseEncrypted)
                {
                    throw new InvalidOperationException($"新資料夾無資料庫時不應要求解鎖: RequiresUnlock={securityRes.RequiresUnlock}");
                }

                // 初始化新資料夾的 StorageService（passphrase 為 null）
                using var storage = new StorageService(newPortableDir, autoInit: true, passphrase: null);
                if (!storage.IsAvailable || storage.IsEncrypted)
                {
                    throw new InvalidOperationException($"新資料夾自動建立之 contacts.sqlite3 必須可用且未加密 (IsAvailable={storage.IsAvailable}, IsEncrypted={storage.IsEncrypted}, Error={storage.ErrorMessage})");
                }

                var customDefs = storage.GetCustomFieldDefinitions(activeOnly: false);
                if (customDefs.Count != 0)
                {
                    throw new InvalidOperationException($"新資料夾建立之空白資料庫不應匯入跨目錄自訂欄位，實際筆數={customDefs.Count}");
                }

                return "LegacySettingsIgnored=true, RequiresUnlock=false, NewDbUnencrypted=true, CustomFieldsCount=0";
            });

            // TC-0051-03: 孤立密碼設定（IsPasswordProtected=true 但無資料庫或資料庫未加密）自動校正與免密碼啟動驗證 (AC-03)
            await AddTestAsync("TC-0051-03", "驗證 settings.json 殘留 IsPasswordProtected=true 但無資料庫或資料庫未加密時，免密碼啟動並自動校正設定", async () =>
            {
                await Task.CompletedTask;
                // 情境 A：有 settings.json (IsPasswordProtected=true)，但無 contacts.sqlite3
                string dirA = Path.Combine(tempRoot, "StaleSettings_NoDb");
                StoragePathHelper.EnsurePrivateDataDirectory(tempRoot, dirA);
                string settingsPathA = Path.Combine(dirA, "settings.json");
                string dbPathA = Path.Combine(dirA, StoragePathHelper.DatabaseFileName);

                var svcA = new AppSettingsService(settingsPathA);
                var (hA, sA) = PasswordSecurityService.HashPassword("StalePass123");
                svcA.UpdateSecuritySettings(true, hA, sA);

                var resA = StorageService.ResolveStartupSecurityState(dbPathA, svcA);
                var reloadedA = svcA.LoadSettings();
                if (resA.RequiresUnlock || !resA.WasStaleSettingsReset || reloadedA.IsPasswordProtected || reloadedA.PasswordHash != null)
                {
                    throw new InvalidOperationException("情境 A（無資料庫）未正確免除密碼詢問或未自動重設孤立設定");
                }

                // 情境 B：有未加密的 contacts.sqlite3，但 settings.json 標記 IsPasswordProtected=true
                string dirB = Path.Combine(tempRoot, "StaleSettings_PlainDb");
                StoragePathHelper.EnsurePrivateDataDirectory(tempRoot, dirB);
                using (var initPlain = new StorageService(dirB, autoInit: true, passphrase: null))
                {
                    if (!initPlain.IsAvailable || initPlain.IsEncrypted)
                        throw new InvalidOperationException($"無法建立未加密測試資料庫: {initPlain.ErrorMessage}");
                }

                string settingsPathB = Path.Combine(dirB, "settings.json");
                string dbPathB = Path.Combine(dirB, StoragePathHelper.DatabaseFileName);
                var svcB = new AppSettingsService(settingsPathB);
                svcB.UpdateSecuritySettings(true, hA, sA);

                var resB = StorageService.ResolveStartupSecurityState(dbPathB, svcB);
                var reloadedB = svcB.LoadSettings();
                if (resB.RequiresUnlock || !resB.IsDatabaseFilePresent || resB.IsDatabaseEncrypted || !resB.WasStaleSettingsReset || reloadedB.IsPasswordProtected)
                {
                    throw new InvalidOperationException("情境 B（未加密資料庫）未正確免除密碼詢問或未自動重設孤立設定");
                }

                return "ScenarioA_NoDbReset=true, ScenarioB_PlainDbReset=true, RequiresUnlock=false";
            });

            // TC-0051-04: 已加密 contacts.sqlite3 強制解鎖保護與設定檔自動同步驗證 (AC-04)
            await AddTestAsync("TC-0051-04", "驗證同目錄存在已加密 contacts.sqlite3 時，即使無 settings.json 仍強制要求主密碼解鎖", async () =>
            {
                string encDir = Path.Combine(tempRoot, "EncryptedDbFolder");
                StoragePathHelper.EnsurePrivateDataDirectory(tempRoot, encDir);
                string masterPwd = "PortableEncrypt#2026";

                using (var storage = new StorageService(encDir, autoInit: true, passphrase: null))
                {
                    await storage.EnableEncryptionAsync(masterPwd);
                }

                string dbPath = Path.Combine(encDir, StoragePathHelper.DatabaseFileName);
                string settingsPath = Path.Combine(encDir, "settings.json");
                if (File.Exists(settingsPath)) File.Delete(settingsPath);

                var settingsSvc = new AppSettingsService(settingsPath);
                var res = StorageService.ResolveStartupSecurityState(dbPath, settingsSvc);

                if (!res.RequiresUnlock || !res.IsDatabaseFilePresent || !res.IsDatabaseEncrypted)
                {
                    throw new InvalidOperationException($"已加密資料庫必須要求解鎖: RequiresUnlock={res.RequiresUnlock}, IsEncrypted={res.IsDatabaseEncrypted}");
                }

                bool wrongRejected = !StorageService.TryVerifyPassphrase(dbPath, "WrongPass999", out _);
                bool rightAccepted = StorageService.TryVerifyPassphrase(dbPath, masterPwd, out _);
                if (!wrongRejected || !rightAccepted)
                {
                    throw new InvalidOperationException($"加密資料庫密碼驗證異常: wrongRejected={wrongRejected}, rightAccepted={rightAccepted}");
                }

                return "EncryptedDbRequiresUnlock=true, WrongPassRejected=true, RightPassAccepted=true";
            });

            // TC-0051-05: 原工作目錄（已存在 contacts.sqlite3）舊設定平滑遷移與同目錄 custom-fields.json 匯入驗證 (AC-04)
            await AddTestAsync("TC-0051-05", "驗證原工作目錄已有 contacts.sqlite3 時平滑遷移舊 settings.json，且支援同目錄 custom-fields.json 匯入", async () =>
            {
                await Task.CompletedTask;
                string existingWorkDir = Path.Combine(tempRoot, "ExistingWorkDir_WithDb");
                string legacySourceDir = Path.Combine(tempRoot, "LegacySourceDir");
                StoragePathHelper.EnsurePrivateDataDirectory(tempRoot, existingWorkDir);
                Directory.CreateDirectory(legacySourceDir);

                // 在原工作目錄放一個同目錄 custom-fields.json，並建立 contacts.sqlite3
                string coLocatedCustomFields = Path.Combine(existingWorkDir, "custom-fields.json");
                File.WriteAllText(coLocatedCustomFields, JsonSerializer.Serialize(new
                {
                    catalog_version = 1,
                    definitions = new[]
                    {
                        new { id = Guid.NewGuid().ToString("D").ToLowerInvariant(), name = "同目錄專屬欄位", active = true, created_at = "2026-10-18T08:00:00+08:00" }
                    }
                }), Encoding.UTF8);

                using (var storage = new StorageService(existingWorkDir, autoInit: true, passphrase: null))
                {
                    var defs = storage.GetCustomFieldDefinitions(activeOnly: false);
                    if (defs.Count != 1 || defs[0].Name != "同目錄專屬欄位")
                    {
                        throw new InvalidOperationException($"同目錄 custom-fields.json 未正確匯入: Count={defs.Count}");
                    }
                }

                // 準備舊版 settings.json
                string legacySettingsPath = Path.Combine(legacySourceDir, "settings.json");
                var legacySvc = new AppSettingsService(legacySettingsPath);
                legacySvc.UpdateFullOcrLanguage(OcrLanguageCatalog.ChiTra);
                legacySvc.UpdateVercelSettings("vercel_tok_portable_0051", "portable-proj", "client.apps.googleusercontent.com");

                // 因為 existingWorkDir 下已經存在 contacts.sqlite3，載入設定時應自動遷移至 existingWorkDir\settings.json
                string portableSettingsPath = Path.Combine(existingWorkDir, "settings.json");
                var portableSvc = new AppSettingsService(portableSettingsPath, legacySettingsFilePathOverride: legacySettingsPath);
                var migrated = portableSvc.LoadSettings();

                if (!File.Exists(portableSettingsPath))
                {
                    throw new InvalidOperationException("原工作目錄已有 contacts.sqlite3 時，未自動將舊 settings.json 遷移至同目錄");
                }
                if (migrated.FullOcrLanguageCode != OcrLanguageCatalog.ChiTra ||
                    migrated.VercelAccessToken != "vercel_tok_portable_0051")
                {
                    throw new InvalidOperationException("遷移後之 settings.json 內容不完整");
                }

                return "CoLocatedMigrationSuccess=true, CoLocatedCustomFieldsImported=true";
            });
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            }
            catch { }
        }

        report.TotalTests = report.TestCases.Count;
        return report;
    }
}
