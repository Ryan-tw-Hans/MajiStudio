using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MajiOCR_Windows.Services.Update;

public enum UpdateChannel
{
    Release,
    Test
}

public static class UpdateChannelExtensions
{
    public static string GetDisplayName(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Release => "正式發布",
        UpdateChannel.Test => "測試版本",
        _ => channel.ToString()
    };
}

public record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("packageUrl")] string PackageUrl,
    [property: JsonPropertyName("changeLogUrl")] string? ChangeLogUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonIgnore] string? ChangeLog = null,
    [property: JsonIgnore] UpdateChannel Channel = UpdateChannel.Release)
{
    public static UpdateManifest Parse(string json, UpdateChannel channel = UpdateChannel.Release)
    {
        // 優先支援 GitHub Releases REST API 格式
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("tag_name", out var tagElem))
            {
                string tag = tagElem.GetString() ?? "";
                string version = tag.TrimStart('v', 'V');
                string? name = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : null;
                string? body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() : null;
                string? pubDate = root.TryGetProperty("published_at", out var pubElem) ? pubElem.GetString() : null;
                if (!string.IsNullOrWhiteSpace(pubDate) && DateTime.TryParse(pubDate, out var dt))
                {
                    pubDate = dt.ToString("yyyy-MM-dd");
                }

                string packageUrl = "";
                string fileName = "";
                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsElem.EnumerateArray())
                    {
                        var aName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var aUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                        if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            packageUrl = aUrl;
                            fileName = aName;
                            if (aName.Equals("MajiOCR.Windows.zip", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }
                        }
                    }
                }

                string sha256 = "";
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var shaMatch = System.Text.RegularExpressions.Regex.Match(body, @"(?:SHA-?256|sha256)[:\s]+([0-9a-fA-F]{64})");
                    if (shaMatch.Success)
                    {
                        sha256 = shaMatch.Groups[1].Value.ToUpperInvariant();
                    }
                }

                return new UpdateManifest(
                    Version: version,
                    ReleaseDate: pubDate,
                    Type: "release",
                    Description: name ?? $"MajiOCR.Windows {tag}",
                    PackageUrl: packageUrl,
                    ChangeLogUrl: null,
                    Sha256: sha256,
                    FileName: !string.IsNullOrWhiteSpace(fileName) ? fileName : $"MajiOCR.Windows_{tag}.zip",
                    ChangeLog: body,
                    Channel: channel
                );
            }
        }
        catch { }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, options)
            ?? throw new InvalidOperationException("無法解析更新索引 JSON。");

        return manifest with { Channel = channel };
    }
}

public record UpdateSettings(
    [property: JsonPropertyName("ReleaseManifestUrl")] string? ReleaseManifestUrl,
    [property: JsonPropertyName("TestManifestUrl")] string? TestManifestUrl,
    [property: JsonPropertyName("IncludeTestVersions")] bool IncludeTestVersions = false,
    [property: JsonPropertyName("CustomManifestUrl")] string? CustomManifestUrl = null)
{
    public UpdateSettings Normalized()
    {
        return new UpdateSettings(
            string.IsNullOrWhiteSpace(ReleaseManifestUrl) ? null : ReleaseManifestUrl.Trim(),
            string.IsNullOrWhiteSpace(TestManifestUrl) ? null : TestManifestUrl.Trim(),
            IncludeTestVersions,
            string.IsNullOrWhiteSpace(CustomManifestUrl) ? null : CustomManifestUrl.Trim()
        );
    }
}

public record UpdateCheckResult(
    UpdateManifest Manifest,
    bool IsAvailable,
    UpdateChannel Channel,
    string ManifestUrl,
    string? Reason = null
);

public record UpdateDownloadProgress(
    long BytesReceived,
    long? TotalBytesToReceive,
    bool Verifying = false
)
{
    public double? ProgressPercentage => TotalBytesToReceive.HasValue && TotalBytesToReceive > 0
        ? (double)BytesReceived / TotalBytesToReceive.Value * 100.0
        : null;
}

public record UpdateDownloadResult(
    string FilePath,
    string Sha256
);

public record AppliedUpdateState(
    [property: JsonPropertyName("appliedAt")] string AppliedAt,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("packageUrl")] string PackageUrl
);
