using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MajiOCR_Windows.Services.Storage;

public sealed class StorageCopyReport
{
    public string IssueId { get; set; } = "ISSUE-0014";
    public string Version { get; set; } = Program.GetAppVersion();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string SourceRoot { get; set; } = "";
    public string DestinationRoot { get; set; } = "";
    public int SchemaVersion { get; set; }
    public int AssetCount { get; set; }
    public int VerifiedAssets { get; set; }
    public int PreservedUnavailableAssets { get; set; }
    public int CopiedFiles { get; set; }
    public string DatabaseSHA256 { get; set; } = "";
    public string SourceDatabaseSHA256Before { get; set; } = "";
    public string SourceDatabaseSHA256After { get; set; } = "";
    public bool IntegrityCheckPassed { get; set; }
    public bool SourceFilesDeleted { get; set; }
    public bool Succeeded { get; set; }
}

// An explicit management operation. Ordinary application startup never imports legacy data.
public static class StorageCopyService
{
    public static string GetLegacyRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MajiOCR.Windows", "Storage");

    public static StorageCopyReport Copy(string sourceRoot, string destinationRoot)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("COPY_OVERLAPPING_ROOTS");
        VerifyAncestors(source); VerifyAncestors(destination);
        if (!Directory.Exists(source) || !Directory.Exists(destination))
            throw new InvalidOperationException("COPY_ROOT_MISSING");
        VerifyDestinationEmpty(destination);
        string sourceDb = Path.Combine(source, StoragePathHelper.DatabaseFileName);
        StoragePathHelper.VerifyPathSafety(source, sourceDb);
        if (!File.Exists(sourceDb)) throw new InvalidOperationException("COPY_SOURCE_DB_MISSING");
        var report = new StorageCopyReport { SourceRoot = source, DestinationRoot = destination,
            SourceDatabaseSHA256Before = Hash(sourceDb) };

