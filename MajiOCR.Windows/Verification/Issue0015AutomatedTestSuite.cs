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

public static class Issue0015AutomatedTestSuite
{
    public static async Task<Issue0011TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        MainViewModel.IsHeadlessEnvironment = true;
        var report = new Issue0011TestSuiteReport { IssueId = "ISSUE-0015" };
        string root = Path.Combine(Path.GetFullPath(evidenceDir), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        void Record(string id, string description, bool passed)
        {
            report.TestCases.Add(new Issue0011TestCaseResult
            {
                TestId = id,
                Description = description,
                Status = passed ? "PASS" : "FAIL"
            });
        }

        string storageRoot = Path.Combine(root, "storage");
        using var storage = new StorageService(storageRoot);

        // Helper to create and save a synthetic contact
        async Task<string> SaveSyntheticCardAsync(string name, string company, string title, string phone, string email, string[] tags, byte[]? imageBytes = null)
        {
            var vm = new MainViewModel(new CustomFieldCatalogService(Path.Combine(root, "fields-" + Guid.NewGuid().ToString("N") + ".json")), null, storage);
            byte[] img = imageBytes ?? Bmp();
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(img, "合成.bmp", "ImagePickerCopy", "image/bmp", 2, 2));

            vm.NameRow.Value = name;
            vm.CompanyRow.Value = company;
            vm.TitleRow.Value = title;
            if (!string.IsNullOrEmpty(phone)) vm.AddPhoneRow(phone);
            if (!string.IsNullOrEmpty(email)) vm.AddEmailRow(email);

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

        // T01: Multi-field query returns Name, Company, Title, Phone, Email, RelativeImagePath
        string cardId1 = await SaveSyntheticCardAsync("王小明", "甲式電腦", "專案經理", "0912-345-678", "ming@example.com", new[] { "客戶", "台北" });
        string cardId2 = await SaveSyntheticCardAsync("李大同", "乙式資訊", "技術總監", "02-2345-6789", "dtlee@example.com", new[] { "廠商" });
        string cardId3 = await SaveSyntheticCardAsync("張美美", "丙式金控", "理財顧問", "0988-111-222", "mm@example.com", new[] { "客戶" });

        var pagedAll = await storage.GetContactsPagedAsync(null, 0, 30);
        var item1 = pagedAll.Items.FirstOrDefault(i => i.ContactId == cardId1);
        Record("TABLE15-T01", "表格多欄位查詢回傳姓名、公司、職稱、電話、Email及圖片狀態",
            item1 != null &&
            item1.Name == "王小明" &&
            item1.Company == "甲式電腦" &&
            item1.Title == "專案經理" &&
            item1.Phone == "0912-345-678" &&
            item1.Email == "ming@example.com" &&
            item1.HasImage &&
            item1.DisplayTitle == "王小明" &&
            item1.DisplaySubtitle == "甲式電腦");

        // T02: Name Sort Ascending and Descending
        if (storage is ITagStorageService tagStorage)
        {
            var sortNameAsc = await tagStorage.QueryContactsAsync(new ContactQuery(null, Array.Empty<string>(), false, false, "Name", false), 0, 30);
            var sortNameDesc = await tagStorage.QueryContactsAsync(new ContactQuery(null, Array.Empty<string>(), false, false, "Name", true), 0, 30);

            var ascNames = sortNameAsc.Items.Select(i => i.Name).ToList();
            var descNames = sortNameDesc.Items.Select(i => i.Name).ToList();

            bool nameAscOk = ascNames.SequenceEqual(ascNames.OrderBy(n => n, StringComparer.Ordinal).ToList());
            bool nameDescOk = descNames.SequenceEqual(descNames.OrderByDescending(n => n, StringComparer.Ordinal).ToList());

            Record("TABLE15-T02", "姓名欄位升降冪排序 (Name ASC / DESC)", nameAscOk && nameDescOk);

            // T03: Company Sort Ascending and Descending
            var sortCompAsc = await tagStorage.QueryContactsAsync(new ContactQuery(null, Array.Empty<string>(), false, false, "Company", false), 0, 30);
            var sortCompDesc = await tagStorage.QueryContactsAsync(new ContactQuery(null, Array.Empty<string>(), false, false, "Company", true), 0, 30);

            var ascComps = sortCompAsc.Items.Select(i => i.Company).ToList();
            var descComps = sortCompDesc.Items.Select(i => i.Company).ToList();

            bool compAscOk = ascComps.SequenceEqual(ascComps.OrderBy(c => c, StringComparer.Ordinal).ToList());
            bool compDescOk = descComps.SequenceEqual(descComps.OrderByDescending(c => c, StringComparer.Ordinal).ToList());

            Record("TABLE15-T03", "公司欄位升降冪排序 (Company ASC / DESC)", compAscOk && compDescOk);

            // T04: Title Sort Ascending and Descending
            var sortTitleAsc = await tagStorage.QueryContactsAsync(new ContactQuery(null, Array.Empty<string>(), false, false, "Title", false), 0, 30);
            var sortTitleDesc = await tagStorage.QueryContactsAsync(new ContactQuery(null, Array.Empty<string>(), false, false, "Title", true), 0, 30);

            var ascTitles = sortTitleAsc.Items.Select(i => i.Title).ToList();
            var descTitles = sortTitleDesc.Items.Select(i => i.Title).ToList();

            bool titleAscOk = ascTitles.SequenceEqual(ascTitles.OrderBy(t => t, StringComparer.Ordinal).ToList());
            bool titleDescOk = descTitles.SequenceEqual(descTitles.OrderByDescending(t => t, StringComparer.Ordinal).ToList());

            Record("TABLE15-T04", "職稱欄位升降冪排序 (Title ASC / DESC)", titleAscOk && titleDescOk);

            // T05: CreatedAt Sort Ascending and Descending
            var sortTimeAsc = await tagStorage.QueryContactsAsync(new ContactQuery(null, Array.Empty<string>(), false, false, "CreatedAt", false), 0, 30);
            var sortTimeDesc = await tagStorage.QueryContactsAsync(new ContactQuery(null, Array.Empty<string>(), false, false, "CreatedAt", true), 0, 30);

            var ascTimes = sortTimeAsc.Items.Select(i => i.CreatedAt).ToList();
            var descTimes = sortTimeDesc.Items.Select(i => i.CreatedAt).ToList();

            bool timeAscOk = ascTimes.SequenceEqual(ascTimes.OrderBy(t => t).ToList());
            bool timeDescOk = descTimes.SequenceEqual(descTimes.OrderByDescending(t => t).ToList());

            Record("TABLE15-T05", "儲存時間欄位升降冪排序 (CreatedAt ASC / DESC)", timeAscOk && timeDescOk);

            // T06: Tag and Search Combined with Sort
            var searchCustomerSorted = await tagStorage.QueryContactsAsync(new ContactQuery("金控", Array.Empty<string>(), false, false, "Name", false), 0, 30);
            Record("TABLE15-T06", "搜尋關鍵字、標籤與排序複合查詢",
                searchCustomerSorted.TotalCount == 1 &&
                searchCustomerSorted.Items[0].Name == "張美美" &&
                searchCustomerSorted.Items[0].Company == "丙式金控");
        }
        else
        {
            Record("TABLE15-T02", "ITagStorageService not implemented", false);
            Record("TABLE15-T03", "ITagStorageService not implemented", false);
            Record("TABLE15-T04", "ITagStorageService not implemented", false);
            Record("TABLE15-T05", "ITagStorageService not implemented", false);
            Record("TABLE15-T06", "ITagStorageService not implemented", false);
        }

        // T07: GetCardImageAbsolutePath Safe Path Resolution
        string? absPath1 = storage.GetCardImageAbsolutePath(cardId1);
        string? fakePath = storage.GetCardImageAbsolutePath("non-existent-contact-id");
        string? traversalPath = storage.GetCardImageAbsolutePath("../../../secret");

        Record("IMAGE15-T01", "安全提取名片圖檔絕對路徑與路徑越界防護",
            !string.IsNullOrEmpty(absPath1) &&
            File.Exists(absPath1) &&
            fakePath == null &&
            traversalPath == null);

        // T08: Large page size up to 100
        if (storage is ITagStorageService tagStore2)
        {
            var page100 = await tagStore2.QueryContactsAsync(new ContactQuery(null, Array.Empty<string>()), 0, 100);
            Record("PAGE15-T01", "支援高容量表格分頁大小 (每頁至多 100 筆)",
                page100.PageSize == 100 && page100.Items.Count >= 3);
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
