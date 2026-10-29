using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiniExcelLibs;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Services;

public class ImportContactDraft
{
    public string Name { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string TaxId { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public string Notes { get; set; } = string.Empty;

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Name) ? Name : (!string.IsNullOrWhiteSpace(Company) ? Company : "未填姓名");
    public string DisplaySubtitle => !string.IsNullOrWhiteSpace(Company) ? Company : string.Empty;
    public string DisplayPhone => !string.IsNullOrWhiteSpace(Mobile) ? Mobile : Phone;
    public string DisplayTags => Tags.Count > 0 ? string.Join("、", Tags) : "無標籤";
}

public enum ImportDuplicateStrategy
{
    /// <summary>
    /// ⏩ 跳過重複名片（同姓名且同公司或同電話時，自動略過不匯入）
    /// </summary>
    SkipDuplicate,

    /// <summary>
    /// 🔄 覆蓋更新現有（以匯入資料更新系統現有名片，補齊未填欄位）
    /// </summary>
    OverwriteExisting,

    /// <summary>
    /// ➕ 一律建立為新名片（允許同名重複建立獨立名片記錄）
    /// </summary>
    AlwaysAdd
}

public class ImportExecutionResult
{
    public int TotalParsed { get; set; }
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool Succeeded => FailedCount == 0 && Errors.Count == 0;
    public string SummaryMessage =>
        $"共解析 {TotalParsed} 筆資料：新增 {InsertedCount} 筆、更新 {UpdatedCount} 筆、略過 {SkippedCount} 筆重複名片" +
        (FailedCount > 0 ? $"、失敗 {FailedCount} 筆" : "。");
}

public interface IContactExcelCsvService
{
    byte[] ExportToExcel(IReadOnlyList<ContactDetailModel> contacts, bool includeTags = true, bool includeNotes = true);
    string ExportToFullCsv(IReadOnlyList<ContactDetailModel> contacts, bool includeTags = true, bool includeNotes = true);
    byte[] GenerateExcelTemplate();
    string GenerateCsvTemplate();
    IReadOnlyList<ImportContactDraft> ParseImportFile(Stream stream, string extension);
    Task<ImportExecutionResult> ExecuteImportAsync(
        IReadOnlyList<ImportContactDraft> drafts,
        ImportDuplicateStrategy strategy,
        IStorageService storageService,
        CancellationToken ct = default);
}

public class ContactExcelCsvService : IContactExcelCsvService
{
    public static readonly string[] StandardHeaders =
    {
        "姓名", "公司", "部門", "職稱", "手機", "公司電話", "Email", "地址", "統一編號", "網站", "分類標籤", "備註", "建立時間"
    };

    public byte[] ExportToExcel(IReadOnlyList<ContactDetailModel> contacts, bool includeTags = true, bool includeNotes = true)
    {
        var rows = new List<Dictionary<string, object>>();

        foreach (var c in contacts ?? Array.Empty<ContactDetailModel>())
        {
            var dict = new Dictionary<string, object>();
            var extracted = ExtractFields(c);

            dict["姓名"] = extracted.Name;
            dict["公司"] = extracted.Company;
            dict["部門"] = extracted.Department;
            dict["職稱"] = extracted.Title;
            dict["手機"] = extracted.Mobile;
            dict["公司電話"] = extracted.Phone;
            dict["Email"] = extracted.Email;
            dict["地址"] = extracted.Address;
            dict["統一編號"] = extracted.TaxId;
            dict["網站"] = extracted.Website;
            if (includeTags)
                dict["分類標籤"] = extracted.Tags;
            if (includeNotes)
                dict["備註"] = extracted.Notes;
            dict["建立時間"] = c.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            rows.Add(dict);
        }

        using var ms = new MemoryStream();
        MiniExcel.SaveAs(ms, rows, excelType: ExcelType.XLSX);
        return ms.ToArray();
    }

