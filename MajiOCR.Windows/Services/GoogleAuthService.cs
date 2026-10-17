using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Services;

public record GoogleAuthResult(bool Success, string? AccessToken, string? ErrorMessage);

public interface IGoogleAuthService
{
    bool IsAuthenticated { get; }
    string? CurrentAccessToken { get; }
    Task<GoogleAuthResult> LoginWithBrowserAsync(string? clientId = null, string? clientSecret = null, CancellationToken ct = default);
    void Logout();
    void SetManualToken(string token);
    string GenerateCodeVerifier();
    string GenerateCodeChallenge(string verifier);
    string BuildAuthorizationUrl(string clientId, string redirectUri, string codeChallenge);
    Task SaveOAuthConfigAsync(string clientId, string? clientSecret);
    Task<(string? ClientId, string? ClientSecret)> LoadOAuthConfigAsync();
    void OpenGoogleCloudConsole();
}

public class GoogleAuthService : IGoogleAuthService
{
    private static readonly HttpClient HttpClient = new();
    private string? _accessToken;
    private const string ConfigFileName = "google_oauth_config.json";

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_accessToken);
    public string? CurrentAccessToken => _accessToken;

    public void Logout()
    {
        _accessToken = null;
    }

    public void SetManualToken(string token)
    {
        _accessToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public string GenerateCodeVerifier()
    {
        byte[] bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    public string GenerateCodeChallenge(string verifier)
    {
        using var sha256 = SHA256.Create();
        byte[] challengeBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(challengeBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public string BuildAuthorizationUrl(string clientId, string redirectUri, string codeChallenge)
    {
        var sb = new StringBuilder("https://accounts.google.com/o/oauth2/v2/auth?");
        sb.Append("client_id=").Append(Uri.EscapeDataString(clientId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        sb.Append("&response_type=code");
        sb.Append("&scope=").Append(Uri.EscapeDataString("https://www.googleapis.com/auth/contacts https://www.googleapis.com/auth/drive.file"));
        sb.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
        sb.Append("&code_challenge_method=S256");
        sb.Append("&access_type=offline");
        sb.Append("&prompt=consent");
        return sb.ToString();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void OpenGoogleCloudConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/credentials") { UseShellExecute = true });
        }
        catch { }
    }

    private static string GetConfigFilePath()
    {
        string root = StoragePathHelper.GetDefaultStorageRoot();
        return Path.Combine(root, ConfigFileName);
    }

    public async Task SaveOAuthConfigAsync(string clientId, string? clientSecret)
    {
        try
        {
            string path = GetConfigFilePath();
            string? trimmedId = clientId?.Trim();
            string? trimmedSecret = clientSecret?.Trim();
            string? protectedSecret = string.IsNullOrEmpty(trimmedSecret)
                ? trimmedSecret
                : CredentialProtectionHelper.Protect(trimmedSecret);
            var data = new { ClientId = trimmedId, ClientSecret = protectedSecret };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        }
        catch { }
    }

    public async Task<(string? ClientId, string? ClientSecret)> LoadOAuthConfigAsync()
    {
        try
        {
            string path = GetConfigFilePath();
            if (File.Exists(path))
            {
                string json = await File.ReadAllTextAsync(path, Encoding.UTF8);
                var node = JsonNode.Parse(json);
                string? clientId = node?["ClientId"]?.GetValue<string>();
                string? rawSecret = node?["ClientSecret"]?.GetValue<string>();
                var (plainSecret, wasLegacyPlainText, _) = CredentialProtectionHelper.UnprotectWithMetadata(rawSecret);

                if (wasLegacyPlainText && !string.IsNullOrWhiteSpace(plainSecret))
                {
                    await SaveOAuthConfigAsync(clientId ?? string.Empty, plainSecret);
                }

                return (clientId, plainSecret);
            }
        }
        catch { }
        return (null, null);
    }

    public async Task<GoogleAuthResult> LoginWithBrowserAsync(
        string? clientId = null,
        string? clientSecret = null,
        CancellationToken ct = default)
    {
        string? effectiveClientId = clientId?.Trim();
        string? effectiveClientSecret = clientSecret?.Trim();

        // If client ID or secret is not provided in parameter, check saved config
        if (string.IsNullOrWhiteSpace(effectiveClientId) || string.IsNullOrWhiteSpace(effectiveClientSecret))
        {
            var (savedId, savedSecret) = await LoadOAuthConfigAsync();
            if (string.IsNullOrWhiteSpace(effectiveClientId)) effectiveClientId = savedId;
            if (string.IsNullOrWhiteSpace(effectiveClientSecret)) effectiveClientSecret = savedSecret;
        }

        if (string.IsNullOrWhiteSpace(effectiveClientId) || string.IsNullOrWhiteSpace(effectiveClientSecret))
        {
            return new GoogleAuthResult(false, null, "尚未完整設定 Google OAuth 2.0 Client ID 與 Client Secret。\n請於下方憑證設定輸入 Client ID 及 Client Secret，或點擊「建立 Client ID」教學按鈕。");
        }

        // Save for future reuse
        await SaveOAuthConfigAsync(effectiveClientId, effectiveClientSecret);

        int port = GetAvailablePort();
        string redirectUri = $"http://127.0.0.1:{port}/oauth2callback/";

        string verifier = GenerateCodeVerifier();
        string challenge = GenerateCodeChallenge(verifier);
        string authUrl = BuildAuthorizationUrl(effectiveClientId, redirectUri, challenge);

        using var httpListener = new HttpListener();
        httpListener.Prefixes.Add(redirectUri);

        try
        {
            httpListener.Start();

            // Open system default browser
            try
            {
                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                return new GoogleAuthResult(false, null, $"無法啟動系統瀏覽器：{ex.Message}");
            }

            // Wait for incoming redirect with cancellation / timeout support
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var contextTask = httpListener.GetContextAsync();
            using (linkedCts.Token.Register(() => { try { httpListener.Stop(); } catch { } }))
            {
                HttpListenerContext context;
                try
                {
                    context = await contextTask;
                }
                catch (Exception)
                {
                    if (ct.IsCancellationRequested)
                        return new GoogleAuthResult(false, null, "使用者已取消登入。");
                    if (timeoutCts.IsCancellationRequested)
                        return new GoogleAuthResult(false, null, "登入已逾時（超過2分鐘未完成授權）。");
                    throw;
                }

                var request = context.Request;
                string? code = request.QueryString["code"];
                string? error = request.QueryString["error"];

                // Send success HTML response to browser
                string responseHtml = @"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Google 登入完成</title>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; text-align: center; padding: 60px 20px; background: #f8fafc; color: #1e293b; }
        .card { max-width: 480px; margin: 0 auto; background: #ffffff; padding: 32px; border-radius: 12px; box-shadow: 0 4px 6px -1px rgba(0,0,0,0.1); }
        h2 { color: #10b981; margin-top: 0; }
        p { color: #64748b; line-height: 1.6; }
    </style>
</head>
<body>
    <div class='card'>
        <h2>🎉 Google 帳號授權完成！</h2>
        <p>您已成功完成 Google 帳號登入授權。<br>現在可以關閉此分頁，返回名片管理系統繼續進行同步、備份或還原操作。</p>
    </div>
</body>
</html>";

                byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
                context.Response.ContentLength64 = buffer.Length;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, ct);
                context.Response.OutputStream.Close();

                if (!string.IsNullOrEmpty(error))
                {
                    return new GoogleAuthResult(false, null, $"Google 授權失敗：{error}");
                }

                if (string.IsNullOrEmpty(code))
                {
                    return new GoogleAuthResult(false, null, "未取得 Google 授權碼。");
                }

                // Exchange code for Access Token
                var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = effectiveClientId,
                    ["client_secret"] = effectiveClientSecret,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri
                });

                var tokenResponse = await HttpClient.PostAsync("https://oauth2.googleapis.com/token", tokenContent, ct);
                if (tokenResponse.IsSuccessStatusCode)
                {
                    string tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct);
                    var node = JsonNode.Parse(tokenJson);
                    string? accessToken = node?["access_token"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        _accessToken = accessToken;
                        return new GoogleAuthResult(true, accessToken, null);
                    }
                    return new GoogleAuthResult(false, null, "Google 伺服器回傳未包含有效的 Access Token。");
                }
                else
                {
                    string errorDetail = await tokenResponse.Content.ReadAsStringAsync(ct);
                    return new GoogleAuthResult(false, null, $"換取 Access Token 失敗 (HTTP {(int)tokenResponse.StatusCode})：{errorDetail}");
                }
            }
        }
        catch (Exception ex)
        {
            return new GoogleAuthResult(false, null, $"OAuth 流程異常：{ex.Message}");
        }
        finally
        {
            try { httpListener.Close(); } catch { }
        }
    }
}
