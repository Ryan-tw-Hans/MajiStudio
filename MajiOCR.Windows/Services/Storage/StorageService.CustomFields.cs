using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services.Storage;

public partial class StorageService
{
    private const string LegacyCustomFieldsFileName = "custom-fields.json";

    public void EnsureCustomFieldsSchema(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS custom_field_definitions (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL UNIQUE CHECK(length(trim(name)) > 0),
                active INTEGER NOT NULL DEFAULT 1 CHECK(active IN (0, 1)),
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_custom_field_definitions_active ON custom_field_definitions (active, name);
        ";
        cmd.ExecuteNonQuery();

        TryMigrateLegacyJson(conn, tx);
    }

    private void TryMigrateLegacyJson(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        try
        {
            // 檢查資料庫是否已有資料
            using var countCmd = conn.CreateCommand();
            if (tx != null) countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM custom_field_definitions;";
            long count = Convert.ToInt64(countCmd.ExecuteScalar() ?? 0);
            if (count > 0) return;

            // 尋找舊版設定檔
            string legacyPath;
            if (!string.IsNullOrWhiteSpace(_legacyJsonPathOverride))
            {
                legacyPath = _legacyJsonPathOverride;
            }
            else if (_applicationDirectoryMode)
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                legacyPath = Path.Combine(localAppData, "MajiOCR.Windows", LegacyCustomFieldsFileName);
            }
            else
            {
                return; // 自訂/測試儲存目錄隔離，不讀取全域使用者設定
            }
            if (!File.Exists(legacyPath)) return;

            string json = File.ReadAllText(legacyPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("definitions", out var defsArray) || defsArray.ValueKind != JsonValueKind.Array)
                return;

            foreach (var elem in defsArray.EnumerateArray())
            {
                string id = elem.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                string name = elem.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                bool active = !elem.TryGetProperty("active", out var actProp) || actProp.GetBoolean();
                string createdAt = elem.TryGetProperty("created_at", out var catProp) ? catProp.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                if (string.IsNullOrWhiteSpace(createdAt)) createdAt = DateTimeOffset.UtcNow.ToString("o");

                using var insertCmd = conn.CreateCommand();
                if (tx != null) insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
                    INSERT OR IGNORE INTO custom_field_definitions (id, name, active, created_at)
                    VALUES (@id, @name, @active, @created_at);
                ";
                insertCmd.Parameters.AddWithValue("@id", id);
                insertCmd.Parameters.AddWithValue("@name", name.Trim());
                insertCmd.Parameters.AddWithValue("@active", active ? 1 : 0);
                insertCmd.Parameters.AddWithValue("@created_at", createdAt);
                insertCmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // 歷史遷移容錯，不阻止正常啟動
        }
    }

    public List<CustomFieldDefinition> GetCustomFieldDefinitions(bool activeOnly = false)
    {
        var list = new List<CustomFieldDefinition>();
        if (!_isAvailable) return list;

        using var conn = OpenConnection(readOnly: true);
        EnsureCustomFieldsSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = activeOnly
            ? "SELECT id, name, active, created_at FROM custom_field_definitions WHERE active = 1 ORDER BY created_at ASC;"
            : "SELECT id, name, active, created_at FROM custom_field_definitions ORDER BY created_at ASC;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CustomFieldDefinition
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Active = reader.GetInt32(2) == 1,
                CreatedAt = reader.GetString(3)
            });
        }

        return list;
    }

    public CustomFieldDefinition AddCustomFieldDefinition(string rawName)
    {
        if (!CustomFieldCatalogService.ValidateName(rawName, out var sanitizedName, out var errMsg))
        {
            throw new ArgumentException(errMsg ?? "名稱無效。");
        }

        using var conn = OpenConnection(readOnly: false);
        EnsureCustomFieldsSchema(conn);

        // 檢查是否已達上限或重複
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM custom_field_definitions;";
        long total = Convert.ToInt64(checkCmd.ExecuteScalar() ?? 0);
        if (total >= CustomFieldCatalogService.MaxCatalogItems)
        {
            throw new InvalidOperationException($"自訂名稱已達 {CustomFieldCatalogService.MaxCatalogItems} 筆上限，無法再新增。");
        }

        using var dupCmd = conn.CreateCommand();
        dupCmd.CommandText = "SELECT COUNT(*) FROM custom_field_definitions WHERE LOWER(name) = LOWER(@name);";
        dupCmd.Parameters.AddWithValue("@name", sanitizedName);
        long dups = Convert.ToInt64(dupCmd.ExecuteScalar() ?? 0);
        if (dups > 0)
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

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO custom_field_definitions (id, name, active, created_at)
            VALUES (@id, @name, 1, @created_at);
        ";
        insertCmd.Parameters.AddWithValue("@id", newDef.Id);
        insertCmd.Parameters.AddWithValue("@name", newDef.Name);
        insertCmd.Parameters.AddWithValue("@created_at", newDef.CreatedAt);
        insertCmd.ExecuteNonQuery();

        return newDef;
    }

    public void RenameCustomFieldDefinition(string id, string newRawName)
    {
        if (!CustomFieldCatalogService.ValidateName(newRawName, out var sanitizedName, out var errMsg))
        {
            throw new ArgumentException(errMsg ?? "名稱無效。");
        }

        using var conn = OpenConnection(readOnly: false);
        EnsureCustomFieldsSchema(conn);

        using var dupCmd = conn.CreateCommand();
        dupCmd.CommandText = "SELECT COUNT(*) FROM custom_field_definitions WHERE LOWER(name) = LOWER(@name) AND id != @id;";
        dupCmd.Parameters.AddWithValue("@name", sanitizedName);
        dupCmd.Parameters.AddWithValue("@id", id);
        if (Convert.ToInt64(dupCmd.ExecuteScalar() ?? 0) > 0)
        {
            throw new InvalidOperationException($"已存在名稱為「{sanitizedName}」的其他自訂欄位。");
        }

        using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = "UPDATE custom_field_definitions SET name = @name WHERE id = @id;";
        updateCmd.Parameters.AddWithValue("@name", sanitizedName);
        updateCmd.Parameters.AddWithValue("@id", id);
        int rows = updateCmd.ExecuteNonQuery();
        if (rows == 0)
        {
            throw new KeyNotFoundException($"找不到 ID 為 '{id}' 的自訂名稱定義。");
        }
    }

    public void SetCustomFieldDefinitionActive(string id, bool active)
    {
        using var conn = OpenConnection(readOnly: false);
        EnsureCustomFieldsSchema(conn);

        using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = "UPDATE custom_field_definitions SET active = @active WHERE id = @id;";
        updateCmd.Parameters.AddWithValue("@active", active ? 1 : 0);
        updateCmd.Parameters.AddWithValue("@id", id);
        int rows = updateCmd.ExecuteNonQuery();
        if (rows == 0)
        {
            throw new KeyNotFoundException($"找不到 ID 為 '{id}' 的自訂名稱定義。");
        }
    }
}