    public string ExportToFullCsv(IReadOnlyList<ContactDetailModel> contacts, bool includeTags = true, bool includeNotes = true)
    {
        var sb = new StringBuilder();
        // UTF-8 BOM
        sb.Append('\uFEFF');

        var headers = new List<string> { "姓名", "公司", "部門", "職稱", "手機", "公司電話", "Email", "地址", "統一編號", "網站" };
        if (includeTags) headers.Add("分類標籤");
        if (includeNotes) headers.Add("備註");
        headers.Add("建立時間");

        sb.AppendLine(string.Join(",", headers.Select(EscapeCsvField)));

        foreach (var c in contacts ?? Array.Empty<ContactDetailModel>())
        {
            var extracted = ExtractFields(c);
            var row = new List<string>
            {
                extracted.Name,
                extracted.Company,
                extracted.Department,
                extracted.Title,
                extracted.Mobile,
                extracted.Phone,
                extracted.Email,
                extracted.Address,
                extracted.TaxId,
                extracted.Website
            };
            if (includeTags) row.Add(extracted.Tags);
            if (includeNotes) row.Add(extracted.Notes);
            row.Add(c.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

            sb.AppendLine(string.Join(",", row.Select(EscapeCsvField)));
        }

        return sb.ToString();
    }

    public byte[] GenerateExcelTemplate()
    {
        var sampleRows = new List<Dictionary<string, object>>
        {
            new()
            {
                ["姓名"] = "王小明",
                ["公司"] = "甲式科技股份有限公司",
                ["部門"] = "研發部",
                ["職稱"] = "技術總監",
                ["手機"] = "0912-345-678",
                ["公司電話"] = "02-2345-6789",
                ["Email"] = "ming@example.com",
                ["地址"] = "台北市信義區信義路五段 7 號",
                ["統一編號"] = "12345678",
                ["網站"] = "https://example.com",
                ["分類標籤"] = "VIP客戶、合作夥伴",
                ["備註"] = "年度核心技術評估聯絡人",
                ["建立時間"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            },
            new()
            {
                ["姓名"] = "李大華",
                ["公司"] = "乙式電訊科技",
                ["部門"] = "業務行銷部",
                ["職稱"] = "業務副總",
                ["手機"] = "0988-112-233",
                ["公司電話"] = "03-556-7788",
                ["Email"] = "hua@example.com",
                ["地址"] = "新竹市科學園區研發一路 1 號",
                ["統一編號"] = "87654321",
                ["網站"] = "https://hua-telecom.example",
                ["分類標籤"] = "供應商",
                ["備註"] = "通訊設備採購窗口",
                ["建立時間"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            }
        };

        using var ms = new MemoryStream();
        MiniExcel.SaveAs(ms, sampleRows, excelType: ExcelType.XLSX);
        return ms.ToArray();
    }

    public string GenerateCsvTemplate()
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF'); // UTF-8 BOM
        sb.AppendLine(string.Join(",", StandardHeaders.Select(EscapeCsvField)));

        var row1 = new[]
        {
            "王小明", "甲式科技股份有限公司", "研發部", "技術總監", "0912-345-678", "02-2345-6789",
            "ming@example.com", "台北市信義區信義路五段 7 號", "12345678", "https://example.com",
            "VIP客戶、合作夥伴", "年度核心技術評估聯絡人", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        var row2 = new[]
        {
            "李大華", "乙式電訊科技", "業務行銷部", "業務副總", "0988-112-233", "03-556-7788",
            "hua@example.com", "新竹市科學園區研發一路 1 號", "87654321", "https://hua-telecom.example",
            "供應商", "通訊設備採購窗口", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        sb.AppendLine(string.Join(",", row1.Select(EscapeCsvField)));
        sb.AppendLine(string.Join(",", row2.Select(EscapeCsvField)));
        return sb.ToString();
    }

    public IReadOnlyList<ImportContactDraft> ParseImportFile(Stream stream, string extension)
    {
        var list = new List<ImportContactDraft>();
        bool isCsv = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);

        var excelType = isCsv ? ExcelType.CSV : ExcelType.XLSX;
        var rows = MiniExcel.Query(stream, useHeaderRow: true, excelType: excelType).ToList();

        foreach (var rowObj in rows)
        {
            if (rowObj is not IDictionary<string, object> rowDict) continue;

            var draft = new ImportContactDraft();
            bool hasAnyField = false;

            foreach (var kvp in rowDict)
            {
                string rawHeader = kvp.Key?.Trim() ?? string.Empty;
                string value = kvp.Value?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawHeader) || string.IsNullOrWhiteSpace(value)) continue;

                var fieldKind = MatchHeaderField(rawHeader);
                switch (fieldKind)
                {
                    case "Name":
                        draft.Name = value;
                        hasAnyField = true;
                        break;
                    case "Company":
                        draft.Company = value;
                        hasAnyField = true;
                        break;
                    case "Department":
                        draft.Department = value;
                        hasAnyField = true;
                        break;
                    case "Title":
                        draft.Title = value;
                        hasAnyField = true;
                        break;
                    case "Mobile":
                        draft.Mobile = value;
                        hasAnyField = true;
                        break;
                    case "Phone":
                        draft.Phone = value;
                        hasAnyField = true;
                        break;
                    case "Email":
                        draft.Email = value;
                        hasAnyField = true;
                        break;
                    case "Address":
                        draft.Address = value;
                        hasAnyField = true;
                        break;
                    case "TaxId":
                        draft.TaxId = value;
                        hasAnyField = true;
                        break;
                    case "Website":
                        draft.Website = value;
                        hasAnyField = true;
                        break;
                    case "Tags":
                        var splitted = value.Split(new[] { ',', '、', ';', '/', '，' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(t => t.Trim())
                                            .Where(t => !string.IsNullOrEmpty(t));
                        draft.Tags.AddRange(splitted);
                        hasAnyField = true;
                        break;
                    case "Notes":
                        draft.Notes = string.IsNullOrEmpty(draft.Notes) ? value : $"{draft.Notes}\n{value}";
                        hasAnyField = true;
                        break;
                }
            }

            if (hasAnyField && (!string.IsNullOrWhiteSpace(draft.Name) || !string.IsNullOrWhiteSpace(draft.Company)))
            {
                // Remove duplicates in tags
                draft.Tags = draft.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                list.Add(draft);
            }
        }

        return list;
    }

    public async Task<ImportExecutionResult> ExecuteImportAsync(
        IReadOnlyList<ImportContactDraft> drafts,
        ImportDuplicateStrategy strategy,
        IStorageService storageService,
        CancellationToken ct = default)
    {
        var result = new ImportExecutionResult
        {
            TotalParsed = drafts?.Count ?? 0
        };

        if (drafts == null || drafts.Count == 0)
        {
            return result;
        }

        // 1. Create safety backup before batch import
        try
        {
            string backupDir = Path.Combine(storageService.StorageRoot, "backups");
            Directory.CreateDirectory(backupDir);
            string backupDb = Path.Combine(backupDir, $"pre_import_{DateTime.UtcNow:yyyyMMdd_HHmmss}.sqlite3");
            if (File.Exists(storageService.DatabasePath))
            {
                File.Copy(storageService.DatabasePath, backupDb, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"建立匯入前安全快照失敗：{ex.Message}");
            result.FailedCount = drafts.Count;
            return result;
        }

        // 2. Load all existing contacts to build duplicate lookup indexes
        var allExisting = new List<ContactDetailModel>();
        int pageIndex = 0;
        const int pageSize = 100;
        while (true)
        {
            var paged = await storageService.GetContactsPagedAsync(null, pageIndex, pageSize, ct);
            if (paged.Items.Count == 0) break;

            foreach (var item in paged.Items)
            {
                var detail = await storageService.GetContactDetailAsync(item.ContactId, ct);
                if (detail != null) allExisting.Add(detail);
            }

            if (!paged.HasNextPage) break;
            pageIndex++;
        }

        // Build duplicate lookup maps
        // Key 1: Name + Company (normalized)
        // Key 2: Name + Phone/Mobile (normalized)
        var nameCompanyMap = new Dictionary<string, ContactDetailModel>(StringComparer.OrdinalIgnoreCase);
        var namePhoneMap = new Dictionary<string, ContactDetailModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in allExisting)
        {
            string normName = NormalizeKey(c.Name);
            string normCompany = NormalizeKey(c.Company);
            string normPhone = NormalizePhone(ExtractPhone(c.Values));

            if (!string.IsNullOrEmpty(normName) && !string.IsNullOrEmpty(normCompany))
            {
                nameCompanyMap[$"{normName}@@{normCompany}"] = c;
            }
            if (!string.IsNullOrEmpty(normName) && !string.IsNullOrEmpty(normPhone))
            {
                namePhoneMap[$"{normName}@@{normPhone}"] = c;
            }
        }

        // 3. Process drafts
        foreach (var draft in drafts)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                string normDraftName = NormalizeKey(draft.Name);
                string normDraftCompany = NormalizeKey(draft.Company);
                string normDraftPhone = NormalizePhone(!string.IsNullOrWhiteSpace(draft.Mobile) ? draft.Mobile : draft.Phone);

                ContactDetailModel? matchedExisting = null;
                if (!string.IsNullOrEmpty(normDraftName) && !string.IsNullOrEmpty(normDraftCompany))
                {
                    nameCompanyMap.TryGetValue($"{normDraftName}@@{normDraftCompany}", out matchedExisting);
                }
                if (matchedExisting == null && !string.IsNullOrEmpty(normDraftName) && !string.IsNullOrEmpty(normDraftPhone))
                {
                    namePhoneMap.TryGetValue($"{normDraftName}@@{normDraftPhone}", out matchedExisting);
                }

                if (matchedExisting != null && strategy != ImportDuplicateStrategy.AlwaysAdd)
                {
                    if (strategy == ImportDuplicateStrategy.SkipDuplicate)
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    if (strategy == ImportDuplicateStrategy.OverwriteExisting)
                    {
                        // Overwrite existing contact
                        await OverwriteContactAsync(matchedExisting, draft, storageService, ct);
                        result.UpdatedCount++;
                        continue;
                    }
                }

                // Insert as new contact
                await InsertNewContactAsync(draft, storageService, ct);
                result.InsertedCount++;
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Errors.Add($"匯入「{draft.DisplayTitle}」失敗：{ex.Message}");
            }
        }

        return result;
    }

    private static async Task InsertNewContactAsync(
        ImportContactDraft draft,
        IStorageService storageService,
        CancellationToken ct)
    {
        string contactId = Guid.NewGuid().ToString("N");
        string opId = Guid.NewGuid().ToString("N");
        byte[] synthBmp = CreateSyntheticBmp(120, 80, 0xF0, 0xF4, 0xF8);
        string imgSha = Convert.ToHexString(SHA256.HashData(synthBmp));

        var values = BuildDraftValues(draft);
        string fingerprint = DraftSnapshotFactory.ComputeFingerprint(contactId, values, imgSha);

        var tagChoices = draft.Tags.Select(t => new TagChoice(null, t)).ToList();

        var snapshot = new ContactDraftSnapshot
        {
            ContactId = contactId,
            SaveOperationId = opId,
            ExpectedRevision = 0,
            Revision = 1,
            Values = values,
            Tags = tagChoices,
            WriteTags = tagChoices.Count > 0,
            ImageBytes = synthBmp,
            ImageSha256 = imgSha,
            MimeType = "image/bmp",
            Width = 120,
            Height = 80,
            CaptureSource = "ImagePickerCopy",
            CompletionSource = "Manual",
            Fingerprint = fingerprint
        };

        var saveRes = await storageService.SaveDraftAsync(snapshot, ct);
        if (!saveRes.Succeeded)
        {
            throw new InvalidOperationException(saveRes.ErrorMessage ?? "儲存失敗");
        }
    }

    private static async Task OverwriteContactAsync(
        ContactDetailModel existing,
        ImportContactDraft draft,
        IStorageService storageService,
        CancellationToken ct)
    {
        string opId = Guid.NewGuid().ToString("N");
        byte[] imgBytes = existing.VerifiedImageBytes ?? CreateSyntheticBmp(120, 80);
        string imgSha = Convert.ToHexString(SHA256.HashData(imgBytes));

        // Merge existing values and draft values
        var mergedValues = MergeValues(existing.Values, draft);
        string fingerprint = DraftSnapshotFactory.ComputeFingerprint(existing.ContactId, mergedValues, imgSha);

        // Merge tags
        var existingTagNames = existing.Tags.Select(t => t.DisplayName).ToList();
        var allTagNames = existingTagNames.Union(draft.Tags, StringComparer.OrdinalIgnoreCase).ToList();
        var tagChoices = allTagNames.Select(t => new TagChoice(null, t)).ToList();

        var snapshot = new ContactDraftSnapshot
        {
            ContactId = existing.ContactId,
            SaveOperationId = opId,
            ExpectedRevision = existing.Revision,
            Revision = existing.Revision + 1,
            Values = mergedValues,
            Tags = tagChoices,
            WriteTags = true,
            ExpectedTagRevision = existing.TagRevision,
            ImageBytes = imgBytes,
            ImageSha256 = imgSha,
            MimeType = "image/bmp",
            Width = existing.ImageAsset?.Width > 0 ? existing.ImageAsset.Width : 120,
            Height = existing.ImageAsset?.Height > 0 ? existing.ImageAsset.Height : 80,
            CaptureSource = existing.ImageAsset?.CaptureSource ?? "ImagePickerCopy",
            CompletionSource = "Manual",
            Fingerprint = fingerprint
        };

        var saveRes = await storageService.SaveDraftAsync(snapshot, ct);
        if (!saveRes.Succeeded)
        {
            throw new InvalidOperationException(saveRes.ErrorMessage ?? "更新名片失敗");
        }
    }

    private static List<DraftValueSnapshot> BuildDraftValues(ImportContactDraft draft)
    {
        var list = new List<DraftValueSnapshot>();
        int ordinal = 0;

        void AddIf(string kind, string value, string label)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(new DraftValueSnapshot
                {
                    ValueKind = kind,
                    RawValue = value.Trim(),
                    Label = label,
                    Ordinal = ordinal++,
                    SourceMode = "Manual",
                    LockedAt = DateTimeOffset.UtcNow
                });
            }
        }

