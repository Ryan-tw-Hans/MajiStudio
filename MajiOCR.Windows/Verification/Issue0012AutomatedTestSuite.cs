using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Verification;
public static class Issue0012AutomatedTestSuite
{
    private static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static SqliteConnection Open(string path) {var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());c.Open();return c;}
    private static object? Sql(SqliteConnection c,string sql){using var cmd=c.CreateCommand();cmd.CommandText=sql;return cmd.ExecuteScalar();}
    private static async Task<MainViewModel> Card(StorageService service,string name,params TagChoice[] tags)
    {
        var vm=new MainViewModel(new CustomFieldCatalogService(Path.Combine(service.StorageRoot,Guid.NewGuid().ToString("N")+".json")),null,service);
        byte[] bmp=Issue0011AutomatedTestSuite.CreateSyntheticBmp(20,12,10,20,30);
        await vm.SetSessionImageAsync(ImageSessionInfo.Create(bmp,"合成名片.bmp","ImagePickerCopy","image/bmp",20,12));
        vm.NameRow.Value=name;vm.NameRow.Confirm();vm.SetClassification(tags);
        var saved=await vm.SaveCurrentDraftAsync();Assert(saved.Succeeded,saved.ErrorMessage??"save");
        return vm;
    }
    public static async Task<Issue0011TestSuiteReport> RunAllTestsAsync(string evidence)
    {
        MainViewModel.IsHeadlessEnvironment=true;
        var report=new Issue0011TestSuiteReport{IssueId="ISSUE-0012"};
        string fixture=Path.Combine(Path.GetFullPath(evidence),"tags_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        async Task Test(string id,string description,Func<string,Task> run)
        {
            var sw=Stopwatch.StartNew();string status="PASS",details="合成資料行為斷言通過。";
            try {await run(Path.Combine(fixture,id));}
            catch(Exception ex){status="FAIL";details=ex.ToString();}
            report.TestCases.Add(new(){TestId=id,Description=description,Status=status,Details=details,LatencyMs=sw.ElapsedMilliseconds});
            report.TotalTests++;if(status=="PASS")report.PassCount++;else report.FailCount++;
        }
        using(var native=Open(":memory:"))report.NativeSqliteVersion=Convert.ToString(Sql(native,"SELECT sqlite_version();"))??"";
        await Test("TAG-T01","建立時多標籤、重開保留、集合去重及分類noChange",async root=>{
            using var s=new StorageService(root);Assert(s.IsAvailable,s.ErrorMessage??"init");
            var vm=await Card(s,"A",new TagChoice(null,"客戶"),new TagChoice(null,"台北"),new TagChoice(null,"客戶"));
            Assert(!vm.HasUnsavedChanges,"saved dirty");
            using var reopened=new StorageService(root);
            var d=await reopened.GetContactDetailAsync(vm.CurrentContactId);
            Assert(d!.Tags.Count==2&&d.TagRevision==1&&d.Revision==1&&d.IsImageAvailable,"roundtrip");
            var nochange=await reopened.SetTagsAsync(d.ContactId,d.TagRevision,d.Tags.Select(t=>new TagChoice(t.TagId,t.DisplayName)).ToArray());
            Assert(nochange.Succeeded&&nochange.State!.Revision==1,"nochange");
        });
        await Test("TAG-T02","大小寫與NFC唯一、名稱校驗、內空白及逗號",async root=>{
            using var s=new StorageService(root);
            var a=await s.CreateTagAsync(" Client ");var b=await s.CreateTagAsync("client");
            Assert(a.Tag!.TagId==b.Tag!.TagId,"case reuse");
            var c=await s.CreateTagAsync("e\u0301");var d=await s.CreateTagAsync("é");Assert(c.Tag!.TagId==d.Tag!.TagId,"NFC");
            foreach(string invalid in new[]{"  ","a\nb","a\u0001b"})Assert(!(await s.CreateTagAsync(invalid)).Succeeded,"invalid");
            Assert((await s.CreateTagAsync("A  B, C")).Tag!.DisplayName=="A  B, C","name preserved");
        });
        await Test("TAG-T03","分類交易失敗回滾字典、關聯及修訂",async root=>{
            using var s=new StorageService(root);var vm=await Card(s,"A");
            s.SetFailureInjectionHook(p=>{if(p=="BeforeTagAssignmentCommit")throw new IOException("fixture");});
            var result=await s.SetTagsAsync(vm.CurrentContactId,0,new[]{new TagChoice(null,"失敗項目")});
            s.SetFailureInjectionHook(null);
            Assert(!result.Succeeded&&(await s.GetTagsAsync(null)).TotalCount==0&&(await s.GetTagStateAsync(vm.CurrentContactId))!.Revision==0,"rollback");
        });
        await Test("TAG-T04","任一、全部、未分類與文字AND、字面LIKE",async root=>{
            using var s=new StorageService(root);
            await Card(s,"A%",new TagChoice(null,"客戶"),new TagChoice(null,"台北"));
            await Card(s,"B",new TagChoice(null,"供應商"),new TagChoice(null,"台北"));
            await Card(s,"C",new TagChoice(null,"客戶"));await Card(s,"D");
            var tags=(await s.GetTagsAsync(null)).Items;var ids=tags.Where(t=>t.DisplayName is "客戶" or "台北").Select(t=>t.TagId).ToArray();
            Assert((await s.QueryContactsAsync(new(null,ids))).TotalCount==3,"any");
            Assert((await s.QueryContactsAsync(new(null,ids,true))).TotalCount==1,"all");
            Assert((await s.QueryContactsAsync(new(null,Array.Empty<string>(),Untagged:true))).Items.Single().Name=="D","untagged");
            Assert((await s.QueryContactsAsync(new("%",ids))).Items.Single().Name=="A%","literal");
            Assert((await s.QueryContactsAsync(new(null,Array.Empty<string>(),true))).TotalCount==4,"empty selection");
        });
        await Test("TAG-T05","65張多重命中穩定分頁30/30/5",async root=>{
            using var s=new StorageService(root);
            for(int i=0;i<65;i++)await Card(s,"卡"+i,new TagChoice(null,"X"),new TagChoice(null,"Y"));
            using(var c=Open(s.DatabasePath))Sql(c,"UPDATE contact_records SET created_at='2026-01-01T00:00:00+00:00';");
            var ids=(await s.GetTagsAsync(null)).Items.Select(t=>t.TagId).ToArray();
            var seen=new List<string>();
            for(int page=0;page<3;page++){var r=await s.QueryContactsAsync(new(null,ids),page);Assert(r.TotalCount==65&&r.Items.Count==(page==2?5:30),"page");seen.AddRange(r.Items.Select(i=>i.ContactId));}
            Assert(seen.Distinct().Count()==65&&seen.SequenceEqual(seen.OrderBy(i=>i,StringComparer.Ordinal)),"stable no duplicates");
        });
        await Test("TAG-T06","兩服務分類CAS衝突不覆寫不留新項目",async root=>{
            using var a=new StorageService(root);var vm=await Card(a,"A");
            using var b=new StorageService(root);
            Assert((await a.SetTagsAsync(vm.CurrentContactId,0,new[]{new TagChoice(null,"X")})).Succeeded,"first");
            Assert(!(await b.SetTagsAsync(vm.CurrentContactId,0,new[]{new TagChoice(null,"Y")})).Succeeded,"conflict");
            Assert((await b.GetTagsAsync(null)).TotalCount==1,"rollback catalog");
        });
        await Test("TAG-T07","分類單獨保存不增文字revision；文字保存保留外部分類",async root=>{
            using var s=new StorageService(root);var vm=await Card(s,"A",new TagChoice(null,"X"));
            vm.SetClassification(new[]{new TagChoice(null,"Y")});
            Assert((await vm.SaveCurrentDraftAsync()).Succeeded&&vm.CurrentRevision==1&&!vm.HasUnsavedChanges,"tags-only");
            var state=(await s.GetTagStateAsync(vm.CurrentContactId))!;
            Assert((await s.SetTagsAsync(vm.CurrentContactId,state.Revision,new[]{new TagChoice(null,"Z")})).Succeeded,"external");
            vm.NameRow.Value="B";vm.NameRow.Confirm();
            Assert((await vm.SaveCurrentDraftAsync()).Succeeded&&vm.CurrentRevision==2,"text save");
            Assert((await s.GetTagStateAsync(vm.CurrentContactId))!.Tags.Single().DisplayName=="Z"&&!vm.HasUnsavedChanges,"preserve latest");
        });
        await Test("TAG-T08","ImageMissing可分類；Failed、Pending及待刪拒絕",async root=>{
            using var s=new StorageService(root);var vm=await Card(s,"A");
            foreach(string state in new[]{"ImageMissing","Failed","PendingFinalize"})
            {
                using(var c=Open(s.DatabasePath))Sql(c,$"UPDATE contact_records SET persistence_state='{state}' WHERE contact_id='{vm.CurrentContactId}';");
                var result=await s.SetTagsAsync(vm.CurrentContactId,0,Array.Empty<TagChoice>());
                Assert(result.Succeeded==(state=="ImageMissing"),state);
            }
            using(var c=Open(s.DatabasePath))Sql(c,"UPDATE contact_records SET persistence_state='Ready',pending_delete=1;");
            Assert(!(await s.SetTagsAsync(vm.CurrentContactId,0,Array.Empty<TagChoice>())).Succeeded,"pending delete");
        });
        await Test("TAG-T09","移除單張及刪名片不影響其他名片或字典",async root=>{
            using var s=new StorageService(root);var a=await Card(s,"A",new TagChoice(null,"X"));var b=await Card(s,"B",new TagChoice(null,"X"));
            Assert((await s.SetTagsAsync(a.CurrentContactId,1,Array.Empty<TagChoice>())).Succeeded,"remove one");
            Assert((await s.GetTagStateAsync(b.CurrentContactId))!.Tags.Count==1,"other");
            Assert((await s.DeleteContactAsync(b.CurrentContactId)).Succeeded&&(await s.GetTagsAsync(null)).TotalCount==1,"cascade retains catalog");
        });
        await Test("TAG-T10","beta.8 schema1/WAL升級前備份與原始資料保留",async root=>{
            StoragePathHelper.EnsureStorageDirectories(root);string db=Path.Combine(root,StoragePathHelper.DatabaseFileName);
            using var keeper=Open(db);Sql(keeper,"PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");Sql(keeper,LegacySchemaFixture.Schema);
            Sql(keeper,"INSERT INTO contact_records VALUES('legacy','0.3',3,'Ready','Manual','2026-01-01T00:00:00+00:00','2026-01-02T00:00:00+00:00',NULL,'op','fp',0); INSERT INTO contact_values VALUES('v','legacy','Custom','def','欄名','原文',NULL,'欄名',0,'Manual','2026-01-01T00:00:00+00:00');");
            using var upgraded=new StorageService(root);Assert(upgraded.IsAvailable,upgraded.ErrorMessage??"upgrade");
            Assert(Convert.ToInt32(Sql(keeper,"PRAGMA user_version;"))==2,"schema");
            Assert(Convert.ToString(Sql(keeper,"SELECT raw_value FROM contact_values;"))=="原文"&&Convert.ToInt32(Sql(keeper,"SELECT revision FROM contact_records;"))==3,"legacy data");
            var backups=Directory.GetFiles(Path.Combine(root,"schema-backups"),"schema1.sqlite3",SearchOption.AllDirectories);
            Assert(backups.Length==1,"backup once");
            using var copy=Open(backups[0]);
            Assert(Convert.ToInt32(Sql(copy,"PRAGMA user_version;"))==1&&Convert.ToString(Sql(copy,"SELECT raw_value FROM contact_values;"))=="原文","WAL committed data in backup");
            Assert(Convert.ToString(Sql(copy,"PRAGMA integrity_check;"))=="ok","integrity");
            copy.Close();
            var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(backups[0])!,"manifest.json")));
            Assert(manifest.RootElement.GetProperty("SHA256").GetString()==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backups[0]))),"hash");
            using var again=new StorageService(root);Assert(again.IsAvailable&&Directory.GetFiles(Path.Combine(root,"schema-backups"),"schema1.sqlite3",SearchOption.AllDirectories).Length==1,"no repeat");
            await Task.CompletedTask;
        });
        await Test("TAG-T10B","升級備份至DDL之間writer reservation阻止未備份寫入",async root=>{
            StoragePathHelper.EnsureStorageDirectories(root);string db=Path.Combine(root,StoragePathHelper.DatabaseFileName);
            using(var c=Open(db))Sql(c,LegacySchemaFixture.Schema);
            bool competingBlocked=false;
            using var s=new StorageService(root,false);
            s.SetFailureInjectionHook(stage=>{
                if(stage!="AfterTagSchemaBackup")return;
                using var other=Open(db);other.DefaultTimeout=1;
                try{Sql(other,"INSERT INTO contact_records VALUES('race','0.3',1,'Ready','Manual','2026-01-01T00:00:00+00:00','2026-01-01T00:00:00+00:00',NULL,'op','fp',0);");}
                catch(SqliteException ex) when(ex.SqliteErrorCode==5||ex.SqliteErrorCode==6){competingBlocked=true;}
            });
            s.InitializeDatabase();Assert(competingBlocked,"competing writer must not commit between backup and migration");
            using var verify=Open(db);Assert(Convert.ToInt32(Sql(verify,"SELECT count(*) FROM contact_records;"))==0,"no race inserted");
            await Task.CompletedTask;
        });
        await Test("TAG-T11","DDL提交前失敗回滾，重試安全",async root=>{
            StoragePathHelper.EnsureStorageDirectories(root);string db=Path.Combine(root,StoragePathHelper.DatabaseFileName);
            using(var c=Open(db))Sql(c,LegacySchemaFixture.Schema);
            using(var s=new StorageService(root,false)){
                s.SetFailureInjectionHook(p=>{if(p=="BeforeTagSchemaCommit")throw new IOException("fixture");});
                try{s.InitializeDatabase();throw new Exception("expected failure");}catch(IOException){}
            }
            using(var c=Open(db))Assert(Convert.ToInt32(Sql(c,"PRAGMA user_version;"))==1&&Convert.ToInt32(Sql(c,"SELECT count(*) FROM sqlite_master WHERE name='contact_tags';"))==0,"atomic rollback");
            using var retry=new StorageService(root);Assert(retry.IsAvailable,retry.ErrorMessage??"retry");await Task.CompletedTask;
        });
        await Test("TAG-T12","未知版本及不完整schema2拒寫並保留bytes",async root=>{
            StoragePathHelper.EnsureStorageDirectories(root);string db=Path.Combine(root,StoragePathHelper.DatabaseFileName);
            using(var c=Open(db))Sql(c,"CREATE TABLE unknown(id); PRAGMA user_version=3;");
            byte[] before=File.ReadAllBytes(db);using var s=new StorageService(root);
            Assert(!s.IsAvailable&&before.SequenceEqual(File.ReadAllBytes(db)),"unknown bytes");
            await Task.CompletedTask;
        });
        await Test("TAG-T13","升級快照可獨立回讀，升級後資料及備份均保存",async root=>{
            StoragePathHelper.EnsureStorageDirectories(root);string db=Path.Combine(root,StoragePathHelper.DatabaseFileName);
            using(var c=Open(db))Sql(c,LegacySchemaFixture.Schema);
            using var s=new StorageService(root);Assert(s.IsAvailable,s.ErrorMessage??"init");
            await Card(s,"升級後",new TagChoice(null,"新分類"));
            string backup=Directory.GetFiles(Path.Combine(root,"schema-backups"),"schema1.sqlite3",SearchOption.AllDirectories).Single();
            using var original=Open(backup);Assert(Convert.ToInt32(Sql(original,"SELECT count(*) FROM contact_records;"))==0,"snapshot before");
            Assert((await s.GetContactsPagedAsync(null,0)).TotalCount==1,"new data retained");
        });
        await Test("TAG-T19","字典取消前不寫，建立標籤與圖一起定稿",async root=>{
            using var s=new StorageService(root);var vm=new MainViewModel(new CustomFieldCatalogService(Path.Combine(root,"catalog.json")),null,s);
            vm.SetClassification(new[]{new TagChoice(null,"草稿")});
            Assert((await s.GetTagsAsync(null)).TotalCount==0&&vm.HasUnsavedChanges,"local only dirty");
            vm.SetClassification(Array.Empty<TagChoice>());
            Assert((await s.GetTagsAsync(null)).TotalCount==0&&!vm.HasUnsavedChanges,"cancel");
            await Card(s,"完整",new TagChoice(null,"分類"));
            var d=(await s.GetContactsPagedAsync(null,0)).Items.Single();Assert(d.Tags.Count==1&&d.PersistenceState=="Ready","finalized");
        });
        await Test("TAG-T20","換圖新建清分類，空草稿僅分類不能存名片",async root=>{
            using var s=new StorageService(root);var vm=await Card(s,"A",new TagChoice(null,"X"));
            vm.CreateBlankDraft();Assert(vm.Classification.Count==0,"new clears");
            vm.SetClassification(new[]{new TagChoice(null,"Y")});
            Assert(!(await vm.SaveCurrentDraftAsync()).Succeeded&&vm.HasUnsavedChanges,"empty blocked");
            byte[] bmp=Issue0011AutomatedTestSuite.CreateSyntheticBmp(20,12,10,20,30);
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(bmp,"fixture.bmp","ImagePickerCopy","image/bmp",20,12));
            Assert(vm.Classification.Count==0,"image clears");
        });
        await Test("TAG-T21","Pending同文字不同標籤先完成A，再保存B",async root=>{
            using var s=new StorageService(root);
            var vm=new MainViewModel(new CustomFieldCatalogService(Path.Combine(root,"catalog.json")),null,s);
            byte[] bmp=Issue0011AutomatedTestSuite.CreateSyntheticBmp(20,12,10,20,30);
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(bmp,"fixture.bmp","ImagePickerCopy","image/bmp",20,12));
            vm.NameRow.Value="A";vm.NameRow.Confirm();vm.SetClassification(new[]{new TagChoice(null,"X")});
            s.SetFailureInjectionHook(p=>{if(p=="AfterPendingFinalizeTransaction")throw new IOException("fixture");});
            Assert(!(await vm.SaveCurrentDraftAsync()).Succeeded,"interrupted");
            s.SetFailureInjectionHook(null);vm.SetClassification(new[]{new TagChoice(null,"Y")});
            var retry=await vm.SaveCurrentDraftAsync();
            Assert(!retry.Succeeded&&retry.RequiresSaveAgain&&vm.HasUnsavedChanges,"A finished B dirty");
            Assert((await s.GetTagStateAsync(vm.CurrentContactId))!.Tags.Single().DisplayName=="X","A kept");
            Assert((await vm.SaveCurrentDraftAsync()).Succeeded&&(await s.GetTagStateAsync(vm.CurrentContactId))!.Tags.Single().DisplayName=="Y"&&vm.CurrentRevision==1&&!vm.HasUnsavedChanges,"B tags-only persisted");
        });
        await Test("TAG-T22","使用中更名維持ID與原資料，碰撞/CAS拒絕",async root=>{
            using var s=new StorageService(root);var a=await Card(s,"A",new TagChoice(null,"X"));var b=await Card(s,"B",new TagChoice(null,"X"));
            var tag=(await s.GetTagsAsync(null)).Items.Single();var before=await s.GetContactDetailAsync(a.CurrentContactId);
            var rename=await s.RenameTagAsync(tag,"客戶");Assert(rename.Succeeded&&rename.Tag!.TagId==tag.TagId,"stable ID");
            Assert((await s.GetContactDetailAsync(b.CurrentContactId))!.Tags.Single().DisplayName=="客戶","all updated");
            Assert(!(await s.RenameTagAsync(tag,"舊競爭")).Succeeded,"CAS");
            await s.CreateTagAsync("Y");Assert(!(await s.RenameTagAsync(rename.Tag!,"Y")).Succeeded,"collision");
            var after=await s.GetContactDetailAsync(a.CurrentContactId);Assert(after!.Revision==before!.Revision&&after.TagRevision==before.TagRevision&&after.VerifiedImageBytes!.SequenceEqual(before.VerifiedImageBytes!),"preserve");
        });
        await Test("TAG-T23","列使用名片確認後全移除，保留名片/圖片/其他標籤",async root=>{
            using var s=new StorageService(root);var a=await Card(s,"A",new TagChoice(null,"X"),new TagChoice(null,"Y"));var b=await Card(s,"B",new TagChoice(null,"X"));
            var tag=(await s.GetTagsAsync("X")).Items.Single();var preview=await s.PreviewTagDeleteAsync(tag.TagId);
            Assert(preview.ContactCount==2&&(await s.QueryContactsAsync(new(null,new[]{tag.TagId}))).TotalCount==2,"list");
            var beforeA=await s.GetContactDetailAsync(a.CurrentContactId);var beforeB=await s.GetContactDetailAsync(b.CurrentContactId);
            Assert((await s.DeleteTagAsync(preview)).Succeeded,"confirm");
            var afterA=await s.GetContactDetailAsync(a.CurrentContactId);var afterB=await s.GetContactDetailAsync(b.CurrentContactId);
            Assert(afterA!.Tags.Single().DisplayName=="Y"&&afterB!.Tags.Count==0&&afterA.TagRevision==2&&afterB.TagRevision==2,"only this tag");
            Assert(afterA.Revision==beforeA!.Revision&&afterB!.Revision==beforeB!.Revision&&afterA.VerifiedImageBytes!.SequenceEqual(beforeA.VerifiedImageBytes!)&&afterB.VerifiedImageBytes!.SequenceEqual(beforeB.VerifiedImageBytes!),"cards images kept");
        });
        await Test("TAG-T23B","刪除清單變动必須重確認、占用與失敗回滾",async root=>{
            using var s=new StorageService(root);var a=await Card(s,"A",new TagChoice(null,"X"));var tag=(await s.GetTagsAsync(null)).Items.Single();
            var preview=await s.PreviewTagDeleteAsync(tag.TagId);await Card(s,"B",new TagChoice(tag.TagId,tag.DisplayName));
            Assert(!(await s.DeleteTagAsync(preview)).Succeeded,"stale membership rejected");
            var current=await s.PreviewTagDeleteAsync(tag.TagId);
            s.SetFailureInjectionHook(p=>{if(p=="BeforeTagDeleteCommit")throw new IOException("fixture");});
            Assert(!(await s.DeleteTagAsync(current)).Succeeded,"rollback");s.SetFailureInjectionHook(null);
            Assert((await s.PreviewTagDeleteAsync(tag.TagId)).ContactCount==2,"links preserved");
            using(var c=Open(s.DatabasePath))Sql(c,"UPDATE contact_records SET persistence_state='PendingFinalize';");
            Assert((await s.PreviewTagDeleteAsync(tag.TagId)).Busy&&!(await s.DeleteTagAsync(await s.PreviewTagDeleteAsync(tag.TagId))).Succeeded,"busy");
        });
        await Test("TAG-T24","已存分類與掃描乾淨草稿同步，未存選擇保留且不復活已刪ID",async root=>{
            using var s=new StorageService(root);var vm=await Card(s,"A",new TagChoice(null,"X"));
            Assert((await s.SetTagsAsync(vm.CurrentContactId,1,new[]{new TagChoice(null,"Y")})).Succeeded,"external");
            await vm.RefreshClassificationAsync();Assert(vm.Classification.Single().DisplayName=="Y"&&!vm.HasUnsavedChanges,"clean refresh");
            vm.SetClassification(new[]{new TagChoice(null,"local")});var state=(await s.GetTagStateAsync(vm.CurrentContactId))!;
            await s.SetTagsAsync(vm.CurrentContactId,state.Revision,Array.Empty<TagChoice>());
            await vm.RefreshClassificationAsync();Assert(vm.Classification.Single().DisplayName=="local"&&vm.HasUnsavedChanges,"dirty retained");
            Assert(!(await vm.SaveCurrentDraftAsync()).Succeeded,"conflict protects");
            var tag=(await s.GetTagsAsync("Y")).Items.Single();await s.DeleteTagAsync(await s.PreviewTagDeleteAsync(tag.TagId));
            Assert(!(await s.SetTagsAsync(vm.CurrentContactId,3,new[]{new TagChoice(tag.TagId,"Y")})).Succeeded,"old ID no resurrection");
        });
        await Test("TAG-T12B","備份路徑失敗停升級且保留schema1",async root=>{
            StoragePathHelper.EnsureStorageDirectories(root);string db=Path.Combine(root,StoragePathHelper.DatabaseFileName);
            using(var c=Open(db))Sql(c,LegacySchemaFixture.Schema);
            File.WriteAllText(Path.Combine(root,"schema-backups"),"synthetic blocker");
            using var s=new StorageService(root);
            Assert(!s.IsAvailable,"backup failure must reject");
            using var verify=Open(db);
            Assert(Convert.ToInt32(Sql(verify,"PRAGMA user_version;"))==1&&Convert.ToInt32(Sql(verify,"SELECT count(*) FROM sqlite_master WHERE name='contact_tags';"))==0,"schema preserved");
            await Task.CompletedTask;
        });
        await Test("TAG-T16","1005標籤字典分頁、有界查詢及大量複選",async root=>{
            using var s=new StorageService(root);
            var choices=Enumerable.Range(0,1005).Select(i=>new TagChoice(null,$"分類{i:D4}")).ToArray();
            var vm=await Card(s,"大量分類",choices);
            var page=await s.GetTagsAsync(null);
            Assert(page.TotalCount==1005&&page.Items.Count==30&&page.HasNextPage,"catalog bounded");
            var state=(await s.GetTagStateAsync(vm.CurrentContactId))!;
            Assert(state.Tags.Count==1005,"no arbitrary limit");
            var ids=state.Tags.Select(t=>t.TagId).ToArray();
            Assert((await s.QueryContactsAsync(new(null,ids,true))).Items.Count==1,"JSON query parameter supports many selections");
            using var c=Open(s.DatabasePath);
            Assert(Convert.ToInt32(Sql(c,"SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_contact_tags_tag';"))==1,"lookup index");
        });
        await Test("TAG-T21B","首存中斷原選擇重試與同時修改的草稿dirty",async root=>{
            using var s=new StorageService(root);
            var vm=new MainViewModel(new CustomFieldCatalogService(Path.Combine(root,"catalog.json")),null,s);
            byte[] bmp=Issue0011AutomatedTestSuite.CreateSyntheticBmp(20,12,10,20,30);
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(bmp,"fixture.bmp","ImagePickerCopy","image/bmp",20,12));
            vm.NameRow.Value="A";vm.NameRow.Confirm();vm.SetClassification(new[]{new TagChoice(null,"X")});
            s.SetFailureInjectionHook(p=>{if(p=="AfterPendingFinalizeTransaction")throw new IOException("fixture");});
            Assert(!(await vm.SaveCurrentDraftAsync()).Succeeded,"interrupted");
            s.SetFailureInjectionHook(null);
            Assert((await vm.SaveCurrentDraftAsync()).Succeeded&&!vm.HasUnsavedChanges,"same payload retry");
            vm.SetClassification(new[]{new TagChoice(null,"Y")});
            var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            s.SetFailureInjectionHook(p=>{if(p=="BeforeTagAssignmentCommit"){entered.TrySetResult(true);release.Task.GetAwaiter().GetResult();}});
            var saving=vm.SaveCurrentDraftAsync();await entered.Task;
            vm.SetClassification(new[]{new TagChoice(null,"Y"),new TagChoice(null,"Z")});release.SetResult(true);
            Assert((await saving).Succeeded&&vm.HasUnsavedChanges&&vm.Classification.Count==2 && vm.Classification.Any(t=>t.DisplayName=="Y"&&t.TagId is not null),"new edit dirty and mapping retained");
            s.SetFailureInjectionHook(null);
            var y=(await s.GetTagsAsync("Y")).Items.Single();Assert((await s.RenameTagAsync(y,"更名Y")).Succeeded,"rename mapped");
            await vm.RefreshClassificationAsync();
            Assert((await vm.SaveCurrentDraftAsync()).Succeeded&&!vm.HasUnsavedChanges&&(await s.GetTagsAsync(null)).TotalCount==3,"next save keeps stable renamed ID");
        });
        foreach(var (id,desc) in new[]{("TAG-T14","真正GUI操作／取消／快速切換"),("TAG-T15","DPI／讀屏／明暗／窄窗"),("TAG-T18","乾淨離線Windows及人類驗收")})
        {
            report.TestCases.Add(new(){TestId=id,Description=desc,Status="NOT_RUN",Details="CLI不代替實際GUI或人類驗收。"});
            report.TotalTests++;report.NotRunCount++;
        }
        return report;
    }
}
