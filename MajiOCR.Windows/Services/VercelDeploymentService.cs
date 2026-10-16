using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MajiOCR_Windows.Services;

public record VercelWebAssetFile(string RelativePath, string ContentUtf8);

public record VercelDeploymentResult(
    bool Success,
    string Message,
    string? ProductionUrl = null,
    string? DeploymentId = null,
    string? ProjectId = null,
    string? VercelUsername = null,
    int UploadedFileCount = 0
);

public interface IVercelDeploymentService
{
    string NormalizeProjectName(string? rawName);
    IReadOnlyList<VercelWebAssetFile> LoadEmbeddedWebAssets(string? webGoogleClientId = null);
    string BuildDeploymentRequestJson(string projectName, string? webGoogleClientId = null);
    Task<VercelDeploymentResult> DeployPwaToVercelAsync(
        string accessToken,
        string? projectName,
        string? webGoogleClientId = null,
        IProgress<(int Percentage, string StatusMessage)>? progress = null,
        CancellationToken ct = default);
    void OpenVercelSignupPage();
    void OpenVercelTokensPage();
}

/// <summary>
/// Service that packages the embedded MajiOCR.Web iOS PWA static assets and deploys them directly
/// to the user's Vercel account via Vercel REST API (GET /v2/user, POST /v13/deployments, PATCH /v9/projects).
/// Conforms to RS-ISSUE-0044-v0.1 and AR-ISSUE-0044-v0.1.
/// </summary>
public class VercelDeploymentService : IVercelDeploymentService
{
    private readonly HttpClient _httpClient;
    public const string DefaultProjectName = "majiocr-ios-pwa";

    private static readonly (string RelativePath, string LogicalName)[] EmbeddedWebManifest =
    {
        ("index.html", "MajiOCR.Web.index.html"),
        ("styles.css", "MajiOCR.Web.styles.css"),
        ("manifest.webmanifest", "MajiOCR.Web.manifest.webmanifest"),
        ("icon.svg", "MajiOCR.Web.icon.svg"),
        ("sw.js", "MajiOCR.Web.sw.js"),
        ("js/config.js", "MajiOCR.Web.js.config.js"),
        ("js/zip-reader.js", "MajiOCR.Web.js.zip-reader.js"),
        ("js/sqlcipher-decryptor.js", "MajiOCR.Web.js.sqlcipher-decryptor.js"),
        ("js/sqlite-parser.js", "MajiOCR.Web.js.sqlite-parser.js"),
        ("js/app.js", "MajiOCR.Web.js.app.js")
    };

    public VercelDeploymentService(HttpClient? customHttpClient = null)
    {
        _httpClient = customHttpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public string NormalizeProjectName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return DefaultProjectName;

        string lower = rawName.Trim().ToLowerInvariant();
        string cleaned = Regex.Replace(lower, @"[^a-z0-9\-]", "-");
        cleaned = Regex.Replace(cleaned, @"-+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(cleaned))
            return DefaultProjectName;
        if (cleaned.Length > 52)
            cleaned = cleaned.Substring(0, 52).TrimEnd('-');
        return cleaned;
    }

    public IReadOnlyList<VercelWebAssetFile> LoadEmbeddedWebAssets(string? webGoogleClientId = null)
    {
        var assembly = typeof(VercelDeploymentService).Assembly;
        var list = new List<VercelWebAssetFile>(EmbeddedWebManifest.Length);

        foreach (var (relPath, logicalName) in EmbeddedWebManifest)
        {
            if (relPath.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
                relPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                continue; // NFR-02 strict privacy guard: never include database files
            }

            string? content = null;
            using (Stream? stream = assembly.GetManifestResourceStream(logicalName))
            {
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    content = reader.ReadToEnd();
                }
            }

            if (content == null)
            {
                // Fallback to local MajiOCR.Web directory during local dev/testing if needed
                string candidate = Path.Combine(AppContext.BaseDirectory, "MajiOCR.Web", relPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(candidate))
                {
                    string? parent = Directory.GetCurrentDirectory();
                    for (int i = 0; i < 5 && parent != null; i++)
                    {
                        string tryPath = Path.Combine(parent, "MajiOCR.Web", relPath.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(tryPath))
                        {
                            candidate = tryPath;
                            break;
                        }
                        parent = Directory.GetParent(parent)?.FullName;
                    }
                }
                if (File.Exists(candidate))
                {
                    content = File.ReadAllText(candidate, Encoding.UTF8);
                }
            }

            if (string.IsNullOrEmpty(content))
            {
                throw new InvalidOperationException($"找不到內嵌的 iOS PWA 靜態資源：{relPath} ({logicalName})。");
            }

            if (relPath == "js/config.js")
            {
                string safeClientId = (webGoogleClientId ?? string.Empty).Trim().Replace("\\", "\\\\").Replace("\"", "\\\"");
                content = $$"""
                    // MajiOCR.Web Configuration (Injected by MajiOCR.Windows VercelDeploymentService)
                    window.MAJIOCR_WEB_CONFIG = {
                      appVersion: "0.1.0-beta.1",
                      googleClientId: "{{safeClientId}}",
                      deployedBy: "MajiOCR.Windows/{{Program.GetAppVersion()}}"
                    };
                    """;
            }

            list.Add(new VercelWebAssetFile(relPath, content));
        }

        return list;
    }