        AddIf(CanonicalValueKind.Name, draft.Name, "姓名");
        AddIf(CanonicalValueKind.Company, draft.Company, "公司");
        AddIf(CanonicalValueKind.Custom, draft.Department, "部門");
        AddIf(CanonicalValueKind.Title, draft.Title, "職稱");
        AddIf(CanonicalValueKind.Phone, draft.Mobile, "手機");
        AddIf(CanonicalValueKind.Phone, draft.Phone, "電話");
        AddIf(CanonicalValueKind.Email, draft.Email, "Email");
        AddIf(CanonicalValueKind.Address, draft.Address, "地址");
        AddIf(CanonicalValueKind.Custom, draft.TaxId, "統一編號");
        AddIf(CanonicalValueKind.Website, draft.Website, "網站");
        AddIf(CanonicalValueKind.Note, draft.Notes, "備註");

        return list;
    }

    private static List<DraftValueSnapshot> MergeValues(
        IReadOnlyList<ContactValueRecord> existingValues,
        ImportContactDraft draft)
    {
        var dict = new Dictionary<string, (string Kind, string RawValue, string Label)>(StringComparer.OrdinalIgnoreCase);

        // Load existing
        foreach (var v in existingValues)
        {
            string key = v.ValueKind == CanonicalValueKind.Custom ? $"Custom_{v.Label}" : v.ValueKind;
            dict[key] = (v.ValueKind, v.RawValue, v.Label);
        }

        // Apply draft non-empty values
        void ApplyDraft(string key, string kind, string val, string label)
        {
            if (!string.IsNullOrWhiteSpace(val))
            {
                dict[key] = (kind, val.Trim(), label);
            }
        }

        ApplyDraft(CanonicalValueKind.Name, CanonicalValueKind.Name, draft.Name, "姓名");
        ApplyDraft(CanonicalValueKind.Company, CanonicalValueKind.Company, draft.Company, "公司");
        ApplyDraft("Custom_部門", CanonicalValueKind.Custom, draft.Department, "部門");
        ApplyDraft(CanonicalValueKind.Title, CanonicalValueKind.Title, draft.Title, "職稱");
        if (!string.IsNullOrWhiteSpace(draft.Mobile)) ApplyDraft("Phone_Mobile", CanonicalValueKind.Phone, draft.Mobile, "手機");
        if (!string.IsNullOrWhiteSpace(draft.Phone)) ApplyDraft("Phone_Tel", CanonicalValueKind.Phone, draft.Phone, "電話");
        ApplyDraft(CanonicalValueKind.Email, CanonicalValueKind.Email, draft.Email, "Email");
        ApplyDraft(CanonicalValueKind.Address, CanonicalValueKind.Address, draft.Address, "地址");
        ApplyDraft("Custom_統一編號", CanonicalValueKind.Custom, draft.TaxId, "統一編號");
        ApplyDraft(CanonicalValueKind.Website, CanonicalValueKind.Website, draft.Website, "網站");
        if (!string.IsNullOrWhiteSpace(draft.Notes))
        {
            if (dict.TryGetValue(CanonicalValueKind.Note, out var existingNote))
            {
                ApplyDraft(CanonicalValueKind.Note, CanonicalValueKind.Note, $"{existingNote.RawValue}\n{draft.Notes}", "備註");
            }
            else
            {
                ApplyDraft(CanonicalValueKind.Note, CanonicalValueKind.Note, draft.Notes, "備註");
            }
        }

        int ord = 0;
        return dict.Values.Select(v => new DraftValueSnapshot
        {
            ValueKind = v.Kind,
            RawValue = v.RawValue,
            Label = v.Label,
            Ordinal = ord++,
            SourceMode = "Manual",
            LockedAt = DateTimeOffset.UtcNow
        }).ToList();
    }

