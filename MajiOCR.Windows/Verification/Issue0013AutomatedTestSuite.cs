using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Verification;

public static class Issue0013AutomatedTestSuite
{
    public static async Task<Issue0011TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        MainViewModel.IsHeadlessEnvironment = true;
        var report = new Issue0011TestSuiteReport { IssueId = "ISSUE-0013" };
        string root = Path.Combine(Path.GetFullPath(evidenceDir), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Record(string id, string description, bool passed)
        {
            report.TestCases.Add(new Issue0011TestCaseResult
            { TestId = id, Description = description, Status = passed ? "PASS" : "FAIL" });
        }
        MainViewModel Vm(string name, IStorageService? storage = null) =>
            new(new CustomFieldCatalogService(Path.Combine(root, name, "fields.json")), null, storage);
        var vm = Vm("bulk");
        vm.NameRow.Value = "合成姓名";
        vm.NameRow.Confirm();
        var confirmedAt = vm.NameRow.ConfirmedAt;
        vm.CompanyRow.Value = "合成公司";
        vm.TitleRow.Value = "  ";
        vm.AddPhoneRow("123"); vm.AddPhoneRow("456"); vm.AddEmailRow("a@example.invalid");
        vm.DraftRows.Add(new DraftRowItem { ValueKind = CanonicalValueKind.Custom,
            CustomFieldDefinitionId = Guid.NewGuid().ToString(), CustomFieldNameSnapshot = "合成自訂", Value = "789" });
        vm.DraftRows.Add(new DraftRowItem { ValueKind = CanonicalValueKind.Unclassified, Value = "保留候選" });
        int count = vm.ConfirmAllDraftRows(out var error);
        var rows = DraftSnapshotFactory.CollectAllDistinctRows(vm);
        Record("CONF13-T01", "一次確認所有非空種類、多值/自訂/未分類，去重且保留空欄與既有時間",
            count == 6 && error == null && rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).All(r => r.IsConfirmed)
            && !vm.TitleRow.IsConfirmed && vm.NameRow.ConfirmedAt == confirmedAt);
        Record("CONF13-T02", "重複按全部確認不改確認時間；改值重新待確認",
            vm.ConfirmAllDraftRows(out error) == 0 && error == null && vm.NameRow.ConfirmedAt == confirmedAt);
        vm.NameRow.Value = "改值";
        Record("CONF13-T03", "人工修改回待確認及空白拒絕確認",
            !vm.NameRow.IsConfirmed && vm.NameRow.ConfirmedAt == null && Vm("empty").ConfirmAllDraftRows(out error) == 0 && error != null);
        var conflict = Vm("conflict"); conflict.NameRow.Value = "A";
        conflict.DraftRows.Add(new DraftRowItem { ValueKind = CanonicalValueKind.Name, Value = "B" });
        Record("CONF13-T04", "單值衝突整批不改動、忙碌拒絕",
            conflict.ConfirmAllDraftRows(out error) == 0 && error != null &&
            DraftSnapshotFactory.CollectAllDistinctRows(conflict).All(r => !r.IsConfirmed));
        vm.IsBusy = true;
        Record("CONF13-T05", "OCR忙碌/儲存中不能全部確認",
            vm.ConfirmAllDraftRows(out error) == 0 && error != null && !vm.NameRow.IsConfirmed);
        vm.IsBusy = false; vm.IsSaving = true;
        bool saveBusy = vm.ConfirmAllDraftRows(out error) == 0 && error != null;
        vm.IsSaving = false;
        if (!saveBusy) report.TestCases.Last().Status = "FAIL";

