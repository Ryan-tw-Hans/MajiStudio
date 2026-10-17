using System;
using System.Text.Json.Serialization;

namespace MajiOCR_Windows.Models;

/// <summary>
/// User application settings model for MajiOCR.Windows.
/// Conforms to RS-ISSUE-0036, AR-ISSUE-0036, and DBA-ISSUE-0036.
/// </summary>
public class AppSettings
{
    [JsonPropertyName("settingsVersion")]
    public int SettingsVersion { get; set; } = 1;

    [JsonPropertyName("fullOcrLanguageCode")]
    public string FullOcrLanguageCode { get; set; } = OcrLanguageCatalog.Mixed5;

    [JsonPropertyName("partialOcrLanguageCode")]
    public string PartialOcrLanguageCode { get; set; } = OcrLanguageCatalog.Mixed5;

    [JsonPropertyName("isPasswordProtected")]
    public bool IsPasswordProtected { get; set; } = false;

    [JsonPropertyName("passwordHash")]
    public string? PasswordHash { get; set; }

    [JsonPropertyName("passwordSalt")]
    public string? PasswordSalt { get; set; }

    [JsonPropertyName("encryptionAlgorithm")]
    public string EncryptionAlgorithm { get; set; } = "SQLCipher-AES256";

    [JsonPropertyName("vercelAccessToken")]
    public string? VercelAccessToken { get; set; }

    [JsonPropertyName("vercelProjectName")]
    public string VercelProjectName { get; set; } = "majiocr-ios-pwa";

    [JsonPropertyName("vercelWebGoogleClientId")]
    public string? VercelWebGoogleClientId { get; set; }

    [JsonPropertyName("lastDeployedVercelUrl")]
    public string? LastDeployedVercelUrl { get; set; }

    [JsonPropertyName("gitHubPagesUrl")]
    public string GitHubPagesUrl { get; set; } = "https://ryan-tw-hans.github.io/MajiStudio/";

    [JsonPropertyName("lastUpdatedAt")]
    public string LastUpdatedAt { get; set; } = string.Empty;
}