    private static (string Name, string Company, string Department, string Title, string Mobile, string Phone, string Email, string Address, string TaxId, string Website, string Tags, string Notes) ExtractFields(ContactDetailModel c)
    {
        string name = c.Name ?? string.Empty;
        string company = c.Company ?? string.Empty;
        string department = string.Empty;
        string title = string.Empty;
        string mobile = string.Empty;
        string phone = string.Empty;
        string email = string.Empty;
        string address = string.Empty;
        string taxId = string.Empty;
        string website = string.Empty;
        var notes = new List<string>();

        foreach (var v in c.Values ?? new List<ContactValueRecord>())
        {
            if (string.IsNullOrWhiteSpace(v.RawValue)) continue;

            switch (v.ValueKind)
            {
                case CanonicalValueKind.Name:
                    if (string.IsNullOrEmpty(name)) name = v.RawValue;
                    break;
                case CanonicalValueKind.Company:
                    if (string.IsNullOrEmpty(company)) company = v.RawValue;
                    break;
                case CanonicalValueKind.Title:
                    if (string.IsNullOrEmpty(title)) title = v.RawValue;
                    break;
                case CanonicalValueKind.Phone:
                    if (v.RawValue.StartsWith("09") || v.RawValue.StartsWith("+8869"))
                    {
                        if (string.IsNullOrEmpty(mobile)) mobile = v.RawValue;
                        else if (string.IsNullOrEmpty(phone)) phone = v.RawValue;
                        else notes.Add($"電話: {v.RawValue}");
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(phone)) phone = v.RawValue;
                        else if (string.IsNullOrEmpty(mobile)) mobile = v.RawValue;
                        else notes.Add($"電話: {v.RawValue}");
                    }
                    break;
                case CanonicalValueKind.Email:
                    if (string.IsNullOrEmpty(email)) email = v.RawValue;
                    else notes.Add($"Email: {v.RawValue}");
                    break;
                case CanonicalValueKind.Address:
                    if (string.IsNullOrEmpty(address)) address = v.RawValue;
                    else notes.Add($"地址: {v.RawValue}");
                    break;
                case CanonicalValueKind.Website:
                    if (string.IsNullOrEmpty(website)) website = v.RawValue;
                    else notes.Add($"網站: {v.RawValue}");
                    break;
                case CanonicalValueKind.Note:
                    notes.Add(v.RawValue);
                    break;
                case CanonicalValueKind.Custom:
                    if (v.Label.Contains("部門") || v.Label.Contains("單位")) department = v.RawValue;
                    else if (v.Label.Contains("統編") || v.Label.Contains("統一編號")) taxId = v.RawValue;
                    else notes.Add($"{v.Label}: {v.RawValue}");
                    break;
                default:
                    notes.Add(v.RawValue);
                    break;
            }
        }

