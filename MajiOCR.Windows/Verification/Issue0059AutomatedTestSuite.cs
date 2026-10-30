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

public static class Issue0059AutomatedTestSuite
{
    public static async Task<Issue0011TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        MainViewModel.IsHeadlessEnvironment = true;
        var report = new Issue0011TestSuiteReport
        {
            IssueId = "ISSUE-0059"
        };

        string runRoot = Path.Combine(Path.GetFullPath(evidenceDir), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);

        void Record(string id, string desc, bool passed, string details = "")
        {
            report.TestCases.Add(new Issue0011TestCaseResult
            {
                TestId = id,
                Description = desc,
                Status = passed ? "PASS" : "FAIL",
                Details = details
            });
        }

        MainViewModel CreateVm(string testName, IStorageService? storage = null)
        {
            string catPath = Path.Combine(runRoot, $"{testName}_fields.json");
            return new MainViewModel(
                new CustomFieldCatalogService(catPath),
                null,
                storage);
        }

        // T01: CheckSingleValueConflict 偵測未確認的單值列
        {
            var vm = CreateVm("t01");
            vm.NameRow.Value = "舊姓名（待確認）";
            vm.EnsureRowInDraft(vm.NameRow);
            var newCandidate = new DraftRowItem
            {
                Id = Guid.NewGuid().ToString("N"),
                ValueKind = CanonicalValueKind.Name,
                Value = "新姓名"
            };
            var conflict = vm.CheckSingleValueConflict(newCandidate, CanonicalValueKind.Name);
            bool t01Passed = conflict != null && conflict.Value == "舊姓名（待確認）";
            Record("ISSUE-0059-T01", "未確認但有內容的單值列，CheckSingleValueConflict 能即時精準偵測不漏報", t01Passed);
        }

        // T02: 單值多筆時超出首筆的 row 自動納入 UnclassifiedRows，絕不隱形
        {
            var vm = CreateVm("t02");
            vm.NameRow.Value = "第一筆姓名";
            var ghostRow = new DraftRowItem
            {
                Id = Guid.NewGuid().ToString("N"),
                ValueKind = CanonicalValueKind.Name,
                Value = "第二筆姓名（幽靈衝突）"
            };
            vm.DraftRows.Add(ghostRow);
            vm.SyncProjections();
            bool t02Passed = vm.UnclassifiedRows.Contains(ghostRow) &&
                             vm.NameRow.Value == "第一筆姓名";
            Record("ISSUE-0059-T02", "重複單值列自動納入 UnclassifiedRows 兜底展示，保證 UI 100% 可視可調不隱形", t02Passed);
        }

        // T03: 替換舊列時舊列轉為待分類，新列成為唯一單值，全部確認通過且可正常儲存
        {
            using var storage = new StorageService(Path.Combine(runRoot, "t03_storage"));
            var vm = CreateVm("t03", storage);

            // 1. 先附上虛擬名片圖片
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(Bmp(), "測試名片.bmp", "ImagePickerCopy", "image/bmp", 2, 2));

            // 2. 模擬名片填入姓名、公司、職稱
            vm.NameRow.Value = "舊姓名";
            vm.EnsureRowInDraft(vm.NameRow);
            vm.CompanyRow.Value = "台北捷運公司";
            vm.EnsureRowInDraft(vm.CompanyRow);
            vm.TitleRow.Value = "資訊處 處長";
            vm.EnsureRowInDraft(vm.TitleRow);

            // 3. 待分類中有一列欲替換為姓名
            var candRow = new DraftRowItem
            {
                Id = Guid.NewGuid().ToString("N"),
                ValueKind = CanonicalValueKind.Unclassified,
                Value = "劉瑞麟 博士"
            };
            vm.DraftRows.Add(candRow);

            // 4. 模擬替換邏輯：舊列退回待分類，新列成為姓名
            var conflictRow = vm.CheckSingleValueConflict(candRow, CanonicalValueKind.Name);
            bool detectedConflict = conflictRow != null;
            if (conflictRow != null)
            {
                conflictRow.ValueKind = CanonicalValueKind.Unclassified;
                conflictRow.CustomFieldDefinitionId = null;
                conflictRow.CustomFieldNameSnapshot = null;
                conflictRow.Unconfirm();
                conflictRow.MarkModified();
            }
            candRow.ValueKind = CanonicalValueKind.Name;
            vm.SyncProjections();

            int confirmedCount = vm.ConfirmAllDraftRows(out string? error);
            bool confirmPassed = confirmedCount > 0 && error == null;

            string confirmationState = vm.CaptureSaveConfirmationState();
            var saveResult = await vm.SaveConfirmedDraftAsync(true, confirmationState);
            bool savePassed = saveResult.Succeeded && vm.CurrentRevision == 1;

