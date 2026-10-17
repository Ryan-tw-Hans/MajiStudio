using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;

namespace MajiOCR_Windows.Verification;

public class Issue0022AutomatedTestSuite
{
    public record TestResult(string TestName, bool Passed, string Message);

    public static async Task<List<TestResult>> RunAllTestsAsync()
    {
        var results = new List<TestResult>();

        results.Add(Test01_OutlookCsv_StructureAndBom());
        results.Add(Test02_OutlookCsv_CategoriesMapping());
        results.Add(Test03_GoogleCsv_StructureAndGroupMembership());
        results.Add(Test04_VCard_FormatAndEscaping());
        results.Add(Test05_VCard_MultipleContactsAggregation());
        results.Add(Test06_GoogleSync_PersonJsonPayload());
        results.Add(await Test07_GoogleSync_OfflineSimulationAsync());
        results.Add(Test08_EmptyAndEdgeCaseHandling());

        return results;
    }

    private static ContactDetailModel CreateSampleContact(
        string id,
        string name,
        string company,
        string title,
        string phone,
        string email,
        string address,
        string website,
        string note,
        params string[] tags)
    {
        var contact = new ContactDetailModel
        {
            ContactId = id,
            Name = name,
            Company = company,
            CreatedAt = DateTimeOffset.UtcNow,
            Tags = tags.Select(t => new TagDefinition(t, t, 1, 0)).ToList()
        };

        int ord = 0;
        if (!string.IsNullOrEmpty(name))
            contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Name, RawValue = name, Label = "姓名", Ordinal = ord++ });
        if (!string.IsNullOrEmpty(company))
            contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Company, RawValue = company, Label = "公司", Ordinal = ord++ });
        if (!string.IsNullOrEmpty(title))
            contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Title, RawValue = title, Label = "職稱", Ordinal = ord++ });
        if (!string.IsNullOrEmpty(phone))
            contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Phone, RawValue = phone, Label = "電話", Ordinal = ord++ });
        if (!string.IsNullOrEmpty(email))
            contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Email, RawValue = email, Label = "Email", Ordinal = ord++ });
        if (!string.IsNullOrEmpty(address))
            contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Address, RawValue = address, Label = "地址", Ordinal = ord++ });
        if (!string.IsNullOrEmpty(website))
            contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Website, RawValue = website, Label = "網站", Ordinal = ord++ });
        if (!string.IsNullOrEmpty(note))
            contact.Values.Add(new ContactValueRecord { ValueKind = CanonicalValueKind.Note, RawValue = note, Label = "備註", Ordinal = ord++ });

        return contact;
    }

    public static TestResult Test01_OutlookCsv_StructureAndBom()
    {
        var exportService = new ContactExportService();
        var contact = CreateSampleContact(
            "C1",
            "王小明",
            "甲式科技, 創新處",
            "資深架構師",
            "0912-345-678",
            "ming@example.com",
            "測試科學園區研新一路1號",
            "https://www.example.com",
            "重要客戶 \"VIP\"",
            "半導體", "VIP");

        string csv = exportService.GenerateOutlookCsv(new[] { contact });

        bool hasHeader = csv.Contains("First Name,Last Name,Title,Company,Department,Job Title");
        bool hasCompanyEscaping = csv.Contains("\"甲式科技, 創新處\"");
        bool hasNoteEscaping = csv.Contains("\"重要客戶 \"\"VIP\"\"\"");
        bool hasName = csv.Contains("小明,王");

        if (hasHeader && hasCompanyEscaping && hasNoteEscaping && hasName)
        {
            return new TestResult("TC-0022-01: Outlook CSV 格式與轉義規範", true, "Outlook CSV 表頭、姓名拆解與雙引號/逗號轉義驗證通過");
        }
        return new TestResult("TC-0022-01: Outlook CSV 格式與轉義規範", false, $"驗證失敗. Header={hasHeader}, CompanyEscaping={hasCompanyEscaping}, NoteEscaping={hasNoteEscaping}");
    }

    public static TestResult Test02_OutlookCsv_CategoriesMapping()
    {
        var exportService = new ContactExportService();
        var contact = CreateSampleContact("C2", "李四", "乙式科技", "處長", "0988-123-456", "lee@example.com", "", "", "", "晶片設計", "重要合作夥伴");

        string csv = exportService.GenerateOutlookCsv(new[] { contact });

        bool hasCategories = csv.Contains("晶片設計; 重要合作夥伴");
        if (hasCategories)
        {
            return new TestResult("TC-0022-02: Outlook CSV 多標籤轉 Categories", true, "多分類標籤成功以分號 '; ' 串接至 Categories 欄位");
        }
        return new TestResult("TC-0022-02: Outlook CSV 多標籤轉 Categories", false, "未在 CSV 中找到分號分隔之 Categories 內容");
    }

    public static TestResult Test03_GoogleCsv_StructureAndGroupMembership()
    {
        var exportService = new ContactExportService();
        var contact = CreateSampleContact("C3", "張三", "丙式電腦", "產品經理", "02-23456789", "sam@example.com", "台北市示範區範例路15號", "https://www.example.com", "備註測試", "硬體供應商");

        string csv = exportService.GenerateGoogleCsv(new[] { contact });

        bool hasHeader = csv.Contains("Name,Given Name,Additional Name,Family Name");
        bool hasGroup = csv.Contains("* myContacts ::: 硬體供應商");
        bool hasOrg = csv.Contains("丙式電腦");

        if (hasHeader && hasGroup && hasOrg)
        {
            return new TestResult("TC-0022-03: Google Contacts CSV 格式與 Group Membership", true, "Google Contacts CSV 表頭、組織與 Group Membership 欄位映射驗證通過");
        }
        return new TestResult("TC-0022-03: Google Contacts CSV 格式與 Group Membership", false, $"驗證失敗. Header={hasHeader}, Group={hasGroup}, Org={hasOrg}");
    }

    public static TestResult Test04_VCard_FormatAndEscaping()
    {
        var exportService = new ContactExportService();
        var contact = CreateSampleContact("C4", "歐陽修", "宋代文壇", "翰林學士", "0911-222-333", "ouyang@example.org", "開封府大相國寺旁; 一巷", "https://example.org", "唐宋八大家; 醉翁亭記", "古人", "文豪");

        string vcf = exportService.GenerateVCard(new[] { contact });

        bool hasBegin = vcf.Contains("BEGIN:VCARD");
        bool hasVersion = vcf.Contains("VERSION:3.0");
        bool hasEnd = vcf.Contains("END:VCARD");
        bool hasCompoundSurname = vcf.Contains("N:歐陽;修;;;");
        bool hasFn = vcf.Contains("FN:歐陽修");
        bool hasCategories = vcf.Contains("CATEGORIES:古人,文豪");
        bool hasEscapedSemicolon = vcf.Contains("開封府大相國寺旁\\; 一巷");

        if (hasBegin && hasVersion && hasEnd && hasCompoundSurname && hasFn && hasCategories && hasEscapedSemicolon)
        {
            return new TestResult("TC-0022-04: vCard 3.0 規範與特殊字元轉義", true, "vCard 3.0 語法、複姓拆解、分號轉義與 CATEGORIES 格式正確");
        }
        return new TestResult("TC-0022-04: vCard 3.0 規範與特殊字元轉義", false, $"驗證失敗. Begin={hasBegin}, CompoundSurname={hasCompoundSurname}, Escaped={hasEscapedSemicolon}");
    }

    public static TestResult Test05_VCard_MultipleContactsAggregation()
    {
        var exportService = new ContactExportService();
        var c1 = CreateSampleContact("C5-1", "名片一", "公司A", "工程師", "0900-1", "a@example.com", "", "", "");
        var c2 = CreateSampleContact("C5-2", "名片二", "公司B", "設計師", "0900-2", "b@example.com", "", "", "");

        string vcf = exportService.GenerateVCard(new[] { c1, c2 });

        int beginCount = vcf.Split(new[] { "BEGIN:VCARD" }, StringSplitOptions.None).Length - 1;
        int endCount = vcf.Split(new[] { "END:VCARD" }, StringSplitOptions.None).Length - 1;

        if (beginCount == 2 && endCount == 2 && vcf.Contains("FN:名片一") && vcf.Contains("FN:名片二"))
        {
            return new TestResult("TC-0022-05: vCard 多筆名片合併匯出", true, "成功在單一 .vcf 檔案中依序輸出 2 個獨立完整的 VCARD 區塊");
        }
        return new TestResult("TC-0022-05: vCard 多筆名片合併匯出", false, $"BeginCount={beginCount}, EndCount={endCount}");
    }

    public static TestResult Test06_GoogleSync_PersonJsonPayload()
    {
        var syncService = new GoogleContactsSyncService();
        var contact = CreateSampleContact("C6", "陳大同", "丁式公司", "技術長", "0933-444-555", "tatung@example.com", "台北市示範區範例北路三段", "https://example.com", "家電專家", "百年企業");

        string json = syncService.BuildGooglePersonJson(contact);
        var node = JsonNode.Parse(json);

        bool hasNames = node?["names"]?[0]?["displayName"]?.GetValue<string>() == "陳大同";
        bool hasOrg = node?["organizations"]?[0]?["name"]?.GetValue<string>() == "丁式公司";
        bool hasPhone = node?["phoneNumbers"]?[0]?["value"]?.GetValue<string>() == "0933-444-555";
        bool hasEmail = node?["emailAddresses"]?[0]?["value"]?.GetValue<string>() == "tatung@example.com";
        bool hasUserDefined = node?["userDefined"]?[0]?["value"]?.GetValue<string>() == "百年企業";

        if (hasNames && hasOrg && hasPhone && hasEmail && hasUserDefined)
        {
            return new TestResult("TC-0022-06: Google People API Person Payload 結構驗證", true, "Google Person JSON Schema 完整包含 names, org, phone, email, tags 屬性");
        }
        return new TestResult("TC-0022-06: Google People API Person Payload 結構驗證", false, $"JSON 驗證失敗: {json}");
    }

    public static async Task<TestResult> Test07_GoogleSync_OfflineSimulationAsync()
    {
        var syncService = new GoogleContactsSyncService();
        var contacts = new List<ContactDetailModel>
        {
            CreateSampleContact("C7-1", "測試甲", "甲公司", "經理", "0911", "a@example.com", "", "", ""),
            CreateSampleContact("C7-2", "測試乙", "乙公司", "專員", "0922", "b@example.com", "", "", "")
        };

        var progressReports = new List<GoogleSyncProgress>();
        var progress = new Progress<GoogleSyncProgress>(p => progressReports.Add(p));

        var result = await syncService.SyncToGoogleContactsAsync(contacts, accessToken: null, progress: progress);

        if (result.IsSuccess && result.TotalCount == 2 && result.SuccessCount == 2 && result.FailedCount == 0)
        {
            return new TestResult("TC-0022-07: Google 聯絡人離線驗證與同步進度回報", true, "GoogleSyncService 離線校驗模擬與 Progress 報告運作正常 (2/2 成功)");
        }
        return new TestResult("TC-0022-07: Google 聯絡人離線驗證與同步進度回報", false, $"同步失敗: {result.SummaryMessage}");
    }

    public static TestResult Test08_EmptyAndEdgeCaseHandling()
    {
        var exportService = new ContactExportService();
        var syncService = new GoogleContactsSyncService();

        string outlookEmpty = exportService.GenerateOutlookCsv(Array.Empty<ContactDetailModel>());
        string googleEmpty = exportService.GenerateGoogleCsv(Array.Empty<ContactDetailModel>());
        string vcardEmpty = exportService.GenerateVCard(Array.Empty<ContactDetailModel>());

        var emptyContact = new ContactDetailModel { ContactId = "EMPTY" };
        string json = syncService.BuildGooglePersonJson(emptyContact);

        bool outlookOk = outlookEmpty.Contains("First Name,Last Name");
        bool googleOk = googleEmpty.Contains("Name,Given Name");
        bool vcardOk = vcardEmpty.Length == 0;
        bool jsonOk = json.Length > 0;

        if (outlookOk && googleOk && vcardOk && jsonOk)
        {
            return new TestResult("TC-0022-08: 空資料集與極端案例容錯驗證", true, "空聯絡人清單、無欄位名片均正常產出表頭且無例外崩潰");
        }
        return new TestResult("TC-0022-08: 空資料集與極端案例容錯驗證", false, "空資料集處理異常");
    }
}
