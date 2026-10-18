using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Services;

/// <summary>
/// Service managing user custom field definitions.
/// When attached to StorageService, definitions are stored directly in SQLite database (ISSUE-0043).
/// Legacy fallback uses portable &lt;StorageRoot&gt;/custom-fields.json alongside the executable (ISSUE-0051).
/// Conforms to RS-ISSUE-0006-v0.2, RS-ISSUE-0043-v0.1, and RS-ISSUE-0051-v0.1.
/// </summary>
public class CustomFieldCatalogService
{
    public const int MaxCatalogItems = 200;
    public const int MaxNameScalarLength = 64;
    public const long MaxFileSizeBytes = 1024 * 1024; // 1 MB limit

    // Strict UTF-8 decoder that throws on invalid bytes
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _catalogFilePath;
    private readonly object _lock = new();
    private StorageService? _storageService;

    public CustomFieldCatalogService(string? customCatalogPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customCatalogPath))
        {
            _catalogFilePath = customCatalogPath;
        }
        else
        {
            string root = Program.GuiVerificationRoot is string guiRoot
                ? guiRoot
                : StoragePathHelper.GetDefaultStorageRoot();
            _catalogFilePath = Path.Combine(root, "custom-fields.json");
        }
    }

    public string CatalogFilePath => _catalogFilePath;

    public void AttachStorage(StorageService? storageService)
    {
        lock (_lock)
        {
            _storageService = storageService;
        }
    }

    public StorageService? AttachedStorage => _storageService;

    /// <summary>
    /// Validates and normalizes a candidate custom field name.
    /// </summary>
    public static bool ValidateName(string? rawName, out string sanitizedName, out string? errorMessage)
    {
        sanitizedName = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawName))
        {
            errorMessage = "自訂名稱不可為空白。";
            return false;
        }

        string trimmed = rawName.Trim();
        string nfcNormalized = trimmed.Normalize(NormalizationForm.FormC);

        if (nfcNormalized.Length == 0)
        {
            errorMessage = "自訂名稱不可為空白。";
            return false;
        }

        // Check for control characters
        foreach (var rune in nfcNormalized.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                errorMessage = "自訂名稱不可包含控制字元或換行符號。";
                return false;
            }
        }

        // Check scalar length (max 64 Unicode scalars)
        int scalarCount = nfcNormalized.EnumerateRunes().Count();
        if (scalarCount > MaxNameScalarLength)
        {
            errorMessage = $"自訂名稱長度不得超過 {MaxNameScalarLength} 個字元 (目前長度: {scalarCount})。";
            return false;
        }

        sanitizedName = nfcNormalized;
        return true;
    }

    /// <summary>
    private static readonly System.Text.RegularExpressions.Regex Iso8601WithTzRegex = new(
        @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}(?::?\d{2})?)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Validates whether a string is a valid ISO 8601 timestamp with an explicit timezone offset (Z or +/-HH:mm).
    /// </summary>
    public static bool IsValidIso8601WithTimezone(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        if (!Iso8601WithTzRegex.IsMatch(str)) return false;
        return DateTimeOffset.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _);
    }

    /// <summary>
    /// Validates whether an ID string is a valid standard lowercase UUID v4.
    /// </summary>
    public static bool IsValidUuidV4(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != 36) return false;
        if (!Guid.TryParseExact(id, "D", out _)) return false;
        if (id != id.ToLowerInvariant()) return false;
        // UUID v4 format: xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx where y is 8, 9, a, or b
        if (id[14] != '4') return false;
        char variant = id[19];
        return variant is '8' or '9' or 'a' or 'b';
    }

    /// <summary>
    /// Loads the catalog from disk with strict UTF-8 and integrity checks.
    /// Returns empty catalog if file does not exist.
    /// Preserves corrupted or newer-version files without overwriting.
    /// </summary>
    public CustomFieldCatalog LoadCatalog()
    {
        lock (_lock)
        {
            if (_storageService != null)
            {
                return new CustomFieldCatalog
                {
                    CatalogVersion = 1,
                    Definitions = _storageService.GetCustomFieldDefinitions(activeOnly: false)
                };
            }

            if (!File.Exists(_catalogFilePath))
            {
                return new CustomFieldCatalog
                {
                    CatalogVersion = 1,
                    Definitions = new List<CustomFieldDefinition>()
                };
            }

            var fileInfo = new FileInfo(_catalogFilePath);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                throw new InvalidDataException($"自訂名稱設定檔大小 ({fileInfo.Length} bytes) 超過上限 (1 MB)，拒絕載入以防止記憶體消耗。");
            }

            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(_catalogFilePath);
            }
            catch (Exception ex)
            {
                throw new IOException($"讀取自訂名稱設定檔失敗: {ex.Message}", ex);
            }

            string json;
            try
            {
                json = StrictUtf8.GetString(fileBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new FormatException($"自訂名稱設定檔包含非法的 UTF-8 位元組序列: {ex.Message}。原檔案已妥善保留。", ex);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"自訂名稱設定檔格式毀損 (JSON 解析失敗): {ex.Message}。原檔案已妥善保留。", ex);
            }

            using var _ = doc;
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("自訂名稱設定檔根項目必須為 JSON 物件。原檔案已妥善保留。");
            }

            if (!doc.RootElement.TryGetProperty("catalogVersion", out var verProp))
            {
                throw new FormatException("自訂名稱設定檔缺少 'catalogVersion' 欄位。原檔案已妥善保留。");
            }

            if (verProp.ValueKind != JsonValueKind.Number || !verProp.TryGetInt32(out int version))
            {
                throw new FormatException("自訂名稱設定檔 'catalogVersion' 必須為整數。原檔案已妥善保留。");
            }

            if (version > 1)
            {
                throw new InvalidOperationException($"未知新版自訂名稱設定檔 (版本 {version})，目前版本僅支援版本 1。已保留原設定檔不予修改。");
            }
            if (version < 1)
            {
                throw new FormatException($"無效的自訂名稱設定版本號 ({version})。原檔案已妥善保留。");
            }

            if (!doc.RootElement.TryGetProperty("definitions", out var defsProp) || defsProp.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("自訂名稱設定檔缺少 definitions 欄位或為 null。原檔案已妥善保留。");
            }

            int defCount = defsProp.GetArrayLength();
            if (defCount > MaxCatalogItems)
            {
                throw new InvalidDataException($"自訂名稱數量 ({defCount}) 超過上限 ({MaxCatalogItems} 筆)。原檔案已妥善保留。");
            }

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var definitionsList = new List<CustomFieldDefinition>(defCount);

            foreach (var elem in defsProp.EnumerateArray())
            {
                if (elem.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("自訂名稱設定檔含有非物件定義項目。原檔案已妥善保留。");
                }

                if (!elem.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("自訂名稱項目缺少 'id' 欄位或非字串。原檔案已妥善保留。");
                }
                string id = idProp.GetString()!;
                if (!IsValidUuidV4(id))
                {
                    throw new FormatException($"自訂名稱項目包含無效 UUID v4 格式 (ID: '{id}')。原檔案已妥善保留。");
                }
                if (!seenIds.Add(id))
                {
                    throw new FormatException($"自訂名稱項目包含重複 ID: '{id}'。原檔案已妥善保留。");
                }

                if (!elem.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("自訂名稱項目缺少 'name' 欄位或非字串。原檔案已妥善保留。");
                }
                string rawName = nameProp.GetString()!;
                if (!ValidateName(rawName, out var sanitizedName, out var err))
                {
                    throw new FormatException($"自訂名稱項目含有無效名稱 '{rawName}': {err}。原檔案已妥善保留。");
                }
                if (!seenNames.Add(sanitizedName))
                {
                    throw new FormatException($"自訂名稱設定包含重複名稱: '{sanitizedName}'。原檔案已妥善保留。");
                }

                if (!elem.TryGetProperty("active", out var activeProp) ||
                    (activeProp.ValueKind != JsonValueKind.True && activeProp.ValueKind != JsonValueKind.False))
                {
                    throw new FormatException($"自訂名稱項目缺少 'active' 布林欄位或為 null (ID: '{id}')。原檔案已妥善保留。");
                }
                bool active = activeProp.GetBoolean();

                if (!elem.TryGetProperty("createdAt", out var createdProp) || createdProp.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException($"自訂名稱項目缺少 'createdAt' 欄位或為 null (ID: '{id}')。原檔案已妥善保留。");
                }
                string createdAt = createdProp.GetString()!;
                if (!IsValidIso8601WithTimezone(createdAt))
                {
                    throw new FormatException($"自訂名稱項目含有無效或未指定明確時區的建立日期時間 (ID: '{id}', CreatedAt: '{createdAt}')。原檔案已妥善保留。");
                }

                definitionsList.Add(new CustomFieldDefinition
                {
                    Id = id,
                    Name = sanitizedName,
                    Active = active,
                    CreatedAt = createdAt
                });
            }

            return new CustomFieldCatalog
            {
                CatalogVersion = version,
                Definitions = definitionsList
            };
        }
    }

    /// <summary>
    /// Atomically saves the catalog to disk using a temporary file in the same directory.
    /// Strictly protects existing files from being overwritten if they are corrupted, non-UTF8, or newer-version.
    /// </summary>
    public void SaveCatalog(CustomFieldCatalog catalog)
    {
        lock (_lock)
        {
            if (_storageService != null)
            {
                // When attached to SQLite StorageService, items are already written directly to DB.
                // We keep this method for backward compatibility.
                return;
            }

            if (catalog.CatalogVersion != 1)
            {
                throw new InvalidOperationException($"嘗試保存不支援的目錄版本: {catalog.CatalogVersion}");
            }

            if (catalog.Definitions == null)
            {
                throw new ArgumentException("目錄 definitions 不可為 null。");
            }

            if (catalog.Definitions.Count > MaxCatalogItems)
            {
                throw new InvalidOperationException($"自訂名稱數量超過上限 ({MaxCatalogItems} 筆)。");
            }

            // Validate in-memory catalog items before writing
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in catalog.Definitions)
            {
                if (def == null) throw new ArgumentException("定義項目不可為 null。");
                if (!IsValidUuidV4(def.Id)) throw new ArgumentException($"無效的 UUID v4 ID: '{def.Id}'");
                if (!seenIds.Add(def.Id)) throw new ArgumentException($"重複 ID: '{def.Id}'");
                if (!ValidateName(def.Name, out var sanitized, out var err)) throw new ArgumentException(err ?? "無效名稱");
                if (!seenNames.Add(sanitized)) throw new ArgumentException($"重複名稱: '{sanitized}'");
                if (!IsValidIso8601WithTimezone(def.CreatedAt)) throw new ArgumentException($"無效或無時區的 CreatedAt: '{def.CreatedAt}'");
                def.Name = sanitized;
            }

            // CRITICAL R06 REQUIREMENT: If target file exists on disk, ensure it is completely valid!
            // Any illegality (corrupted, non-UTF8, definitions:null, active:null, invalid UUID, unknown new version)
            // MUST reject saving and preserve the byte-for-byte original file!
            if (File.Exists(_catalogFilePath))
            {
                // This call will throw FormatException, InvalidOperationException, DecoderFallbackException, etc.
                // if the existing file on disk is invalid or unknown new version.
                // We DO NOT catch it! The existing file on disk remains 100% byte-for-byte untouched!
                LoadCatalog();
            }

            string? dir = Path.GetDirectoryName(_catalogFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(catalog, JsonOptions);
            string tmpFile = _catalogFilePath + ".tmp." + Guid.NewGuid().ToString("N");

            try
            {
                File.WriteAllBytes(tmpFile, StrictUtf8.GetBytes(json));

                // Atomic replacement
                File.Move(tmpFile, _catalogFilePath, overwrite: true);
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

    /// <summary>
    /// Adds a new definition with a lowercase UUID v4, current ISO8601 timestamp, and active=true.
    /// </summary>
    public CustomFieldDefinition AddDefinition(string rawName)
    {
        if (!ValidateName(rawName, out var sanitizedName, out var errMsg))
        {
            throw new ArgumentException(errMsg ?? "名稱無效。");
        }

        lock (_lock)
        {
            if (_storageService != null)
            {
                return _storageService.AddCustomFieldDefinition(sanitizedName);
            }

            var catalog = LoadCatalog();

            if (catalog.Definitions.Count >= MaxCatalogItems)
            {
                throw new InvalidOperationException($"自訂名稱已達 {MaxCatalogItems} 筆上限，無法再新增。");
            }

            if (catalog.Definitions.Any(d => string.Equals(d.Name, sanitizedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"已存在名稱為「{sanitizedName}」的自訂欄位，不可重複新增。");
            }

            var newDef = new CustomFieldDefinition
            {
                Id = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                Name = sanitizedName,
                Active = true,
                CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")
            };

            catalog.Definitions.Add(newDef);
            SaveCatalog(catalog);

            return newDef;
        }
    }

    /// <summary>
    /// Renames an existing definition while keeping its stable ID intact.
    /// </summary>
    public void RenameDefinition(string id, string newRawName)
    {
        if (!ValidateName(newRawName, out var sanitizedName, out var errMsg))
        {
            throw new ArgumentException(errMsg ?? "名稱無效。");
        }

        lock (_lock)
        {
            if (_storageService != null)
            {
                _storageService.RenameCustomFieldDefinition(id, sanitizedName);
                return;
            }

            var catalog = LoadCatalog();
            var def = catalog.Definitions.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (def == null)
            {
                throw new KeyNotFoundException($"找不到 ID 為 '{id}' 的自訂名稱定義。");
            }

            // Check duplicate name on other definitions
            if (catalog.Definitions.Any(d => !string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(d.Name, sanitizedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"已存在名稱為「{sanitizedName}」的其他自訂欄位。");
            }

            def.Name = sanitizedName;
            SaveCatalog(catalog);
        }
    }

    /// <summary>
    /// Activates or deactivates an existing custom field definition.
    /// Deactivated definitions are hidden from new selection menus, but existing card drafts retain their values.
    /// </summary>
    public void SetDefinitionActive(string id, bool active)
    {
        lock (_lock)
        {
            if (_storageService != null)
            {
                _storageService.SetCustomFieldDefinitionActive(id, active);
                return;
            }

            var catalog = LoadCatalog();
            var def = catalog.Definitions.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (def == null)
            {
                throw new KeyNotFoundException($"找不到 ID 為 '{id}' 的自訂名稱定義。");
            }

            def.Active = active;
            SaveCatalog(catalog);
        }
    }

    /// <summary>
    /// Returns all currently active custom field definitions for dropdown menus.
    /// </summary>
    public List<CustomFieldDefinition> GetActiveDefinitions()
    {
        lock (_lock)
        {
            if (_storageService != null)
            {
                return _storageService.GetCustomFieldDefinitions(activeOnly: true);
            }

            var catalog = LoadCatalog();
            return catalog.Definitions.Where(d => d.Active).ToList();
        }
    }

    /// <summary>
    /// Returns all definitions (both active and inactive) for management.
    /// </summary>
    public List<CustomFieldDefinition> GetAllDefinitions()
    {
        lock (_lock)
        {
            if (_storageService != null)
            {
                return _storageService.GetCustomFieldDefinitions(activeOnly: false);
            }

            var catalog = LoadCatalog();
            return catalog.Definitions.ToList();
        }
    }
}
