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

public static class Issue0016AutomatedTestSuite
{
    public static async Task<Issue0011TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        MainViewModel.IsHeadlessEnvironment = true;
        var report = new Issue0011TestSuiteReport { IssueId = "ISSUE-0016" };
        string root = Path.Combine(Path.GetFullPath(evidenceDir), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        void Record(string id, string description, bool passed, string? details = null)
        {
            report.TestCases.Add(new Issue0011TestCaseResult
            {
                TestId = id,
                Description = description,
                Status = passed ? "PASS" : "FAIL",
                Details = details ?? (passed ? "驗證通過" : "驗證失敗")
            });
        }

        string storageRoot = Path.Combine(root, "storage");
        using var storage = new StorageService(storageRoot);

        // Helper to create and save a synthetic contact
        async Task<string> SaveSyntheticCardAsync(string name, string company, string title, string phone, string email, string note, string[] tags, byte[]? imageBytes = null)
        {
            var vm = new MainViewModel(new CustomFieldCatalogService(Path.Combine(root, "fields-" + Guid.NewGuid().ToString("N") + ".json")), null, storage);
            byte[] img = imageBytes ?? Bmp();
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(img, $"{name}.bmp", "ImagePickerCopy", "image/bmp", 2, 2));

            vm.NameRow.Value = name;
            vm.CompanyRow.Value = company;
            vm.TitleRow.Value = title;
            if (!string.IsNullOrEmpty(phone)) vm.AddPhoneRow(phone);
            if (!string.IsNullOrEmpty(email)) vm.AddEmailRow(email);
            if (!string.IsNullOrEmpty(note)) vm.NoteRow.Value = note;

            vm.ConfirmAllDraftRows(out _);
            if (tags.Length > 0)
            {
                vm.SetClassification(tags.Select(t => new TagChoice(null, t)).ToArray());
            }

            string token = vm.CaptureSaveConfirmationState();
            var res = await vm.SaveConfirmedDraftAsync(true, token);
            if (!res.Succeeded) throw new InvalidOperationException($"Save failed: {res.ErrorCode} - {res.ErrorMessage}");
            return vm.CurrentContactId;
        }

        // Setup Test Data
        // Card 1: 王小明, 甲式電子, 資深研發工程師, 0912-345-678, ming@example.com, 專長電源管理, 標籤: [客戶, 台北, 研發]
        string c1 = await SaveSyntheticCardAsync("王小明", "甲式電子", "資深研發工程師", "0912-345-678", "ming@example.com", "專長電源管理", new[] { "客戶", "台北", "研發" });
        // Card 2: 李大同, 乙式電腦, 產品經理, 02-2345-6789, dtlee@example.com, 筆電事業群, 標籤: [廠商, 台北]
        string c2 = await SaveSyntheticCardAsync("李大同", "乙式電腦", "產品經理", "02-2345-6789", "dtlee@example.com", "筆電事業群", new[] { "廠商", "台北" });
        // Card 3: 張美美, 甲式電子, 行銷協理, 0988-111-222, mm@example.com, 綠能推廣, 標籤: [客戶, 新竹]
        string c3 = await SaveSyntheticCardAsync("張美美", "甲式電子", "行銷協理", "0988-111-222", "mm@example.com", "綠能推廣", new[] { "客戶", "新竹" });
        // Card 4: 王小華, 丙式電腦, 研發經理, 0933-444-555, hua@example.com, 主機板部門, 標籤: [廠商, 研發]
        string c4 = await SaveSyntheticCardAsync("王小華", "丙式電腦", "研發經理", "0933-444-555", "hua@example.com", "主機板部門", new[] { "廠商", "研發" });

        var tagStore = storage as ITagStorageService;
        if (tagStore == null)
        {
            Record("SEARCH16-T01", "ITagStorageService not implemented", false);
            return report;
        }

        // SEARCH16-T01: AND 運算子 `+` (如 "王小明+甲式")
        var resAnd = await tagStore.QueryContactsAsync(new ContactQuery("王小明+甲式", Array.Empty<string>()), 0, 30);
        bool t01Passed = resAnd.TotalCount == 1 && resAnd.Items[0].ContactId == c1;
        Record("SEARCH16-T01", "AND 運算子「+」多條件精確交集比對 (如「王小明+甲式」)", t01Passed, $"預期 1 筆 (c1)，實際回傳 {resAnd.TotalCount} 筆");

