using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public class Issue0044TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0044TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0044TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.24";
    public string IssueId { get; set; } = "ISSUE-0044";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0 && TotalTests > 0;
    public List<Issue0044TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0044AutomatedTestSuite
{
    private sealed class MockVercelHttpHandler : HttpMessageHandler
    {
        private readonly bool _simulateUnauthorized;
        public int GetUserCalls { get; private set; }
        public int PostDeployCalls { get; private set; }
        public int PatchProjectCalls { get; private set; }

        public MockVercelHttpHandler(bool simulateUnauthorized)
        {
            _simulateUnauthorized = simulateUnauthorized;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_simulateUnauthorized)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"Invalid token\"}}", Encoding.UTF8, "application/json")
                });
            }

            string url = request.RequestUri?.ToString() ?? string.Empty;
            if (request.Method == HttpMethod.Get && url.Contains("/v2/user"))
            {
                GetUserCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"user\":{\"username\":\"majiocr-tester\",\"email\":\"tester@example.com\"}}", Encoding.UTF8, "application/json")
                });
            }

            if (request.Method == HttpMethod.Post && url.Contains("/v13/deployments"))
            {
                PostDeployCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"id\":\"dpl_issue0044_test\",\"projectId\":\"prj_majiocr_ios\",\"url\":\"majiocr-ios-pwa-git-main.vercel.app\",\"alias\":[\"majiocr-ios-pwa.vercel.app\"]}",
                        Encoding.UTF8,
                        "application/json")
                });
            }

            if (request.Method == HttpMethod.Patch && url.Contains("/v9/projects/"))
            {
                PatchProjectCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"prj_majiocr_ios\",\"ssoProtection\":null}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    public static async Task<Issue0044TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0044TestSuiteReport();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0044TestCaseResult
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

        string baseTemp = Path.Combine(Path.GetTempPath(), $"issue0044_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseTemp);
        Directory.CreateDirectory(evidenceDir);

        string encryptedDbPathForT2 = string.Empty;
        const string TestMasterPassphrase = "Maji@iOS_2026#Secret";

        try
        {
            // T1: 真實 SQLCipher-AES256 資料庫解密與標準 SQLite 無密碼讀取驗證 (對應 AC-07, NFR-01)
            {
                var sw = Stopwatch.StartNew();
                string storageDir = Path.Combine(baseTemp, "T1_EncryptedStorage");
                using var storage = new StorageService(storageDir, autoInit: true);

                // 建立分類標籤、自訂欄位與名片資料
                var tagVip = await storage.CreateTagAsync("VIP客戶");
                var customDef = storage.AddCustomFieldDefinition("LINE ID");

                byte[] sampleBmp = QrCodeBmpGenerator.GenerateBmpBytes("https://example.com", scale: 2, quietZoneModules: 2);
                StorageService.TryParseImageDimensions(sampleBmp, "image/bmp", out int bmpW, out int bmpH);
                string contactId = Guid.NewGuid().ToString("N");

                var draft = new ContactDraftSnapshot
                {
                    ContactId = contactId,
                    SaveOperationId = Guid.NewGuid().ToString("N"),
                    Revision = 1,
                    ImageBytes = sampleBmp,
                    ImageSha256 = Convert.ToHexString(SHA256.HashData(sampleBmp)),
                    CaptureSource = "ImagePickerCopy",
                    MimeType = "image/bmp",
                    Width = bmpW,
                    Height = bmpH,
                    Values = new List<DraftValueSnapshot>
                    {
                        new() { ValueKind = CanonicalValueKind.Name, RawValue = "王大明", NormalizedValue = "王大明", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow },
                        new() { ValueKind = CanonicalValueKind.Company, RawValue = "麻吉數位股份有限公司", NormalizedValue = "麻吉數位股份有限公司", Label = "公司", Ordinal = 1, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow },
                        new() { ValueKind = CanonicalValueKind.Title, RawValue = "技術總監", NormalizedValue = "技術總監", Label = "職稱", Ordinal = 2, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow },
                        new() { ValueKind = CanonicalValueKind.Phone, RawValue = "0912-345-678", NormalizedValue = "0912345678", Label = "手機", Ordinal = 3, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow },
                        new() { ValueKind = CanonicalValueKind.Email, RawValue = "daming@example.com", NormalizedValue = "daming@example.com", Label = "Email", Ordinal = 4, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow },
                        new() { ValueKind = CanonicalValueKind.Custom, RawValue = "@daming_line", NormalizedValue = "@daming_line", Label = "LINE ID", CustomFieldDefinitionId = customDef.Id, CustomFieldNameSnapshot = customDef.Name, Ordinal = 5, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                    }
                };

                var saveRes = await storage.SaveDraftAsync(draft);
                if (saveRes.Succeeded && tagVip.Tag != null)
                {
                    await storage.SetTagsAsync(contactId, 0, new[] { new TagChoice(tagVip.Tag.TagId, tagVip.Tag.DisplayName) });
                }

                // 啟用 SQLCipher-AES256 加密並執行 WAL Checkpoint
                bool encEnabled = await storage.EnableEncryptionAsync(TestMasterPassphrase);
                SqliteConnection.ClearAllPools();

                encryptedDbPathForT2 = Path.Combine(baseTemp, "encrypted_copy.sqlite3");
                File.Copy(storage.DatabasePath, encryptedDbPathForT2, overwrite: true);

                byte[] encryptedBytes = await File.ReadAllBytesAsync(encryptedDbPathForT2);
                bool rawIsEncrypted = !SqlCipher4Decryptor.IsPlainSqliteBuffer(encryptedBytes);

                // 使用 SQLCipher 4 標準頁面解密器解密為標準 SQLite
                byte[] decryptedBytes = SqlCipher4Decryptor.DecryptDatabase(encryptedBytes, TestMasterPassphrase);
                bool decIsPlain = SqlCipher4Decryptor.IsPlainSqliteBuffer(decryptedBytes);
                byte reservedByte = decryptedBytes[20];

                string plainVerifyPath = Path.Combine(baseTemp, "decrypted_plain.sqlite3");
                await File.WriteAllBytesAsync(plainVerifyPath, decryptedBytes);

                // 以無密碼的標準 SQLite 連線開啟並驗證完整性與資料內容
                string quickCheck = string.Empty;
                int contactCount = 0;
                string loadedName = string.Empty;
                string loadedTag = string.Empty;

                using (var plainConn = new SqliteConnection($"Data Source={plainVerifyPath};Mode=ReadOnly;Pooling=False"))
                {
                    await plainConn.OpenAsync();
                    using (var qcCmd = plainConn.CreateCommand())
                    {
                        qcCmd.CommandText = "PRAGMA quick_check;";
                        quickCheck = Convert.ToString(await qcCmd.ExecuteScalarAsync()) ?? string.Empty;
                    }
                    using (var cntCmd = plainConn.CreateCommand())
                    {
                        cntCmd.CommandText = "SELECT COUNT(*) FROM contact_records WHERE pending_delete = 0;";
                        contactCount = Convert.ToInt32(await cntCmd.ExecuteScalarAsync());
                    }
                    using (var valCmd = plainConn.CreateCommand())
                    {
                        valCmd.CommandText = "SELECT raw_value FROM contact_values WHERE value_kind = 'Name' LIMIT 1;";
                        loadedName = Convert.ToString(await valCmd.ExecuteScalarAsync()) ?? string.Empty;
                    }
                    using (var tagCmd = plainConn.CreateCommand())
                    {
                        tagCmd.CommandText = "SELECT display_name FROM contact_tag_catalog LIMIT 1;";
                        loadedTag = Convert.ToString(await tagCmd.ExecuteScalarAsync()) ?? string.Empty;
                    }
                }

                sw.Stop();
                bool passed = encEnabled &&
                              rawIsEncrypted &&
                              decIsPlain &&
                              (reservedByte == 80 || reservedByte == 48) &&
                              quickCheck.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
                              contactCount == 1 &&
                              loadedName == "王大明" &&
                              loadedTag == "VIP客戶";

                Record(
                    "T1-SQLCIPHER-DECRYPT-PARITY",
                    "驗證 SQLCipher-AES256 加密資料庫可由 SQLCipher 4 分頁解密器 100% 還原為標準 SQLite 並無密碼讀出名片與標籤",
                    passed,
                    $"rawEncrypted={rawIsEncrypted}, decPlain={decIsPlain}, reserve={reservedByte}, quickCheck={quickCheck}, contacts={contactCount}, name={loadedName}, tag={loadedTag}",
                    sw.ElapsedMilliseconds);
            }

            // T2: SQLCipher 錯誤主密碼與空密碼 HMAC 攔截驗證 (對應 AC-07)
            {
                var sw = Stopwatch.StartNew();
                byte[] encryptedBytes = await File.ReadAllBytesAsync(encryptedDbPathForT2);

                bool wrongRejected = false;
                string wrongErrorMsg = string.Empty;
                try
                {
                    SqlCipher4Decryptor.DecryptDatabase(encryptedBytes, "WrongPassword_999");
                }
                catch (CryptographicException ex)
                {
                    wrongRejected = ex.Message.Contains("INVALID_PASSWORD", StringComparison.Ordinal);
                    wrongErrorMsg = ex.Message;
                }

                bool emptyRejected = false;
                try
                {
                    SqlCipher4Decryptor.DecryptDatabase(encryptedBytes, string.Empty);
                }
                catch (CryptographicException ex)
                {
                    emptyRejected = ex.Message.Contains("PASSWORD_REQUIRED", StringComparison.Ordinal);
                }

                sw.Stop();
                bool passed = wrongRejected && emptyRejected;
                Record(
                    "T2-SQLCIPHER-WRONG-PASSWORD-REJECT",
                    "驗證輸入錯誤主密碼或空密碼時，Page 1 HMAC-SHA512 立即攔截並回傳明確錯誤",
                    passed,
                    $"wrongRejected={wrongRejected} ({wrongErrorMsg}), emptyRejected={emptyRejected}",
                    sw.ElapsedMilliseconds);
            }

            // T3: 內嵌 MajiOCR.Web PWA 資產完整性與 Vercel 部署 Payload 隱私邊界驗證 (對應 AC-02, AC-03, NFR-02)
            {
                var sw = Stopwatch.StartNew();
                var vercelSvc = new VercelDeploymentService();
                string normalized = vercelSvc.NormalizeProjectName("  My MajiOCR_iOS App! 2026 ");
                var assets = vercelSvc.LoadEmbeddedWebAssets("998877-ios.apps.googleusercontent.com");
                string payloadJson = vercelSvc.BuildDeploymentRequestJson("My MajiOCR_iOS App! 2026", "998877-ios.apps.googleusercontent.com");

                var node = JsonNode.Parse(payloadJson);
                string? payloadName = node?["name"]?.GetValue<string>();
                string? target = node?["target"]?.GetValue<string>();
                var filesArr = node?["files"]?.AsArray();

                bool hasAllCoreFiles =
                    assets.Any(a => a.RelativePath == "index.html") &&
                    assets.Any(a => a.RelativePath == "manifest.webmanifest") &&
                    assets.Any(a => a.RelativePath == "sw.js") &&
                    assets.Any(a => a.RelativePath == "js/sqlcipher-decryptor.js") &&
                    assets.Any(a => a.RelativePath == "js/sqlite-parser.js") &&
                    assets.Any(a => a.RelativePath == "js/zip-reader.js") &&
                    assets.Any(a => a.RelativePath == "js/app.js");

                bool noSqliteIncluded = !assets.Any(a =>
                    a.RelativePath.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
                    a.RelativePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase));

                var configAsset = assets.FirstOrDefault(a => a.RelativePath == "js/config.js");
                bool clientIdInjected = configAsset != null &&
                                        configAsset.ContentUtf8.Contains("998877-ios.apps.googleusercontent.com", StringComparison.Ordinal);

                sw.Stop();
                bool passed = normalized == "my-majiocr-ios-app-2026" &&
                              payloadName == "my-majiocr-ios-app-2026" &&
                              target == "production" &&
                              filesArr != null &&
                              filesArr.Count == 10 &&
                              hasAllCoreFiles &&
                              noSqliteIncluded &&
                              clientIdInjected;

                Record(
                    "T3-EMBEDDED-PWA-ASSETS-AND-PAYLOAD",
                    "驗證內嵌 MajiOCR.Web 10 項靜態資產齊全、Google Client ID 正確注入且絕不夾帶本機 SQLite 資料庫",
                    passed,
                    $"normalized={normalized}, filesCount={filesArr?.Count}, hasAllCoreFiles={hasAllCoreFiles}, noSqliteIncluded={noSqliteIncluded}, clientIdInjected={clientIdInjected}",
                    sw.ElapsedMilliseconds);
            }

            // T4: Vercel REST API (GET /v2/user, POST /v13/deployments, PATCH /v9/projects) 互動與 401 錯誤處理驗證 (對應 AC-03)
            {
                var sw = Stopwatch.StartNew();

                // 4a. 模擬 401 Token 無效
                var unauthHandler = new MockVercelHttpHandler(simulateUnauthorized: true);
                var unauthSvc = new VercelDeploymentService(new HttpClient(unauthHandler));
                var unauthRes = await unauthSvc.DeployPwaToVercelAsync("invalid_token", "majiocr-ios-pwa");

                // 4b. 模擬 200 部署成功
                var okHandler = new MockVercelHttpHandler(simulateUnauthorized: false);
                var okSvc = new VercelDeploymentService(new HttpClient(okHandler));
                var okRes = await okSvc.DeployPwaToVercelAsync("valid_vercel_token_123", "majiocr-ios-pwa", "client-id.apps.googleusercontent.com");

                sw.Stop();
                bool passed = !unauthRes.Success &&
                              unauthRes.Message.Contains("401/403", StringComparison.Ordinal) &&
                              okRes.Success &&
                              okRes.ProductionUrl == "https://majiocr-ios-pwa.vercel.app" &&
                              okRes.UploadedFileCount == 10 &&
                              okHandler.GetUserCalls == 1 &&
                              okHandler.PostDeployCalls == 1 &&
                              okHandler.PatchProjectCalls == 1;

                Record(
                    "T4-VERCEL-REST-API-DEPLOY-FLOW",
                    "驗證 VercelDeploymentService 正確執行帳號驗證、靜態包部署、SSO 公開權限設定及 401 錯誤提示",
                    passed,
                    $"unauthBlocked={!unauthRes.Success}, okSuccess={okRes.Success}, url={okRes.ProductionUrl}, calls=({okHandler.GetUserCalls},{okHandler.PostDeployCalls},{okHandler.PatchProjectCalls})",
                    sw.ElapsedMilliseconds);
            }

            // T5: 本機 QR Code BMP 產生器、ISO/IEC 18004 解碼校驗與 AppSettings Vercel 設定持久化驗證 (對應 AC-02, AC-03)
            {
                var sw = Stopwatch.StartNew();
                string url = "https://majiocr-ios-pwa.vercel.app";
                string longVercelUrl = "https://majiocr-ios-pwa-demo-projects-1234abcd.vercel.app";
                bool[,] matrix = QrCodeBmpGenerator.GenerateMatrix(url);
                bool decodedOk1 = QrCodeBmpGenerator.TryVerifyAndDecodeMatrix(matrix, out string decodedUrl1, out string qrErr1);
                bool[,] matrix2 = QrCodeBmpGenerator.GenerateMatrix(longVercelUrl);
                bool decodedOk2 = QrCodeBmpGenerator.TryVerifyAndDecodeMatrix(matrix2, out string decodedUrl2, out string qrErr2);
                byte[] bmpBytes = QrCodeBmpGenerator.GenerateBmpBytes(url, scale: 6, quietZoneModules: 4);

                bool validBmp = bmpBytes.Length > 1000 && bmpBytes[0] == (byte)'B' && bmpBytes[1] == (byte)'M';

                string settingsPath = Path.Combine(baseTemp, "vercel_settings_test.json");
                var settingsSvc = new AppSettingsService(settingsPath);
                settingsSvc.UpdateVercelSettings("tok_abc123", "majiocr-iphone", "gid_xyz.apps.googleusercontent.com", url);
                var reloaded = settingsSvc.LoadSettings();

                sw.Stop();
                bool passed = matrix.GetLength(0) >= 21 &&
                              decodedOk1 && decodedUrl1 == url &&
                              decodedOk2 && decodedUrl2 == longVercelUrl &&
                              validBmp &&
                              reloaded.VercelAccessToken == "tok_abc123" &&
                              reloaded.VercelProjectName == "majiocr-iphone" &&
                              reloaded.VercelWebGoogleClientId == "gid_xyz.apps.googleusercontent.com" &&
                              reloaded.LastDeployedVercelUrl == url;

                Record(
                    "T5-QRCODE-AND-SETTINGS-PERSISTENCE",
                    "驗證本機 QR Code 產生器通過 ISO/IEC 18004 Format BCH 與 Reed-Solomon 解碼還原網址且 AppSettings 正確持久化",
                    passed,
                    $"qrDimension={matrix.GetLength(0)}x{matrix.GetLength(1)}, decoded1={decodedUrl1}, decoded2={decodedUrl2}, bmpBytes={bmpBytes.Length}, err={qrErr1}{qrErr2}",
                    sw.ElapsedMilliseconds);
            }

            // T6: Google Drive 加密備份包 (.zip) 打包、解壓與 iPhone PWA 雙管道規格相容性驗證 (對應 AC-04 ~ AC-11)
            {
                var sw = Stopwatch.StartNew();
                string storageDir = Path.Combine(baseTemp, "T1_EncryptedStorage");
                var backupSvc = new GoogleDriveBackupService();
                var (zipPath, manifest) = await backupSvc.CreateLocalBackupPackageAsync(
                    storageDir,
                    platform: "windows",
                    passphrase: TestMasterPassphrase);

                bool zipExists = File.Exists(zipPath);
                byte[] extractedDbBytes = Array.Empty<byte>();
                bool hasManifestInZip = false;
                bool hasCardImagesInZip = false;

                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    hasManifestInZip = archive.GetEntry("manifest.json") != null;
                    hasCardImagesInZip = archive.Entries.Any(e => e.FullName.Replace('\\', '/').StartsWith("card-images/", StringComparison.OrdinalIgnoreCase));
                    var dbEntry = archive.GetEntry("contacts.sqlite3");
                    if (dbEntry != null)
                    {
                        using var ms = new MemoryStream();
                        using var es = dbEntry.Open();
                        await es.CopyToAsync(ms);
                        extractedDbBytes = ms.ToArray();
                    }
                }

                byte[] decryptedFromZip = SqlCipher4Decryptor.DecryptDatabase(extractedDbBytes, TestMasterPassphrase);
                bool decryptedValid = SqlCipher4Decryptor.IsPlainSqliteBuffer(decryptedFromZip);

                sw.Stop();
                bool passed = zipExists &&
                              manifest.IsPasswordProtected &&
                              manifest.EncryptionAlgorithm == "SQLCipher-AES256" &&
                              manifest.ContactCount == 1 &&
                              hasManifestInZip &&
                              hasCardImagesInZip &&
                              decryptedValid;

                Record(
                    "T6-BACKUP-ZIP-DUAL-PIPELINE-COMPATIBILITY",
                    "驗證加密備份 ZIP 內含 manifest.json、SQLCipher contacts.sqlite3 與 card-images/ 且可完整解壓與解密",
                    passed,
                    $"isProtected={manifest.IsPasswordProtected}, algo={manifest.EncryptionAlgorithm}, contacts={manifest.ContactCount}, hasImages={hasCardImagesInZip}, decryptedValid={decryptedValid}",
                    sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Record("UNHANDLED-EX", "測試過程發生未預期例外", false, ex.ToString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(baseTemp)) Directory.Delete(baseTemp, recursive: true);
            }
            catch { }
        }

        return report;
    }
}
