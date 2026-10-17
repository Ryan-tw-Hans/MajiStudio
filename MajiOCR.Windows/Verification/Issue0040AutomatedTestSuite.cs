using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public class Issue0040TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0040TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0040TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.20";
    public string IssueId { get; set; } = "ISSUE-0040";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0040TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0040AutomatedTestSuite
{
    public static async Task<Issue0040TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0040TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0040TestCaseResult
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

        string configBackupPath = Path.Combine(Path.GetTempPath(), $"google_oauth_config_backup_{Guid.NewGuid():N}.json");
        string configPath = Path.Combine(StoragePathHelper.GetDefaultStorageRoot(), "google_oauth_config.json");
        bool hadExistingConfig = File.Exists(configPath);

        try
        {
            if (hadExistingConfig)
            {
                File.Copy(configPath, configBackupPath, true);
            }

            var authService = new GoogleAuthService();

            // TC01: OAuth Configuration Save & Load Persistence
            var tc01Sw = Stopwatch.StartNew();
            string testClientId = "test-client-id-12345.apps.googleusercontent.com";
            string testClientSecret = "GOCSPX-testsecret67890";
            await authService.SaveOAuthConfigAsync(testClientId, testClientSecret);
            var (loadedId, loadedSecret) = await authService.LoadOAuthConfigAsync();

            bool tc01JsonValid = false;
            if (File.Exists(configPath))
            {
                string rawJson = await File.ReadAllTextAsync(configPath);
                var jsonNode = JsonNode.Parse(rawJson);
                tc01JsonValid = jsonNode?["ClientId"]?.GetValue<string>() == testClientId &&
                                jsonNode?["ClientSecret"]?.GetValue<string>() == testClientSecret;
            }

            bool tc01Passed = loadedId == testClientId && loadedSecret == testClientSecret && tc01JsonValid;
            tc01Sw.Stop();
            Record("TC01", "Google OAuth 憑證配置儲存與讀取持久化驗證 (Client ID & Client Secret)",
                tc01Passed,
                $"LoadedClientId={loadedId}, LoadedSecret={loadedSecret}, JsonMatches={tc01JsonValid}",
                tc01Sw.ElapsedMilliseconds);

            // TC02: Missing Credentials Validation in LoginWithBrowserAsync
            var tc02Sw = Stopwatch.StartNew();
            // Clear config first
            await authService.SaveOAuthConfigAsync("", "");

            // 1) Both empty
            var resBothEmpty = await authService.LoginWithBrowserAsync("", "");
            // 2) Client ID only
            var resIdOnly = await authService.LoginWithBrowserAsync("some-client-id", "");
            // 3) Client Secret only
            var resSecretOnly = await authService.LoginWithBrowserAsync("", "some-secret");

            bool tc02Passed = !resBothEmpty.Success && !string.IsNullOrEmpty(resBothEmpty.ErrorMessage) &&
                              !resIdOnly.Success && !string.IsNullOrEmpty(resIdOnly.ErrorMessage) &&
                              !resSecretOnly.Success && !string.IsNullOrEmpty(resSecretOnly.ErrorMessage) &&
                              resBothEmpty.ErrorMessage.Contains("Client ID 與 Client Secret");
            tc02Sw.Stop();
            Record("TC02", "瀏覽器登入流程中缺漏 Client ID 或 Client Secret 時之阻擋與錯誤提示校驗",
                tc02Passed,
                $"BothEmptyBlocked={!resBothEmpty.Success}, IdOnlyBlocked={!resIdOnly.Success}, SecretOnlyBlocked={!resSecretOnly.Success}",
                tc02Sw.ElapsedMilliseconds);

            // TC03: Authentication State Lifecycle & Logout
            var tc03Sw = Stopwatch.StartNew();
            authService.Logout();
            bool initialLoggedOut = !authService.IsAuthenticated && authService.CurrentAccessToken == null;
            authService.SetManualToken("test-mock-bearer-token");
            bool manualSet = authService.IsAuthenticated && authService.CurrentAccessToken == "test-mock-bearer-token";
            authService.Logout();
            bool clearedOnLogout = !authService.IsAuthenticated && authService.CurrentAccessToken == null;

            bool tc03Passed = initialLoggedOut && manualSet && clearedOnLogout;
            tc03Sw.Stop();
            Record("TC03", "GoogleAuthService 狀態管理生命週期 (驗證就緒、登出清除與狀態重設)",
                tc03Passed,
                $"InitialLoggedOut={initialLoggedOut}, MockTokenSet={manualSet}, ClearedOnLogout={clearedOnLogout}",
                tc03Sw.ElapsedMilliseconds);

            // TC04: PKCE Code Challenge & OAuth URL Construction
            var tc04Sw = Stopwatch.StartNew();
            string verifier = authService.GenerateCodeVerifier();
            string challenge = authService.GenerateCodeChallenge(verifier);
            string redirectUri = "http://127.0.0.1:54321/oauth2callback/";
            string authUrl = authService.BuildAuthorizationUrl("my-test-client-id", redirectUri, challenge);

            bool verifierValid = !string.IsNullOrWhiteSpace(verifier) && verifier.Length >= 43;
            bool challengeValid = !string.IsNullOrWhiteSpace(challenge) && challenge != verifier;
            bool urlValid = authUrl.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?") &&
                            authUrl.Contains("client_id=my-test-client-id") &&
                            authUrl.Contains("code_challenge=" + challenge) &&
                            authUrl.Contains("code_challenge_method=S256") &&
                            authUrl.Contains("scope=") &&
                            authUrl.Contains("access_type=offline");

            bool tc04Passed = verifierValid && challengeValid && urlValid;
            tc04Sw.Stop();
            Record("TC04", "PKCE 授權碼產生與 Google OAuth 2.0 授權端點 URL 組裝驗證",
                tc04Passed,
                $"VerifierLen={verifier.Length}, ChallengeValid={challengeValid}, UrlValid={urlValid}",
                tc04Sw.ElapsedMilliseconds);

            // Restore backed up config if present
            if (hadExistingConfig && File.Exists(configBackupPath))
            {
                File.Copy(configBackupPath, configPath, true);
            }
            else if (!hadExistingConfig && File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
        catch (Exception ex)
        {
            Record("TC_ERR", "測試執行過程發生未預期例外", false, ex.ToString());
        }
        finally
        {
            try
            {
                if (File.Exists(configBackupPath)) File.Delete(configBackupPath);
            }
            catch { }
        }

        sw.Stop();
        return report;
    }
}