        // SEARCH16-T02: OR 運算子 `空格` (如 "王小明 李大同")
        var resOr = await tagStore.QueryContactsAsync(new ContactQuery("王小明 李大同", Array.Empty<string>()), 0, 30);
        var orIds = resOr.Items.Select(i => i.ContactId).ToHashSet();
        bool t02Passed = resOr.TotalCount == 2 && orIds.Contains(c1) && orIds.Contains(c2);
        Record("SEARCH16-T02", "OR 運算子「空格」多條件聯集比對 (如「王小明 李大同」)", t02Passed, $"預期 2 筆 (c1, c2)，實際回傳 {resOr.TotalCount} 筆");

        // SEARCH16-T03: 多詞連續 AND (如 "王小明+甲式+電源管理")
        var resMultiAnd = await tagStore.QueryContactsAsync(new ContactQuery("王小明+甲式+電源管理", Array.Empty<string>()), 0, 30);
        bool t03Passed = resMultiAnd.TotalCount == 1 && resMultiAnd.Items[0].ContactId == c1;
        Record("SEARCH16-T03", "多詞連續 AND 複合比對 (如「王小明+甲式+電源管理」)", t03Passed, $"預期 1 筆 (c1)，實際回傳 {resMultiAnd.TotalCount} 筆");

        // SEARCH16-T04: 混合語法 (如 "王小明+甲式 李大同")
        var resMixed = await tagStore.QueryContactsAsync(new ContactQuery("王小明+甲式 李大同", Array.Empty<string>()), 0, 30);
        var mixIds = resMixed.Items.Select(i => i.ContactId).ToHashSet();
        bool t04Passed = resMixed.TotalCount == 2 && mixIds.Contains(c1) && mixIds.Contains(c2);
        Record("SEARCH16-T04", "混合 AND/OR 語法分組查詢 (如「王小明+甲式 李大同」)", t04Passed, $"預期 2 筆 (c1, c2)，實際回傳 {resMixed.TotalCount} 筆");

        // SEARCH16-T05: 特殊字元與 SQL 注入防護
        var resInjection = await tagStore.QueryContactsAsync(new ContactQuery("'+OR+'1'='1", Array.Empty<string>()), 0, 30);
        var resWildcard = await tagStore.QueryContactsAsync(new ContactQuery("%", Array.Empty<string>()), 0, 30);
        bool t05Passed = resInjection.TotalCount == 0 && resWildcard.TotalCount == 0;
        Record("SEARCH16-T05", "特殊字元跳脫與 SQL 注入防護 ('+OR+'1'='1 與 % 通配符)", t05Passed, $"注入查詢回傳 {resInjection.TotalCount} 筆，未跳脫通配符回傳 {resWildcard.TotalCount} 筆");

        // SEARCH16-T06: 標籤名稱複合比對 (如 "客戶+甲式", "研發+丙式")
        var resTagMatch1 = await tagStore.QueryContactsAsync(new ContactQuery("客戶+甲式", Array.Empty<string>()), 0, 30);
        var tagIds1 = resTagMatch1.Items.Select(i => i.ContactId).ToHashSet();
        bool t06Passed1 = resTagMatch1.TotalCount == 2 && tagIds1.Contains(c1) && tagIds1.Contains(c3);

        var resTagMatch2 = await tagStore.QueryContactsAsync(new ContactQuery("研發+丙式", Array.Empty<string>()), 0, 30);
        bool t06Passed2 = resTagMatch2.TotalCount == 1 && resTagMatch2.Items[0].ContactId == c4;
        Record("SEARCH16-T06", "欄位值與分類標籤跨表複合搜尋 (如「客戶+甲式」「研發+丙式」)", t06Passed1 && t06Passed2, $"客戶+甲式: {resTagMatch1.TotalCount} 筆, 研發+丙式: {resTagMatch2.TotalCount} 筆");

        // RELOAD16-T01: 全欄位與圖片載回主畫面
        var detail1 = await storage.GetContactDetailAsync(c1);
        if (detail1 == null) throw new InvalidOperationException("Detail1 not found");