            bool t03Passed = detectedConflict && confirmPassed && savePassed && vm.NameRow.Value == "劉瑞麟 博士";
            Record("ISSUE-0059-T03", "單值衝突替換舊列後，舊列退回待分類無重複單值，全部確認順利通過且名片成功儲存", t03Passed,
                $"DetectedConflict={detectedConflict}, ConfirmPassed={confirmPassed}, SavePassed={savePassed}, SaveError={saveResult.ErrorMessage}, ConfirmError={error}");
        }

        // T04: 姓名、公司、職稱皆有值時，無需清空即可正常儲存名片
        {
            using var storage = new StorageService(Path.Combine(runRoot, "t04_storage"));
            var vm = CreateVm("t04", storage);

            await vm.SetSessionImageAsync(ImageSessionInfo.Create(Bmp(), "測試名片.bmp", "ImagePickerCopy", "image/bmp", 2, 2));

            vm.NameRow.Value = "劉瑞麟";
            vm.EnsureRowInDraft(vm.NameRow);
            vm.CompanyRow.Value = "台北捷運公司";
            vm.EnsureRowInDraft(vm.CompanyRow);
            vm.TitleRow.Value = "資訊處 處長";
            vm.EnsureRowInDraft(vm.TitleRow);

            int confirmedCount = vm.ConfirmAllDraftRows(out string? error);
            string token = vm.CaptureSaveConfirmationState();
            var saveResult = await vm.SaveConfirmedDraftAsync(true, token);

            bool t04Passed = error == null && saveResult.Succeeded &&
                             !string.IsNullOrEmpty(vm.NameRow.Value) &&
                             !string.IsNullOrEmpty(vm.CompanyRow.Value) &&
                             !string.IsNullOrEmpty(vm.TitleRow.Value);
            Record("ISSUE-0059-T04", "姓名、公司、職稱完整填寫時，全部確認與儲存暢行無阻，無需清空任何欄位", t04Passed,
                $"SaveSucceeded={saveResult.Succeeded}, SaveError={saveResult.ErrorMessage}, ConfirmError={error}");
        }

        // T05: 局部辨識遇既有單值欄位時，安全自動路由至待分類
        {
            var vm = CreateVm("t05");
            vm.NameRow.Value = "既有姓名";
            vm.EnsureRowInDraft(vm.NameRow);

            var candidates = new List<OcrCandidateItem>
            {
                new()
                {
                    Text = "姓名：新辨識姓名", // FieldSuggester 會判定為 Name
                    X = 10, Y = 10, Width = 50, Height = 20, Source = "ROI"
                }
            };
            vm.PopulateDraftRowsFromCandidates(candidates, vm.CurrentRequestId, isRegion: true);

            var extraNameInSingleSlot = vm.DraftRows.Count(r => r.ValueKind == CanonicalValueKind.Name);
            bool t05Passed = extraNameInSingleSlot == 1 &&
                             vm.UnclassifiedRows.Any(r => r.Value.Contains("新辨識姓名"));
            Record("ISSUE-0059-T05", "局部辨識遇既有單值欄位，安全自動路由至待分類，不污染單值欄位", t05Passed);
        }

        // T06: CreateBlankDraft 完整重置圖片與工作階段狀態，避免跳回上一次狀態
        {
            var vm = CreateVm("t06");
            vm.NameRow.Value = "上一張名片資料";
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(Bmp(), "舊名片.bmp", "ImagePickerCopy", "image/bmp", 2, 2));
            string oldContactId = vm.CurrentContactId;

            // 建立空白新工作階段
            vm.CreateBlankDraft();

            bool t06Passed = vm.CurrentSession == null &&
                             vm.DisplayImage == null &&
                             vm.CurrentContactId != oldContactId &&
                             vm.CurrentRevision == 0 &&
                             string.IsNullOrEmpty(vm.NameRow.Value) &&
                             vm.DraftRows.Count == 0;
            Record("ISSUE-0059-T06", "CreateBlankDraft 徹底重置圖片、ID、修訂版次與所有草稿列，開啟全新名片不殘留舊狀態", t06Passed);
        }

        report.TotalTests = report.TestCases.Count;
        report.PassCount = report.TestCases.Count(t => t.Status == "PASS");
        report.FailCount = report.TestCases.Count(t => t.Status == "FAIL");

        return report;
    }

    private static byte[] Bmp()
    {
        byte[] b = new byte[70];
        b[0] = 66; b[1] = 77;
        BitConverter.GetBytes(70).CopyTo(b, 2);
        BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(2).CopyTo(b, 18);
        BitConverter.GetBytes(2).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);
        BitConverter.GetBytes((short)24).CopyTo(b, 28);
        BitConverter.GetBytes(16).CopyTo(b, 34);
        return b;
    }
}