        string tags = c.Tags != null && c.Tags.Count > 0
            ? string.Join("、", c.Tags.Select(t => t.DisplayName))
            : string.Empty;

        return (name, company, department, title, mobile, phone, email, address, taxId, website, tags, string.Join("; ", notes));
    }

    private static string MatchHeaderField(string rawHeader)
    {
        string h = rawHeader.Trim().Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();

        if (h is "姓名" or "聯絡人" or "名字" or "name" or "fullname" or "contact" or "contactname" or "名") return "Name";
        if (h is "公司" or "公司名稱" or "單位" or "組織" or "company" or "organization" or "org" or "companyname") return "Company";
        if (h is "部門" or "處室" or "課別" or "department" or "dept") return "Department";
        if (h is "職稱" or "頭銜" or "職位" or "title" or "jobtitle" or "position") return "Title";
        if (h is "手機" or "行動電話" or "移動電話" or "mobile" or "cell" or "mobilephone" or "cellphone") return "Mobile";
        if (h is "電話" or "公司電話" or "辦公室電話" or "市話" or "座機" or "phone" or "businessphone" or "officephone" or "workphone" or "tel") return "Phone";
        if (h is "email" or "電子郵件" or "電子信箱" or "mail" or "e-mail") return "Email";
        if (h is "地址" or "公司地址" or "通訊地址" or "聯絡地址" or "address" or "businessaddress" or "street") return "Address";
        if (h is "統一編號" or "統編" or "稅號" or "taxid" or "vat") return "TaxId";
        if (h is "網站" or "公司網址" or "網址" or "website" or "web" or "url") return "Website";
        if (h is "標籤" or "分類" or "分類標籤" or "類別" or "tags" or "categories" or "category") return "Tags";
        if (h is "備註" or "說明" or "附註" or "notes" or "note" or "comments") return "Notes";

        return string.Empty;
    }

    private static string NormalizeKey(string? val) =>
        string.IsNullOrWhiteSpace(val) ? string.Empty : val.Trim().Replace(" ", "").ToLowerInvariant();

    private static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("886")) digits = "0" + digits.Substring(3);
        return digits;
    }

    private static string ExtractPhone(IReadOnlyList<ContactValueRecord> values)
    {
        foreach (var v in values ?? Array.Empty<ContactValueRecord>())
        {
            if (v.ValueKind == CanonicalValueKind.Phone && !string.IsNullOrWhiteSpace(v.RawValue))
                return v.RawValue;
        }
        return string.Empty;
    }

    public static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;
        if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    public static byte[] CreateSyntheticBmp(int width, int height, byte r = 240, byte g = 244, byte b = 248)
    {
        int rowBytes = ((width * 3 + 3) / 4) * 4;
        int imageSize = rowBytes * height;
        int fileSize = 54 + imageSize;
        byte[] bmp = new byte[fileSize];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);

        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(0).CopyTo(bmp, 30);
        BitConverter.GetBytes(imageSize).CopyTo(bmp, 34);

        for (int y = 0; y < height; y++)
        {
            int rowStart = 54 + y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int pixelStart = rowStart + x * 3;
                if (x == 0 || x == width - 1 || y == 0 || y == height - 1)
                {
                    bmp[pixelStart] = 0xAA;
                    bmp[pixelStart + 1] = 0xAA;
                    bmp[pixelStart + 2] = 0xAA;
                }
                else
                {
                    bmp[pixelStart] = b;
                    bmp[pixelStart + 1] = g;
                    bmp[pixelStart + 2] = r;
                }
            }
        }
        return bmp;
    }
}