        var testVm = new MainViewModel(new CustomFieldCatalogService(Path.Combine(root, "fields-" + Guid.NewGuid().ToString("N") + ".json")), null, storage);
        await testVm.ReloadSavedContactAsync(detail1);

        bool t07Passed = testVm.CurrentContactId == c1 &&
                         testVm.CurrentRevision == detail1.Revision &&
                         testVm.NameRow.Value == "王小明" &&
                         testVm.CompanyRow.Value == "甲式電子" &&
                         testVm.TitleRow.Value == "資深研發工程師" &&
                         testVm.PhoneRows.Count == 1 && testVm.PhoneRows[0].Value == "0912-345-678" &&
                         testVm.EmailRows.Count == 1 && testVm.EmailRows[0].Value == "ming@example.com" &&
                         testVm.NoteRow.Value == "專長電源管理" &&
                         testVm.CurrentSession != null &&
                         testVm.Classification.Count == 3;
        Record("RELOAD16-T01", "名片全欄位、標籤及原圖工作階段載回主畫面 (ReloadSavedContactAsync)", t07Passed);

        // RELOAD16-T02: 載回後狀態機與指紋驗證 (Confirmed 狀態，無未儲存變更)
        bool allConfirmed = testVm.DraftRows.All(r => r.Status == DraftItemStatus.Confirmed);
        bool noUnsaved = !testVm.HasUnsavedChanges;
        string? currentFingerprint = DraftSnapshotFactory.ComputeCurrentDraftFingerprint(testVm, c1);
        bool fingerprintMatches = testVm.LastSavedFingerprint == currentFingerprint;
        bool t08Passed = allConfirmed && noUnsaved && fingerprintMatches;
        Record("RELOAD16-T02", "載回後全草稿狀態為 Confirmed 且基準指紋一致 (無未存變更標記)", t08Passed, $"AllConfirmed={allConfirmed}, HasUnsaved={testVm.HasUnsavedChanges}, FingerprintMatch={fingerprintMatches}");

        // RELOAD16-T03: 載回後局部修改與 CAS 升級 (Revision + 1)
        testVm.TitleRow.Value = "技術副總裁";
        bool hasUnsavedAfterEdit = testVm.HasUnsavedChanges;
        testVm.TitleRow.Confirm();
        string saveToken = testVm.CaptureSaveConfirmationState();
        var updateRes = await testVm.SaveConfirmedDraftAsync(true, saveToken);
        bool updateSuccess = updateRes.Succeeded && updateRes.ContactId == c1 && updateRes.PersistedRevision == detail1.Revision + 1;

        var reloadedDetail = await storage.GetContactDetailAsync(c1);
        bool dbUpdated = reloadedDetail != null && reloadedDetail.Revision == detail1.Revision + 1 &&
                         reloadedDetail.Values.Any(v => v.ValueKind == CanonicalValueKind.Title && v.RawValue == "技術副總裁");
        bool t09Passed = hasUnsavedAfterEdit && updateSuccess && dbUpdated;
        Record("RELOAD16-T03", "載回後修改/局部再辨識並儲存，CAS 版本正確遞增 (Revision + 1)", t09Passed, $"HasUnsaved={hasUnsavedAfterEdit}, UpdateSuccess={updateSuccess} (Code={updateRes.ErrorCode}, Msg={updateRes.ErrorMessage}, TargetRev={detail1.Revision + 1}, ActualPersistedRev={updateRes.PersistedRevision}), NewRev={reloadedDetail?.Revision}");

        // RELOAD16-T04: 未存草稿防護提示邏輯驗證
        var guardVm = new MainViewModel(new CustomFieldCatalogService(Path.Combine(root, "fields-" + Guid.NewGuid().ToString("N") + ".json")), null, storage);
        await guardVm.SetSessionImageAsync(ImageSessionInfo.Create(Bmp(), "temp.bmp", "ImagePickerCopy", "image/bmp", 2, 2));
        guardVm.NameRow.Value = "尚未儲存的新聯絡人";
        bool hasUnsavedDraft = guardVm.HasUnsavedChanges;

        Record("RELOAD16-T04", "載回切換前檢測 HasUnsavedChanges 提供未存草稿防護提示機制", hasUnsavedDraft, $"HasUnsavedChanges={hasUnsavedDraft}");

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
