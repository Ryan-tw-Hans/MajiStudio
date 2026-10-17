using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services.Storage;

public partial class StorageService
{
    private static SqliteCommand TagCommand(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static void ApplyTagSchema(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = TagCommand(conn, tx, """
            ALTER TABLE contact_records ADD COLUMN tag_revision INTEGER NOT NULL DEFAULT 0 CHECK(tag_revision >= 0);
            ALTER TABLE contact_records ADD COLUMN tag_updated_at TEXT;
            ALTER TABLE contact_records ADD COLUMN tag_save_fingerprint TEXT NOT NULL DEFAULT '';
            ALTER TABLE contact_records ADD COLUMN tag_save_mapping TEXT NOT NULL DEFAULT '{}';
            CREATE TABLE contact_tag_catalog(
                tag_id TEXT PRIMARY KEY NOT NULL,
                display_name TEXT NOT NULL CHECK(length(trim(display_name)) > 0),
                normalized_key TEXT NOT NULL UNIQUE CHECK(length(normalized_key) > 0),
                revision INTEGER NOT NULL CHECK(revision >= 1),
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE contact_tags(
                contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
                tag_id TEXT NOT NULL REFERENCES contact_tag_catalog(tag_id) ON DELETE RESTRICT,
                assigned_at TEXT NOT NULL,
                PRIMARY KEY(contact_id, tag_id));
            CREATE INDEX idx_contact_tags_tag ON contact_tags(tag_id, contact_id);
            CREATE INDEX idx_contact_records_created ON contact_records(created_at DESC, contact_id ASC);
            PRAGMA user_version=2;
            """);
        cmd.ExecuteNonQuery();
    }
    private static void ValidateTagSchema(SqliteConnection conn)
    {
        foreach (var (table, required) in new[] {
            ("contact_records", new[] {"tag_revision","tag_updated_at","tag_save_fingerprint","tag_save_mapping"}),
            ("contact_tag_catalog", new[] {"tag_id","display_name","normalized_key","revision","created_at","updated_at"}),
            ("contact_tags", new[] {"contact_id","tag_id","assigned_at"}) })
        {
            using var cmd = TagCommand(conn, null, $"PRAGMA table_info('{table}');");
            using var reader = cmd.ExecuteReader();
            var names = new HashSet<string>();
            while (reader.Read()) names.Add(reader.GetString(1));
            if (required.Any(n => !names.Contains(n))) throw new InvalidOperationException("SCHEMA_TAG_COLUMNS_UNSUPPORTED");
        }
        using var constraints = TagCommand(conn, null, """
            SELECT count(*) FROM sqlite_master WHERE
              (type='index' AND name IN ('idx_contact_tags_tag','idx_contact_records_created'))
              OR (type='table' AND name='contact_tags' AND sql LIKE '%ON DELETE CASCADE%' AND sql LIKE '%ON DELETE RESTRICT%' AND sql LIKE '%PRIMARY KEY%')
              OR (type='table' AND name='contact_tag_catalog' AND sql LIKE '%UNIQUE%' AND sql LIKE '%CHECK%');
            """);
        if (Convert.ToInt32(constraints.ExecuteScalar()) != 4) throw new InvalidOperationException("SCHEMA_TAG_CONSTRAINTS_UNSUPPORTED");
    }
    private void UpgradeTagSchema()
    {
        using var writer = OpenConnection();
        using (var cp = TagCommand(writer, null, "PRAGMA wal_checkpoint(FULL);"))
        using (var r = cp.ExecuteReader())
            if (!r.Read() || r.GetInt32(0) != 0) throw new InvalidOperationException("MIGRATION_BUSY");

        // IMMEDIATE reservation prevents commits between the separate read-only backup and DDL.
        using var tx = writer.BeginTransaction(deferred: false);
        using var version = TagCommand(writer, tx, "PRAGMA user_version;");
        int current = Convert.ToInt32(version.ExecuteScalar());
        if (current == 2) { tx.Commit(); return; }
        if (current != 1) throw new InvalidOperationException("MIGRATION_VERSION_CHANGED");

        string backupParent = Path.Combine(_storageRoot, "schema-backups");
        StoragePathHelper.VerifyPathSafety(_storageRoot, backupParent, true);
        if (_applicationDirectoryMode) StoragePathHelper.EnsurePrivateDataDirectory(_storageRoot, backupParent);
        else Directory.CreateDirectory(backupParent);
        StoragePathHelper.VerifyAclProtection(backupParent, false);
        string backupDir = Path.Combine(backupParent, Guid.NewGuid().ToString("N"));
        StoragePathHelper.VerifyPathSafety(_storageRoot, backupDir, true);
        if (_applicationDirectoryMode) StoragePathHelper.EnsurePrivateDataDirectory(_storageRoot, backupDir);
        else Directory.CreateDirectory(backupDir);
        StoragePathHelper.VerifyAclProtection(backupDir, false);
        string backupPath = Path.Combine(backupDir, "schema1.sqlite3");
        StoragePathHelper.VerifyPathSafety(_storageRoot, backupPath, true);
        using (var source = new SqliteConnection(BuildConnectionString(_dbPath, true)))
        using (var destination = new SqliteConnection(BuildConnectionString(backupPath)))
        {
            source.Open(); destination.Open();
            source.BackupDatabase(destination);
            using var check = TagCommand(destination, null, "PRAGMA integrity_check;");
            if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.Ordinal))
                throw new InvalidOperationException("MIGRATION_BACKUP_INVALID");
        }
        StoragePathHelper.VerifyPathSafety(_storageRoot, backupPath, true);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backupPath)));
        File.WriteAllText(Path.Combine(backupDir, "manifest.json"), JsonSerializer.Serialize(new {
            SchemaVersion = 1, CreatedAt = DateTimeOffset.UtcNow, SHA256 = hash, IntegrityCheck = "ok"
        }));
        _failureHook?.Invoke("AfterTagSchemaBackup");
        ApplyTagSchema(writer, tx);
        _failureHook?.Invoke("BeforeTagSchemaCommit");
        using (var check = TagCommand(writer, tx, "PRAGMA foreign_key_check;"))
        using (var reader = check.ExecuteReader())
            if (reader.Read()) throw new InvalidOperationException("MIGRATION_FOREIGN_KEY_FAILED");
        tx.Commit();
        ValidateTagSchema(writer);
    }
    private static string TagError(Exception ex) => ex is SqliteException
        ? "標籤操作未完成，可能有同名項目或資料正忙；請重新載入後再試。"
        : ex is InvalidOperationException ? ex.Message : "標籤操作未完成，請稍後再試。";
    private static TagState? ReadTagState(SqliteConnection conn, SqliteTransaction? tx, string id)
    {
        using var cmd = TagCommand(conn, tx, "SELECT tag_revision,tag_save_fingerprint,tag_save_mapping FROM contact_records WHERE contact_id=@id;", ("@id",id));
        int revision; string fingerprint; Dictionary<string,string>? mapping;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return null;
            revision=reader.GetInt32(0); fingerprint=reader.GetString(1);
            mapping=JsonSerializer.Deserialize<Dictionary<string,string>>(reader.GetString(2));
        }
        using var links = TagCommand(conn, tx, """
            SELECT t.tag_id,t.display_name,t.revision FROM contact_tags l
            JOIN contact_tag_catalog t ON t.tag_id=l.tag_id WHERE l.contact_id=@id
            ORDER BY t.normalized_key,t.tag_id;
            """, ("@id",id));
        using var rows=links.ExecuteReader();
        var tags=new List<TagDefinition>();
        while(rows.Read()) tags.Add(new(rows.GetString(0),rows.GetString(1),rows.GetInt32(2)));
        return new(revision,tags,fingerprint,mapping);
    }
    private static TagDefinition ResolveTag(SqliteConnection conn, SqliteTransaction tx, string name)
    {
        string display=TagNames.Display(name), key=TagNames.Key(name);
        using var find=TagCommand(conn,tx,"SELECT tag_id,display_name,revision FROM contact_tag_catalog WHERE normalized_key=@key;",("@key",key));
        using(var reader=find.ExecuteReader())
            if(reader.Read()) return new(reader.GetString(0),reader.GetString(1),reader.GetInt32(2));
        string id=Guid.NewGuid().ToString("N"), now=DateTimeOffset.UtcNow.ToString("o");
        using var insert=TagCommand(conn,tx,"INSERT INTO contact_tag_catalog VALUES(@id,@name,@key,1,@now,@now);",
            ("@id",id),("@name",display),("@key",key),("@now",now));
        insert.ExecuteNonQuery(); return new(id,display,1);
    }
    private TagState ReplaceTags(SqliteConnection conn, SqliteTransaction tx, string id, int expected,
        IReadOnlyList<TagChoice> choices, bool initial)
    {
        foreach(var choice in choices) { if(choice.TagId is null) TagNames.Display(choice.DisplayName); }
        using(var target=TagCommand(conn,tx,"SELECT tag_revision,persistence_state,pending_delete FROM contact_records WHERE contact_id=@id;",("@id",id)))
        using(var r=target.ExecuteReader())
        {
            if(!r.Read() || r.GetInt32(2)!=0) throw new InvalidOperationException("名片已刪除或正在刪除。");
            if(r.GetInt32(0)!=expected) throw new InvalidOperationException("分類已被其他操作修改；請重新載入再調整。");
            string state=r.GetString(1);
            if(!initial && state!="Ready" && state!="ImageMissing") throw new InvalidOperationException("名片尚未完成保存，請稍後重試。");
        }
        var ids=new HashSet<string>(StringComparer.Ordinal);
        var mapping=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var choice in choices)
        {
            if(choice.TagId is null){string resolved=ResolveTag(conn,tx,choice.DisplayName).TagId;ids.Add(resolved);mapping[choice.Token]=resolved;}
            else
            {
                using var check=TagCommand(conn,tx,"SELECT count(*) FROM contact_tag_catalog WHERE tag_id=@id;",("@id",choice.TagId));
                if(Convert.ToInt32(check.ExecuteScalar())!=1) throw new InvalidOperationException("選取的標籤已刪除，請重新選取。");
                ids.Add(choice.TagId);mapping[choice.Token]=choice.TagId;
            }
        }
        var before=ReadTagState(conn,tx,id)!;
        bool changed=!ids.SetEquals(before.Tags.Select(t=>t.TagId));
        string fingerprint=TagChoice.Fingerprint(choices);
        if(changed)
        {
            using var remove=TagCommand(conn,tx,"DELETE FROM contact_tags WHERE contact_id=@id;",("@id",id)); remove.ExecuteNonQuery();
            string now=DateTimeOffset.UtcNow.ToString("o");
            foreach(string tagId in ids)
            {
                using var insert=TagCommand(conn,tx,"INSERT INTO contact_tags VALUES(@id,@tag,@now);",("@id",id),("@tag",tagId),("@now",now));
                insert.ExecuteNonQuery();
            }
            using var update=TagCommand(conn,tx,"UPDATE contact_records SET tag_revision=tag_revision+1,tag_updated_at=@now,tag_save_fingerprint=@fp,tag_save_mapping=@map WHERE contact_id=@id AND tag_revision=@rev;",
                ("@now",now),("@fp",fingerprint),("@map",JsonSerializer.Serialize(mapping)),("@id",id),("@rev",expected));
            if(update.ExecuteNonQuery()!=1) throw new InvalidOperationException("分類修訂衝突，請重新載入。");
        }
        else
        {
            using var update=TagCommand(conn,tx,"UPDATE contact_records SET tag_save_fingerprint=@fp,tag_save_mapping=@map WHERE contact_id=@id;",("@fp",fingerprint),("@map",JsonSerializer.Serialize(mapping)),("@id",id));
            update.ExecuteNonQuery();
        }
        _failureHook?.Invoke("BeforeTagAssignmentCommit");
        return ReadTagState(conn,tx,id)!;
    }
    private async Task<TagMutationResult> MutateTagAsync(Func<SqliteConnection,SqliteTransaction,TagMutationResult> action, CancellationToken ct)
    {
        if(!IsAvailable) return new(false,ErrorMessage);
        await _gate.WaitAsync(ct);
        try { return await Task.Run(() => {
            using var conn=OpenConnection(); using var tx=conn.BeginTransaction(deferred:false);
            try { var result=action(conn,tx); tx.Commit(); return result; }
            catch(Exception ex) { tx.Rollback(); return new TagMutationResult(false,TagError(ex)); }
        },ct); }
        finally {_gate.Release();}
    }
    public Task<TagMutationResult> SetTagsAsync(string contactId,int expectedRevision,IReadOnlyList<TagChoice> choices,CancellationToken ct=default)
    {
        var frozen=choices.ToArray();
        return MutateTagAsync((c,t)=>new(true,null,ReplaceTags(c,t,contactId,expectedRevision,frozen,false)),ct);
    }
    public Task<TagMutationResult> CreateTagAsync(string name,CancellationToken ct=default) =>
        MutateTagAsync((c,t)=>new(true,null,Tag:ResolveTag(c,t,name)),ct);
    public Task<TagMutationResult> RenameTagAsync(TagDefinition tag,string name,CancellationToken ct=default) =>
        MutateTagAsync((c,t)=>{
            string display=TagNames.Display(name),key=TagNames.Key(name);
            using var update=TagCommand(c,t,"UPDATE contact_tag_catalog SET display_name=@name,normalized_key=@key,revision=revision+1,updated_at=@now WHERE tag_id=@id AND revision=@rev;",
                ("@name",display),("@key",key),("@now",DateTimeOffset.UtcNow.ToString("o")),("@id",tag.TagId),("@rev",tag.Revision));
            if(update.ExecuteNonQuery()!=1) throw new InvalidOperationException("標籤已修改或刪除，請重新載入。");
            return new(true,null,Tag:new(tag.TagId,display,tag.Revision+1));
        },ct);
    public Task<IReadOnlyList<TagDefinition>> FindTagsAsync(IReadOnlyList<string> ids,CancellationToken ct=default)
    {
        string selected=JsonSerializer.Serialize(ids.Distinct(StringComparer.Ordinal));
        return Task.Run<IReadOnlyList<TagDefinition>>(()=>{
            using var c=OpenConnection(true);
            using var cmd=TagCommand(c,null,"SELECT tag_id,display_name,revision FROM contact_tag_catalog WHERE tag_id IN(SELECT value FROM json_each(@ids));",("@ids",selected));
            using var r=cmd.ExecuteReader();var result=new List<TagDefinition>();
            while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2)));
            return result;
        },ct);
    }
    public Task<TagDefinition?> FindTagAsync(string tagId,CancellationToken ct=default)=>Task.Run(()=>{
        using var c=OpenConnection(true);
        using var cmd=TagCommand(c,null,"SELECT tag_id,display_name,revision FROM contact_tag_catalog WHERE tag_id=@id;",("@id",tagId));
        using var r=cmd.ExecuteReader();
        return r.Read()?new TagDefinition(r.GetString(0),r.GetString(1),r.GetInt32(2)):null;
    },ct);
    public Task<TagState?> GetTagStateAsync(string contactId,CancellationToken ct=default) => Task.Run(()=>{
        using var c=OpenConnection(true); using var tx=c.BeginTransaction(deferred:true);
        return ReadTagState(c,tx,contactId);
    },ct);
    private static string EscapeTagLike(string value)=>value.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_");
    public Task<PagedResult<TagDefinition>> GetTagsAsync(string? search,int page=0,CancellationToken ct=default) => Task.Run(()=>{
        using var c=OpenConnection(true); using var tx=c.BeginTransaction(deferred:true);
        string text=search?.Trim()??"";
        var result=new PagedResult<TagDefinition>{PageIndex=Math.Max(0,page),PageSize=30};
        const string condition="(@empty=1 OR display_name LIKE @q ESCAPE '\\')";
        using(var count=TagCommand(c,tx,"SELECT count(*) FROM contact_tag_catalog WHERE "+condition,("@empty",text.Length==0?1:0),("@q","%"+EscapeTagLike(text)+"%")))
            result.TotalCount=Convert.ToInt32(count.ExecuteScalar());
        using var cmd=TagCommand(c,tx,"SELECT tag_id,display_name,revision,(SELECT count(*) FROM contact_tags l WHERE l.tag_id=contact_tag_catalog.tag_id) FROM contact_tag_catalog WHERE "+condition+" ORDER BY normalized_key,tag_id LIMIT 30 OFFSET @offset;",
            ("@empty",text.Length==0?1:0),("@q","%"+EscapeTagLike(text)+"%"),("@offset",result.PageIndex*30));
        using var r=cmd.ExecuteReader();
        while(r.Read()) result.Items.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3)));
        return result;
    },ct);
    private static TagDeletePreview ReadDeletePreview(SqliteConnection c,SqliteTransaction tx,string id)
    {
        TagDefinition tag;
        using(var cmd=TagCommand(c,tx,"SELECT tag_id,display_name,revision FROM contact_tag_catalog WHERE tag_id=@id;",("@id",id)))
        using(var r=cmd.ExecuteReader()) {
            if(!r.Read()) throw new InvalidOperationException("標籤已刪除，請重新載入。");
            tag=new(r.GetString(0),r.GetString(1),r.GetInt32(2));
        }
        using var members=TagCommand(c,tx,"SELECT l.contact_id,c.tag_revision,c.persistence_state,c.pending_delete FROM contact_tags l JOIN contact_records c ON c.contact_id=l.contact_id WHERE l.tag_id=@id ORDER BY l.contact_id;",("@id",id));
        using var reader=members.ExecuteReader();
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count=0; bool busy=false;
        while(reader.Read())
        {
            var row=new[]{reader.GetString(0),reader.GetInt32(1).ToString(),reader.GetString(2),reader.GetInt32(3).ToString()};
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row)));
            busy|=row[2]=="PendingFinalize" || row[3]=="1";
            count++;
        }
        return new(tag,count,Convert.ToHexString(hash.GetHashAndReset()),busy);
    }
    public Task<TagDeletePreview> PreviewTagDeleteAsync(string tagId,CancellationToken ct=default)=>Task.Run(()=>{
        using var c=OpenConnection(true); using var t=c.BeginTransaction(deferred:true);
        return ReadDeletePreview(c,t,tagId);
    },ct);
    public Task<TagMutationResult> DeleteTagAsync(TagDeletePreview preview,CancellationToken ct=default) =>
        MutateTagAsync((c,t)=>{
            var current=ReadDeletePreview(c,t,preview.Tag.TagId);
            if(current.Tag.Revision!=preview.Tag.Revision || current.MembershipToken!=preview.MembershipToken)
                throw new InvalidOperationException("使用名片或標籤已變動，請重新查看清單並確認。");
            if(current.Busy) throw new InvalidOperationException("使用此標籤的名片正在保存或刪除，請稍後重新確認。");
            using(var revisions=TagCommand(c,t,"UPDATE contact_records SET tag_revision=tag_revision+1,tag_updated_at=@now,tag_save_fingerprint='',tag_save_mapping='{}' WHERE contact_id IN(SELECT contact_id FROM contact_tags WHERE tag_id=@id);",
                ("@now",DateTimeOffset.UtcNow.ToString("o")),("@id",preview.Tag.TagId))) revisions.ExecuteNonQuery();
            using(var links=TagCommand(c,t,"DELETE FROM contact_tags WHERE tag_id=@id;",("@id",preview.Tag.TagId))) links.ExecuteNonQuery();
            using(var tag=TagCommand(c,t,"DELETE FROM contact_tag_catalog WHERE tag_id=@id;",("@id",preview.Tag.TagId))) tag.ExecuteNonQuery();
            _failureHook?.Invoke("BeforeTagDeleteCommit");
            return new(true,null);
        },ct);
    private static string BuildSearchFilter(string text, List<(string, object?)> parameters)
    {
        if (string.IsNullOrWhiteSpace(text)) return "1=1";

        int paramIndex = parameters.Count;

        // Top-level OR groups split by whitespace (space, tab, fullwidth space)
        var orGroups = text.Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (orGroups.Length == 0) return "1=1";

        var orSqlBlocks = new List<string>();

        foreach (var orGroup in orGroups)
        {
            // Inner AND terms split by '+'
            var andTerms = orGroup.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (andTerms.Length == 0) continue;

            var andSqlBlocks = new List<string>();
            foreach (var term in andTerms)
            {
                string pName = $"@p{paramIndex++}";
                parameters.Add((pName, "%" + EscapeTagLike(term) + "%"));
                andSqlBlocks.Add($"(EXISTS(SELECT 1 FROM contact_values v WHERE v.contact_id=c.contact_id AND v.raw_value LIKE {pName} ESCAPE '\\') OR EXISTS(SELECT 1 FROM contact_tags ct JOIN contact_tag_catalog tc ON tc.tag_id=ct.tag_id WHERE ct.contact_id=c.contact_id AND tc.display_name LIKE {pName} ESCAPE '\\'))");
            }

            if (andSqlBlocks.Count == 1)
            {
                orSqlBlocks.Add(andSqlBlocks[0]);
            }
            else if (andSqlBlocks.Count > 1)
            {
                orSqlBlocks.Add("(" + string.Join(" AND ", andSqlBlocks) + ")");
            }
        }

        if (orSqlBlocks.Count == 0) return "1=1";
        if (orSqlBlocks.Count == 1) return orSqlBlocks[0];
        return "(" + string.Join(" OR ", orSqlBlocks) + ")";
    }

    public Task<PagedResult<ContactSummaryItem>> QueryContactsAsync(ContactQuery query,int page=0,int pageSize=30,CancellationToken ct=default)
    {
        var ids=query.TagIds.Distinct(StringComparer.Ordinal).ToArray();
        string text=query.Text?.Trim()??"";
        page=Math.Max(0,page); pageSize=Math.Clamp(pageSize,1,100);
        return Task.Run(()=>{
            using var c=OpenConnection(true); using var tx=c.BeginTransaction(deferred:true);
            var parameters=new List<(string,object?)>();
            string searchCondition = BuildSearchFilter(text, parameters);
            string filter = searchCondition;
            if(query.Untagged) filter+=" AND NOT EXISTS(SELECT 1 FROM contact_tags l WHERE l.contact_id=c.contact_id)";
            else if(ids.Length>0)
            {
                // JSON table parameter avoids SQLite's bind parameter limit for many selected tags.
                parameters.Add(("@tags",JsonSerializer.Serialize(ids)));
                filter+=query.MatchAll
                    ? " AND NOT EXISTS(SELECT 1 FROM json_each(@tags) s WHERE NOT EXISTS(SELECT 1 FROM contact_tags l WHERE l.contact_id=c.contact_id AND l.tag_id=s.value))"
                    : " AND EXISTS(SELECT 1 FROM contact_tags l WHERE l.contact_id=c.contact_id AND l.tag_id IN(SELECT value FROM json_each(@tags)))";
            }
            var result=new PagedResult<ContactSummaryItem>{PageIndex=page,PageSize=pageSize};
            using(var count=TagCommand(c,tx,"SELECT count(*) FROM contact_records c WHERE "+filter,parameters.ToArray()))
                result.TotalCount=Convert.ToInt32(count.ExecuteScalar());
            var pageParameters=parameters.Concat(new (string,object?)[]{("@limit",pageSize),("@offset",(long)page*pageSize)}).ToArray();

            string sortDir = query.IsDescending ? "DESC" : "ASC";
            string orderClause = query.OrderBy switch
            {
                "Name" => $"ORDER BY COALESCE((SELECT raw_value FROM contact_values v WHERE v.contact_id=c.contact_id AND value_kind='Name' ORDER BY ordinal LIMIT 1),'') COLLATE NOCASE {sortDir}, c.created_at DESC",
                "Company" => $"ORDER BY COALESCE((SELECT raw_value FROM contact_values v WHERE v.contact_id=c.contact_id AND value_kind='Company' ORDER BY ordinal LIMIT 1),'') COLLATE NOCASE {sortDir}, c.created_at DESC",
                "Title" => $"ORDER BY COALESCE((SELECT raw_value FROM contact_values v WHERE v.contact_id=c.contact_id AND value_kind='Title' ORDER BY ordinal LIMIT 1),'') COLLATE NOCASE {sortDir}, c.created_at DESC",
                "Status" => $"ORDER BY c.persistence_state {sortDir}, c.created_at DESC",
                _ => $"ORDER BY c.created_at {sortDir}, c.contact_id ASC"
            };

            using(var cmd=TagCommand(c,tx,"""
                SELECT c.contact_id,c.revision,c.persistence_state,c.completion_source,c.created_at,c.updated_at,c.completed_at,c.pending_delete,c.tag_revision,
                COALESCE((SELECT raw_value FROM contact_values v WHERE v.contact_id=c.contact_id AND value_kind='Name' ORDER BY ordinal LIMIT 1),''),
                COALESCE((SELECT raw_value FROM contact_values v WHERE v.contact_id=c.contact_id AND value_kind='Company' ORDER BY ordinal LIMIT 1),''),
                EXISTS(SELECT 1 FROM card_image_assets a WHERE a.contact_id=c.contact_id AND a.storage_state != 'PendingDelete'),
                COALESCE((SELECT raw_value FROM contact_values v WHERE v.contact_id=c.contact_id AND value_kind='Title' ORDER BY ordinal LIMIT 1),''),
                COALESCE((SELECT raw_value FROM contact_values v WHERE v.contact_id=c.contact_id AND value_kind='Phone' ORDER BY ordinal LIMIT 1),''),
                COALESCE((SELECT raw_value FROM contact_values v WHERE v.contact_id=c.contact_id AND value_kind='Email' ORDER BY ordinal LIMIT 1),''),
                (SELECT relative_path FROM card_image_assets a WHERE a.contact_id=c.contact_id AND a.storage_state != 'PendingDelete' LIMIT 1)
                FROM contact_records c WHERE 
                """+filter+" "+orderClause+" LIMIT @limit OFFSET @offset;",pageParameters))
            using(var r=cmd.ExecuteReader())
                while(r.Read()) result.Items.Add(new(){
                    ContactId=r.GetString(0),Revision=r.GetInt32(1),PersistenceState=r.GetString(2),CompletionSource=r.GetString(3),
                    CreatedAt=DateTimeOffset.Parse(r.GetString(4)),UpdatedAt=DateTimeOffset.Parse(r.GetString(5)),CompletedAt=r.IsDBNull(6)?null:DateTimeOffset.Parse(r.GetString(6)),
                    PendingDelete=r.GetInt32(7)!=0,TagRevision=r.GetInt32(8),Name=r.GetString(9),Company=r.GetString(10),HasImage=r.GetInt32(11)!=0,
                    Title=r.GetString(12),Phone=r.GetString(13),Email=r.GetString(14),RelativeImagePath=r.IsDBNull(15)?null:r.GetString(15)});
            if(result.Items.Count>0)
            {
                using var links=TagCommand(c,tx,"SELECT l.contact_id,t.tag_id,t.display_name,t.revision FROM contact_tags l JOIN contact_tag_catalog t ON t.tag_id=l.tag_id WHERE l.contact_id IN(SELECT value FROM json_each(@ids)) ORDER BY t.normalized_key,t.tag_id;",
                    ("@ids",JsonSerializer.Serialize(result.Items.Select(i=>i.ContactId))));
                using var r=links.ExecuteReader(); var map=result.Items.ToDictionary(i=>i.ContactId,_=>new List<TagDefinition>());
                while(r.Read()) map[r.GetString(0)].Add(new(r.GetString(1),r.GetString(2),r.GetInt32(3)));
                foreach(var item in result.Items) item.Tags=map[item.ContactId];
            }
            return result;
        },ct);
    }
}
