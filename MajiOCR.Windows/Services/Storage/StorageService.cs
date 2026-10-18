using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Services.Storage;

public partial class StorageService : IStorageService, ITagStorageService
{
    private readonly string _storageRoot;
    private readonly string _dbPath;
    private readonly bool _applicationDirectoryMode;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _passphrase;
    private bool _isAvailable;
    private string? _errorMessage;
    private Action<string>? _failureHook;
    private bool _disposed;

    public bool IsAvailable => _isAvailable;
    public string? ErrorMessage => _errorMessage;
    public string StorageRoot => _storageRoot;
    public string DatabasePath => _dbPath;
    public bool IsEncrypted => IsDatabaseEncrypted(_dbPath);
    public string? CurrentPassphrase => _passphrase;

    public void SetCurrentPassphrase(string? passphrase)
    {
        _passphrase = passphrase;
    }

    private readonly string? _legacyJsonPathOverride;

    public StorageService(string? customStorageRoot = null, bool autoInit = true, string? passphrase = null, string? legacyJsonPathOverride = null)
    {
        _legacyJsonPathOverride = legacyJsonPathOverride;
        _applicationDirectoryMode = customStorageRoot is null;
        _storageRoot = customStorageRoot ?? StoragePathHelper.GetDefaultStorageRoot();
        _passphrase = passphrase;
        try
        {
            StoragePathHelper.EnsureStorageDirectories(_storageRoot, _applicationDirectoryMode);
            _dbPath = Path.Combine(_storageRoot, StoragePathHelper.DatabaseFileName);

            if (autoInit)
            {
                InitializeDatabase();
            }
            _isAvailable = true;
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _errorMessage = FormatStorageError("DB_INIT", ex);
            _dbPath = Path.Combine(_storageRoot, StoragePathHelper.DatabaseFileName);
        }
    }

    public void SetFailureInjectionHook(Action<string>? hook)
    {
        _failureHook = hook;
    }

