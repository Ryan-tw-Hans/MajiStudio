using System;
using System.IO;
using System.Text;
using System.Text.Json;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services;

public interface IAppSettingsService
{
    string SettingsFilePath { get; }
    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);
    void UpdateFullOcrLanguage(string code);
    void UpdatePartialOcrLanguage(string code);
    void UpdateSecuritySettings(bool isPasswordProtected, string? passwordHash, string? passwordSalt);
    void UpdateVercelSettings(string? accessToken, string? projectName, string? webGoogleClientId, string? lastDeployedUrl = null);
    void UpdateGitHubPagesUrl(string? url);
}

/// <summary>
/// Service managing user application preferences stored in %LocalAppData%/MajiOCR.Windows/settings.json.
/// Conforms to RS-ISSUE-0036, AR-ISSUE-0036, and DBA-ISSUE-0036.
/// Implements atomic file replacement, strict UTF-8 validation, allowlist sanitization, and corruption resilience.
/// </summary>
public class AppSettingsService : IAppSettingsService
{
    public const int MaxFileSizeBytes = 64 * 1024; // 64 KB limit
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsFilePath;
    private readonly object _lock = new();

    public AppSettingsService(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _settingsFilePath = customPath;
        }
        else
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _settingsFilePath = Path.Combine(localAppData, "MajiOCR.Windows", "settings.json");
        }
    }

    public string SettingsFilePath => _settingsFilePath;

    public AppSettings LoadSettings()
    {
        lock (_lock)
        {
            if (!File.Exists(_settingsFilePath))
            {
                return CreateDefaultSettings();
            }

            try
            {
                var fileInfo = new FileInfo(_settingsFilePath);
                if (fileInfo.Length > MaxFileSizeBytes)
                {
                    return CreateDefaultSettings();
                }

                byte[] bytes = File.ReadAllBytes(_settingsFilePath);
                string json = StrictUtf8.GetString(bytes);

                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings == null)
                {
                    return CreateDefaultSettings();
                }

                // Sanitize language codes against strict allowlist
                if (!OcrLanguageCatalog.IsValidCode(settings.FullOcrLanguageCode))
                {
                    settings.FullOcrLanguageCode = OcrLanguageCatalog.Mixed5;
                }
                if (!OcrLanguageCatalog.IsValidCode(settings.PartialOcrLanguageCode))
                {
                    settings.PartialOcrLanguageCode = OcrLanguageCatalog.Mixed5;
                }

                return settings;
            }
            catch
            {
                // Safe fallback on corrupted JSON, non-UTF8 bytes, or IO errors
                return CreateDefaultSettings();
            }
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_lock)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (!OcrLanguageCatalog.IsValidCode(settings.FullOcrLanguageCode))
            {
                settings.FullOcrLanguageCode = OcrLanguageCatalog.Mixed5;
            }
            if (!OcrLanguageCatalog.IsValidCode(settings.PartialOcrLanguageCode))
            {
                settings.PartialOcrLanguageCode = OcrLanguageCatalog.Mixed5;
            }

            settings.SettingsVersion = 1;
            settings.LastUpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

            string? dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string tmpFile = _settingsFilePath + ".tmp." + Guid.NewGuid().ToString("N");

            try
            {
                File.WriteAllBytes(tmpFile, StrictUtf8.GetBytes(json));
                File.Move(tmpFile, _settingsFilePath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tmpFile)) File.Delete(tmpFile);
                }
                catch { }
                throw;
            }
        }
    }

    public void UpdateFullOcrLanguage(string code)
    {
        lock (_lock)
        {
            var current = LoadSettings();
            current.FullOcrLanguageCode = OcrLanguageCatalog.IsValidCode(code) ? code : OcrLanguageCatalog.Mixed5;
            SaveSettings(current);
        }
    }

    public void UpdatePartialOcrLanguage(string code)
    {
        lock (_lock)
        {
            var current = LoadSettings();
            current.PartialOcrLanguageCode = OcrLanguageCatalog.IsValidCode(code) ? code : OcrLanguageCatalog.Mixed5;
            SaveSettings(current);
        }
    }

    public void UpdateSecuritySettings(bool isPasswordProtected, string? passwordHash, string? passwordSalt)
    {
        lock (_lock)
        {
            var current = LoadSettings();
            current.IsPasswordProtected = isPasswordProtected;
            current.PasswordHash = isPasswordProtected ? passwordHash : null;
            current.PasswordSalt = isPasswordProtected ? passwordSalt : null;
            current.EncryptionAlgorithm = "SQLCipher-AES256";
            SaveSettings(current);
        }
    }

    public void UpdateVercelSettings(string? accessToken, string? projectName, string? webGoogleClientId, string? lastDeployedUrl = null)
    {
        lock (_lock)
        {
            var current = LoadSettings();
            current.VercelAccessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken.Trim();
            current.VercelProjectName = string.IsNullOrWhiteSpace(projectName) ? "majiocr-ios-pwa" : projectName.Trim();
            current.VercelWebGoogleClientId = string.IsNullOrWhiteSpace(webGoogleClientId) ? null : webGoogleClientId.Trim();
            if (!string.IsNullOrWhiteSpace(lastDeployedUrl))
            {
                current.LastDeployedVercelUrl = lastDeployedUrl.Trim();
            }
            SaveSettings(current);
        }
    }

    public void UpdateGitHubPagesUrl(string? url)
    {
        lock (_lock)
        {
            var current = LoadSettings();
            current.GitHubPagesUrl = string.IsNullOrWhiteSpace(url)
                ? "https://ryan-tw-hans.github.io/MajiStudio/"
                : url.Trim();
            SaveSettings(current);
        }
    }

    private static AppSettings CreateDefaultSettings()
    {
        return new AppSettings
        {
            SettingsVersion = 1,
            FullOcrLanguageCode = OcrLanguageCatalog.Mixed5,
            PartialOcrLanguageCode = OcrLanguageCatalog.Mixed5,
            LastUpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")
        };
    }
}
