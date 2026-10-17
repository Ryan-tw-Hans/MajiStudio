using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;

namespace MajiOCR_Windows.Verification;

public class Issue0026AutomatedTestSuite
{
    public record TestResult(string TestName, bool Passed, string Message);

    public static async Task<List<TestResult>> RunAllTestsAsync()
    {
        var results = new List<TestResult>();

        results.Add(Test01_PkceGeneration());
        results.Add(Test02_AuthorizationUrlFormat());
        results.Add(Test03_AuthService_StateManagement());
        results.Add(await Test04_GooglePeopleApi_WithAuthServiceAsync());

        return results;
    }

    public static TestResult Test01_PkceGeneration()
    {
        var authService = new GoogleAuthService();
        string verifier = authService.GenerateCodeVerifier();
        string challenge = authService.GenerateCodeChallenge(verifier);

        bool verifierValid = !string.IsNullOrEmpty(verifier) && verifier.Length >= 40 && !verifier.Contains("+") && !verifier.Contains("/") && !verifier.Contains("=");
        bool challengeValid = !string.IsNullOrEmpty(challenge) && challenge.Length >= 40 && !challenge.Contains("+") && !challenge.Contains("/") && !challenge.Contains("=");

        if (verifierValid && challengeValid)
        {
            return new TestResult("TC-0026-01: PKCE 驗證碼與 Challenge 產生規範", true, $"PKCE 驗證通過. Verifier 長度={verifier.Length}, Challenge 長度={challenge.Length}, 均符合 URL-Safe Base64 規範");
        }
        return new TestResult("TC-0026-01: PKCE 驗證碼與 Challenge 產生規範", false, $"PKCE 產生格式不符合規範. verifierValid={verifierValid}, challengeValid={challengeValid}");
    }

    public static TestResult Test02_AuthorizationUrlFormat()
    {
        var authService = new GoogleAuthService();
        string clientId = "123456789-test.apps.googleusercontent.com";
        string redirectUri = "http://127.0.0.1:54321/oauth2callback/";
        string challenge = "test_challenge_abc123";

        string url = authService.BuildAuthorizationUrl(clientId, redirectUri, challenge);

        bool hasBase = url.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?");
        bool hasClientId = url.Contains("client_id=" + Uri.EscapeDataString(clientId));
        bool hasRedirect = url.Contains("redirect_uri=" + Uri.EscapeDataString(redirectUri));
        bool hasScope = url.Contains("scope=" + Uri.EscapeDataString("https://www.googleapis.com/auth/contacts"));
        bool hasChallenge = url.Contains("code_challenge=" + challenge);
        bool hasMethod = url.Contains("code_challenge_method=S256");

        if (hasBase && hasClientId && hasRedirect && hasScope && hasChallenge && hasMethod)
        {
            return new TestResult("TC-0026-02: Google OAuth 2.0 授權網址建構驗證", true, "授權 URL 正確包含 contacts scope, client_id, redirect_uri 與 S256 PKCE challenge");
        }
        return new TestResult("TC-0026-02: Google OAuth 2.0 授權網址建構驗證", false, $"授權 URL 不完整: {url}");
    }

    public static TestResult Test03_AuthService_StateManagement()
    {
        var authService = new GoogleAuthService();

        bool initialNotAuth = !authService.IsAuthenticated && authService.CurrentAccessToken == null;

        authService.SetManualToken("ya29.sample_token_12345");
        bool loggedIn = authService.IsAuthenticated && authService.CurrentAccessToken == "ya29.sample_token_12345";

        authService.Logout();
        bool loggedOut = !authService.IsAuthenticated && authService.CurrentAccessToken == null;

        if (initialNotAuth && loggedIn && loggedOut)
        {
            return new TestResult("TC-0026-03: GoogleAuthService 狀態切換與 Token 生命週期", true, "未登入、手動輸入/接收 Token 與登出狀態切換運作正確");
        }
        return new TestResult("TC-0026-03: GoogleAuthService 狀態切換與 Token 生命週期", false, $"狀態機異常: initial={initialNotAuth}, loggedIn={loggedIn}, loggedOut={loggedOut}");
    }

    public static async Task<TestResult> Test04_GooglePeopleApi_WithAuthServiceAsync()
    {
        var authService = new GoogleAuthService();
        var syncService = new GoogleContactsSyncService();

        authService.SetManualToken("mock_bearer_token");

        var contact = new ContactDetailModel
        {
            ContactId = "C-AUTH-01",
            Name = "王小明",
            Company = "宏碁科技",
            CreatedAt = DateTimeOffset.UtcNow
        };
        contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Name, RawValue = "王小明", Label = "姓名" });
        contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Phone, RawValue = "0912-333-444", Label = "電話" });

        var progress = new Progress<GoogleSyncProgress>();
        var result = await syncService.SyncToGoogleContactsAsync(new[] { contact }, authService.CurrentAccessToken, progress);

        if (result.IsSuccess && result.TotalCount == 1)
        {
            return new TestResult("TC-0026-04: GoogleAuthService 與 GoogleContactsSyncService 協同同步", true, "透過 AuthService 取得之 Token 成功傳遞至 SyncService 執行校驗與同步流程");
        }
        return new TestResult("TC-0026-04: GoogleAuthService 與 GoogleContactsSyncService 協同同步", false, $"同步失敗: {result.SummaryMessage}");
    }
}
