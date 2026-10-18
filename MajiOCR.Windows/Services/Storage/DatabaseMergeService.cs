using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services.Storage;

public interface IDatabaseMergeService
{
    Task<DatabaseMergeDiffReport> CompareDatabasesAsync(
        string localStorageRoot,
        string externalStorageRoot,
        string? localPassphrase = null,
        string? externalPassphrase = null,
        CancellationToken ct = default);

    Task<DriveOperationResult> ExecuteMergeAsync(
        string localStorageRoot,
        string externalStorageRoot,
        DatabaseMergeDiffReport diffReport,
        string? localPassphrase = null,
        string? externalPassphrase = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default);
}

public class DatabaseMergeService : IDatabaseMergeService
{
    private record RawContactData(
        string ContactId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        bool PendingDelete,
        string Name,
        string Company,
        string JobTitle,
        string Notes,
        List<(string Kind, string RawValue, string Label)> Values,
        List<string> Tags,
        string? FrontImageRelPath,
        string? FrontImageSha256
    );

    public async Task<DatabaseMergeDiffReport> CompareDatabasesAsync(
        string localStorageRoot,
        string externalStorageRoot,
        string? localPassphrase = null,
        string? externalPassphrase = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            string localDb = Path.Combine(localStorageRoot, StoragePathHelper.DatabaseFileName);
            string remoteDb = Path.Combine(externalStorageRoot, StoragePathHelper.DatabaseFileName);

            var localDict = LoadContactDictionary(localDb, localPassphrase);
            var remoteDict = LoadContactDictionary(remoteDb, externalPassphrase);

            var allKeys = new HashSet<string>(localDict.Keys, StringComparer.OrdinalIgnoreCase);
            allKeys.UnionWith(remoteDict.Keys);

            var report = new DatabaseMergeDiffReport();

            foreach (var key in allKeys)
            {
                ct.ThrowIfCancellationRequested();

                bool inLocal = localDict.TryGetValue(key, out var localContact);
                bool inRemote = remoteDict.TryGetValue(key, out var remoteContact);

                if (inLocal && !inRemote)
                {
                    report.Items.Add(new ContactDiffItem
                    {
                        ContactId = key,
                        Status = ContactDiffStatus.OnlyInLocal,
                        DisplayName = localContact!.Name,
                        Company = localContact.Company,
                        JobTitle = localContact.JobTitle,
                        Phone = ExtractPhone(localContact.Values),
                        LocalUpdatedAt = localContact.UpdatedAt,
                        RemoteUpdatedAt = null,
                        Resolution = MergeResolutionStrategy.KeepLocal
                    });
                }
                else if (!inLocal && inRemote)
                {
                    report.Items.Add(new ContactDiffItem
                    {
                        ContactId = key,
                        Status = ContactDiffStatus.OnlyInRemote,
                        DisplayName = remoteContact!.Name,
                        Company = remoteContact.Company,
                        JobTitle = remoteContact.JobTitle,
                        Phone = ExtractPhone(remoteContact.Values),
                        LocalUpdatedAt = null,
                        RemoteUpdatedAt = remoteContact.UpdatedAt,
                        Resolution = MergeResolutionStrategy.KeepRemote
                    });
                }
                else
                {
                    // 兩者皆有，比對差異
                    var diffs = CompareFields(localContact!, remoteContact!);
                    if (diffs.Count == 0)
                    {
                        report.Items.Add(new ContactDiffItem
                        {
                            ContactId = key,
                            Status = ContactDiffStatus.Identical,
                            DisplayName = localContact!.Name,
                            Company = localContact.Company,
                            JobTitle = localContact.JobTitle,
                            Phone = ExtractPhone(localContact.Values),
                            LocalUpdatedAt = localContact.UpdatedAt,
                            RemoteUpdatedAt = remoteContact!.UpdatedAt,
                            Resolution = MergeResolutionStrategy.KeepNewer
                        });
                    }
                    else
                    {
                        var item = new ContactDiffItem
                        {
                            ContactId = key,
                            Status = ContactDiffStatus.Conflicted,
                            DisplayName = string.IsNullOrEmpty(remoteContact!.Name) ? localContact!.Name : remoteContact.Name,
                            Company = string.IsNullOrEmpty(remoteContact.Company) ? localContact!.Company : remoteContact.Company,
                            JobTitle = string.IsNullOrEmpty(remoteContact.JobTitle) ? localContact!.JobTitle : remoteContact.JobTitle,
                            Phone = ExtractPhone(remoteContact.Values.Count > 0 ? remoteContact.Values : localContact!.Values),
                            LocalUpdatedAt = localContact!.UpdatedAt,
                            RemoteUpdatedAt = remoteContact.UpdatedAt,
                            Differences = diffs,
                            Resolution = MergeResolutionStrategy.KeepNewer
                        };
                        report.Items.Add(item);
                    }
                }
            }

            // 依衝突優先、再依最新更新時間降冪排序
            report.Items = report.Items
                .OrderBy(i => i.Status == ContactDiffStatus.Conflicted ? 0 :
                              i.Status == ContactDiffStatus.OnlyInRemote ? 1 :
                              i.Status == ContactDiffStatus.OnlyInLocal ? 2 : 3)
                .ThenByDescending(i => i.RemoteUpdatedAt ?? i.LocalUpdatedAt ?? DateTimeOffset.MinValue)
                .ToList();

            return report;
        }, ct);
    }

    private static string ExtractPhone(List<(string Kind, string RawValue, string Label)> values)
    {
        var phone = values.FirstOrDefault(v => v.Kind.Equals("phone", StringComparison.OrdinalIgnoreCase) ||
                                              v.Label.Contains("電話") || v.Label.Contains("手機"));
        return phone.RawValue ?? string.Empty;
    }

    private static List<FieldDiffDetail> CompareFields(RawContactData local, RawContactData remote)
    {
        var diffs = new List<FieldDiffDetail>();

        if (!string.Equals(local.Name.Trim(), remote.Name.Trim(), StringComparison.Ordinal))
        {
            diffs.Add(new FieldDiffDetail { FieldName = "姓名", LocalValue = local.Name, RemoteValue = remote.Name });
        }
        if (!string.Equals(local.Company.Trim(), remote.Company.Trim(), StringComparison.Ordinal))
        {
            diffs.Add(new FieldDiffDetail { FieldName = "公司", LocalValue = local.Company, RemoteValue = remote.Company });
        }
        if (!string.Equals(local.JobTitle.Trim(), remote.JobTitle.Trim(), StringComparison.Ordinal))
        {
            diffs.Add(new FieldDiffDetail { FieldName = "職稱", LocalValue = local.JobTitle, RemoteValue = remote.JobTitle });
        }
        if (!string.Equals(local.Notes.Trim(), remote.Notes.Trim(), StringComparison.Ordinal))
        {
            diffs.Add(new FieldDiffDetail { FieldName = "備註", LocalValue = local.Notes, RemoteValue = remote.Notes });
        }

        string localPhone = ExtractPhone(local.Values);
        string remotePhone = ExtractPhone(remote.Values);
        if (!string.Equals(localPhone.Trim(), remotePhone.Trim(), StringComparison.Ordinal))
        {
            diffs.Add(new FieldDiffDetail { FieldName = "電話", LocalValue = localPhone, RemoteValue = remotePhone });
        }

        if (local.PendingDelete != remote.PendingDelete)
        {
            diffs.Add(new FieldDiffDetail
            {
                FieldName = "狀態",
                LocalValue = local.PendingDelete ? "已刪除" : "正常",
                RemoteValue = remote.PendingDelete ? "已刪除" : "正常"
            });
        }

        // 比對標籤
        var localTags = string.Join(",", local.Tags.OrderBy(t => t));
        var remoteTags = string.Join(",", remote.Tags.OrderBy(t => t));
        if (!string.Equals(localTags, remoteTags, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new FieldDiffDetail { FieldName = "分類標籤", LocalValue = localTags, RemoteValue = remoteTags });
        }

        // 比對正面圖檔 SHA-256
        if (!string.Equals(local.FrontImageSha256 ?? "", remote.FrontImageSha256 ?? "", StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new FieldDiffDetail
            {
                FieldName = "名片圖檔",
                LocalValue = string.IsNullOrEmpty(local.FrontImageSha256) ? "無圖檔" : "原圖檔",
                RemoteValue = string.IsNullOrEmpty(remote.FrontImageSha256) ? "無圖檔" : "新圖檔"
            });
        }

        return diffs;
    }

    private static Dictionary<string, RawContactData> LoadContactDictionary(string dbPath, string? passphrase)
    {
        var dict = new Dictionary<string, RawContactData>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(dbPath)) return dict;

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

        // 1. Contact records
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT contact_id, created_at, updated_at, pending_delete
                FROM contact_records;
            ";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string id = reader.GetString(0);
                DateTimeOffset created = DateTimeOffset.TryParse(reader.GetString(1), out var c) ? c : DateTimeOffset.UtcNow;
                DateTimeOffset updated = DateTimeOffset.TryParse(reader.GetString(2), out var u) ? u : created;
                bool pendingDel = reader.GetInt32(3) == 1;

                dict[id] = new RawContactData(
                    ContactId: id,
                    CreatedAt: created,
                    UpdatedAt: updated,
                    PendingDelete: pendingDel,
                    Name: string.Empty,
                    Company: string.Empty,
                    JobTitle: string.Empty,
                    Notes: string.Empty,
                    Values: new List<(string, string, string)>(),
                    Tags: new List<string>(),
                    FrontImageRelPath: null,
                    FrontImageSha256: null
                );
            }
        }

        // 2. Contact values
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT contact_id, value_kind, raw_value, label
                FROM contact_values
                ORDER BY ordinal ASC;
            ";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string contactId = reader.GetString(0);
                if (!dict.TryGetValue(contactId, out var contact)) continue;

                string kind = reader.GetString(1);
                string rawVal = reader.IsDBNull(2) ? "" : reader.GetString(2);
                string label = reader.IsDBNull(3) ? "" : reader.GetString(3);

                contact.Values.Add((kind, rawVal, label));

                if (string.IsNullOrEmpty(contact.Name) && (kind.Equals("name", StringComparison.OrdinalIgnoreCase) || label.Contains("姓名")))
                {
                    dict[contactId] = contact with { Name = rawVal };
                }
                else if (string.IsNullOrEmpty(contact.Company) && (kind.Equals("company", StringComparison.OrdinalIgnoreCase) || label.Contains("公司")))
                {
                    dict[contactId] = contact with { Company = rawVal };
                }
                else if (string.IsNullOrEmpty(contact.JobTitle) && (kind.Equals("job_title", StringComparison.OrdinalIgnoreCase) || label.Contains("職稱")))
                {
                    dict[contactId] = contact with { JobTitle = rawVal };
                }
                else if (string.IsNullOrEmpty(contact.Notes) && (kind.Equals("notes", StringComparison.OrdinalIgnoreCase) || label.Contains("備註")))
                {
                    dict[contactId] = contact with { Notes = rawVal };
                }
            }
        }

        // 3. Card image assets
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT contact_id, relative_path, sha256
                FROM card_image_assets;
            ";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string contactId = reader.GetString(0);
                if (dict.TryGetValue(contactId, out var contact))
                {
                    dict[contactId] = contact with
                    {
                        FrontImageRelPath = reader.IsDBNull(1) ? null : reader.GetString(1),
                        FrontImageSha256 = reader.IsDBNull(2) ? null : reader.GetString(2)
                    };
                }
            }
        }

        // 4. Tags
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT r.contact_id, c.tag_name
                FROM contact_record_tags r
                INNER JOIN contact_tag_catalog c ON r.tag_id = c.tag_id;
            ";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string contactId = reader.GetString(0);
                string tagName = reader.GetString(1);
                if (dict.TryGetValue(contactId, out var contact))
                {
                    contact.Tags.Add(tagName);
                }
            }
        }
        catch { }

        return dict;
    }

    public async Task<DriveOperationResult> ExecuteMergeAsync(
        string localStorageRoot,
        string externalStorageRoot,
        DatabaseMergeDiffReport diffReport,
        string? localPassphrase = null,
        string? externalPassphrase = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            progress?.Report(new BackupProgress(10, "正在建立本地整併前安全防護快照..."));

            string localDb = Path.Combine(localStorageRoot, StoragePathHelper.DatabaseFileName);
            string remoteDb = Path.Combine(externalStorageRoot, StoragePathHelper.DatabaseFileName);
            string localImgDir = Path.Combine(localStorageRoot, StoragePathHelper.CardImagesDirName);
            string remoteImgDir = Path.Combine(externalStorageRoot, StoragePathHelper.CardImagesDirName);

            Directory.CreateDirectory(localImgDir);

            // Step 1: 建立安全快照
            string backupDir = Path.Combine(localStorageRoot, "backups");
            Directory.CreateDirectory(backupDir);
            string safetyBackupPath = Path.Combine(backupDir, $"pre_merge_{DateTime.UtcNow:yyyyMMdd_HHmmss}.sqlite3");
            if (File.Exists(localDb))
            {
                File.Copy(localDb, safetyBackupPath, overwrite: true);
            }

            progress?.Report(new BackupProgress(25, "正在開啟本機與外部資料庫連線..."));

            var localBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = localDb,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            if (!string.IsNullOrEmpty(localPassphrase)) localBuilder.Password = localPassphrase;

            var remoteBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = remoteDb,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            if (!string.IsNullOrEmpty(externalPassphrase)) remoteBuilder.Password = externalPassphrase;

            using var localConn = new SqliteConnection(localBuilder.ConnectionString);
            localConn.Open();

            using var remoteConn = new SqliteConnection(remoteBuilder.ConnectionString);
            remoteConn.Open();

            progress?.Report(new BackupProgress(40, "正在執行原子化資料庫整併事務..."));

            using var tx = localConn.BeginTransaction();
            try
            {
                int mergedNewCount = 0;
                int mergedUpdatedCount = 0;
                int mergedKeepBothCount = 0;

                // 建立本地標籤快取 (TagName.ToUpper -> TagId)
                var localTagCache = GetLocalTagCache(localConn, tx);

                foreach (var item in diffReport.Items)
                {
                    ct.ThrowIfCancellationRequested();

                    if (item.Status == ContactDiffStatus.Identical)
                    {
                        // 內容一致，略過
                        continue;
                    }

                    if (item.Status == ContactDiffStatus.OnlyInLocal)
                    {
                        // 本機獨有，保留本機，無需動作
                        continue;
                    }

                    if (item.Status == ContactDiffStatus.OnlyInRemote)
                    {
                        // 外部新增：整筆複製至本機
                        InsertFullContactFromRemote(remoteConn, localConn, tx, item.ContactId, item.ContactId, localTagCache, remoteImgDir, localImgDir, isClone: false);
                        mergedNewCount++;
                        continue;
                    }

                    if (item.Status == ContactDiffStatus.Conflicted)
                    {
                        // 衝突裁決
                        var effectiveResolution = item.Resolution;
                        if (effectiveResolution == MergeResolutionStrategy.KeepNewer)
                        {
                            effectiveResolution = item.IsRemoteNewer ? MergeResolutionStrategy.KeepRemote : MergeResolutionStrategy.KeepLocal;
                        }

                        switch (effectiveResolution)
                        {
                            case MergeResolutionStrategy.KeepLocal:
                                // 保留本機，不變更
                                break;

                            case MergeResolutionStrategy.KeepRemote:
                                // 採納外部：清除本機舊關聯並整筆覆寫
                                DeleteExistingContactRelations(localConn, tx, item.ContactId);
                                UpdateOrInsertMainContact(remoteConn, localConn, tx, item.ContactId);
                                InsertValuesAndImagesAndTags(remoteConn, localConn, tx, item.ContactId, item.ContactId, localTagCache, remoteImgDir, localImgDir, isClone: false);
                                mergedUpdatedCount++;
                                break;

                            case MergeResolutionStrategy.KeepBoth:
                                // 兩側同時保留：本機不動，外部另存新 UUID
                                string newContactId = Guid.NewGuid().ToString("D");
                                InsertFullContactFromRemote(remoteConn, localConn, tx, item.ContactId, newContactId, localTagCache, remoteImgDir, localImgDir, isClone: true);
                                mergedKeepBothCount++;
                                break;
                        }
                    }
                }

                tx.Commit();
                progress?.Report(new BackupProgress(100, "資料庫整併成功完成！"));

                string summaryMsg = $"✅ 資料庫整併成功！\n" +
                                   $"• 新增匯入：{mergedNewCount} 筆\n" +
                                   $"• 更新採納：{mergedUpdatedCount} 筆\n" +
                                   $"• 兩側同時保留另存：{mergedKeepBothCount} 筆\n" +
                                   $"• 安全快照已保存至 backups 資料夾";

                return new DriveOperationResult(true, summaryMsg);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return new DriveOperationResult(false, $"整併失敗已安全還原：{ex.Message}");
            }
        }, ct);
    }

    private static Dictionary<string, string> GetLocalTagCache(SqliteConnection conn, SqliteTransaction tx)
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT tag_name, tag_id FROM contact_tag_catalog;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cache[reader.GetString(0).Trim()] = reader.GetString(1);
            }
        }
        catch { }
        return cache;
    }

    private static string EnsureTagId(SqliteConnection conn, SqliteTransaction tx, string tagName, Dictionary<string, string> cache)
    {
        string trimmed = tagName.Trim();
        if (cache.TryGetValue(trimmed, out var existingId)) return existingId;

        string newTagId = Guid.NewGuid().ToString("D");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO contact_tag_catalog (tag_id, tag_name, revision) VALUES (@id, @name, 1);";
        cmd.Parameters.AddWithValue("@id", newTagId);
        cmd.Parameters.AddWithValue("@name", trimmed);
        cmd.ExecuteNonQuery();

        cache[trimmed] = newTagId;
        return newTagId;
    }

    private static void DeleteExistingContactRelations(SqliteConnection conn, SqliteTransaction tx, string contactId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            DELETE FROM contact_values WHERE contact_id = @id;
            DELETE FROM contact_record_tags WHERE contact_id = @id;
            DELETE FROM card_image_assets WHERE contact_id = @id;
        ";
        cmd.Parameters.AddWithValue("@id", contactId);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateOrInsertMainContact(SqliteConnection remoteConn, SqliteConnection localConn, SqliteTransaction tx, string contactId)
    {
        // 讀取 remote
        using var readCmd = remoteConn.CreateCommand();
        readCmd.CommandText = "SELECT revision, persistence_state, completion_source, created_at, updated_at, pending_delete FROM contact_records WHERE contact_id = @id;";
        readCmd.Parameters.AddWithValue("@id", contactId);
        using var reader = readCmd.ExecuteReader();
        if (!reader.Read()) return;

        int rev = reader.GetInt32(0);
        string state = reader.GetString(1);
        string source = reader.GetString(2);
        string created = reader.GetString(3);
        string updated = reader.GetString(4);
        int pendingDel = reader.GetInt32(5);

        using var writeCmd = localConn.CreateCommand();
        writeCmd.Transaction = tx;
        writeCmd.CommandText = @"
            INSERT INTO contact_records (contact_id, contract_version, revision, persistence_state, completion_source, created_at, updated_at, save_operation_id, content_fingerprint, pending_delete)
            VALUES (@id, '0.3', @rev, @state, @source, @created, @updated, @opId, @fp, @pendingDel)
            ON CONFLICT(contact_id) DO UPDATE SET
                revision = @rev,
                persistence_state = @state,
                completion_source = @source,
                updated_at = @updated,
                pending_delete = @pendingDel;
        ";
        writeCmd.Parameters.AddWithValue("@id", contactId);
        writeCmd.Parameters.AddWithValue("@rev", rev);
        writeCmd.Parameters.AddWithValue("@state", state);
        writeCmd.Parameters.AddWithValue("@source", source);
        writeCmd.Parameters.AddWithValue("@created", created);
        writeCmd.Parameters.AddWithValue("@updated", updated);
        writeCmd.Parameters.AddWithValue("@opId", Guid.NewGuid().ToString("N"));
        writeCmd.Parameters.AddWithValue("@fp", "merged");
        writeCmd.Parameters.AddWithValue("@pendingDel", pendingDel);
        writeCmd.ExecuteNonQuery();
    }

    private static void InsertFullContactFromRemote(
        SqliteConnection remoteConn,
        SqliteConnection localConn,
        SqliteTransaction tx,
        string srcContactId,
        string targetContactId,
        Dictionary<string, string> tagCache,
        string remoteImgDir,
        string localImgDir,
        bool isClone)
    {
        // 1. 寫入 main record
        using (var readCmd = remoteConn.CreateCommand())
        {
            readCmd.CommandText = "SELECT revision, persistence_state, completion_source, created_at, updated_at, pending_delete FROM contact_records WHERE contact_id = @id;";
            readCmd.Parameters.AddWithValue("@id", srcContactId);
            using var reader = readCmd.ExecuteReader();
            if (reader.Read())
            {
                int rev = reader.GetInt32(0);
                string state = reader.GetString(1);
                string source = reader.GetString(2);
                string created = reader.GetString(3);
                string updated = DateTimeOffset.UtcNow.ToString("o");
                int pendingDel = reader.GetInt32(5);

                using var writeCmd = localConn.CreateCommand();
                writeCmd.Transaction = tx;
                writeCmd.CommandText = @"
                    INSERT INTO contact_records (contact_id, contract_version, revision, persistence_state, completion_source, created_at, updated_at, save_operation_id, content_fingerprint, pending_delete)
                    VALUES (@id, '0.3', @rev, @state, @source, @created, @updated, @opId, @fp, @pendingDel);
                ";
                writeCmd.Parameters.AddWithValue("@id", targetContactId);
                writeCmd.Parameters.AddWithValue("@rev", rev);
                writeCmd.Parameters.AddWithValue("@state", state);
                writeCmd.Parameters.AddWithValue("@source", source);
                writeCmd.Parameters.AddWithValue("@created", created);
                writeCmd.Parameters.AddWithValue("@updated", updated);
                writeCmd.Parameters.AddWithValue("@opId", Guid.NewGuid().ToString("N"));
                writeCmd.Parameters.AddWithValue("@fp", "merged");
                writeCmd.Parameters.AddWithValue("@pendingDel", pendingDel);
                writeCmd.ExecuteNonQuery();
            }
        }

        // 2. 寫入 Values, Tags, Images
        InsertValuesAndImagesAndTags(remoteConn, localConn, tx, srcContactId, targetContactId, tagCache, remoteImgDir, localImgDir, isClone);
    }

    private static void InsertValuesAndImagesAndTags(
        SqliteConnection remoteConn,
        SqliteConnection localConn,
        SqliteTransaction tx,
        string srcContactId,
        string targetContactId,
        Dictionary<string, string> tagCache,
        string remoteImgDir,
        string localImgDir,
        bool isClone)
    {
        // Values
        using (var readCmd = remoteConn.CreateCommand())
        {
            readCmd.CommandText = @"
                SELECT value_kind, custom_field_definition_id, custom_field_name_snapshot,
                       raw_value, normalized_value, label, ordinal, source_mode, locked_at
                FROM contact_values
                WHERE contact_id = @id;
            ";
            readCmd.Parameters.AddWithValue("@id", srcContactId);
            using var reader = readCmd.ExecuteReader();
            while (reader.Read())
            {
                string kind = reader.GetString(0);
                string? defId = reader.IsDBNull(1) ? null : reader.GetString(1);
                string? snap = reader.IsDBNull(2) ? null : reader.GetString(2);
                string rawVal = reader.IsDBNull(3) ? "" : reader.GetString(3);
                string? normVal = reader.IsDBNull(4) ? null : reader.GetString(4);
                string label = reader.IsDBNull(5) ? "" : reader.GetString(5);
                int ordinal = reader.GetInt32(6);
                string mode = reader.IsDBNull(7) ? "Manual" : reader.GetString(7);
                string locked = reader.IsDBNull(8) ? DateTimeOffset.UtcNow.ToString("o") : reader.GetString(8);

                if (isClone && (kind.Equals("name", StringComparison.OrdinalIgnoreCase) || label.Contains("姓名")))
                {
                    rawVal += " (同步複本)";
                }

                string newValueId = Guid.NewGuid().ToString("D");
                using var writeCmd = localConn.CreateCommand();
                writeCmd.Transaction = tx;
                writeCmd.CommandText = @"
                    INSERT INTO contact_values (value_id, contact_id, value_kind, custom_field_definition_id,
                                                custom_field_name_snapshot, raw_value, normalized_value, label,
                                                ordinal, source_mode, locked_at)
                    VALUES (@vid, @cid, @kind, @defId, @snap, @raw, @norm, @label, @ord, @mode, @locked);
                ";
                writeCmd.Parameters.AddWithValue("@vid", newValueId);
                writeCmd.Parameters.AddWithValue("@cid", targetContactId);
                writeCmd.Parameters.AddWithValue("@kind", kind);
                writeCmd.Parameters.AddWithValue("@defId", (object?)defId ?? DBNull.Value);
                writeCmd.Parameters.AddWithValue("@snap", (object?)snap ?? DBNull.Value);
                writeCmd.Parameters.AddWithValue("@raw", rawVal);
                writeCmd.Parameters.AddWithValue("@norm", (object?)normVal ?? DBNull.Value);
                writeCmd.Parameters.AddWithValue("@label", label);
                writeCmd.Parameters.AddWithValue("@ord", ordinal);
                writeCmd.Parameters.AddWithValue("@mode", mode);
                writeCmd.Parameters.AddWithValue("@locked", locked);
                writeCmd.ExecuteNonQuery();
            }
        }

        // Tags
        try
        {
            using var readCmd = remoteConn.CreateCommand();
            readCmd.CommandText = @"
                SELECT c.tag_name
                FROM contact_record_tags r
                INNER JOIN contact_tag_catalog c ON r.tag_id = c.tag_id
                WHERE r.contact_id = @id;
            ";
            readCmd.Parameters.AddWithValue("@id", srcContactId);
            using var reader = readCmd.ExecuteReader();
            while (reader.Read())
            {
                string tagName = reader.GetString(0);
                string tagId = EnsureTagId(localConn, tx, tagName, tagCache);

                using var writeCmd = localConn.CreateCommand();
                writeCmd.Transaction = tx;
                writeCmd.CommandText = "INSERT OR IGNORE INTO contact_record_tags (contact_id, tag_id) VALUES (@cid, @tid);";
                writeCmd.Parameters.AddWithValue("@cid", targetContactId);
                writeCmd.Parameters.AddWithValue("@tid", tagId);
                writeCmd.ExecuteNonQuery();
            }
        }
        catch { }

        // Images
        using (var readCmd = remoteConn.CreateCommand())
        {
            readCmd.CommandText = @"
                SELECT capture_source, storage_state, relative_path, staging_relative_path,
                       sha256, mime_type, byte_count, width, height, created_at, updated_at
                FROM card_image_assets
                WHERE contact_id = @id;
            ";
            readCmd.Parameters.AddWithValue("@id", srcContactId);
            using var reader = readCmd.ExecuteReader();
            while (reader.Read())
            {
                string captureSource = reader.GetString(0);
                string storageState = reader.GetString(1);
                string relPath = reader.GetString(2);
                string? stagingPath = reader.IsDBNull(3) ? null : reader.GetString(3);
                string sha256 = reader.GetString(4);
                string mime = reader.GetString(5);
                long bytes = reader.GetInt64(6);
                int w = reader.GetInt32(7);
                int h = reader.GetInt32(8);
                string created = reader.GetString(9);
                string updated = reader.GetString(10);

                string targetRelPath = relPath;
                if (isClone)
                {
                    string ext = Path.GetExtension(relPath);
                    targetRelPath = $"card-images/{targetContactId}_front{ext}";
                }

                // 複製實體檔案
                try
                {
                    string srcPhysical = Path.Combine(remoteImgDir, Path.GetFileName(relPath));
                    string dstPhysical = Path.Combine(localImgDir, Path.GetFileName(targetRelPath));
                    if (File.Exists(srcPhysical))
                    {
                        File.Copy(srcPhysical, dstPhysical, overwrite: true);
                    }
                }
                catch { }

                string newAssetId = Guid.NewGuid().ToString("D");
                using var writeCmd = localConn.CreateCommand();
                writeCmd.Transaction = tx;
                writeCmd.CommandText = @"
                    INSERT INTO card_image_assets (asset_id, contact_id, capture_source, storage_state,
                                                   relative_path, staging_relative_path, sha256, mime_type,
                                                   byte_count, width, height, created_at, updated_at)
                    VALUES (@aid, @cid, @src, @state, @rel, @staging, @sha, @mime, @bytes, @w, @h, @c, @u);
                ";
                writeCmd.Parameters.AddWithValue("@aid", newAssetId);
                writeCmd.Parameters.AddWithValue("@cid", targetContactId);
                writeCmd.Parameters.AddWithValue("@src", captureSource);
                writeCmd.Parameters.AddWithValue("@state", storageState);
                writeCmd.Parameters.AddWithValue("@rel", targetRelPath);
                writeCmd.Parameters.AddWithValue("@staging", (object?)stagingPath ?? DBNull.Value);
                writeCmd.Parameters.AddWithValue("@sha", sha256);
                writeCmd.Parameters.AddWithValue("@mime", mime);
                writeCmd.Parameters.AddWithValue("@bytes", bytes);
                writeCmd.Parameters.AddWithValue("@w", w);
                writeCmd.Parameters.AddWithValue("@h", h);
                writeCmd.Parameters.AddWithValue("@c", created);
                writeCmd.Parameters.AddWithValue("@u", updated);
                writeCmd.ExecuteNonQuery();
            }
        }
    }
}