        // Keep the source connection open throughout the copy. SQLite's backup includes
        // committed WAL pages without changing or checkpointing the source database.
        using var input = new SqliteConnection(Connection(sourceDb, readOnly: true));
        input.Open();
        int schema = Convert.ToInt32(Scalar(input, "PRAGMA user_version;"));
        if (schema != 1 && schema != 2) throw new InvalidOperationException("COPY_SOURCE_SCHEMA_UNSUPPORTED");
        CheckIntegrity(input);
        string stage = Path.Combine(destination, ".storage-copy-" + Guid.NewGuid().ToString("N"));
        StoragePathHelper.EnsureStorageDirectories(stage);
        string stagedDb = Path.Combine(stage, StoragePathHelper.DatabaseFileName);
        StoragePathHelper.EnsurePrivateDatabaseFiles(stage, includeAuxiliary: false);
        using (var output = new SqliteConnection(Connection(stagedDb, readOnly: false)))
        {
            output.Open(); input.BackupDatabase(output);
            CheckIntegrity(output);
            report.SchemaVersion = Convert.ToInt32(Scalar(output, "PRAGMA user_version;"));
        }
        foreach (string folder in new[] { StoragePathHelper.CardImagesDirName, StoragePathHelper.PendingDirName, "schema-backups" })
        {
            string from = Path.Combine(source, folder);
            StoragePathHelper.VerifyPathSafety(source, from);
            string to = Path.Combine(stage, folder);
            StoragePathHelper.EnsurePrivateDataDirectory(stage, to);
            if (!Directory.Exists(from)) continue;
            report.CopiedFiles += CopyDirectory(source, from, stage, to);
        }
        using (var snapshot = new SqliteConnection(Connection(stagedDb, readOnly: true)))
        {
            snapshot.Open(); CheckIntegrity(snapshot); VerifyAssets(snapshot, stage, report);
        }
        report.IntegrityCheckPassed = true;
        report.DatabaseSHA256 = Hash(stagedDb);
        report.SourceDatabaseSHA256After = Hash(sourceDb);
        string marker = Path.Combine(stage, "copy-manifest.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        StoragePathHelper.ProtectDataFile(marker);

        // Do not merge or overwrite a directory that appeared while the source was copied.
        VerifyDestinationEmpty(destination);
        foreach (string folder in new[] { StoragePathHelper.CardImagesDirName, StoragePathHelper.PendingDirName, "schema-backups" })
        {
            string from = Path.Combine(stage, folder);
            string to = Path.Combine(destination, folder);
            StoragePathHelper.VerifyPathSafety(stage, from);
            StoragePathHelper.VerifyPathSafety(destination, to);
            if (Directory.Exists(from)) Directory.Move(from, to);
        }
        string destinationDb = Path.Combine(destination, StoragePathHelper.DatabaseFileName);
        StoragePathHelper.VerifyPathSafety(destination, destinationDb);
        File.Move(stagedDb, destinationDb, overwrite: false); // Publish the database last.
        report.Succeeded = true;
        File.WriteAllText(marker, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }

    private static void VerifyDestinationEmpty(string root)
    {
        foreach (string name in new[] { StoragePathHelper.DatabaseFileName, StoragePathHelper.DatabaseFileName + "-wal",
            StoragePathHelper.DatabaseFileName + "-shm", StoragePathHelper.DatabaseFileName + "-journal",
            StoragePathHelper.CardImagesDirName, StoragePathHelper.PendingDirName, "schema-backups" })
        {
            string path = Path.Combine(root, name);
            StoragePathHelper.VerifyPathSafety(root, path);
            if (File.Exists(path) || Directory.Exists(path)) throw new InvalidOperationException("COPY_DESTINATION_HAS_DATA");
        }
    }
    private static void VerifyAncestors(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (StoragePathHelper.IsReparsePoint(current)) throw new InvalidOperationException("COPY_REPARSE_POINT");
            current = Path.GetDirectoryName(current);
        }
    }
    private static int CopyDirectory(string sourceRoot, string from, string stageRoot, string to)
    {
        int count = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(from))
        {
            StoragePathHelper.VerifyPathSafety(sourceRoot, entry);
            string target = Path.Combine(to, Path.GetFileName(entry));
            StoragePathHelper.VerifyPathSafety(stageRoot, target);
            if (Directory.Exists(entry))
            {
                StoragePathHelper.EnsurePrivateDataDirectory(stageRoot, target);
                count += CopyDirectory(sourceRoot, entry, stageRoot, target);
            }
            else
            {
                File.Copy(entry, target, overwrite: false);
                StoragePathHelper.ProtectDataFile(target);
                // A concurrently changed image must not be silently accepted.
                if (Hash(entry) != Hash(target)) throw new InvalidOperationException("COPY_SOURCE_FILE_CHANGED");
                count++;
            }
        }
        return count;
    }
    private static void VerifyAssets(SqliteConnection database, string root, StorageCopyReport report)
    {
        using var cmd = database.CreateCommand();
        cmd.CommandText = "SELECT asset_id, storage_state, relative_path, staging_relative_path, sha256, byte_count FROM card_image_assets;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            report.AssetCount++;
            string id = reader.GetString(0), state = reader.GetString(1), relative = reader.GetString(2), hash = reader.GetString(4);
            string? staging = reader.IsDBNull(3) ? null : reader.GetString(3);
            long bytes = reader.GetInt64(5);
            bool finalOk = VerifyAssetFile(root, relative, id, hash, bytes);
            bool stagingOk = staging != null && VerifyAssetFile(root, staging, id, hash, bytes);
            if (state == "Ready" && !finalOk || state == "PendingFinalize" && !finalOk && !stagingOk)
                throw new InvalidOperationException("COPY_ASSET_MISSING_OR_CHANGED");
            if (finalOk || stagingOk) report.VerifiedAssets++;
            else report.PreservedUnavailableAssets++;
        }
    }
    private static bool VerifyAssetFile(string root, string relative, string id, string hash, long bytes)
    {
        string path = StoragePathHelper.ResolveSafePath(root, relative);
        if (!StoragePathHelper.IsOwnedAssetPath(root, path, out _, id))
            throw new InvalidOperationException("COPY_ASSET_PATH_INVALID");
        return File.Exists(path) && new FileInfo(path).Length == bytes && Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    private static string Connection(string path, bool readOnly) => new SqliteConnectionStringBuilder {
        DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ConnectionString;
    private static object? Scalar(SqliteConnection conn, string sql)
    { using var cmd = conn.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
    private static void CheckIntegrity(SqliteConnection conn)
    {
        if (!string.Equals(Convert.ToString(Scalar(conn, "PRAGMA integrity_check;")), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("COPY_DB_INTEGRITY_FAILED");
        using var cmd = conn.CreateCommand(); cmd.CommandText = "PRAGMA foreign_key_check;";
        using var rows = cmd.ExecuteReader();
        if (rows.Read()) throw new InvalidOperationException("COPY_DB_FOREIGN_KEY_FAILED");
    }
    public static string Hash(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)); }
}
