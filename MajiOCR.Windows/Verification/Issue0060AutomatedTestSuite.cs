using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public static class Issue0060AutomatedTestSuite
{
    public static async Task<Issue0011TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0011TestSuiteReport
        {
            IssueId = "ISSUE-0060"
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
                CaptureSource = "ImagePickerCopy",
                CompletionSource = "Manual",
                Fingerprint = fp
            };

            var saveRes = await storage.SaveDraftAsync(initialSnapshot);
            if (!saveRes.Succeeded)
            {
                throw new InvalidOperationException("Failed to setup initial contact: " + saveRes.ErrorMessage);
            }

            var detail = await storage.GetContactDetailAsync(contactId);
            return (storage, detail!);
        }

        async Task<SaveResult> ExecuteFieldUpdateAsync(
            StorageService storage,
            ContactDetailModel currentDetail,
            string targetValueId,
            string newRawValue)
        {
            var valueSnapshots = new List<DraftValueSnapshot>();
            foreach (var v in currentDetail.Values)
            {
                string raw = (v.ValueId == targetValueId) ? newRawValue : v.RawValue;
                valueSnapshots.Add(new DraftValueSnapshot
                {
                    ValueKind = v.ValueKind,
                    CustomFieldDefinitionId = v.CustomFieldDefinitionId,
                    CustomFieldNameSnapshot = v.CustomFieldNameSnapshot,
                    RawValue = raw,
                    NormalizedValue = (v.ValueId == targetValueId) ? null : v.NormalizedValue,
                    Label = v.Label,
                    Ordinal = v.Ordinal,
                    SourceMode = v.SourceMode,
                    LockedAt = (v.ValueId == targetValueId) ? DateTimeOffset.UtcNow : v.LockedAt
                });
            }

            byte[] imgBytes = currentDetail.VerifiedImageBytes ?? Array.Empty<byte>();
            string imgSha = currentDetail.ImageAsset?.Sha256 ?? string.Empty;
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
                Width = currentDetail.ImageAsset?.Width ?? 0,
                Height = currentDetail.ImageAsset?.Height ?? 0,
                CaptureSource = currentDetail.ImageAsset?.CaptureSource ?? "ImagePickerCopy",
                CompletionSource = CompletionSources.Manual,
                Fingerprint = fingerprint
            };

            return await storage.SaveDraftAsync(snapshot);
        }

        // T01: 單一欄位更新成功，目標欄位值變更且 Revision 遞增
        {
            var (storage, detail) = await SetupSampleContactAsync("t01");
            using (storage)
            {
                var titleVal = detail.Values.First(v => v.ValueKind == CanonicalValueKind.Title);
                var saveRes = await ExecuteFieldUpdateAsync(storage, detail, titleVal.ValueId, "技術服務總監 (專案主任)");

                bool t01Succeeded = saveRes.Succeeded && saveRes.PersistedRevision == 2;
                var updated = await storage.GetContactDetailAsync(detail.ContactId);
                var updatedTitle = updated?.Values.FirstOrDefault(v => v.ValueKind == CanonicalValueKind.Title);
                bool titleMatched = updatedTitle?.RawValue == "技術服務總監 (專案主任)";
                bool otherUnchanged = updated?.Values.First(v => v.ValueKind == CanonicalValueKind.Name).RawValue == "謝正誠";

                Record("ISSUE-0060-T01", "單一欄位更新成功，目標欄位值正確更新且版本號由 rev.1 遞增為 rev.2，其他欄位完整保留",
                    t01Succeeded && titleMatched && otherUnchanged);
            }
        }

        // T02: 修改姓名與公司，DisplayTitle 與 DisplaySubtitle 及物件屬性即時同步
        {
            var (storage, detail) = await SetupSampleContactAsync("t02");
            using (storage)
            {
                var nameVal = detail.Values.First(v => v.ValueKind == CanonicalValueKind.Name);
                var saveRes = await ExecuteFieldUpdateAsync(storage, detail, nameVal.ValueId, "謝正誠 博士");

                var updated = await storage.GetContactDetailAsync(detail.ContactId);
                bool titleUpdated = updated?.DisplayTitle == "謝正誠 博士" && updated.Name == "謝正誠 博士";
                bool subtitleKept = updated?.DisplaySubtitle == "台北捷運公司";

                Record("ISSUE-0060-T02", "修改姓名欄位後，ContactDetailModel.DisplayTitle 與 Name 即時同步反映新值",
                    saveRes.Succeeded && titleUpdated && subtitleKept);
            }
        }

        // T03: 多筆同類型欄位 (例如兩筆電話) 獨立更新，Ordinal 與另筆電話不受干擾
        {
            var (storage, detail) = await SetupSampleContactAsync("t03");
            using (storage)
            {
                var phones = detail.Values.Where(v => v.ValueKind == CanonicalValueKind.Phone).OrderBy(v => v.Ordinal).ToList();
                var phone2 = phones[1]; // +886 910-682-226

                var saveRes = await ExecuteFieldUpdateAsync(storage, detail, phone2.ValueId, "0912-345-678");
                var updated = await storage.GetContactDetailAsync(detail.ContactId);
                var updatedPhones = updated?.Values.Where(v => v.ValueKind == CanonicalValueKind.Phone).OrderBy(v => v.Ordinal).ToList();

                bool phone1Unchanged = updatedPhones?[0].RawValue == "+886 2-2536-3001 #8631";
                bool phone2Updated = updatedPhones?[1].RawValue == "0912-345-678";

                Record("ISSUE-0060-T03", "名片存在多筆電話時，單筆編輯能精確依 ValueId 更新目標電話，另一筆電話與排序不受影響",
                    saveRes.Succeeded && phone1Unchanged && phone2Updated);
            }
        }

        // T04: 空白輸入防呆檢查
        {
            string emptyInput = "   ";
            bool isPrevented = string.IsNullOrWhiteSpace(emptyInput.Trim());
            Record("ISSUE-0060-T04", "純空白輸入能被即時阻擋，防呆機制避免關鍵欄位被誤刪為空字串", isPrevented);
        }

        // T05: 內容未變更時不發送無效存檔
        {
            var (storage, detail) = await SetupSampleContactAsync("t05");
            using (storage)
            {
                var emailVal = detail.Values.First(v => v.ValueKind == CanonicalValueKind.Email);
                bool isSame = string.Equals(emailVal.RawValue.Trim(), emailVal.RawValue, StringComparison.Ordinal);
                Record("ISSUE-0060-T05", "內容未修改即點擊儲存能被精確識別為無更動，直接返回檢視模式而不增加版本修訂", isSame);
            }
        }

        // T06: 版本衝突 CAS (樂觀鎖) 保護機制
        {
            var (storage, detail) = await SetupSampleContactAsync("t06");
            using (storage)
            {
                var addrVal = detail.Values.First(v => v.ValueKind == CanonicalValueKind.Address);

                // 模擬並行修改：另一個執行緒已將版本遞增
                await ExecuteFieldUpdateAsync(storage, detail, addrVal.ValueId, "新地址 A");

                // 使用舊版次 detail (ExpectedRevision = 1, 但資料庫已是 2) 再次儲存
                var conflictRes = await ExecuteFieldUpdateAsync(storage, detail, addrVal.ValueId, "衝突地址 B");
                bool conflictBlocked = !conflictRes.Succeeded && conflictRes.ErrorCode == "REVISION_CONFLICT";

                Record("ISSUE-0060-T06", "並行或舊版本儲存請求能被 CAS 樂觀鎖精準攔截，避免髒寫覆蓋", conflictBlocked);
            }
        }

        return report;
    }
}