        using var storage = new StorageService(Path.Combine(root, "storage"));
        var saved = Vm("saving", storage);
        await saved.SetSessionImageAsync(ImageSessionInfo.Create(Bmp(), "合成.bmp", "ImagePickerCopy", "image/bmp", 2, 2));
        saved.NameRow.Value = "原始合成姓名"; saved.ConfirmAllDraftRows(out _);
        string token = saved.CaptureSaveConfirmationState();
        var cancel = await saved.SaveConfirmedDraftAsync(false, token);
        Record("SAVE13-T01", "取消儲存無名片寫入/修訂/草稿改動",
            cancel.ErrorCode == "SAVE_CANCELLED" && (await storage.GetContactsPagedAsync(null,0)).TotalCount == 0 &&
            saved.CurrentRevision == 0 && saved.CaptureSaveConfirmationState() == token);
        var first = await saved.SaveConfirmedDraftAsync(true,token);
        Record("SAVE13-T02", "確認才走原交易，儲存與讀回成功",
            first.Succeeded && saved.CurrentRevision == 1 && (await storage.GetContactDetailAsync(saved.CurrentContactId))?.Name == "原始合成姓名");
        token = saved.CaptureSaveConfirmationState(); saved.NameRow.Value = "變更姓名";
        var stale = await saved.SaveConfirmedDraftAsync(true,token);
        Record("SAVE13-T03", "確認後文字/狀態改變拒絕且原資料不變",
            stale.ErrorCode == "SAVE_CONFIRMATION_CHANGED" && saved.CurrentRevision == 1 &&
            (await storage.GetContactDetailAsync(saved.CurrentContactId))?.Name == "原始合成姓名");
        saved.ConfirmAllDraftRows(out _);token=saved.CaptureSaveConfirmationState();
        saved.SetClassification(new[] { new TagChoice(null,"新分類") });
        stale=await saved.SaveConfirmedDraftAsync(true,token);
        Record("SAVE13-T04", "分類更改須重確認，不新增字典或關聯",
            stale.ErrorCode == "SAVE_CONFIRMATION_CHANGED" && (await storage.GetTagStateAsync(saved.CurrentContactId))!.Tags.Count==0);
        token=saved.CaptureSaveConfirmationState();saved.CurrentSession!.ImageBytes[54] ^= 1;
        stale=await saved.SaveConfirmedDraftAsync(true,token);saved.CurrentSession.ImageBytes[54] ^= 1;
        Record("SAVE13-T05", "圖片bytes改變拒絕儲存",stale.ErrorCode=="SAVE_CONFIRMATION_CHANGED");
        token=saved.CaptureSaveConfirmationState();saved.NameRow.Unconfirm();
        stale=await saved.SaveConfirmedDraftAsync(true,token);
        var unconfirmed=await saved.SaveConfirmedDraftAsync(true,saved.CaptureSaveConfirmationState());
        Record("SAVE13-T06", "確認狀態改變及原未確認守門",
            stale.ErrorCode=="SAVE_CONFIRMATION_CHANGED" && unconfirmed.ErrorCode==nameof(DraftValidationCode.UnconfirmedFields));
        saved.ConfirmAllDraftRows(out _);token=saved.CaptureSaveConfirmationState();
        saved.CreateBlankDraft();stale=await saved.SaveConfirmedDraftAsync(true,token);
        Record("SAVE13-T07", "更換草稿/目標拒絕，名片數維持",stale.ErrorCode=="SAVE_CONFIRMATION_CHANGED" && (await storage.GetContactsPagedAsync(null,0)).TotalCount==1);
        report.TotalTests=report.TestCases.Count;
        report.PassCount=report.TestCases.Count(t=>t.Status=="PASS");report.FailCount=report.TestCases.Count(t=>t.Status=="FAIL");
        return report;
    }
    private static byte[] Bmp()
    {
        byte[] b=new byte[70];b[0]=66;b[1]=77;
        BitConverter.GetBytes(70).CopyTo(b,2);BitConverter.GetBytes(54).CopyTo(b,10);
        BitConverter.GetBytes(40).CopyTo(b,14);BitConverter.GetBytes(2).CopyTo(b,18);BitConverter.GetBytes(2).CopyTo(b,22);
        BitConverter.GetBytes((short)1).CopyTo(b,26);BitConverter.GetBytes((short)24).CopyTo(b,28);
        BitConverter.GetBytes(16).CopyTo(b,34);return b;
    }
}