    public static bool IsDatabaseEncrypted(string dbPath)
    {
        if (!File.Exists(dbPath)) return false;
        try
        {
            using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 16) return false;
            byte[] header = new byte[16];
            int read = fs.Read(header, 0, 16);
            if (read < 16) return false;
            string headerStr = Encoding.ASCII.GetString(header);
            return !headerStr.StartsWith("SQLite format 3\0", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public readonly record struct StartupSecurityResolution(
        bool RequiresUnlock,
        bool IsDatabaseFilePresent,
        bool IsDatabaseEncrypted,
        bool WasStaleSettingsReset);

    /// <summary>
    /// Coordinates startup password unlock requirements using the actual SQLCipher header of contacts.sqlite3
    /// as the single source of truth (RS-ISSUE-0051 / DBA-ISSUE-0051).
    /// If contacts.sqlite3 does not exist or is not encrypted, no password unlock is required and any stale
    /// IsPasswordProtected flag in settings.json is automatically reset to false.
    /// </summary>
    public static StartupSecurityResolution ResolveStartupSecurityState(string dbPath, IAppSettingsService? settingsService)
    {
        bool fileExists = File.Exists(dbPath);
        bool isEncrypted = fileExists && IsDatabaseEncrypted(dbPath);
        bool staleReset = false;

        if (settingsService != null)
        {
            var settings = settingsService.LoadSettings();
            if (!isEncrypted && settings.IsPasswordProtected)
            {
                settingsService.UpdateSecuritySettings(false, null, null);
                staleReset = true;
            }
        }

        return new StartupSecurityResolution(
            RequiresUnlock: isEncrypted,
            IsDatabaseFilePresent: fileExists,
            IsDatabaseEncrypted: isEncrypted,
            WasStaleSettingsReset: staleReset);
    }

    public static bool TryVerifyPassphrase(string dbPath, string? passphrase, out string? error)
    {
        error = null;
        if (!File.Exists(dbPath)) return true;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            if (!string.IsNullOrEmpty(passphrase))
            {
                builder.Password = passphrase;
            }
            using var conn = new SqliteConnection(builder.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private string BuildConnectionString(string dbPath, bool readOnly = false, string? customPassphrase = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
        };
        string? effectivePass = customPassphrase ?? _passphrase;
        if (!string.IsNullOrEmpty(effectivePass))
        {
            builder.Password = effectivePass;
        }
        return builder.ConnectionString;
    }

    public void InitializeDatabase()
    {
        StoragePathHelper.VerifyPathSafety(_storageRoot, _dbPath, checkReparse: true);

        int userVersion = 0;
        int tableCount = 0;
        bool fileExisted = File.Exists(_dbPath);

        if (fileExisted)
        {
            // Gate check: open read-only first to inspect schema version without altering bytes
            string roConnStr = BuildConnectionString(_dbPath, readOnly: true);
            using (var checkConn = new SqliteConnection(roConnStr))
            {
                PreparePrivateDatabaseFiles();
                checkConn.Open();
                using (var cmd = checkConn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA user_version;";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        userVersion = Convert.ToInt32(reader[0]);
                    }
                }

                if (userVersion != 0 && userVersion != 1 && userVersion != 2)
                {
                    throw new InvalidOperationException($"未支援之資料庫版本 (user_version={userVersion})，支援版本 1 與 2。");
                }

                using (var cmd = checkConn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                    tableCount = Convert.ToInt32(cmd.ExecuteScalar());
                }

                if (userVersion == 0 && tableCount > 0)
                {
                    // Automatic schema identification for databases where user_version was not preserved during export
                    bool isKnownSchema = false;
                    string[] requiredTables = { "contact_records", "contact_values", "card_image_assets", "pending_delete" };
                    bool allExist = true;
                    foreach (var tbl in requiredTables)
                    {
                        using var cmd = checkConn.CreateCommand();
                        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = @tbl;";
                        cmd.Parameters.AddWithValue("@tbl", tbl);
                        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                        {
                            allExist = false;
                            break;
                        }
                    }

                    if (allExist)
                    {
                        try
                        {
                            ValidateRequiredColumns(checkConn);
                            bool hasTagCatalog = false;
                            using (var cmd = checkConn.CreateCommand())
                            {
                                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = 'contact_tag_catalog';";
                                hasTagCatalog = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                            }
                            userVersion = hasTagCatalog ? 2 : 1;
                            isKnownSchema = true;
                        }
                        catch
                        {
                            isKnownSchema = false;
                        }
                    }

                    if (!isKnownSchema)
                    {
                        throw new InvalidOperationException("未知的 Schema 0 資料庫 (存在未識別之資料表)，為保護資料拒絕寫入。");
                    }
                }

                if (userVersion == 1 || userVersion == 2)
                {
                    // Verify quick_check and foreign_key_check in read-only mode before opening RW WAL
                    using (var cmd = checkConn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA quick_check;";
                        string checkResult = Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
                        if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"資料庫完整性校驗失敗: {checkResult}");
                        }
                    }

                    using (var cmd = checkConn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA foreign_key_check;";
                        using var fkReader = cmd.ExecuteReader();
                        if (fkReader.Read())
                        {
                            throw new InvalidOperationException("資料庫外鍵關聯完整性校驗失敗。");
                        }
                    }

                    // Verify required tables exist
                    string[] requiredTables = { "contact_records", "contact_values", "card_image_assets", "pending_delete" };
                    foreach (var tbl in requiredTables)
                    {
                        using var cmd = checkConn.CreateCommand();
                        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = @tbl;";
                        cmd.Parameters.AddWithValue("@tbl", tbl);
                        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                        {
                            throw new InvalidOperationException($"資料庫缺少必要資料表「{tbl}」。");
                        }
                    }

                    ValidateRequiredColumns(checkConn);
                    if (userVersion == 2) ValidateTagSchema(checkConn);
                    // Read-only integrity verified; now configure WAL and ensure user_version on RW connection
                    string rwConnStr = BuildConnectionString(_dbPath, readOnly: false);
                    using (var conn = new SqliteConnection(rwConnStr))
                    {
                        PreparePrivateDatabaseFiles();
                        conn.Open();
                        using (var pragmaCmd = conn.CreateCommand())
                        {
                            pragmaCmd.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA user_version = {userVersion};";
                            pragmaCmd.ExecuteNonQuery();
                        }
                    }
                    if (userVersion == 1) UpgradeTagSchema();
                    using (var customConn = new SqliteConnection(rwConnStr))
                    {
                        customConn.Open();
                        EnsureCustomFieldsSchema(customConn);
                    }
                    return;
                }
            }
        }

        // Fresh database or empty file (userVersion == 0 && tableCount == 0): apply schema 1 inside transaction
        string freshConnStr = BuildConnectionString(_dbPath, readOnly: false);
        using (var conn = new SqliteConnection(freshConnStr))
        {
            PreparePrivateDatabaseFiles();
            conn.Open();
            // PRAGMAs executed before transaction
            using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS contact_records (
                        contact_id TEXT PRIMARY KEY,
                        contract_version TEXT NOT NULL,
                        revision INTEGER NOT NULL CHECK (revision >= 1),
                        persistence_state TEXT NOT NULL CHECK (persistence_state IN ('PendingFinalize', 'Ready', 'Failed', 'ImageMissing')),
                        completion_source TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        completed_at TEXT,
                        save_operation_id TEXT NOT NULL,
                        content_fingerprint TEXT NOT NULL,
                        pending_delete INTEGER NOT NULL DEFAULT 0 CHECK (pending_delete IN (0, 1))
                    );

                    CREATE INDEX IF NOT EXISTS idx_contact_records_state_updated ON contact_records (persistence_state, updated_at DESC);
                    CREATE INDEX IF NOT EXISTS idx_contact_records_pending_delete ON contact_records (pending_delete);
                    CREATE INDEX IF NOT EXISTS idx_contact_records_save_op ON contact_records (save_operation_id);

                    CREATE TABLE IF NOT EXISTS contact_values (
                        value_id TEXT PRIMARY KEY,
                        contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
                        value_kind TEXT NOT NULL,
                        custom_field_definition_id TEXT,
                        custom_field_name_snapshot TEXT,
                        raw_value TEXT NOT NULL,
                        normalized_value TEXT,
                        label TEXT NOT NULL,
                        ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
                        source_mode TEXT NOT NULL,
                        locked_at TEXT NOT NULL,
                        UNIQUE (contact_id, ordinal)
                    );

                    CREATE INDEX IF NOT EXISTS idx_contact_values_contact ON contact_values (contact_id);
                    CREATE INDEX IF NOT EXISTS idx_contact_values_kind ON contact_values (value_kind);
                    CREATE INDEX IF NOT EXISTS idx_contact_values_search ON contact_values (value_kind, raw_value);

                    CREATE TABLE IF NOT EXISTS card_image_assets (
                        asset_id TEXT PRIMARY KEY,
                        contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
                        capture_source TEXT NOT NULL,
                        storage_state TEXT NOT NULL CHECK (storage_state IN ('PendingFinalize', 'Ready', 'Failed', 'ImageMissing', 'PendingDelete')),
                        relative_path TEXT NOT NULL UNIQUE,
                        staging_relative_path TEXT,
                        sha256 TEXT NOT NULL,
                        mime_type TEXT NOT NULL,
                        byte_count INTEGER NOT NULL CHECK (byte_count > 0),
                        width INTEGER NOT NULL CHECK (width > 0),
                        height INTEGER NOT NULL CHECK (height > 0),
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_card_image_assets_contact ON card_image_assets (contact_id);

                    CREATE TABLE IF NOT EXISTS pending_delete (
                        job_id TEXT PRIMARY KEY,
                        contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE UNIQUE,
                        asset_relative_path TEXT,
                        created_at TEXT NOT NULL,
                        status TEXT NOT NULL CHECK (status IN ('Pending', 'Processing', 'Failed')),
                        retry_count INTEGER NOT NULL DEFAULT 0,
                        last_error TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_pending_delete_status ON pending_delete (status);

                    PRAGMA user_version = 1;
                ";
                cmd.ExecuteNonQuery();
            }
            ApplyTagSchema(conn, tx);
            EnsureCustomFieldsSchema(conn, tx);
            tx.Commit();

            // Verify with quick_check after schema creation
            using (var verifyCmd = conn.CreateCommand())
            {
                verifyCmd.CommandText = "PRAGMA quick_check;";
                using var reader = verifyCmd.ExecuteReader();
                if (reader.Read())
                {
                    string check = reader.GetString(0);
                    if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"初始化資料庫後完整性校驗失敗: {check}");
                    }
                }
            }
        }
    }

    private void PreparePrivateDatabaseFiles()
    {
        if (_applicationDirectoryMode) StoragePathHelper.EnsurePrivateDatabaseFiles(_storageRoot);
    }

    private SqliteConnection OpenConnection(bool readOnly = false)
    {
        StoragePathHelper.VerifyPathSafety(_storageRoot, _dbPath, checkReparse: true);
        PreparePrivateDatabaseFiles();
        var conn = new SqliteConnection(BuildConnectionString(_dbPath, readOnly));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = readOnly ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public async Task<bool> EnableEncryptionAsync(string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newPassword))
            throw new ArgumentException("密碼不可為空白。", nameof(newPassword));

        await _gate.WaitAsync(ct);
        try
        {
            if (IsDatabaseEncrypted(_dbPath))
                throw new InvalidOperationException("資料庫已經處於加密狀態。");

            string tmpEncPath = _dbPath + ".tmp_enc." + Guid.NewGuid().ToString("N");
            if (File.Exists(_dbPath))
            {
                // Checkpoint WAL first
                using (var chkConn = new SqliteConnection(BuildConnectionString(_dbPath, readOnly: false, customPassphrase: "")))
                {
                    await chkConn.OpenAsync(ct);
                    using var cmdChk = chkConn.CreateCommand();
                    cmdChk.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await cmdChk.ExecuteNonQueryAsync(ct);
                }

                int srcUserVersion = 1;
                // Export to encrypted database
                using (var srcConn = new SqliteConnection(BuildConnectionString(_dbPath, readOnly: false, customPassphrase: "")))
                {
                    await srcConn.OpenAsync(ct);
                    using var verCmd = srcConn.CreateCommand();
                    verCmd.CommandText = "PRAGMA user_version;";
                    srcUserVersion = Convert.ToInt32(await verCmd.ExecuteScalarAsync(ct));

                    using var cmd = srcConn.CreateCommand();
                    string escapedPath = tmpEncPath.Replace("'", "''");
                    string escapedKey = newPassword.Replace("'", "''");
                    cmd.CommandText = $"ATTACH DATABASE '{escapedPath}' AS encrypted KEY '{escapedKey}'; SELECT sqlcipher_export('encrypted'); PRAGMA encrypted.user_version = {srcUserVersion}; DETACH DATABASE encrypted;";
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // Explicitly set user_version on encrypted target DB and verify
                using (var verConn = new SqliteConnection(BuildConnectionString(tmpEncPath, readOnly: false, customPassphrase: newPassword)))
                {
                    await verConn.OpenAsync(ct);
                    using var setVerCmd = verConn.CreateCommand();
                    setVerCmd.CommandText = $"PRAGMA user_version = {srcUserVersion};";
                    await setVerCmd.ExecuteNonQueryAsync(ct);
                }

                // Verify encrypted database
                if (!TryVerifyPassphrase(tmpEncPath, newPassword, out var err))
                {
                    if (File.Exists(tmpEncPath)) File.Delete(tmpEncPath);
                    throw new InvalidOperationException($"加密轉換校驗失敗: {err}");
                }

                // Remove WAL/SHM if present
                string wal = _dbPath + "-wal";
                string shm = _dbPath + "-shm";
                if (File.Exists(wal)) File.Delete(wal);
                if (File.Exists(shm)) File.Delete(shm);

                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();

                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        File.Move(tmpEncPath, _dbPath, overwrite: true);
                        break;
                    }
                    catch when (i < 4)
                    {
                        await Task.Delay(100, ct);
                    }
                }
            }

            _passphrase = newPassword;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newPassword))
            throw new ArgumentException("新密碼不可為空白。", nameof(newPassword));

        await _gate.WaitAsync(ct);
        try
        {
            if (!IsDatabaseEncrypted(_dbPath))
                throw new InvalidOperationException("資料庫尚未啟用加密。");

            using (var conn = new SqliteConnection(BuildConnectionString(_dbPath, readOnly: false, customPassphrase: oldPassword)))
            {
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                string escapedNewKey = newPassword.Replace("'", "''");
                cmd.CommandText = $"PRAGMA rekey = '{escapedNewKey}';";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Verify new password
            if (!TryVerifyPassphrase(_dbPath, newPassword, out var err))
            {
                throw new InvalidOperationException($"變更密碼後校驗失敗: {err}");
            }

            _passphrase = newPassword;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DisableEncryptionAsync(string currentPassword, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!IsDatabaseEncrypted(_dbPath))
                return true;

            string tmpPlainPath = _dbPath + ".tmp_plain." + Guid.NewGuid().ToString("N");

            // Checkpoint WAL first
            using (var chkConn = new SqliteConnection(BuildConnectionString(_dbPath, readOnly: false, customPassphrase: currentPassword)))
            {
                await chkConn.OpenAsync(ct);
                using var cmdChk = chkConn.CreateCommand();
                cmdChk.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await cmdChk.ExecuteNonQueryAsync(ct);
            }

            int srcUserVersion = 1;
            // Export to plaintext database
            using (var srcConn = new SqliteConnection(BuildConnectionString(_dbPath, readOnly: false, customPassphrase: currentPassword)))
            {
                await srcConn.OpenAsync(ct);
                using var verCmd = srcConn.CreateCommand();
                verCmd.CommandText = "PRAGMA user_version;";
                srcUserVersion = Convert.ToInt32(await verCmd.ExecuteScalarAsync(ct));

                using var cmd = srcConn.CreateCommand();
                string escapedPath = tmpPlainPath.Replace("'", "''");
                cmd.CommandText = $"ATTACH DATABASE '{escapedPath}' AS plaintext KEY ''; SELECT sqlcipher_export('plaintext'); PRAGMA plaintext.user_version = {srcUserVersion}; DETACH DATABASE plaintext;";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Explicitly set user_version on plaintext target DB and verify
            using (var verConn = new SqliteConnection(BuildConnectionString(tmpPlainPath, readOnly: false, customPassphrase: "")))
            {
                await verConn.OpenAsync(ct);
                using var setVerCmd = verConn.CreateCommand();
                setVerCmd.CommandText = $"PRAGMA user_version = {srcUserVersion};";
                await setVerCmd.ExecuteNonQueryAsync(ct);
            }

            // Verify new plaintext database
            if (!TryVerifyPassphrase(tmpPlainPath, null, out var err))
            {
                if (File.Exists(tmpPlainPath)) File.Delete(tmpPlainPath);
                throw new InvalidOperationException($"解密轉換校驗失敗: {err}");
            }

            // Remove WAL/SHM if present
            string wal = _dbPath + "-wal";
            string shm = _dbPath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);

            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Move(tmpPlainPath, _dbPath, overwrite: true);
                    break;
                }
                catch when (i < 4)
                {
                    await Task.Delay(100, ct);
                }
            }

            _passphrase = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateRequiredColumns(SqliteConnection conn)
    {
        var tables = new Dictionary<string, string[]>
        {
            ["contact_records"] = new[] { "contact_id", "contract_version", "revision", "persistence_state", "completion_source", "created_at", "updated_at", "completed_at", "save_operation_id", "content_fingerprint", "pending_delete" },
            ["contact_values"] = new[] { "value_id", "contact_id", "value_kind", "custom_field_definition_id", "custom_field_name_snapshot", "raw_value", "normalized_value", "label", "ordinal", "source_mode", "locked_at" },
            ["card_image_assets"] = new[] { "asset_id", "contact_id", "capture_source", "storage_state", "relative_path", "staging_relative_path", "sha256", "mime_type", "byte_count", "width", "height", "created_at", "updated_at" },
            ["pending_delete"] = new[] { "job_id", "contact_id", "asset_relative_path", "created_at", "status", "retry_count", "last_error" }
        };
        foreach (var table in tables)
        {
            using var cmd = conn.CreateCommand();
            // Table names come exclusively from the constants above.
            cmd.CommandText = $"PRAGMA table_info('{table.Key}');";
            using var reader = cmd.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) columns.Add(reader.GetString(1));
            foreach (string column in table.Value)
                if (!columns.Contains(column)) throw new InvalidOperationException("SCHEMA_COLUMNS_UNSUPPORTED");
        }
    }

    public static bool TryParseImageDimensions(byte[] bytes, string mimeType, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes == null || bytes.Length < 16) return false;

        if (mimeType == "image/png" && bytes.Length >= 24)
        {
            if (bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')
            {
                width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return width > 0 && height > 0;
            }
        }
        else if (mimeType == "image/bmp" && bytes.Length >= 26)
        {
            if (bytes[0] == 0x42 && bytes[1] == 0x4D)
            {
                int dibHeaderSize = BitConverter.ToInt32(bytes, 14);
                if (dibHeaderSize >= 12 && bytes.Length >= 14 + dibHeaderSize)
                {
                    if (dibHeaderSize == 12)
                    {
                        width = BitConverter.ToInt16(bytes, 18);
                        height = Math.Abs((int)BitConverter.ToInt16(bytes, 20));
                    }
                    else
                    {
                        width = BitConverter.ToInt32(bytes, 18);
                        height = Math.Abs(BitConverter.ToInt32(bytes, 22));
                    }
                    return width > 0 && height > 0;
                }
            }
        }
        else if (mimeType == "image/jpeg" && bytes.Length >= 4)
        {
            int i = 2;
            while (i < bytes.Length - 1)
            {
                if (bytes[i] != 0xFF) { i++; continue; }
                byte marker = bytes[i + 1];
                i += 2;
                if (marker == 0xD9 || marker == 0xDA) break;
                if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;

                if (i + 2 > bytes.Length) break;
                int len = (bytes[i] << 8) | bytes[i + 1];
                if (marker >= 0xC0 && marker <= 0xC3)
                {
                    if (i + 7 <= bytes.Length)
                    {
                        height = (bytes[i + 3] << 8) | bytes[i + 4];
                        width = (bytes[i + 5] << 8) | bytes[i + 6];
                        return width > 0 && height > 0;
                    }
                }
                i += len;
            }
        }

        return false;
    }

    public static async Task<(bool valid, int width, int height, string mimeType, string? error)> ValidateImageAsync(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return (false, 0, 0, string.Empty, "圖片資料為空。");
        }

        string mimeType = ImageService.DetectMimeType(bytes);
        if (string.IsNullOrEmpty(mimeType))
        {
            return (false, 0, 0, string.Empty, "檔案非支援的有效圖片格式 (僅支援真實 PNG、JPEG 或 BMP 內容)。");
        }

        int parsedWidth = 0, parsedHeight = 0;
        bool parsed = TryParseImageDimensions(bytes, mimeType, out parsedWidth, out parsedHeight);
        if (!parsed || parsedWidth <= 0 || parsedHeight <= 0 || parsedWidth > ImageService.MaxDimension || parsedHeight > ImageService.MaxDimension)
        {
            return (false, 0, 0, mimeType, $"圖片資料無效或尺寸超出上限 ({parsedWidth}x{parsedHeight})。");
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            int wicWidth = (int)decoder.PixelWidth;
            int wicHeight = (int)decoder.PixelHeight;

            if (wicWidth != parsedWidth || wicHeight != parsedHeight)
            {
                return (false, 0, 0, mimeType, $"WIC 解碼尺寸 ({wicWidth}x{wicHeight}) 與圖檔標頭尺寸 ({parsedWidth}x{parsedHeight}) 不符。");
            }

            var pixelDataProvider = await decoder.GetPixelDataAsync();
            byte[] pixels = pixelDataProvider.DetachPixelData();
            if (pixels == null || pixels.Length == 0)
            {
                return (false, 0, 0, mimeType, "WIC 解碼失敗: 無法取得像素資料。");
            }

            return (true, parsedWidth, parsedHeight, mimeType, null);
        }
        catch (Exception ex)
        {
            return (false, 0, 0, mimeType, $"圖片資料損毀，無法解碼（IMAGE_DECODE/{ex.GetType().Name}/0x{ex.HResult:X8}）。");
        }
    }

    public static string ComputeStableAssetId(string contactId, string saveOperationId)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{contactId}:{saveOperationId}");
        byte[] hash = MD5.HashData(bytes);
        return new Guid(hash).ToString("N");
    }

    private static CardImageAssetRecord? ReadStoredAsset(SqliteConnection conn, string contactId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT asset_id, relative_path, staging_relative_path, sha256, mime_type, byte_count, width, height, storage_state FROM card_image_assets WHERE contact_id = @id;";
        cmd.Parameters.AddWithValue("@id", contactId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var asset = new CardImageAssetRecord
        {
            AssetId = reader.GetString(0), ContactId = contactId,
            RelativePath = reader.GetString(1), StagingRelativePath = reader.IsDBNull(2) ? null : reader.GetString(2),
            Sha256 = reader.GetString(3), MimeType = reader.GetString(4), ByteCount = reader.GetInt64(5),
            Width = reader.GetInt32(6), Height = reader.GetInt32(7), StorageState = reader.GetString(8)
        };
        if (reader.Read()) throw new SecurityException("AMBIGUOUS_CONTACT_ASSET");
        return asset;
    }

    private string ResolveOwnedAssetPath(CardImageAssetRecord asset, string relativePath, bool staging)
    {
        string fullPath = StoragePathHelper.ResolveSafePath(_storageRoot, relativePath);
        string folder = staging ? StoragePathHelper.PendingDirName : StoragePathHelper.CardImagesDirName;
        if (!StoragePathHelper.IsOwnedAssetPath(_storageRoot, fullPath, out _, asset.AssetId) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(fullPath)), folder, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("ASSET_PATH_NOT_OWNED");
        return fullPath;
    }

    private async Task<byte[]?> ReadVerifiedAssetBytesAsync(CardImageAssetRecord asset, string relativePath, bool staging, CancellationToken ct)
    {
        string fullPath = ResolveOwnedAssetPath(asset, relativePath, staging);
        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length != asset.ByteCount) return null;
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, ct);
        if (bytes.LongLength != asset.ByteCount ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
        var image = await ValidateImageAsync(bytes);
        return image.valid && image.width == asset.Width && image.height == asset.Height &&
            image.mimeType.Equals(asset.MimeType, StringComparison.OrdinalIgnoreCase) ? bytes : null;
    }

    private void DeleteOwnedAssetFiles(SqliteConnection conn, string contactId, string? pendingJobPath)
    {
        var asset = ReadStoredAsset(conn, contactId);
        if (asset == null)
        {
            if (pendingJobPath != null) throw new SecurityException("DELETE_ASSET_METADATA_MISSING");
            return;
        }
        if (pendingJobPath != null && !string.Equals(pendingJobPath, asset.RelativePath, StringComparison.Ordinal))
            throw new SecurityException("DELETE_JOB_ASSET_MISMATCH");
        var paths = new List<string> { ResolveOwnedAssetPath(asset, asset.RelativePath, staging: false) };
        if (!string.IsNullOrEmpty(asset.StagingRelativePath))
            paths.Add(ResolveOwnedAssetPath(asset, asset.StagingRelativePath, staging: true));
        // Validate every DB-owned path before removing any file. Never enumerate or recurse.
        foreach (string path in paths)
            if (File.Exists(path)) File.Delete(path);
    }

    public async Task<(bool valid, string? code, string? error)> VerifyStoredAssetAsync(
        SqliteConnection conn,
        string contactId,
        ContactDraftSnapshot? expectedSnapshot = null,
        CancellationToken ct = default)
    {
        string? assetId = null;
        string? storageState = null;
        string? relativePath = null;
        string? sha256 = null;
        string? mimeType = null;
        long byteCount = 0;
        int width = 0;
        int height = 0;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT asset_id, storage_state, relative_path, sha256, mime_type, byte_count, width, height FROM card_image_assets WHERE contact_id = @id;";
            cmd.Parameters.AddWithValue("@id", contactId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                assetId = reader.GetString(0);
                storageState = reader.GetString(1);
                relativePath = reader.GetString(2);
                sha256 = reader.GetString(3);
                mimeType = reader.GetString(4);
                byteCount = reader.GetInt64(5);
                width = reader.GetInt32(6);
                height = reader.GetInt32(7);
            }
        }

        if (string.IsNullOrEmpty(assetId) || string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(sha256))
        {
            return (false, "NO_ASSET_METADATA", "找不到關聯圖檔中繼資料。");
        }

        if (storageState == StorageStates.ImageMissing || storageState == StorageStates.PendingDelete)
        {
            return (false, "IMAGE_MISSING", "名片圖檔已被標記為遺失或待刪除。");
        }

        string fullPath = StoragePathHelper.ResolveSafePath(_storageRoot, relativePath);
        if (!StoragePathHelper.IsOwnedAssetPath(_storageRoot, fullPath, out _, assetId))
        {
            return (false, "SECURITY_VIOLATION", "圖檔非受控資產目錄路徑。");
        }

        if (!File.Exists(fullPath))
        {
            MarkImageMissing(conn, contactId);
            return (false, "IMAGE_MISSING", "磁碟上找不到名片圖檔。");
        }

        byte[] diskBytes = await File.ReadAllBytesAsync(fullPath, ct);
        if (diskBytes.Length != byteCount)
        {
            MarkImageMissing(conn, contactId);
            return (false, "IMAGE_CORRUPTED", "圖檔磁碟大小與中繼資料不符。");
        }

        using (var sha = SHA256.Create())
        {
            string diskHash = Convert.ToHexString(sha.ComputeHash(diskBytes));
            if (!diskHash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                MarkImageMissing(conn, contactId);
                return (false, "IMAGE_CORRUPTED", "圖檔磁碟雜湊校驗不符。");
            }
        }

        var diskImg = await ValidateImageAsync(diskBytes);
        if (!diskImg.valid || diskImg.width != width || diskImg.height != height || !diskImg.mimeType.Equals(mimeType, StringComparison.OrdinalIgnoreCase))
        {
            MarkImageMissing(conn, contactId);
            return (false, "IMAGE_CORRUPTED", "圖檔影像解碼校驗不符。");
        }

        if (expectedSnapshot != null)
        {
            if (!sha256.Equals(expectedSnapshot.ImageSha256, StringComparison.OrdinalIgnoreCase) ||
                byteCount != expectedSnapshot.ImageBytes.Length ||
                width != expectedSnapshot.Width ||
                height != expectedSnapshot.Height ||
                !mimeType.Equals(expectedSnapshot.MimeType, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "IMAGE_MISMATCH", "更新請求中的圖片中繼資料與已儲存的資產不一致。");
            }
        }

        return (true, null, null);
    }

    public async Task<SaveResult> SaveDraftAsync(ContactDraftSnapshot snapshot, CancellationToken ct = default)
    {
        if (!_isAvailable)
        {
            return SaveResult.Failed("DB_UNAVAILABLE", _errorMessage ?? "資料庫目前無法使用。");
        }

        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(async () =>
            {
                var result = await DoSaveDraftAsync(snapshot, ct);
                if (result.Succeeded || result.RequiresSaveAgain)
                {
                    using var metadata = OpenConnection(true);
                    result.PersistedTags = ReadTagState(metadata, null, snapshot.ContactId);
                }
                return result;
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SaveResult> DoSaveDraftAsync(ContactDraftSnapshot snapshot, CancellationToken ct)
    {
        using var conn = OpenConnection();

        // 1. Check existing record for same ContactId
        int existingRev = 0;
        string? existingState = null;
        string? existingFingerprint = null;
        bool existingPendingDelete = false;
        string? existingSaveOpId = null;
        string existingTagFingerprint = "";

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT revision, persistence_state, content_fingerprint, pending_delete, save_operation_id, tag_save_fingerprint FROM contact_records WHERE contact_id = @id;";
            cmd.Parameters.AddWithValue("@id", snapshot.ContactId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                existingRev = reader.GetInt32(0);
                existingState = reader.GetString(1);
                existingFingerprint = reader.GetString(2);
                existingPendingDelete = reader.GetInt32(3) == 1;
                existingSaveOpId = reader.GetString(4);
                existingTagFingerprint = reader.GetString(5);
            }
        }

        // Case A: Record does not exist in DB
        if (existingState == null)
        {
            if (snapshot.ExpectedRevision != 0 || snapshot.Revision != 1)
            {
                return SaveResult.Failed("CONTACT_DELETED", "名片不存在或已被刪除，無法以更新方式儲存。");
            }
            // Proceed to initial 5-phase save
        }
        else
        {
            // Case B: Record exists in DB
            if (existingPendingDelete)
            {
                return SaveResult.Failed("CONTACT_DELETED", "此名片已排入刪除，無法儲存。");
            }

            // Handling PendingFinalize retry / reentrancy
            if (existingState == PersistenceStates.PendingFinalize)
            {
                if (string.Equals(existingSaveOpId, snapshot.SaveOperationId, StringComparison.Ordinal))
                {
                    if (string.Equals(existingFingerprint, snapshot.Fingerprint, StringComparison.Ordinal) &&
                        string.Equals(existingTagFingerprint, TagChoice.Fingerprint(snapshot.Tags), StringComparison.Ordinal))
                    {
                        // Safe retry of identical save operation that was interrupted after PendingFinalize
                        return await ReenterFinalizeAsync(conn, snapshot, ct);
                    }
                    else
                    {
                        // Content was modified (Draft B != Draft A) while previous save was in PendingFinalize!
                        // Complete A to Ready so DB is consistent, but return that B must be saved again.
                        var reenterResult = await ReenterFinalizeAsync(conn, snapshot, ct);
                        if (!reenterResult.Succeeded)
                        {
                            return reenterResult;
                        }

                        return SaveResult.Failed(
                            "PENDING_CONTENT_MODIFIED",
                            "先前的儲存作業已完成定稿，但內容已被修改，請再次儲存新修訂內容。",
                            persistedRevision: reenterResult.PersistedRevision,
                            persistedFingerprint: reenterResult.PersistedFingerprint,
                            requiresSaveAgain: true);
                    }
                }
                else
                {
                    return SaveResult.Failed("PENDING_FINALIZE_IN_PROGRESS", "此名片尚有未完成的儲存作業正在處理中，請稍後重試。");
                }
            }

            if (existingState == PersistenceStates.Failed)
            {
                return SaveResult.Failed("PERSISTENCE_FAILED", "此名片先前儲存狀態異常，請開啟新工作階段重新儲存。");
            }

            if (existingState == PersistenceStates.ImageMissing)
            {
                return SaveResult.Failed("IMAGE_MISSING", "此名片圖片已遺失，無法以更新方式儲存。請建立新名片工作階段。");
            }

            if (existingState == PersistenceStates.Ready)
            {
                if (string.Equals(existingFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
                {
                    // Duplicate save: verify DB and asset on disk before confirming duplicate success
                    var dupCheck = await VerifyStoredAssetAsync(conn, snapshot.ContactId, snapshot, ct);
                    if (!dupCheck.valid)
                    {
                        return SaveResult.Failed(dupCheck.code ?? "IMAGE_MISSING", dupCheck.error ?? "名片圖片已遺失或損毀，無法重複儲存。");
                    }
                    using var tagTx = conn.BeginTransaction();
                    try
                    {
                        if (snapshot.WriteTags) ReplaceTags(conn, tagTx, snapshot.ContactId, snapshot.ExpectedTagRevision, snapshot.Tags, false);
                        tagTx.Commit();
                    }
                    catch (Exception ex) { return SaveResult.Failed("TAG_CONFLICT", TagError(ex)); }
                    return SaveResult.Success(snapshot.ContactId, persistedRevision: existingRev, persistedFingerprint: existingFingerprint, isDuplicate: true);
                }

                // Revision CAS Update: strict caller expected revision check
                if (snapshot.ExpectedRevision != existingRev || snapshot.Revision != existingRev + 1)
                {
                    return SaveResult.Failed("REVISION_CONFLICT", $"名片版本衝突 (目前版本為 {existingRev}，請求預期版本為 {snapshot.ExpectedRevision}，新修訂版本為 {snapshot.Revision})。");
                }

                // Verify existing image asset on disk before revision update
                var revCheck = await VerifyStoredAssetAsync(conn, snapshot.ContactId, snapshot, ct);
                if (!revCheck.valid)
                {
                    return SaveResult.Failed(revCheck.code ?? "IMAGE_MISSING", revCheck.error ?? "名片圖片已遺失或損毀，無法更新版本。");
                }

                // Execute revision CAS update in a single transaction
                using var tx = conn.BeginTransaction();
                try
                {
                    using (var delCmd = conn.CreateCommand())
                    {
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM contact_values WHERE contact_id = @id;";
                        delCmd.Parameters.AddWithValue("@id", snapshot.ContactId);
                        delCmd.ExecuteNonQuery();
                    }

                    InsertContactValues(conn, tx, snapshot.ContactId, snapshot.Values);

                    string nowIso = DateTimeOffset.UtcNow.ToString("o");
                    using (var updateCmd = conn.CreateCommand())
                    {
                        updateCmd.Transaction = tx;
                        updateCmd.CommandText = @"
                            UPDATE contact_records
                            SET revision = @new_rev,
                                updated_at = @updated_at,
                                completion_source = @comp_source,
                                content_fingerprint = @fingerprint,
                                save_operation_id = @save_op
                            WHERE contact_id = @id AND revision = @expected_rev AND pending_delete = 0 AND persistence_state = 'Ready';
                        ";
                        updateCmd.Parameters.AddWithValue("@new_rev", snapshot.Revision);
                        updateCmd.Parameters.AddWithValue("@updated_at", nowIso);
                        updateCmd.Parameters.AddWithValue("@comp_source", snapshot.CompletionSource);
                        updateCmd.Parameters.AddWithValue("@fingerprint", snapshot.Fingerprint);
                        updateCmd.Parameters.AddWithValue("@save_op", snapshot.SaveOperationId);
                        updateCmd.Parameters.AddWithValue("@id", snapshot.ContactId);
                        updateCmd.Parameters.AddWithValue("@expected_rev", existingRev);

                        int rows = updateCmd.ExecuteNonQuery();
                        if (rows != 1)
                        {
                            tx.Rollback();
                            return SaveResult.Failed("REVISION_CONFLICT", "名片版本更新衝突。");
                        }
                    }

                    if (snapshot.WriteTags) ReplaceTags(conn, tx, snapshot.ContactId, snapshot.ExpectedTagRevision, snapshot.Tags, false);
                    tx.Commit();
                    return SaveResult.Success(snapshot.ContactId, persistedRevision: snapshot.Revision, persistedFingerprint: snapshot.Fingerprint, isDuplicate: false);
                }
                catch (Exception ex)
                {
                    tx.Rollback();
                    return SaveResult.Failed("REVISION_UPDATE_FAILED", FormatStorageError("REVISION_UPDATE_FAILED", ex));
                }
            }
        }

        // 2. Initial Save: 5-phase commit
        // Pre-validate image bytes and dimensions before disk write
        var imgValidation = await ValidateImageAsync(snapshot.ImageBytes);
        if (!imgValidation.valid)
        {
            return SaveResult.Failed("IMAGE_VALIDATION_FAILED", imgValidation.error ?? "圖片資料無效。");
        }
        if (imgValidation.width != snapshot.Width || imgValidation.height != snapshot.Height)
        {
            return SaveResult.Failed("IMAGE_VALIDATION_FAILED", $"圖片實際尺寸 ({imgValidation.width}x{imgValidation.height}) 與中繼資料 ({snapshot.Width}x{snapshot.Height}) 不符。");
        }
        if (!imgValidation.mimeType.Equals(snapshot.MimeType, StringComparison.OrdinalIgnoreCase))
        {
            return SaveResult.Failed("IMAGE_VALIDATION_FAILED", $"圖片實際格式 ({imgValidation.mimeType}) 與中繼資料 ({snapshot.MimeType}) 不符。");
        }

        string assetId = ComputeStableAssetId(snapshot.ContactId, snapshot.SaveOperationId);
        string ext = snapshot.MimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" :
                     (snapshot.MimeType.Equals("image/bmp", StringComparison.OrdinalIgnoreCase) ? ".bmp" : ".png");

        string stagingRel = Path.Combine(StoragePathHelper.PendingDirName, $"{assetId}.tmp");
        string finalRel = Path.Combine(StoragePathHelper.CardImagesDirName, $"{assetId}{ext}");

        string fullStaging = StoragePathHelper.ResolveSafePath(_storageRoot, stagingRel);
        string fullFinal = StoragePathHelper.ResolveSafePath(_storageRoot, finalRel);

        // Phase 1: Staging durable flush to disk
        try
        {
            using (var fs = new FileStream(
                fullStaging,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                await fs.WriteAsync(snapshot.ImageBytes, 0, snapshot.ImageBytes.Length, ct);
                fs.Flush(flushToDisk: true);
            }

            using (var sha = SHA256.Create())
            {
                using var stream = File.OpenRead(fullStaging);
                string computedHash = Convert.ToHexString(sha.ComputeHash(stream));
                if (!computedHash.Equals(snapshot.ImageSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(fullStaging);
                    return SaveResult.Failed("STAGING_HASH_MISMATCH", "暫存圖檔雜湊校驗不符。");
                }
            }
        }
        catch (Exception ex)
        {
            return SaveResult.Failed("STAGING_FLUSH_FAILED", FormatStorageError("STAGING_FLUSH_FAILED", ex));
        }

        _failureHook?.Invoke("AfterStagingFlush");

        // Phase 2: DB PendingFinalize
        string now = DateTimeOffset.UtcNow.ToString("o");
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        INSERT INTO contact_records (
                            contact_id, contract_version, revision, persistence_state,
                            completion_source, created_at, updated_at, completed_at,
                            save_operation_id, content_fingerprint, pending_delete
                        ) VALUES (
                            @id, '0.3', 1, @state,
                            @source, @created, @updated, NULL,
                            @save_op, @fingerprint, 0
                        );
                    ";
                    cmd.Parameters.AddWithValue("@id", snapshot.ContactId);
                    cmd.Parameters.AddWithValue("@state", PersistenceStates.PendingFinalize);
                    cmd.Parameters.AddWithValue("@source", snapshot.CompletionSource);
                    cmd.Parameters.AddWithValue("@created", now);
                    cmd.Parameters.AddWithValue("@updated", now);
                    cmd.Parameters.AddWithValue("@save_op", snapshot.SaveOperationId);
                    cmd.Parameters.AddWithValue("@fingerprint", snapshot.Fingerprint);
                    cmd.ExecuteNonQuery();
                }

                InsertContactValues(conn, tx, snapshot.ContactId, snapshot.Values);
                ReplaceTags(conn, tx, snapshot.ContactId, 0, snapshot.Tags, true);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        INSERT INTO card_image_assets (
                            asset_id, contact_id, capture_source, storage_state,
                            relative_path, staging_relative_path, sha256, mime_type,
                            byte_count, width, height, created_at, updated_at
                        ) VALUES (
                            @asset_id, @contact_id, @capture_source, @storage_state,
                            @relative_path, @staging_path, @sha256, @mime,
                            @bytes, @w, @h, @created, @updated
                        );
                    ";
                    cmd.Parameters.AddWithValue("@asset_id", assetId);
                    cmd.Parameters.AddWithValue("@contact_id", snapshot.ContactId);
                    cmd.Parameters.AddWithValue("@capture_source", snapshot.CaptureSource);
                    cmd.Parameters.AddWithValue("@storage_state", StorageStates.PendingFinalize);
                    cmd.Parameters.AddWithValue("@relative_path", finalRel);
                    cmd.Parameters.AddWithValue("@staging_path", stagingRel);
                    cmd.Parameters.AddWithValue("@sha256", snapshot.ImageSha256);
                    cmd.Parameters.AddWithValue("@mime", snapshot.MimeType);
                    cmd.Parameters.AddWithValue("@bytes", snapshot.ImageBytes.Length);
                    cmd.Parameters.AddWithValue("@w", snapshot.Width);
                    cmd.Parameters.AddWithValue("@h", snapshot.Height);
                    cmd.Parameters.AddWithValue("@created", now);
                    cmd.Parameters.AddWithValue("@updated", now);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return SaveResult.Failed("DB_PENDING_FAILED", FormatStorageError("DB_PENDING_FAILED", ex));
            }
        }

        _failureHook?.Invoke("AfterPendingFinalizeTransaction");

        // Phase 3: Same-volume file rename
        try
        {
            if (File.Exists(fullFinal)) File.Delete(fullFinal);
            File.Move(fullStaging, fullFinal);
        }
        catch (Exception ex)
        {
            return SaveResult.Failed("FILE_RENAME_FAILED", FormatStorageError("FILE_RENAME_FAILED", ex));
        }

        _failureHook?.Invoke("AfterFinalRename");

        // Phase 4: Final verification on disk bytes
        try
        {
            if (!File.Exists(fullFinal))
            {
                return SaveResult.Failed("FINAL_VERIFY_NOT_FOUND", "定稿圖檔不存在。");
            }

            byte[] finalBytes = await File.ReadAllBytesAsync(fullFinal, ct);
            if (finalBytes.Length != snapshot.ImageBytes.Length)
            {
                return SaveResult.Failed("FINAL_VERIFY_FAILED", "定稿圖檔大小與預期不符。");
            }

            using var sha = SHA256.Create();
            string finalHash = Convert.ToHexString(sha.ComputeHash(finalBytes));
            if (!finalHash.Equals(snapshot.ImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                return SaveResult.Failed("FINAL_HASH_MISMATCH", "定稿圖檔雜湊校驗不符。");
            }

            var finalImgCheck = await ValidateImageAsync(finalBytes);
            if (!finalImgCheck.valid || finalImgCheck.width != snapshot.Width || finalImgCheck.height != snapshot.Height)
            {
                return SaveResult.Failed("FINAL_VERIFY_FAILED", "定稿圖檔影像解碼校驗不符。");
            }
        }
        catch (Exception ex)
        {
            return SaveResult.Failed("FINAL_VERIFY_FAILED", FormatStorageError("FINAL_VERIFY_FAILED", ex));
        }

        _failureHook?.Invoke("AfterFinalVerification");

        // Phase 5: DB Ready transaction
        // Initial save and recovery share revision/state/asset checks. A pending
        // delete or another state change cannot be promoted back to Ready.
        var finalized = await FinalizePendingAsync(conn, snapshot.ContactId, ct);
        if (!finalized.Succeeded) return finalized;

        _failureHook?.Invoke("AfterReadyTransaction");
        return finalized;
    }

    private Task<SaveResult> ReenterFinalizeAsync(SqliteConnection conn, ContactDraftSnapshot snapshot, CancellationToken ct) =>
        FinalizePendingAsync(conn, snapshot.ContactId, ct);

    private async Task<SaveResult> FinalizePendingAsync(SqliteConnection conn, string contactId, CancellationToken ct)
    {
        int revision;
        string fingerprint;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT revision, content_fingerprint, persistence_state, pending_delete FROM contact_records WHERE contact_id = @id;";
            cmd.Parameters.AddWithValue("@id", contactId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(3) != 0)
                return SaveResult.Failed("CONTACT_DELETED", "名片已刪除或正在刪除，不能定稿。");
            if (reader.GetString(2) != PersistenceStates.PendingFinalize)
                return SaveResult.Failed("FINALIZE_STATE_CHANGED", "名片狀態已變更，請重新查詢。");
            revision = reader.GetInt32(0);
            fingerprint = reader.GetString(1);
        }
        var asset = ReadStoredAsset(conn, contactId);
        if (asset == null) return SaveResult.Failed("NO_ASSET_METADATA", "圖片資料不存在，文字仍保留。");
        if (asset.StorageState != StorageStates.PendingFinalize)
            return SaveResult.Failed("FINALIZE_STATE_CHANGED", "圖片狀態已變更，不能定稿。");

        var finalBytes = await ReadVerifiedAssetBytesAsync(asset, asset.RelativePath, false, ct);
        if (finalBytes == null && !string.IsNullOrEmpty(asset.StagingRelativePath))
        {
            var stagedBytes = await ReadVerifiedAssetBytesAsync(asset, asset.StagingRelativePath, true, ct);
            if (stagedBytes != null)
            {
                File.Move(ResolveOwnedAssetPath(asset, asset.StagingRelativePath, true),
                          ResolveOwnedAssetPath(asset, asset.RelativePath, false), overwrite: true);
                finalBytes = await ReadVerifiedAssetBytesAsync(asset, asset.RelativePath, false, ct);
            }
        }
        if (finalBytes == null)
            return SaveResult.Failed("FINAL_VERIFY_FAILED", "圖片無法完成定稿，文字仍保留。");

        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE contact_records SET persistence_state = 'Ready', completed_at = @now, updated_at = @now
                WHERE contact_id = @id AND pending_delete = 0 AND persistence_state = 'PendingFinalize'
                  AND revision = @rev AND content_fingerprint = @fp;";
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@id", contactId);
            cmd.Parameters.AddWithValue("@rev", revision);
            cmd.Parameters.AddWithValue("@fp", fingerprint);
            if (cmd.ExecuteNonQuery() != 1)
                return SaveResult.Failed("FINALIZE_STATE_CHANGED", "名片狀態已變更，定稿未提交。");
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE card_image_assets SET storage_state = 'Ready', staging_relative_path = NULL, updated_at = @now
                WHERE asset_id = @asset AND contact_id = @id AND storage_state = 'PendingFinalize';";
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@asset", asset.AssetId);
            cmd.Parameters.AddWithValue("@id", contactId);
            if (cmd.ExecuteNonQuery() != 1)
                return SaveResult.Failed("FINALIZE_STATE_CHANGED", "圖片狀態已變更，定稿未提交。");
        }
        tx.Commit();
        return SaveResult.Success(contactId, persistedRevision: revision, persistedFingerprint: fingerprint);
    }

    private static void InsertContactValues(SqliteConnection conn, SqliteTransaction tx, string contactId, IEnumerable<DraftValueSnapshot> values)
    {
        foreach (var v in values)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO contact_values (
                    value_id, contact_id, value_kind, custom_field_definition_id,
                    custom_field_name_snapshot, raw_value, normalized_value, label,
                    ordinal, source_mode, locked_at
                ) VALUES (
                    @value_id, @contact_id, @value_kind, @custom_id,
                    @custom_name, @raw_value, @norm_value, @label,
                    @ordinal, @source_mode, @locked_at
                );
            ";
            cmd.Parameters.AddWithValue("@value_id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@contact_id", contactId);
            cmd.Parameters.AddWithValue("@value_kind", v.ValueKind);
            cmd.Parameters.AddWithValue("@custom_id", (object?)v.CustomFieldDefinitionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@custom_name", (object?)v.CustomFieldNameSnapshot ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@raw_value", v.RawValue);
            cmd.Parameters.AddWithValue("@norm_value", (object?)v.NormalizedValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@label", v.Label);
            cmd.Parameters.AddWithValue("@ordinal", v.Ordinal);
            cmd.Parameters.AddWithValue("@source_mode", v.SourceMode);
            cmd.Parameters.AddWithValue("@locked_at", v.LockedAt.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public async Task<DeleteResult> DeleteContactAsync(string contactId, CancellationToken ct = default)
    {
        if (!_isAvailable)
        {
            return DeleteResult.Failed("DB_UNAVAILABLE", _errorMessage ?? "資料庫目前無法使用。");
        }

        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => DoDeleteContact(contactId), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DeleteResult DoDeleteContact(string contactId)
    {
        using var conn = OpenConnection();

        // 1. Check contact exists
        string? assetRelative = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT relative_path FROM card_image_assets WHERE contact_id = @id;";
            cmd.Parameters.AddWithValue("@id", contactId);
            object? res = cmd.ExecuteScalar();
            if (res != null && res != DBNull.Value) assetRelative = res.ToString();
        }

        if (assetRelative == null)
        {
            // Verify if contact record exists even without asset
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM contact_records WHERE contact_id = @id;";
            cmd.Parameters.AddWithValue("@id", contactId);
            int count = Convert.ToInt32(cmd.ExecuteScalar());
            if (count == 0)
            {
                return DeleteResult.Failed("NOT_FOUND", "找不到指定的名片紀錄。");
            }
        }

        // Phase 1: DB Pending Delete
        string now = DateTimeOffset.UtcNow.ToString("o");
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        INSERT INTO pending_delete (job_id, contact_id, asset_relative_path, created_at, status)
                        VALUES (@job_id, @contact_id, @asset_path, @created_at, 'Pending')
                        ON CONFLICT(contact_id) DO UPDATE SET status = 'Pending', retry_count = retry_count + 1;
                    ";
                    cmd.Parameters.AddWithValue("@job_id", Guid.NewGuid().ToString("N"));
                    cmd.Parameters.AddWithValue("@contact_id", contactId);
                    cmd.Parameters.AddWithValue("@asset_path", (object?)assetRelative ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@created_at", now);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE contact_records SET pending_delete = 1 WHERE contact_id = @id;";
                    cmd.Parameters.AddWithValue("@id", contactId);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE card_image_assets SET storage_state = @pending WHERE contact_id = @id;";
                    cmd.Parameters.AddWithValue("@pending", StorageStates.PendingDelete);
                    cmd.Parameters.AddWithValue("@id", contactId);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return DeleteResult.Failed("DELETE_PENDING_FAILED", FormatStorageError("DELETE_PENDING_FAILED", ex));
            }
        }

        _failureHook?.Invoke("AfterDeletePendingTransaction");

        // Phase 2: Remove only the final/staged files belonging to this DB asset.
        try
        {
            using var jobCmd = conn.CreateCommand();
            jobCmd.CommandText = "SELECT asset_relative_path FROM pending_delete WHERE contact_id = @id;";
            jobCmd.Parameters.AddWithValue("@id", contactId);
            object? jobPath = jobCmd.ExecuteScalar();
            DeleteOwnedAssetFiles(conn, contactId, jobPath == null || jobPath == DBNull.Value ? null : jobPath.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            using var updateJobCmd = conn.CreateCommand();
            updateJobCmd.CommandText = "UPDATE pending_delete SET status = 'Failed', last_error = @err WHERE contact_id = @id;";
            updateJobCmd.Parameters.AddWithValue("@err", FormatStorageError("DELETE_FILE", ex));
            updateJobCmd.Parameters.AddWithValue("@id", contactId);
            updateJobCmd.ExecuteNonQuery();
            if (ex is SecurityException)
                return DeleteResult.Failed("SECURITY_VIOLATION", "圖片資產資訊不一致，刪除未完成。");
            _failureHook?.Invoke("AfterDeleteFileLocked");
            return DeleteResult.Failed("FILE_LOCKED", "圖片檔案目前無法移除，已記錄待刪除，可稍後重試。");
        }

        _failureHook?.Invoke("AfterDeleteFileDeleted");

        // Phase 3: DB cascade cleanup
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM pending_delete WHERE contact_id = @id;";
                    cmd.Parameters.AddWithValue("@id", contactId);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM contact_records WHERE contact_id = @id;";
                    cmd.Parameters.AddWithValue("@id", contactId);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return DeleteResult.Failed("DELETE_CLEANUP_FAILED", FormatStorageError("DELETE_CLEANUP_FAILED", ex));
            }
        }

        _failureHook?.Invoke("AfterDeleteCleanupTransaction");
        return DeleteResult.Success(contactId);
    }

    public Task<PagedResult<ContactSummaryItem>> GetContactsPagedAsync(string? searchQuery, int pageIndex, int pageSize = 30, CancellationToken ct = default) =>
        QueryContactsAsync(new ContactQuery(searchQuery, Array.Empty<string>()), pageIndex, pageSize, ct);

    public async Task<ContactDetailModel?> GetContactDetailAsync(string contactId, CancellationToken ct = default)
    {
        if (!_isAvailable) return null;

        return await Task.Run(async () =>
        {
            using var conn = OpenConnection(readOnly: true);
            ContactDetailModel? detail = null;

            using var detailTx = conn.BeginTransaction(deferred: true);
            // 1. Contact record
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = detailTx;
                cmd.CommandText = "SELECT contact_id, revision, persistence_state, completion_source, created_at, updated_at, completed_at, pending_delete FROM contact_records WHERE contact_id = @id;";
                cmd.Parameters.AddWithValue("@id", contactId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    detail = new ContactDetailModel
                    {
                        ContactId = reader.GetString(0),
                        Revision = reader.GetInt32(1),
                        PersistenceState = reader.GetString(2),
                        CompletionSource = reader.GetString(3),
                        CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                        UpdatedAt = DateTimeOffset.Parse(reader.GetString(5)),
                        CompletedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                        PendingDelete = reader.GetInt32(7) == 1
                    };
                }
            }

            if (detail == null) return null;

            // 2. Values in ordinal order
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = detailTx;
                cmd.CommandText = @"
                    SELECT value_id, contact_id, value_kind, custom_field_definition_id,
                           custom_field_name_snapshot, raw_value, normalized_value, label,
                           ordinal, source_mode, locked_at
                    FROM contact_values
                    WHERE contact_id = @id
                    ORDER BY ordinal ASC;
                ";
                cmd.Parameters.AddWithValue("@id", contactId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var val = new ContactValueRecord
                    {
                        ValueId = reader.GetString(0),
                        ContactId = reader.GetString(1),
                        ValueKind = reader.GetString(2),
                        CustomFieldDefinitionId = reader.IsDBNull(3) ? null : reader.GetString(3),
                        CustomFieldNameSnapshot = reader.IsDBNull(4) ? null : reader.GetString(4),
                        RawValue = reader.GetString(5),
                        NormalizedValue = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Label = reader.GetString(7),
                        Ordinal = reader.GetInt32(8),
                        SourceMode = reader.GetString(9),
                        LockedAt = DateTimeOffset.Parse(reader.GetString(10))
                    };
                    detail.Values.Add(val);

                    if (val.ValueKind == CanonicalValueKind.Name && string.IsNullOrEmpty(detail.Name))
                    {
                        detail.Name = val.RawValue;
                    }
                    else if (val.ValueKind == CanonicalValueKind.Company && string.IsNullOrEmpty(detail.Company))
                    {
                        detail.Company = val.RawValue;
                    }
                }
            }

            // 3. Image asset
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = detailTx;
                cmd.CommandText = @"
                    SELECT asset_id, contact_id, capture_source, storage_state,
                           relative_path, staging_relative_path, sha256, mime_type,
                           byte_count, width, height, created_at, updated_at
                    FROM card_image_assets
                    WHERE contact_id = @id;
                ";
                cmd.Parameters.AddWithValue("@id", contactId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    detail.ImageAsset = new CardImageAssetRecord
                    {
                        AssetId = reader.GetString(0),
                        ContactId = reader.GetString(1),
                        CaptureSource = reader.GetString(2),
                        StorageState = reader.GetString(3),
                        RelativePath = reader.GetString(4),
                        StagingRelativePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Sha256 = reader.GetString(6),
                        MimeType = reader.GetString(7),
                        ByteCount = reader.GetInt64(8),
                        Width = reader.GetInt32(9),
                        Height = reader.GetInt32(10),
                        CreatedAt = DateTimeOffset.Parse(reader.GetString(11)),
                        UpdatedAt = DateTimeOffset.Parse(reader.GetString(12))
                    };
                }
            }

            var tagState = ReadTagState(conn, detailTx, contactId);
            detail.TagRevision = tagState?.Revision ?? 0;
            detail.Tags = tagState?.Tags ?? Array.Empty<TagDefinition>();
            detailTx.Commit();

            // A Ready contact must own exactly one image asset.
            if (detail.PersistenceState == PersistenceStates.Ready && detail.ImageAsset == null)
            {
                detail.PersistenceState = PersistenceStates.ImageMissing;
                MarkImageMissing(contactId);
            }

            // 4. Verify image file if asset exists
            if (detail.ImageAsset != null && detail.PersistenceState == PersistenceStates.Ready &&
                detail.ImageAsset.StorageState == StorageStates.Ready)
            {
                try
                {
                    byte[]? bytes = await ReadVerifiedAssetBytesAsync(detail.ImageAsset, detail.ImageAsset.RelativePath, false, ct);
                    if (bytes != null)
                    {
                        detail.VerifiedImageBytes = bytes;
                    }
                    else
                    {
                        detail.PersistenceState = PersistenceStates.ImageMissing;
                        detail.ImageNotice = "圖片無法使用；已確認文字仍保留";
                        MarkImageMissing(contactId);
                    }
                }
                catch
                {
                    detail.PersistenceState = PersistenceStates.ImageMissing;
                    detail.ImageNotice = "圖片無法使用；已確認文字仍保留";
                    MarkImageMissing(contactId);
                }
            }
            else
            {
                detail.ImageNotice = detail.PersistenceState == PersistenceStates.ImageMissing
                    ? "圖片無法使用；已確認文字仍保留"
                    : "圖片尚未就緒或正在刪除；已確認文字仍保留";
            }

            return detail;
        }, ct);
    }

    public string? GetCardImageAbsolutePath(string contactId)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(contactId)) return null;
        try
        {
            using var conn = OpenConnection(readOnly: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT relative_path FROM card_image_assets WHERE contact_id = @id AND storage_state = 'Ready';";
            cmd.Parameters.AddWithValue("@id", contactId);
            var res = cmd.ExecuteScalar();
            if (res == null || res == DBNull.Value) return null;
            string rel = res.ToString()!;
            if (string.IsNullOrWhiteSpace(rel)) return null;
            string fullPath = StoragePathHelper.ResolveSafePath(_storageRoot, rel, checkReparse: true);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task RecoverPendingOperationsAsync(CancellationToken ct = default)
    {
        if (!_isAvailable) return;
        await _gate.WaitAsync(ct);
        try
        {
            await Task.Run(async () =>
            {
                using var conn = OpenConnection();
                var deleteIds = new List<string>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT contact_id FROM pending_delete;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) deleteIds.Add(reader.GetString(0));
                }
                foreach (string id in deleteIds)
                {
                    ct.ThrowIfCancellationRequested();
                    try { DoDeleteContact(id); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* Leave the owned delete job for retry; continue other contacts. */ }
                }

                var pendingIds = new List<string>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT contact_id FROM contact_records WHERE persistence_state = 'PendingFinalize' AND pending_delete = 0;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) pendingIds.Add(reader.GetString(0));
                }
                foreach (string id in pendingIds)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var result = await FinalizePendingAsync(conn, id, ct);
                        if (!result.Succeeded && result.ErrorCode != "FINALIZE_STATE_CHANGED" && result.ErrorCode != "CONTACT_DELETED")
                            MarkPendingFailed(conn, id);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (SecurityException) { MarkPendingFailed(conn, id); }
                    catch { /* A locked file stays Pending; other contacts can still recover. */ }
                }
            }, ct);
        }
        finally { _gate.Release(); }
    }

    private static void MarkPendingFailed(SqliteConnection conn, string contactId)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"UPDATE contact_records SET persistence_state = 'Failed', updated_at = @now
                WHERE contact_id = @id AND pending_delete = 0 AND persistence_state = 'PendingFinalize';
            UPDATE card_image_assets SET storage_state = 'Failed', updated_at = @now
                WHERE contact_id = @id AND storage_state != 'PendingDelete'
                  AND EXISTS (SELECT 1 FROM contact_records WHERE contact_id = @id AND pending_delete = 0 AND persistence_state = 'Failed');";
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", contactId);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static string? GetAssetRelativePath(SqliteConnection conn, string contactId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT relative_path FROM card_image_assets WHERE contact_id = @id;";
        cmd.Parameters.AddWithValue("@id", contactId);
        object? res = cmd.ExecuteScalar();
        return (res != null && res != DBNull.Value) ? res.ToString() : null;
    }

    private void MarkImageMissing(string contactId)
    {
        try
        {
            using var conn = OpenConnection(readOnly: false);
            MarkImageMissing(conn, contactId);
        }
        catch
        {
            // Best effort state update
        }
    }

    private static void MarkImageMissing(SqliteConnection conn, string contactId)
    {
        try
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE contact_records SET persistence_state = @missing, updated_at = @now 
                WHERE contact_id = @id AND pending_delete = 0;

                UPDATE card_image_assets SET storage_state = @missing, updated_at = @now 
                WHERE contact_id = @id AND storage_state != @pendingDelete
                  AND EXISTS (SELECT 1 FROM contact_records WHERE contact_id = @id AND pending_delete = 0);
            ";
            cmd.Parameters.AddWithValue("@missing", PersistenceStates.ImageMissing);
            cmd.Parameters.AddWithValue("@pendingDelete", StorageStates.PendingDelete);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@id", contactId);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        catch
        {
            // Best effort state update
        }
    }

    public static string SanitizeErrorMessage(string rawMessage)
    {
        return "本機操作未完成，請保留目前資料並稍後重試。（STORAGE_ERROR）";
    }

    public static string FormatStorageError(string code, Exception exception) =>
        $"本機操作未完成，請保留目前資料並稍後重試。（{code}/{exception.GetType().Name}）";

    private static string EscapeLike(string text)
    {
        return text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_isAvailable && File.Exists(_dbPath))
            {
                StoragePathHelper.VerifyPathSafety(_storageRoot, _dbPath, checkReparse: true);
                using var conn = new SqliteConnection(BuildConnectionString(_dbPath, readOnly: false));
                PreparePrivateDatabaseFiles();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // Best effort on dispose
        }
        finally
        {
            _gate.Dispose();
        }
    }
}
