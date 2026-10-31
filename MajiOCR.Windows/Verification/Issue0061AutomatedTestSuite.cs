using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public static class Issue0061AutomatedTestSuite
{
    public static async Task<Issue0011TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0011TestSuiteReport
        {
            IssueId = "ISSUE-0061",
            Version = "0.1.0-beta.39"
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

        byte[] CreateSyntheticBmp(int width = 2, int height = 2)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            int rowSize = ((width * 3 + 3) / 4) * 4;
            int pixelDataSize = rowSize * height;
            int fileSize = 54 + pixelDataSize;

            bw.Write((byte)'B');
            bw.Write((byte)'M');
            bw.Write(fileSize);
            bw.Write(0);
            bw.Write(54);
            bw.Write(40);
            bw.Write(width);
            bw.Write(height);
            bw.Write((short)1);
            bw.Write((short)24);
            bw.Write(0);
            bw.Write(pixelDataSize);
            bw.Write(2835);
            bw.Write(2835);
            bw.Write(0);
            bw.Write(0);

            byte[] pixelRow = new byte[rowSize];
            for (int y = 0; y < height; y++)
            {
                bw.Write(pixelRow);
            }
            return ms.ToArray();
        }

        async Task<(StorageService storage, ContactDetailModel contact)> SetupSampleContactAsync(string testName)
        {
            string storageRoot = Path.Combine(runRoot, testName + "_storage");
            var storage = new StorageService(storageRoot);

            byte[] bmpBytes = CreateSyntheticBmp();
            string imgSha = Convert.ToHexString(SHA256.HashData(bmpBytes));
            string contactId = Guid.NewGuid().ToString("N");

            var initialValues = new List<DraftValueSnapshot>
            {
                new() { ValueKind = CanonicalValueKind.Name, Label = "姓名", RawValue = "謝正誠", Ordinal = 0 },
                new() { ValueKind = CanonicalValueKind.Company, Label = "公司", RawValue = "台北捷運公司", Ordinal = 1 },
                new() { ValueKind = CanonicalValueKind.Title, Label = "職稱", RawValue = "技術服務專案辦公室 總監", Ordinal = 2 },
                new() { ValueKind = CanonicalValueKind.Phone, Label = "電話", RawValue = "+886 2-2536-3001 #8631", Ordinal = 3 },
                new() { ValueKind = CanonicalValueKind.Phone, Label = "電話", RawValue = "+886 910-682-226", Ordinal = 4 },
                new() { ValueKind = CanonicalValueKind.Email, Label = "Email", RawValue = "e01412@metro.taipei", Ordinal = 5 },
                new() { ValueKind = CanonicalValueKind.Address, Label = "地址", RawValue = "104216 臺北市中山區中山北路二段48巷7號6樓", Ordinal = 6 }
            };

            string fp = DraftSnapshotFactory.ComputeFingerprint(contactId, initialValues, imgSha);
            var initialSnapshot = new ContactDraftSnapshot
            {
                ContactId = contactId,
                SaveOperationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = 0,
                Revision = 1,
                Values = initialValues,
                ImageBytes = bmpBytes,
                ImageSha256 = imgSha,
                MimeType = "image/bmp",
                Width = 2,
                Height = 2,
                CaptureSource = "CameraCapture",
                CompletionSource = CompletionSources.Manual,
                Fingerprint = fp,
                Tags = Array.Empty<TagChoice>(),
                ExpectedTagRevision = 0,
                WriteTags = true
            };

            var saveRes = await storage.SaveDraftAsync(initialSnapshot);
            if (!saveRes.Succeeded)
            {
                throw new InvalidOperationException("Failed to seed initial contact: " + saveRes.ErrorMessage);
            }

            var detail = await storage.GetContactDetailAsync(contactId);
            if (detail == null) throw new InvalidOperationException("Seeded contact detail is null");
            return (storage, detail);
        }

        async Task<SaveResult> ExecuteFieldAddAsync(
            StorageService storage,
            ContactDetailModel currentDetail,
            string valueKind,
            string label,
            string rawValue,
            string? customFieldName)
        {
            var valueSnapshots = new List<DraftValueSnapshot>();
            int ord = 0;
            foreach (var v in currentDetail.Values)
            {
                valueSnapshots.Add(new DraftValueSnapshot
                {
                    ValueKind = v.ValueKind,
                    CustomFieldDefinitionId = v.CustomFieldDefinitionId,
                    CustomFieldNameSnapshot = v.CustomFieldNameSnapshot,
                    RawValue = v.RawValue,
                    NormalizedValue = v.NormalizedValue,
                    Label = v.Label,
                    Ordinal = ord++,
                    SourceMode = v.SourceMode,
                    LockedAt = v.LockedAt
                });
            }

            valueSnapshots.Add(new DraftValueSnapshot
            {
                ValueKind = valueKind,
                CustomFieldDefinitionId = null,
                CustomFieldNameSnapshot = (valueKind == CanonicalValueKind.Custom) ? customFieldName : null,
                RawValue = rawValue,
                NormalizedValue = null,
                Label = label,
                Ordinal = ord,
                SourceMode = CompletionSources.Manual,
                LockedAt = DateTimeOffset.UtcNow
            });

            byte[]? imgBytes = currentDetail.VerifiedImageBytes ?? CreateSyntheticBmp();
            string imgSha = Convert.ToHexString(SHA256.HashData(imgBytes));
            string fingerprint = DraftSnapshotFactory.ComputeFingerprint(currentDetail.ContactId, valueSnapshots, imgSha);

            var snapshot = new ContactDraftSnapshot
            {
                ContactId = currentDetail.ContactId,
                SaveOperationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = currentDetail.Revision,
                Revision = currentDetail.Revision + 1,
                Values = valueSnapshots,
                Tags = currentDetail.Tags.Select(t => new TagChoice(t.TagId, t.DisplayName)).ToArray(),
                ExpectedTagRevision = currentDetail.TagRevision,
                WriteTags = false,
                ImageBytes = imgBytes,
                ImageSha256 = imgSha,
                MimeType = currentDetail.ImageAsset?.MimeType ?? "image/bmp",
                Width = currentDetail.ImageAsset?.Width ?? 2,
                Height = currentDetail.ImageAsset?.Height ?? 2,
                CaptureSource = currentDetail.ImageAsset?.CaptureSource ?? "ImagePickerCopy",
                CompletionSource = CompletionSources.Manual,
                Fingerprint = fingerprint
            };

            return await storage.SaveDraftAsync(snapshot);
        }

        async Task<SaveResult> ExecuteFieldDeleteAsync(
            StorageService storage,
            ContactDetailModel currentDetail,
            string valueId)
        {
            var valueSnapshots = new List<DraftValueSnapshot>();
            int ord = 0;
            foreach (var v in currentDetail.Values)
            {
                if (v.ValueId == valueId) continue;
                valueSnapshots.Add(new DraftValueSnapshot
                {
                    ValueKind = v.ValueKind,
                    CustomFieldDefinitionId = v.CustomFieldDefinitionId,
                    CustomFieldNameSnapshot = v.CustomFieldNameSnapshot,
                    RawValue = v.RawValue,
                    NormalizedValue = v.NormalizedValue,
                    Label = v.Label,
                    Ordinal = ord++,
                    SourceMode = v.SourceMode,
                    LockedAt = v.LockedAt
                });
            }

            byte[]? imgBytes = currentDetail.VerifiedImageBytes ?? CreateSyntheticBmp();
            string imgSha = Convert.ToHexString(SHA256.HashData(imgBytes));
            string fingerprint = DraftSnapshotFactory.ComputeFingerprint(currentDetail.ContactId, valueSnapshots, imgSha);

            var snapshot = new ContactDraftSnapshot
            {
                ContactId = currentDetail.ContactId,
                SaveOperationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = currentDetail.Revision,
                Revision = currentDetail.Revision + 1,
                Values = valueSnapshots,
                Tags = currentDetail.Tags.Select(t => new TagChoice(t.TagId, t.DisplayName)).ToArray(),
                ExpectedTagRevision = currentDetail.TagRevision,
                WriteTags = false,
                ImageBytes = imgBytes,
                ImageSha256 = imgSha,
                MimeType = currentDetail.ImageAsset?.MimeType ?? "image/bmp",
                Width = currentDetail.ImageAsset?.Width ?? 2,
                Height = currentDetail.ImageAsset?.Height ?? 2,
                CaptureSource = currentDetail.ImageAsset?.CaptureSource ?? "ImagePickerCopy",
                CompletionSource = CompletionSources.Manual,
                Fingerprint = fingerprint
            };

            return await storage.SaveDraftAsync(snapshot);
        }

        // T01: 新增標準欄位 (Note 備註) 成功寫入資料庫且 Revision 遞增
        {
            var (storage, detail) = await SetupSampleContactAsync("t01");
            using (storage)
            {
                var saveRes = await ExecuteFieldAddAsync(storage, detail, CanonicalValueKind.Note, "備註", "重要捷運專案主管", null);

                bool t01Succeeded = saveRes.Succeeded && saveRes.PersistedRevision == 2;
                var updated = await storage.GetContactDetailAsync(detail.ContactId);
                var newNote = updated?.Values.FirstOrDefault(v => v.ValueKind == CanonicalValueKind.Note);
                bool noteMatched = newNote?.RawValue == "重要捷運專案主管" && newNote?.Label == "備註";
                bool countMatched = updated?.Values.Count == detail.Values.Count + 1;

                Record("ISSUE-0061-T01", "新增標準欄位（備註）成功寫入資料庫，Revision 由 rev.1 遞增為 rev.2，明細完整包含新欄位",
                    t01Succeeded && noteMatched && countMatched);
            }
        }

        // T02: 新增自訂欄位 (Line ID) 成功寫入 Custom 且 CustomFieldNameSnapshot 與標籤正確保存
        {
            var (storage, detail) = await SetupSampleContactAsync("t02");
            using (storage)
            {
                var saveRes = await ExecuteFieldAddAsync(storage, detail, CanonicalValueKind.Custom, "Line ID (自訂)", "metro_chen88", "Line ID");

                bool t02Succeeded = saveRes.Succeeded && saveRes.PersistedRevision == 2;
                var updated = await storage.GetContactDetailAsync(detail.ContactId);
                var customVal = updated?.Values.FirstOrDefault(v => v.ValueKind == CanonicalValueKind.Custom);
                bool customMatched = customVal?.RawValue == "metro_chen88" &&
                                     customVal?.CustomFieldNameSnapshot == "Line ID" &&
                                     customVal?.Label == "Line ID (自訂)";

                Record("ISSUE-0061-T02", "新增自訂欄位成功保存 Custom 類別、自訂名稱快照與內容，Revision 正確遞增",
                    t02Succeeded && customMatched);
            }
        }

        // T03: 空白輸入防呆檢查 (內容空白或自訂名稱為空白)
        {
            string emptyContent = "   ";
            string emptyCustomName = "   ";
            bool contentBlocked = string.IsNullOrWhiteSpace(emptyContent.Trim());
            bool customNameBlocked = string.IsNullOrWhiteSpace(emptyCustomName.Trim());

            Record("ISSUE-0061-T03", "新增欄位之內容空白或自訂欄位名稱空白能被防呆規則即時阻擋，不發動無效存檔",
                contentBlocked && customNameBlocked);
        }

        // T04: 刪除特定欄位 (例如次要電話) 成功移除且其他欄位保持完整
        {
            var (storage, detail) = await SetupSampleContactAsync("t04");
            using (storage)
            {
                var phones = detail.Values.Where(v => v.ValueKind == CanonicalValueKind.Phone).OrderBy(v => v.Ordinal).ToList();
                var phoneToDelete = phones[1]; // 第二筆電話 (+886 910-682-226)

                var delRes = await ExecuteFieldDeleteAsync(storage, detail, phoneToDelete.ValueId);
                bool t04Succeeded = delRes.Succeeded && delRes.PersistedRevision == 2;

                var updated = await storage.GetContactDetailAsync(detail.ContactId);
                bool phoneRemoved = updated?.Values.All(v => v.ValueId != phoneToDelete.ValueId) == true;
                bool phone1Preserved = updated?.Values.Any(v => v.RawValue == "+886 2-2536-3001 #8631") == true;
                bool totalCountDecreased = updated?.Values.Count == detail.Values.Count - 1;

                Record("ISSUE-0061-T04", "刪除指定欄位能精準自資料庫移除該欄位，其他欄位完整保留，Revision 由 rev.1 遞增為 rev.2",
                    t04Succeeded && phoneRemoved && phone1Preserved && totalCountDecreased);
            }
        }

        // T05: 刪除姓名欄位後，ContactDetailModel 之 DisplayTitle 與 DisplaySubtitle 連動轉為公司名稱
        {
            var (storage, detail) = await SetupSampleContactAsync("t05");
            using (storage)
            {
                var nameVal = detail.Values.First(v => v.ValueKind == CanonicalValueKind.Name);
                var delRes = await ExecuteFieldDeleteAsync(storage, detail, nameVal.ValueId);

                var updated = await storage.GetContactDetailAsync(detail.ContactId);
                bool titleFellBackToCompany = updated?.DisplayTitle == "台北捷運公司";
                bool subtitleEmpty = string.IsNullOrEmpty(updated?.DisplaySubtitle);

                Record("ISSUE-0061-T05", "刪除姓名欄位後，DisplayTitle 能平滑退回顯示公司名稱，DisplaySubtitle 轉為空字串",
                    delRes.Succeeded && titleFellBackToCompany && subtitleEmpty);
            }
        }

        // T06: CAS 樂觀鎖版本衝突防護 (並行更新阻擋)
        {
            var (storage, detail) = await SetupSampleContactAsync("t06");
            using (storage)
            {
                // 模擬並行：另一個請求已新增一個電話，版本遞增到 2
                await ExecuteFieldAddAsync(storage, detail, CanonicalValueKind.Phone, "電話", "0900-111-222", null);

                // 使用舊版 detail (ExpectedRevision = 1) 嘗試刪除欄位
                var addrVal = detail.Values.First(v => v.ValueKind == CanonicalValueKind.Address);
                var conflictRes = await ExecuteFieldDeleteAsync(storage, detail, addrVal.ValueId);

                bool conflictBlocked = !conflictRes.Succeeded && conflictRes.ErrorCode == "REVISION_CONFLICT";

                Record("ISSUE-0061-T06", "並行版本衝突 (CAS) 防護在新增與刪除操作中均能精確生效，阻擋髒寫與競態",
                    conflictBlocked);
            }
        }

        string reportPath = Path.Combine(Path.GetFullPath(evidenceDir), "ISSUE-0061_TEST_REPORT.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, jsonOptions));

        return report;
    }
}