    public string BuildDeploymentRequestJson(string projectName, string? webGoogleClientId = null)
    {
        string normalizedName = NormalizeProjectName(projectName);
        var assets = LoadEmbeddedWebAssets(webGoogleClientId);

        var filesArray = new JsonArray();
        foreach (var asset in assets)
        {
            filesArray.Add(new JsonObject
            {
                ["file"] = asset.RelativePath,
                ["data"] = asset.ContentUtf8,
                ["encoding"] = "utf-8"
            });
        }

        var root = new JsonObject
        {
            ["name"] = normalizedName,
            ["target"] = "production",
            ["files"] = filesArray,
            ["projectSettings"] = new JsonObject
            {
                ["framework"] = null,
                ["buildCommand"] = null,
                ["outputDirectory"] = null,
                ["installCommand"] = null
            }
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    public async Task<VercelDeploymentResult> DeployPwaToVercelAsync(
        string accessToken,
        string? projectName,
        string? webGoogleClientId = null,
        IProgress<(int Percentage, string StatusMessage)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new VercelDeploymentResult(false, "請先輸入有效的 Vercel Access Token。");
        }

        string cleanToken = accessToken.Trim();
        string normalizedProject = NormalizeProjectName(projectName);

        try
        {
            // Step 1: Verify Vercel Token & User Identity
            progress?.Report((15, "正在連線 Vercel 驗證 Access Token 與帳號身分..."));
            string? username = null;
            using (var userReq = new HttpRequestMessage(HttpMethod.Get, "https://api.vercel.com/v2/user"))
            {
                userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cleanToken);
                using var userResp = await _httpClient.SendAsync(userReq, ct);
                string userBody = await userResp.Content.ReadAsStringAsync(ct);

                if (userResp.StatusCode == HttpStatusCode.Unauthorized || userResp.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new VercelDeploymentResult(
                        false,
                        "Vercel Access Token 無效或已過期 (HTTP 401/403)。請前往 https://vercel.com/account/tokens 重新建立 Token。");
                }

                if (!userResp.IsSuccessStatusCode)
                {
                    return new VercelDeploymentResult(
                        false,
                        $"驗證 Vercel 帳號失敗 (HTTP {(int)userResp.StatusCode})：{ExtractVercelError(userBody)}");
                }

                var userNode = JsonNode.Parse(userBody);
                username = userNode?["user"]?["username"]?.GetValue<string>()
                           ?? userNode?["user"]?["email"]?.GetValue<string>()
                           ?? "vercel-user";
            }

            // Step 2: Package embedded MajiOCR.Web PWA assets & POST /v13/deployments
            var assets = LoadEmbeddedWebAssets(webGoogleClientId);
            progress?.Report((45, $"帳號「{username}」驗證成功！正在上傳並部署 iOS PWA 專案 ({assets.Count} 個靜態檔案)..."));

            string payloadJson = BuildDeploymentRequestJson(normalizedProject, webGoogleClientId);
            string? deploymentId = null;
            string? projectId = null;
            string? productionUrl = null;

            using (var deployReq = new HttpRequestMessage(HttpMethod.Post, "https://api.vercel.com/v13/deployments?skipAutoDetectionConfirmation=1"))
            {
                deployReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cleanToken);
                deployReq.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                using var deployResp = await _httpClient.SendAsync(deployReq, ct);
                string deployBody = await deployResp.Content.ReadAsStringAsync(ct);

                if (!deployResp.IsSuccessStatusCode)
                {
                    return new VercelDeploymentResult(
                        false,
                        $"部署至 Vercel 失敗 (HTTP {(int)deployResp.StatusCode})：{ExtractVercelError(deployBody)}");
                }

                var deployNode = JsonNode.Parse(deployBody);
                deploymentId = deployNode?["id"]?.GetValue<string>();
                projectId = deployNode?["projectId"]?.GetValue<string>() ?? normalizedProject;

                // Prefer clean production alias (e.g. <project>.vercel.app) if present in alias array
                var aliasArray = deployNode?["alias"]?.AsArray();
                if (aliasArray != null && aliasArray.Count > 0)
                {
                    string? firstAlias = aliasArray[0]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(firstAlias))
                    {
                        productionUrl = firstAlias.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? firstAlias
                            : $"https://{firstAlias}";
                    }
                }

                if (string.IsNullOrWhiteSpace(productionUrl))
                {
                    string? rawUrl = deployNode?["url"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(rawUrl))
                    {
                        productionUrl = rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? rawUrl
                            : $"https://{rawUrl}";
                    }
                    else
                    {
                        productionUrl = $"https://{normalizedProject}.vercel.app";
                    }
                }
            }

            // Step 3: Disable Vercel Authentication SSO protection on the static PWA shell so iPhone Safari can open it directly
            progress?.Report((85, "正在設定公開 PWA 存取權限（免登入 Vercel 直接於 iPhone 開啟）..."));
            try
            {
                string targetProj = Uri.EscapeDataString(projectId ?? normalizedProject);
                using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"https://api.vercel.com/v9/projects/{targetProj}");
                patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cleanToken);
                patchReq.Content = new StringContent("{\"ssoProtection\":null}", Encoding.UTF8, "application/json");
                using var _ = await _httpClient.SendAsync(patchReq, ct);
            }
            catch
            {
                // Non-fatal if account tier doesn't require ssoProtection patch
            }

            progress?.Report((100, $"✅ 部署成功！正式網址：{productionUrl}"));
            return new VercelDeploymentResult(
                Success: true,
                Message: $"✅ 已成功將 MajiOCR iOS PWA 部署至 Vercel ({productionUrl})！請使用 iPhone 相機掃描下方 QR Code 安裝。",
                ProductionUrl: productionUrl,
                DeploymentId: deploymentId,
                ProjectId: projectId,
                VercelUsername: username,
                UploadedFileCount: assets.Count);
        }
        catch (OperationCanceledException)
        {
            return new VercelDeploymentResult(false, "已取消 Vercel 部署作業。");
        }
        catch (Exception ex)
        {
            return new VercelDeploymentResult(false, $"連線至 Vercel API 時發生異常：{ex.Message}");
        }
    }

    public void OpenVercelSignupPage()
    {
        TryOpenBrowser("https://vercel.com/signup");
    }

    public void OpenVercelTokensPage()
    {
        TryOpenBrowser("https://vercel.com/account/tokens");
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static string ExtractVercelError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return "未知的伺服器回應";
        try
        {
            var node = JsonNode.Parse(responseBody);
            string? msg = node?["error"]?["message"]?.GetValue<string>()
                          ?? node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(msg))
                return msg;
        }
        catch { }
        return responseBody.Length > 200 ? responseBody.Substring(0, 200) + "..." : responseBody;
    }
}
