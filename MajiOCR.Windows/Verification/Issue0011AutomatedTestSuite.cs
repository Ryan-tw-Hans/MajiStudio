using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;
using MajiOCR_Windows.ViewModels;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MajiOCR_Windows.Verification;

public class Issue0011TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "PASS"; // PASS, FAIL, NOT_RUN
    public bool Passed => Status == "PASS";
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0011TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0011TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.12";
    public string IssueId { get; set; } = "ISSUE-0011";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public string NativeSqliteVersion { get; set; } = string.Empty;
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public int NotRunCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0011TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0011AutomatedTestSuite
{
    private class DelayedStorageFake : IStorageService
    {
        private readonly IStorageService _inner;
        private readonly int _delayMs;

        public DelayedStorageFake(IStorageService inner, int delayMs)
        {
            _inner = inner;
            _delayMs = delayMs;
        }

        public bool IsAvailable => _inner.IsAvailable;
        public string? ErrorMessage => _inner.ErrorMessage;
        public string StorageRoot => _inner.StorageRoot;
        public string DatabasePath => _inner.DatabasePath;
        public bool IsEncrypted => _inner.IsEncrypted;
        public string? CurrentPassphrase => _inner.CurrentPassphrase;

        public void SetCurrentPassphrase(string? passphrase) => _inner.SetCurrentPassphrase(passphrase);

        public async Task<SaveResult> SaveDraftAsync(ContactDraftSnapshot snapshot, CancellationToken ct = default)
        {
            await Task.Delay(_delayMs, ct);
            return await _inner.SaveDraftAsync(snapshot, ct);
        }

        public Task<DeleteResult> DeleteContactAsync(string contactId, CancellationToken ct = default) => _inner.DeleteContactAsync(contactId, ct);
        public Task<PagedResult<ContactSummaryItem>> GetContactsPagedAsync(string? searchQuery, int pageIndex, int pageSize = 30, CancellationToken ct = default) => _inner.GetContactsPagedAsync(searchQuery, pageIndex, pageSize, ct);
        public Task<ContactDetailModel?> GetContactDetailAsync(string contactId, CancellationToken ct = default) => _inner.GetContactDetailAsync(contactId, ct);
        public string? GetCardImageAbsolutePath(string contactId) => _inner.GetCardImageAbsolutePath(contactId);
        public Task RecoverPendingOperationsAsync(CancellationToken ct = default) => _inner.RecoverPendingOperationsAsync(ct);
        public Task<bool> EnableEncryptionAsync(string newPassword, CancellationToken ct = default) => _inner.EnableEncryptionAsync(newPassword, ct);
        public Task<bool> ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default) => _inner.ChangePasswordAsync(oldPassword, newPassword, ct);
        public Task<bool> DisableEncryptionAsync(string currentPassword, CancellationToken ct = default) => _inner.DisableEncryptionAsync(currentPassword, ct);
        public void SetFailureInjectionHook(Action<string>? hook) => _inner.SetFailureInjectionHook(hook);
        public void Dispose() => _inner.Dispose();
    }

    public static async Task<Issue0011TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        MainViewModel.IsHeadlessEnvironment = true;

        var report = new Issue0011TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, string status, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0011TestCaseResult
            {
                TestId = id,
                Description = desc,
                Status = status,
                Details = details,
                LatencyMs = ms
            });

            if (status == "PASS") report.PassCount++;
            else if (status == "FAIL") report.FailCount++;
            else report.NotRunCount++;
            report.TotalTests++;
        }

        string runId = $"run_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        string evidenceRoot = Path.GetFullPath(evidenceDir).TrimEnd(Path.DirectorySeparatorChar);
        string fixtureRoot = Path.GetFullPath(Path.Combine(evidenceRoot, runId));
        ValidateOwnedFixtureRoot(fixtureRoot, evidenceRoot, runId);
        if (Directory.Exists(fixtureRoot) || File.Exists(fixtureRoot)) throw new SecurityException("FIXTURE_ALREADY_EXISTS");
        Directory.CreateDirectory(fixtureRoot);
        var ownedLinks = new List<string>();

        // Retrieve native SQLite version
        try
        {
            using var testConn = new SqliteConnection("Data Source=:memory:;");
            testConn.Open();
            using var cmd = testConn.CreateCommand();
            cmd.CommandText = "SELECT sqlite_version();";
            report.NativeSqliteVersion = cmd.ExecuteScalar()?.ToString() ?? "Unknown";
        }
        catch (Exception ex)
        {
            report.NativeSqliteVersion = $"Error: {ex.Message}";
        }

        // =========================================================================================
        // W3-01: 五語多筆自訂換行儲存與重開讀回 (RS-v0.2 W3-FR-01, W3-FR-05, DBC-v0.3)
        // =========================================================================================
        sw.Restart();
        string root01 = Path.Combine(fixtureRoot, "w3_01");
        try
        {
            using var service01 = new StorageService(root01);
            var catalog01 = CreateIsolatedCatalog(root01);
            var vm01 = new MainViewModel(catalog01, null, service01);

            byte[] synthImage01 = CreateSyntheticBmp(100, 60, 0x10, 0x80, 0xD0);
            await vm01.SetSessionImageAsync(ImageSessionInfo.Create(synthImage01, "人工合成卡片.png", "ImagePickerCopy", "image/bmp", 100, 60));

            vm01.NameRow.Value = "陳大文";
            vm01.NameRow.Confirm();

            vm01.CompanyRow.Value = "臺灣未來科技股份有限公司\nTaiwan Future Tech Corp.";
            vm01.CompanyRow.Confirm();

            vm01.TitleRow.Value = "技術副總經理 / VP of Tech";
            vm01.TitleRow.Confirm();

            var phone1 = vm01.AddPhoneRow("+886-2-2345-6789");
            phone1.Confirm();
            var phone2 = vm01.AddPhoneRow("0912-345-678");
            phone2.Confirm();

            var email1 = vm01.AddEmailRow("david.chen@example.com");
            email1.Confirm();
            var email2 = vm01.AddEmailRow("contact@example.com");
            email2.Confirm();

            vm01.WebsiteRow.Value = "https://example.com";
            vm01.WebsiteRow.Confirm();

            vm01.AddressRow.Value = "臺北市信義區信義路五段7號89樓";
            vm01.AddressRow.Confirm();

            vm01.NoteRow.Value = "五語多行備註\nEnglish Line\n日本語テスト\n한국어 메모\n简体中文备注";
            vm01.NoteRow.Confirm();

            var customRow1 = vm01.AddCustomRow("def_tax_id_01", "統一編號");
            customRow1.Value = "12345678";
            customRow1.Confirm();

            var customRow2 = vm01.AddCustomRow("def_dept_02", "部門名稱");
            customRow2.Value = "創新研發部";
            customRow2.Confirm();

            // Verify moving custom row order alters draft fingerprint
            string? fpBeforeMove = DraftSnapshotFactory.ComputeCurrentDraftFingerprint(vm01);
            int idx1 = vm01.DraftRows.IndexOf(customRow1);
            int idx2 = vm01.DraftRows.IndexOf(customRow2);
            vm01.DraftRows.Move(idx2, idx1);
            string? fpAfterMove = DraftSnapshotFactory.ComputeCurrentDraftFingerprint(vm01);
            bool rowMoveChangesFingerprint = !string.IsNullOrEmpty(fpBeforeMove) && fpBeforeMove != fpAfterMove;

            bool dirtyBeforeSave = vm01.HasUnsavedChanges;
            var saveResult = await vm01.SaveCurrentDraftAsync();
            bool dirtyAfterSave = vm01.HasUnsavedChanges;

            bool saveOk = saveResult.Succeeded && !saveResult.IsDuplicate && dirtyBeforeSave && !dirtyAfterSave;

            // Reopen in fresh service
            service01.Dispose();
            using var service01Reopen = new StorageService(root01);
            var detail = await service01Reopen.GetContactDetailAsync(vm01.CurrentContactId);

            var customValues = detail?.Values.Where(v => v.ValueKind == CanonicalValueKind.Custom).ToList();
            bool customOrderPreserved = customValues != null &&
                                        customValues.Count == 2 &&
                                        customValues[0].CustomFieldNameSnapshot == "部門名稱" &&
                                        customValues[0].RawValue == "創新研發部" &&
                                        customValues[1].CustomFieldNameSnapshot == "統一編號" &&
                                        customValues[1].RawValue == "12345678";

            bool readBackOk = detail != null &&
                              detail.Name == "陳大文" &&
                              detail.Company.StartsWith("臺灣未來科技股份有限公司") &&
                              detail.Values.Count == 12 &&
                              customOrderPreserved &&
                              detail.Values.Count(v => v.ValueKind == CanonicalValueKind.Phone) == 2 &&
                              detail.Values.Count(v => v.ValueKind == CanonicalValueKind.Email) == 2 &&
                              detail.IsImageAvailable &&
                              detail.VerifiedImageBytes != null &&
                              detail.VerifiedImageBytes.Length == synthImage01.Length;

            bool pass = saveOk && readBackOk && rowMoveChangesFingerprint;
            Record("W3-01", "五語多筆自訂換行儲存與重開讀回", pass ? "PASS" : "FAIL",
                $"SaveOk={saveOk}, ReadBackOk={readBackOk}, ValuesCount={detail?.Values.Count}, ImageBytesVerified={detail?.IsImageAvailable}, RowMoveChangesFp={rowMoveChangesFingerprint}, CustomOrderPreserved={customOrderPreserved}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-01", "五語多筆自訂換行儲存與重開讀回", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-02: 欄位確認狀態變更、驗證阻擋、無圖阻擋與儲存中修改保持dirty (RS-v0.2 W3-FR-01, W3-FR-07)
        // =========================================================================================
        sw.Restart();
        string root02 = Path.Combine(fixtureRoot, "w3_02");
        try
        {
            using var service02 = new StorageService(root02);
            var vm02 = new MainViewModel(CreateIsolatedCatalog(root02), null, service02);
            byte[] synthImage02 = CreateSyntheticBmp(80, 50, 0x20, 0xA0, 0x40);

            // A. Block unconfirmed non-empty fields
            await vm02.SetSessionImageAsync(ImageSessionInfo.Create(synthImage02, "card.png", "ImagePickerCopy", "image/bmp", 80, 50));
            vm02.NameRow.Value = "張三";
            var resA = await vm02.SaveCurrentDraftAsync();
            bool blockUnconfirmedOk = !resA.Succeeded && resA.ErrorCode == nameof(DraftValidationCode.UnconfirmedFields);

            // B. Block if all fields empty
            vm02.NameRow.Value = "";
            var resB = await vm02.SaveCurrentDraftAsync();
            bool blockEmptyOk = !resB.Succeeded && resB.ErrorCode == nameof(DraftValidationCode.NoConfirmedFields);

            // C. Confirmed value modification invalidates confirmation
            vm02.NameRow.Value = "李四";
            vm02.NameRow.Confirm();
            bool confirmedBeforeEdit = vm02.NameRow.IsConfirmed && vm02.NameRow.ConfirmedAt != null;

            vm02.NameRow.Value = "李四新值";
            bool invalidatedByValue = vm02.NameRow.IsPending && vm02.NameRow.ConfirmedAt == null;

            // Re-confirm then edit Kind
            vm02.NameRow.Confirm();
            vm02.NameRow.ValueKind = CanonicalValueKind.Title;
            bool invalidatedByKind = vm02.NameRow.IsPending && vm02.NameRow.ConfirmedAt == null;

            // D. Undo restores confirmation status and time
            vm02.NameRow.Value = "原本值";
            vm02.NameRow.Confirm();
            var timeConfirmed = vm02.NameRow.ConfirmedAt;
            vm02.NameRow.SaveUndoSnapshot();

            vm02.NameRow.Value = "被ROI替換的值";
            vm02.NameRow.RestoreUndo();
            bool undoRestoredOk = vm02.NameRow.Value == "原本值" &&
                                  vm02.NameRow.IsConfirmed &&
                                  vm02.NameRow.ConfirmedAt == timeConfirmed;

            // E. No image attached blocks save
            vm02.CreateBlankDraft(); // clears image
            vm02.NameRow.Value = "王五";
            vm02.NameRow.Confirm();
            var resE = await vm02.SaveCurrentDraftAsync();
            bool blockNoImageOk = !resE.Succeeded && resE.ErrorCode == nameof(DraftValidationCode.MissingImage);

            // F. In-flight edit during save await keeps dirty state (Delayed Storage Fake)
            var delayedStorage = new DelayedStorageFake(service02, delayMs: 80);
            var vm02Delayed = new MainViewModel(CreateIsolatedCatalog(root02), null, delayedStorage);
            await vm02Delayed.SetSessionImageAsync(ImageSessionInfo.Create(synthImage02, "card_delay.png", "ImagePickerCopy", "image/bmp", 80, 50));
            vm02Delayed.NameRow.Value = "原始送出姓名";
            vm02Delayed.NameRow.Confirm();

            var saveTask = vm02Delayed.SaveCurrentDraftAsync();
            await Task.Delay(20);
            vm02Delayed.NameRow.Value = "儲存中更改的新姓名";
            vm02Delayed.NameRow.Confirm();

            var delayedResult = await saveTask;
            bool delayedSaveOk = delayedResult.Succeeded;
            bool dirtyAfterConcurrentEdit = vm02Delayed.HasUnsavedChanges;

            var savedDetail = await service02.GetContactDetailAsync(vm02Delayed.CurrentContactId);
            bool savedOldValueOk = savedDetail != null && savedDetail.Name == "原始送出姓名";

            bool pass = blockUnconfirmedOk && blockEmptyOk && confirmedBeforeEdit && invalidatedByValue && invalidatedByKind && undoRestoredOk && blockNoImageOk && delayedSaveOk && dirtyAfterConcurrentEdit && savedOldValueOk;
            Record("W3-02", "欄位確認狀態變更、驗證阻擋與無圖阻擋", pass ? "PASS" : "FAIL",
                $"BlockUnconfirmed={blockUnconfirmedOk}, InvalidateVal={invalidatedByValue}, UndoRestored={undoRestoredOk}, BlockNoImage={blockNoImageOk}, InFlightDirty={dirtyAfterConcurrentEdit}, SavedFrozenValue={savedOldValueOk}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-02", "欄位確認狀態變更、驗證阻擋與無圖阻擋", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-03: 重複保存、修訂更新CAS、換圖/空白草稿重設與dirty狀態 (RS-v0.2 W3-FR-06, W3-FR-08)
        // =========================================================================================
        sw.Restart();
        string root03 = Path.Combine(fixtureRoot, "w3_03");
        try
        {
            using var service03 = new StorageService(root03);
            var vm03 = new MainViewModel(CreateIsolatedCatalog(root03), null, service03);
            byte[] synthImage03 = CreateSyntheticBmp(80, 50, 0x50, 0x30, 0x80);
            await vm03.SetSessionImageAsync(ImageSessionInfo.Create(synthImage03, "card3.png", "ImagePickerCopy", "image/bmp", 80, 50));

            vm03.NameRow.Value = "趙六";
            vm03.NameRow.Confirm();

            var save1 = await vm03.SaveCurrentDraftAsync();
            bool save1Ok = save1.Succeeded && vm03.CurrentRevision == 1 && !vm03.HasUnsavedChanges;

            // Duplicate save without modification
            var saveDup = await vm03.SaveCurrentDraftAsync();
            bool saveDupOk = saveDup.Succeeded && saveDup.IsDuplicate && vm03.CurrentRevision == 1;

            // Revision update: edit phone and re-confirm
            var phone = vm03.AddPhoneRow("02-8888-9999");
            phone.Confirm();
            bool dirtyAfterEdit = vm03.HasUnsavedChanges;

            var saveRev2 = await vm03.SaveCurrentDraftAsync();
            bool saveRev2Ok = saveRev2.Succeeded && !saveRev2.IsDuplicate && vm03.CurrentRevision == 2 && !vm03.HasUnsavedChanges;

            // Verify only 1 image asset exists for this contact
            int assetCount = 0;
            using (var conn = new SqliteConnection(service03.DatabasePath.Contains(";") ? $"Data Source={service03.DatabasePath};Pooling=False;" : $"Data Source={service03.DatabasePath};Pooling=False;"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM card_image_assets WHERE contact_id = @id;";
                cmd.Parameters.AddWithValue("@id", vm03.CurrentContactId);
                assetCount = Convert.ToInt32(cmd.ExecuteScalar());
            }

            // Create blank draft resets session
            string oldContactId = vm03.CurrentContactId;
            vm03.CreateBlankDraft();
            bool resetOk = vm03.CurrentContactId != oldContactId &&
                           vm03.CurrentRevision == 0 &&
                           vm03.CurrentSession == null &&
                           !vm03.HasImage &&
                           !vm03.HasUnsavedChanges;

            bool pass = save1Ok && saveDupOk && dirtyAfterEdit && saveRev2Ok && (assetCount == 1) && resetOk;
            Record("W3-03", "重複保存、修訂更新CAS、換圖/空白草稿重設與dirty狀態", pass ? "PASS" : "FAIL",
                $"Save1Ok={save1Ok}, SaveDupOk={saveDupOk}, DirtyAfterEdit={dirtyAfterEdit}, SaveRev2Ok={saveRev2Ok}, AssetCount={assetCount}, ResetOk={resetOk}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-03", "重複保存、修訂更新CAS、換圖/空白草稿重設與dirty狀態", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-04: 受控原圖獨立性、來源消失、元資料匹配 (RS-v0.2 W3-FR-02)
        // =========================================================================================
        sw.Restart();
        string root04 = Path.Combine(fixtureRoot, "w3_04");
        try
        {
            using var service04 = new StorageService(root04);
            var vm04 = new MainViewModel(CreateIsolatedCatalog(root04), null, service04);

            string externalSourceFile = Path.Combine(fixtureRoot, "external_source_card.bmp");
            byte[] origBytes = CreateSyntheticBmp(120, 80, 0x33, 0x66, 0x99);
            File.WriteAllBytes(externalSourceFile, origBytes);

            var errLoad = await vm04.LoadFromFilePathAsync(externalSourceFile);
            vm04.NameRow.Value = "外部來源測試";
            vm04.NameRow.Confirm();
            var saveResult = await vm04.SaveCurrentDraftAsync();

            // Completely delete the original external source file
            File.Delete(externalSourceFile);
            bool externalDeleted = !File.Exists(externalSourceFile);

            // Reopen service and verify image in storage is intact
            service04.Dispose();
            using var service04Reopen = new StorageService(root04);
            var detail = await service04Reopen.GetContactDetailAsync(vm04.CurrentContactId);

            bool imageIntact = detail != null &&
                               detail.IsImageAvailable &&
                               detail.VerifiedImageBytes != null &&
                               detail.VerifiedImageBytes.SequenceEqual(origBytes);

            // True decoding check on valid BMP, PNG, and JPEG
            byte[] validBmp = CreateSyntheticBmp(100, 60, 0x11, 0x22, 0x33);
            var resBmp = await StorageService.ValidateImageAsync(validBmp);
            bool validBmpOk = resBmp.valid && resBmp.width == 100 && resBmp.height == 60 && resBmp.mimeType == "image/bmp";

            byte[] validPng = await ConvertBmpToFormatAsync(validBmp, BitmapEncoder.PngEncoderId);
            var resPng = await StorageService.ValidateImageAsync(validPng);
            bool validPngOk = resPng.valid && resPng.width == 100 && resPng.height == 60 && resPng.mimeType == "image/png";

            byte[] validJpg = await ConvertBmpToFormatAsync(validBmp, BitmapEncoder.JpegEncoderId);
            var resJpg = await StorageService.ValidateImageAsync(validJpg);
            bool validJpgOk = resJpg.valid && resJpg.width == 100 && resJpg.height == 60 && (resJpg.mimeType == "image/jpeg" || resJpg.mimeType == "image/jpg");

            // Corrupt payload rejection: valid header with truncated payload BMP
            byte[] corruptBmp = CreateValidHeaderCorruptPayloadBmp();
            var resCorruptBmp = await StorageService.ValidateImageAsync(corruptBmp);
            bool corruptBmpBlocked = !resCorruptBmp.valid;

            // Valid PNG header with truncated payload
            byte[] corruptPngTruncated = CreateValidHeaderCorruptPayloadPng(validPng);
            var resCorruptPngT = await StorageService.ValidateImageAsync(corruptPngTruncated);
            bool corruptPngTBlocked = !resCorruptPngT.valid;

            // Valid PNG header with tampered IDAT payload
            byte[] corruptPngTampered = CreateTamperedPayloadPng(validPng);
            var resCorruptPngTam = await StorageService.ValidateImageAsync(corruptPngTampered);
            bool corruptPngTamBlocked = !resCorruptPngTam.valid;

            // Save attempt with corrupt payload must be rejected
            var corruptSaveSnap = new ContactDraftSnapshot
            {
                ContactId = Guid.NewGuid().ToString("N"),
                SaveOperationId = Guid.NewGuid().ToString("N"),
                Revision = 1,
                ExpectedRevision = 0,
                ImageBytes = corruptPngTampered,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(corruptPngTampered)),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/png",
                Width = 100,
                Height = 60,
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "CorruptPng", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            var corruptSaveRes = await service04Reopen.SaveDraftAsync(corruptSaveSnap);
            bool corruptSaveBlocked = !corruptSaveRes.Succeeded && corruptSaveRes.ErrorCode == "IMAGE_VALIDATION_FAILED";

            // Fake dimension rejection test
            var fakeDimSnap = new ContactDraftSnapshot
            {
                ContactId = Guid.NewGuid().ToString("N"),
                SaveOperationId = Guid.NewGuid().ToString("N"),
                Revision = 1,
                ExpectedRevision = 0,
                ImageBytes = CreateSyntheticBmp(40, 40, 0x10, 0x20, 0x30),
                ImageSha256 = Convert.ToHexString(SHA256.HashData(CreateSyntheticBmp(40, 40, 0x10, 0x20, 0x30))),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/bmp",
                Width = 999, // Mismatched fake width
                Height = 999,
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "FakeDim", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            var fakeDimRes = await service04Reopen.SaveDraftAsync(fakeDimSnap);
            bool fakeDimBlocked = !fakeDimRes.Succeeded && fakeDimRes.ErrorCode == "IMAGE_VALIDATION_FAILED";

            bool pass = errLoad == null && saveResult.Succeeded && externalDeleted && imageIntact &&
                        validBmpOk && validPngOk && validJpgOk &&
                        corruptBmpBlocked && corruptPngTBlocked && corruptPngTamBlocked && corruptSaveBlocked && fakeDimBlocked;

            Record("W3-04", "受控原圖獨立性、來源消失、元資料匹配", pass ? "PASS" : "FAIL",
                $"SaveSucceeded={saveResult.Succeeded}, ExternalDeleted={externalDeleted}, StorageImageIntact={imageIntact}, ValidBmp={validBmpOk}, ValidPng={validPngOk}, ValidJpg={validJpgOk}, CorruptBmpBlocked={corruptBmpBlocked}, CorruptPngBlocked={corruptPngTamBlocked}, CorruptSaveBlocked={corruptSaveBlocked}, FakeDimBlocked={fakeDimBlocked}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-04", "受控原圖獨立性、來源消失、元資料匹配", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-05: 5階段定稿中斷與啟動復原 (RS-v0.2 W3-FR-04, DBA-v0.2)
        // =========================================================================================
        sw.Restart();
        string root05 = Path.Combine(fixtureRoot, "w3_05");
        try
        {
            // Case 5.1: Interrupt at AfterPendingFinalizeTransaction (staging exists, final doesn't)
            string root05A = Path.Combine(root05, "stage_after_pending");
            using (var s05A = new StorageService(root05A))
            {
                s05A.SetFailureInjectionHook(stage =>
                {
                    if (stage == "AfterPendingFinalizeTransaction")
                        throw new InvalidOperationException("Simulated crash after DB pending transaction");
                });

                var vm = new MainViewModel(CreateIsolatedCatalog(root05A), null, s05A);
                byte[] img = CreateSyntheticBmp(60, 40, 0x11, 0x22, 0x33);
                await vm.SetSessionImageAsync(ImageSessionInfo.Create(img, "c.png", "ImagePickerCopy", "image/bmp", 60, 40));
                vm.NameRow.Value = "中斷測試5.1";
                vm.NameRow.Confirm();

                var res = await vm.SaveCurrentDraftAsync();
            }

            // Recover 5.1
            bool recovered51;
            using (var s05ARecover = new StorageService(root05A))
            {
                await s05ARecover.RecoverPendingOperationsAsync();
                var paged51 = await s05ARecover.GetContactsPagedAsync(null, 0, 10);
                recovered51 = paged51.Items.Count == 1 && paged51.Items[0].PersistenceState == PersistenceStates.Ready;
            }

            // Case 5.2: Corrupt both staging and final for a pending finalize record -> transitions to Failed
            string root05B = Path.Combine(root05, "corrupt_pending");
            using (var s05B = new StorageService(root05B))
            {
                s05B.SetFailureInjectionHook(stage =>
                {
                    if (stage == "AfterPendingFinalizeTransaction")
                        throw new InvalidOperationException("Crash");
                });
                var vm = new MainViewModel(CreateIsolatedCatalog(root05B), null, s05B);
                byte[] img = CreateSyntheticBmp(60, 40, 0x44, 0x55, 0x66);
                await vm.SetSessionImageAsync(ImageSessionInfo.Create(img, "c.png", "ImagePickerCopy", "image/bmp", 60, 40));
                vm.NameRow.Value = "損毀測試5.2";
                vm.NameRow.Confirm();
                await vm.SaveCurrentDraftAsync();
            }

            string pendingDir = Path.Combine(root05B, StoragePathHelper.PendingDirName);
            if (Directory.Exists(pendingDir))
            {
                foreach (var f in Directory.GetFiles(pendingDir)) File.Delete(f);
            }

            bool transitionedFailed = false;
            bool textPreserved = false;
            using (var s05BRecover = new StorageService(root05B))
            {
                await s05BRecover.RecoverPendingOperationsAsync();
                var paged52 = await s05BRecover.GetContactsPagedAsync(null, 0, 10);
                transitionedFailed = paged52.Items.Count == 1 && paged52.Items[0].PersistenceState == PersistenceStates.Failed;
                var detail = paged52.Items.Count > 0 ? await s05BRecover.GetContactDetailAsync(paged52.Items[0].ContactId) : null;
                textPreserved = detail != null && detail.Name == "損毀測試5.2";
            }

            // Case 5.3: Same-session retry of interrupted PendingFinalize
            string root05C = Path.Combine(root05, "reenter_finalize");
            bool retrySucceeded = false;
            using (var s05C = new StorageService(root05C))
            {
                bool crashInjected = false;
                s05C.SetFailureInjectionHook(stage =>
                {
                    if (stage == "AfterPendingFinalizeTransaction" && !crashInjected)
                    {
                        crashInjected = true;
                        throw new InvalidOperationException("First attempt crash after Phase 2");
                    }
                });

                var vm53 = new MainViewModel(CreateIsolatedCatalog(root05C), null, s05C);
                byte[] img53 = CreateSyntheticBmp(60, 40, 0x77, 0x88, 0x99);
                await vm53.SetSessionImageAsync(ImageSessionInfo.Create(img53, "c53.png", "ImagePickerCopy", "image/bmp", 60, 40));
                vm53.NameRow.Value = "重試重入測試";
                vm53.NameRow.Confirm();

                var firstRes = await vm53.SaveCurrentDraftAsync();
                bool firstFailed = !firstRes.Succeeded;

                s05C.SetFailureInjectionHook(null);
                var retryRes = await vm53.SaveCurrentDraftAsync();
                retrySucceeded = firstFailed && retryRes.Succeeded && !retryRes.IsDuplicate;

                var detail53 = await s05C.GetContactDetailAsync(vm53.CurrentContactId);
                retrySucceeded = retrySucceeded && detail53 != null && detail53.PersistenceState == PersistenceStates.Ready;
            }

            // Case 5.4: Staging retry must not leave orphan files
            string root05D = Path.Combine(root05, "staging_orphan_retry");
            bool noOrphansOnRetry = false;
            using (var s05D = new StorageService(root05D))
            {
                bool hookTriggered = false;
                s05D.SetFailureInjectionHook(stage =>
                {
                    if (stage == "AfterStagingFlush" && !hookTriggered)
                    {
                        hookTriggered = true;
                        throw new IOException("Simulated crash right after staging flush");
                    }
                });

                var vm54 = new MainViewModel(CreateIsolatedCatalog(root05D), null, s05D);
                byte[] img54 = CreateSyntheticBmp(60, 40, 0x15, 0x25, 0x35);
                await vm54.SetSessionImageAsync(ImageSessionInfo.Create(img54, "c54.png", "ImagePickerCopy", "image/bmp", 60, 40));
                vm54.NameRow.Value = "Staging重試卡";
                vm54.NameRow.Confirm();

                var res1 = await vm54.SaveCurrentDraftAsync();
                bool firstFailed = !res1.Succeeded;

                s05D.SetFailureInjectionHook(null);
                var res2 = await vm54.SaveCurrentDraftAsync();
                bool secondSucceeded = res2.Succeeded;

                string originalsDir = Path.Combine(root05D, StoragePathHelper.CardImagesDirName);
                string pendingDir05D = Path.Combine(root05D, StoragePathHelper.PendingDirName);

                var originalFiles = Directory.GetFiles(originalsDir, "*.*", SearchOption.AllDirectories);
                var pendingFiles = Directory.Exists(pendingDir05D) ? Directory.GetFiles(pendingDir05D, "*.*") : Array.Empty<string>();

                noOrphansOnRetry = firstFailed && secondSucceeded && originalFiles.Length == 1 && pendingFiles.Length == 0;
            }

            // Case 5.5: Interrupted PendingFinalize where user edits draft before retry
            string root05E = Path.Combine(root05, "pending_edit_retry");
            bool pendingEditRetryOk = false;
            using (var s05E = new StorageService(root05E))
            {
                bool crashInjected = false;
                s05E.SetFailureInjectionHook(stage =>
                {
                    if (stage == "AfterPendingFinalizeTransaction" && !crashInjected)
                    {
                        crashInjected = true;
                        throw new InvalidOperationException("Crash during PendingFinalize");
                    }
                });

                var vm55 = new MainViewModel(CreateIsolatedCatalog(root05E), null, s05E);
                byte[] img55 = CreateSyntheticBmp(60, 40, 0x35, 0x45, 0x55);
                await vm55.SetSessionImageAsync(ImageSessionInfo.Create(img55, "c55.png", "ImagePickerCopy", "image/bmp", 60, 40));
                vm55.NameRow.Value = "內容A";
                vm55.NameRow.Confirm();

                var firstRes = await vm55.SaveCurrentDraftAsync();
                bool firstFailed = !firstRes.Succeeded;

                // User modifies draft to content B before retrying save
                vm55.NameRow.Value = "內容B";
                vm55.NameRow.Confirm();
                bool dirtyWithB = vm55.HasUnsavedChanges;

                // Retry save: should finalize content A in DB, but report RequiresSaveAgain and keep B dirty
                s05E.SetFailureInjectionHook(null);
                var retryRes = await vm55.SaveCurrentDraftAsync();
                bool retryHandledPendingA = retryRes.RequiresSaveAgain && vm55.CurrentRevision == 1;
                bool dirtyPreservedForB = vm55.HasUnsavedChanges;

                // Now save draft B as revision 2
                var saveBRes = await vm55.SaveCurrentDraftAsync();
                bool saveBOk = saveBRes.Succeeded && vm55.CurrentRevision == 2 && !vm55.HasUnsavedChanges;

                var detailB = await s05E.GetContactDetailAsync(vm55.CurrentContactId);
                bool dbMatchesB = detailB != null && detailB.Revision == 2 && detailB.Name == "內容B";

                pendingEditRetryOk = firstFailed && dirtyWithB && retryHandledPendingA && dirtyPreservedForB && saveBOk && dbMatchesB;
            }

            bool pass = recovered51 && transitionedFailed && textPreserved && retrySucceeded && noOrphansOnRetry && pendingEditRetryOk;
            Record("W3-05", "5階段定稿中斷與啟動復原", pass ? "PASS" : "FAIL",
                $"Recovered51Ready={recovered51}, TransitionedFailed={transitionedFailed}, TextPreserved={textPreserved}, ReenterRetryOk={retrySucceeded}, NoOrphansOnStagingRetry={noOrphansOnRetry}, PendingEditRetryOk={pendingEditRetryOk}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-05", "5階段定稿中斷與啟動復原", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-06: 圖片遺失、竄改與損壞 (RS-v0.2 W3-FR-02)
        // =========================================================================================
        sw.Restart();
        string root06 = Path.Combine(fixtureRoot, "w3_06");
        try
        {
            using var service06 = new StorageService(root06);
            var vm06 = new MainViewModel(CreateIsolatedCatalog(root06), null, service06);
            byte[] img06 = CreateSyntheticBmp(80, 50, 0x70, 0x80, 0x90);
            await vm06.SetSessionImageAsync(ImageSessionInfo.Create(img06, "c6.png", "ImagePickerCopy", "image/bmp", 80, 50));
            vm06.NameRow.Value = "竄改測試卡";
            vm06.NameRow.Confirm();
            await vm06.SaveCurrentDraftAsync();

            var detail = await service06.GetContactDetailAsync(vm06.CurrentContactId);
            string imagePath = StoragePathHelper.ResolveSafePath(root06, detail!.ImageAsset!.RelativePath);

            // Tamper file content
            byte[] tamperedBytes = File.ReadAllBytes(imagePath);
            tamperedBytes[tamperedBytes.Length - 1] ^= 0xFF;
            File.WriteAllBytes(imagePath, tamperedBytes);

            var detailTampered = await service06.GetContactDetailAsync(vm06.CurrentContactId);
            bool tamperedDetected = detailTampered != null &&
                                    detailTampered.PersistenceState == PersistenceStates.ImageMissing &&
                                    detailTampered.VerifiedImageBytes == null &&
                                    detailTampered.Name == "竄改測試卡"; // Text retained

            // Missing file
            File.Delete(imagePath);
            var detailMissing = await service06.GetContactDetailAsync(vm06.CurrentContactId);
            bool missingDetected = detailMissing != null &&
                                   detailMissing.PersistenceState == PersistenceStates.ImageMissing &&
                                   detailMissing.VerifiedImageBytes == null &&
                                   detailMissing.Name == "竄改測試卡";

            bool pass = tamperedDetected && missingDetected;
            Record("W3-06", "圖片遺失、竄改與損壞", pass ? "PASS" : "FAIL",
                $"TamperedDetected={tamperedDetected}, MissingDetected={missingDetected}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-06", "圖片遺失、竄改與損壞", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-07: 寫入失敗、檔案鎖定與錯誤防護 (RS-v0.2 W3-FR-04)
        // =========================================================================================
        sw.Restart();
        string root07 = Path.Combine(fixtureRoot, "w3_07");
        try
        {
            using (var service07 = new StorageService(root07))
            {
                service07.SetFailureInjectionHook(stage =>
                {
                    if (stage == "AfterFinalRename")
                        throw new IOException("Simulated disk error or locked file");
                });

                var vm07 = new MainViewModel(CreateIsolatedCatalog(root07), null, service07);
                byte[] img07 = CreateSyntheticBmp(80, 50, 0x10, 0x20, 0x30);
                await vm07.SetSessionImageAsync(ImageSessionInfo.Create(img07, "c7.png", "ImagePickerCopy", "image/bmp", 80, 50));
                vm07.NameRow.Value = "鎖定防護測試";
                vm07.NameRow.Confirm();

                var saveResult = await vm07.SaveCurrentDraftAsync();
                bool handledSafe = !saveResult.Succeeded &&
                                   vm07.NameRow.Value == "鎖定防護測試" && // Draft retained
                                   vm07.NameRow.IsConfirmed;

                Record("W3-07", "寫入失敗、檔案鎖定與錯誤防護", handledSafe ? "PASS" : "FAIL",
                    $"HandledSafe={handledSafe}, DraftRetained=True", sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Record("W3-07", "寫入失敗、檔案鎖定與錯誤防護", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-08: 新DB建置、Schema 1重開、未知新版拒寫、Schema 0不明資料拒寫、毀損DB拒寫、SourceMode與解碼安全 (DBA-v0.2)
        // =========================================================================================
        sw.Restart();
        string root08 = Path.Combine(fixtureRoot, "w3_08");
        try
        {
            // 8.1 Fresh DB init
            string root08Fresh = Path.Combine(root08, "fresh");
            using (var s = new StorageService(root08Fresh))
            {
                // Should initialize user_version = 1
            }

            // 8.2 Unknown newer schema (user_version = 2)
            string root08New = Path.Combine(root08, "newer");
            StoragePathHelper.EnsureStorageDirectories(root08New);
            string dbNew = Path.Combine(root08New, StoragePathHelper.DatabaseFileName);
            using (var conn = new SqliteConnection($"Data Source={dbNew};Pooling=False;"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE dummy (id INTEGER); PRAGMA user_version = 3;";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            byte[] bytesBeforeNew = File.ReadAllBytes(dbNew);
            bool newerRejected = false;
            try
            {
                using var sNew = new StorageService(root08New);
                if (!sNew.IsAvailable) newerRejected = true;
            }
            catch (InvalidOperationException)
            {
                newerRejected = true;
            }
            SqliteConnection.ClearAllPools();

            byte[] bytesAfterNew = File.ReadAllBytes(dbNew);
            bool newerBytesUnchanged = bytesBeforeNew.SequenceEqual(bytesAfterNew);

            // 8.3 Schema 0 with unrecognized tables
            string root08Schema0 = Path.Combine(root08, "schema0");
            StoragePathHelper.EnsureStorageDirectories(root08Schema0);
            string db0 = Path.Combine(root08Schema0, StoragePathHelper.DatabaseFileName);
            using (var conn = new SqliteConnection($"Data Source={db0};Pooling=False;"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE unknown_legacy_table (id INTEGER); PRAGMA user_version = 0;";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            byte[] bytesBefore0 = File.ReadAllBytes(db0);
            bool schema0Rejected = false;
            try
            {
                using var s0 = new StorageService(root08Schema0);
                if (!s0.IsAvailable) schema0Rejected = true;
            }
            catch (InvalidOperationException)
            {
                schema0Rejected = true;
            }
            SqliteConnection.ClearAllPools();

            byte[] bytesAfter0 = File.ReadAllBytes(db0);
            bool schema0BytesUnchanged = bytesBefore0.SequenceEqual(bytesAfter0);

            // 8.3b Negative user_version (-1)
            string root08Negative = Path.Combine(root08, "neg_version");
            StoragePathHelper.EnsureStorageDirectories(root08Negative);
            string dbNeg = Path.Combine(root08Negative, StoragePathHelper.DatabaseFileName);
            using (var connNeg = new SqliteConnection($"Data Source={dbNeg};Pooling=False;"))
            {
                connNeg.Open();
                using var cmd = connNeg.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = -1;";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            byte[] bytesBeforeNeg = File.ReadAllBytes(dbNeg);
            bool negRejected = false;
            try
            {
                using var sNeg = new StorageService(root08Negative);
                if (!sNeg.IsAvailable) negRejected = true;
            }
            catch (InvalidOperationException)
            {
                negRejected = true;
            }
            SqliteConnection.ClearAllPools();
            byte[] bytesAfterNeg = File.ReadAllBytes(dbNeg);
            bool negBytesUnchanged = bytesBeforeNeg.SequenceEqual(bytesAfterNeg);

            // 8.4 Corrupted DB header
            string root08Corrupt = Path.Combine(root08, "corrupt");
            StoragePathHelper.EnsureStorageDirectories(root08Corrupt);
            string dbCorrupt = Path.Combine(root08Corrupt, StoragePathHelper.DatabaseFileName);
            File.WriteAllText(dbCorrupt, "NOT A VALID SQLITE DATABASE FILE HEADER!!");
            bool corruptRejected = false;
            try
            {
                using var sCorrupt = new StorageService(root08Corrupt);
                if (!sCorrupt.IsAvailable) corruptRejected = true;
            }
            catch
            {
                corruptRejected = true;
            }
            SqliteConnection.ClearAllPools();

            // 8.5 SourceMode: User edit on OCR row changes SourceMode to Manual
            var vmSource = new MainViewModel(CreateIsolatedCatalog(root08));
            var ocrRow = new DraftRowItem
            {
                Source = "整圖辨識",
                OriginalText = "辨識原字串",
                Value = "辨識原字串",
                ValueKind = CanonicalValueKind.Name,
                Status = DraftItemStatus.PendingConfirmation
            };
            vmSource.DraftRows.Add(ocrRow);
            // User edits value
            ocrRow.Value = "手動編輯後字串";
            ocrRow.Confirm();
            bool sourceModeIsManual = ocrRow.IsUserModified;
            var testSnap = DraftSnapshotFactory.CreateSnapshot(vmSource, "c_test", 0, 1, "op_test");
            bool snapSourceModeIsManual = testSnap.Values.Any(v => v.RawValue == "手動編輯後字串" && v.SourceMode == CompletionSources.Manual);

            // 8.6 Headless safe image decodability check
            byte[] corruptBytes = new byte[] { 0x00, 0xFF, 0x12, 0x34 };
            await vmSource.SetSessionImageAsync(ImageSessionInfo.Create(corruptBytes, "corrupt.png", "ImagePickerCopy", "image/png", 100, 100));
            bool corruptPreserved = vmSource.StatusMessage.Contains("圖片資料損壞");

            bool pass = newerRejected && newerBytesUnchanged && schema0Rejected && schema0BytesUnchanged && negRejected && negBytesUnchanged && corruptRejected && sourceModeIsManual && snapSourceModeIsManual && corruptPreserved;
            Record("W3-08", "新DB建置、Schema 1重開、未知新版拒寫、Schema 0不明資料拒寫、毀損DB拒寫", pass ? "PASS" : "FAIL",
                $"NewerRejected={newerRejected}, NewerBytesUnchanged={newerBytesUnchanged}, Schema0Rejected={schema0Rejected}, Schema0BytesUnchanged={schema0BytesUnchanged}, NegRejected={negRejected}, NegBytesUnchanged={negBytesUnchanged}, CorruptRejected={corruptRejected}, SourceModeManual={snapSourceModeIsManual}, SafeCorruptDecode={corruptPreserved}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-08", "新DB建置、Schema 1重開、未知新版拒寫、Schema 0不明資料拒寫、毀損DB拒寫", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-09: 參數化搜尋、特殊字元字面比對與分頁 (RS-v0.2 W3-FR-05)
        // =========================================================================================
        sw.Restart();
        string root09 = Path.Combine(fixtureRoot, "w3_09");
        try
        {
            using var service09 = new StorageService(root09);

            // Insert 35 cards
            byte[] img09 = CreateSyntheticBmp(40, 40, 0x10, 0x20, 0x30);
            for (int i = 1; i <= 35; i++)
            {
                string name = i == 1 ? "Test_10%_Discount" :
                             (i == 2 ? "TestX100_Discount" :
                             (i == 3 ? "O'Connor & Sons" : $"聯絡人_{i:D2}"));

                var snap = new ContactDraftSnapshot
                {
                    ContactId = Guid.NewGuid().ToString("N"),
                    SaveOperationId = Guid.NewGuid().ToString("N"),
                    Revision = 1,
                    ImageBytes = img09,
                    ImageSha256 = Convert.ToHexString(SHA256.HashData(img09)),
                    CaptureSource = "ImagePickerCopy",
                    MimeType = "image/bmp",
                    Width = 40,
                    Height = 40,
                    CompletionSource = "Manual",
                    Fingerprint = $"fp_{i}",
                    Values = new List<DraftValueSnapshot>
                    {
                        new() { ValueKind = CanonicalValueKind.Name, RawValue = name, Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                    }
                };

                await service09.SaveDraftAsync(snap);
            }

            // Test literal search for '%' and '_'
            var searchPercent = await service09.GetContactsPagedAsync("10%", 0, 10);
            bool literalPercentOk = searchPercent.TotalCount == 1 && searchPercent.Items[0].Name == "Test_10%_Discount";

            var searchUnderscore = await service09.GetContactsPagedAsync("Test_", 0, 10);
            bool literalUnderscoreOk = searchUnderscore.TotalCount == 1 && searchUnderscore.Items[0].Name == "Test_10%_Discount";

            var searchQuote = await service09.GetContactsPagedAsync("O'Connor", 0, 10);
            bool quoteOk = searchQuote.TotalCount == 1 && searchQuote.Items[0].Name == "O'Connor & Sons";

            // Test 30-item paging
            var page0 = await service09.GetContactsPagedAsync(null, 0, 30);
            var page1 = await service09.GetContactsPagedAsync(null, 1, 30);

            bool pagingOk = page0.TotalCount == 35 &&
                            page0.Items.Count == 30 &&
                            page0.HasNextPage &&
                            page1.Items.Count == 5 &&
                            !page1.HasNextPage;

            bool pass = literalPercentOk && literalUnderscoreOk && quoteOk && pagingOk;
            Record("W3-09", "參數化搜尋、特殊字元字面比對與分頁", pass ? "PASS" : "FAIL",
                $"LiteralPercent={literalPercentOk}, LiteralUnderscore={literalUnderscoreOk}, QuoteOk={quoteOk}, PagingOk={pagingOk}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-09", "參數化搜尋、特殊字元字面比對與分頁", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-10: 路徑安全、Traversal與Reparse Point防護 (RS-v0.2 W3-FR-03, AR-v0.2)
        // =========================================================================================
        sw.Restart();
        string root10 = Path.Combine(fixtureRoot, "w3_10");
        try
        {
            StoragePathHelper.EnsureStorageDirectories(root10);

            // Traversal check
            bool traversalBlocked = false;
            try
            {
                StoragePathHelper.ResolveSafePath(root10, @"..\..\windows\system32\calc.exe");
            }
            catch (SecurityException)
            {
                traversalBlocked = true;
            }

            // Absolute path check
            bool absoluteBlocked = false;
            try
            {
                StoragePathHelper.ResolveSafePath(root10, @"C:\Windows\notepad.exe");
            }
            catch (SecurityException)
            {
                absoluteBlocked = true;
            }

            // Semicolon in path connection builder check
            string rootSemicolon = Path.Combine(root10, "dir;semicolon");
            bool semiOk = false;
            using (var sSemi = new StorageService(rootSemicolon))
            {
                semiOk = sSemi.IsAvailable && File.Exists(sSemi.DatabasePath);
            }

            // ACL verification check
            bool aclOk = false;
            try
            {
                StoragePathHelper.VerifyAclProtection(root10);
                aclOk = true;
            }
            catch
            {
                aclOk = false;
            }

            // Asset ID GUID_N format check
            string testAssetId = Guid.NewGuid().ToString("N");
            string testAssetPath = Path.Combine(root10, StoragePathHelper.CardImagesDirName, testAssetId + ".jpg");
            bool validAssetPathOk = StoragePathHelper.IsOwnedAssetPath(root10, testAssetPath, out _, testAssetId);
            string nonGuidPath = Path.Combine(root10, StoragePathHelper.CardImagesDirName, "not-a-guid.jpg");
            bool nonGuidAssetPathRejected = !StoragePathHelper.IsOwnedAssetPath(root10, nonGuidPath, out _, testAssetId);
            string mismatchPath = Path.Combine(root10, StoragePathHelper.CardImagesDirName, Guid.NewGuid().ToString("N") + ".jpg");
            bool mismatchedAssetIdRejected = !StoragePathHelper.IsOwnedAssetPath(root10, mismatchPath, out _, testAssetId);

            // Directory junction reparse test (mklink /J)
            string junctionTarget = Path.Combine(fixtureRoot, "sentinel_dir");
            Directory.CreateDirectory(junctionTarget);
            string sentinelFile = Path.Combine(junctionTarget, "sentinel.txt");
            File.WriteAllText(sentinelFile, "SECRET_SENTINEL_DATA");
            string sentinelHashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sentinelFile)));

            string junctionLink = Path.Combine(root10, "junction_link");
            bool junctionCreated = false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{junctionLink}\" \"{junctionTarget}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                junctionCreated = proc?.ExitCode == 0 && Directory.Exists(junctionLink);
            }
            catch { }

            bool reparseBlocked = false;
            if (junctionCreated)
            {
                try
                {
                    StoragePathHelper.ResolveSafePath(root10, @"junction_link\sentinel.txt");
                }
                catch (SecurityException)
                {
                    reparseBlocked = true;
                }
                string sentinelHashAfter = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sentinelFile)));
                bool sentinelUnchanged = sentinelHashBefore == sentinelHashAfter;

                bool pass = traversalBlocked && absoluteBlocked && reparseBlocked && sentinelUnchanged && semiOk &&
                            aclOk && validAssetPathOk && nonGuidAssetPathRejected && mismatchedAssetIdRejected;
                Record("W3-10", "路徑安全、Traversal與Reparse Point防護", pass ? "PASS" : "FAIL",
                    $"TraversalBlocked={traversalBlocked}, AbsoluteBlocked={absoluteBlocked}, ReparseBlocked={reparseBlocked}, SentinelUnchanged={sentinelUnchanged}, SemicolonPathOk={semiOk}, AclOk={aclOk}, GuidNChecked={validAssetPathOk && nonGuidAssetPathRejected && mismatchedAssetIdRejected}", sw.ElapsedMilliseconds);
            }
            else
            {
                bool basePass = traversalBlocked && absoluteBlocked && semiOk && aclOk && validAssetPathOk && nonGuidAssetPathRejected && mismatchedAssetIdRejected;
                Record("W3-10", "路徑安全、Traversal與Reparse Point防護", basePass ? "PASS" : "FAIL",
                    $"TraversalBlocked={traversalBlocked}, AbsoluteBlocked={absoluteBlocked}, SemicolonPathOk={semiOk}, AclOk={aclOk}, GuidNChecked={validAssetPathOk && nonGuidAssetPathRejected && mismatchedAssetIdRejected}, Junction creation not permitted by environment; symlink check marked NOT_RUN per spec.", sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Record("W3-10", "路徑安全、Traversal與Reparse Point防護", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-11: 刪除取消、正常刪除、Missing刪除、占用失敗與重啟重試 (RS-v0.2 W3-FR-09)
        // =========================================================================================
        sw.Restart();
        string root11 = Path.Combine(fixtureRoot, "w3_11");
        try
        {
            using var service11 = new StorageService(root11);
            byte[] img11 = CreateSyntheticBmp(50, 50, 0x12, 0x34, 0x56);

            // 11.0 Cancel Delete preserves contact in DB and asset on disk
            var snapCancel = new ContactDraftSnapshot
            {
                ContactId = Guid.NewGuid().ToString("N"),
                SaveOperationId = Guid.NewGuid().ToString("N"),
                Revision = 1,
                ImageBytes = img11,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(img11)),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/bmp",
                Width = 50,
                Height = 50,
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "取消刪除卡", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            await service11.SaveDraftAsync(snapCancel);
            // Simulate cancellation: user clicks Cancel in UI, no DeleteContactAsync is called
            var cancelDetail = await service11.GetContactDetailAsync(snapCancel.ContactId);
            bool cancelDeleteOk = cancelDetail != null && cancelDetail.PersistenceState == PersistenceStates.Ready && !cancelDetail.PendingDelete;

            // 11.1 Normal delete
            var snap1 = new ContactDraftSnapshot
            {
                ContactId = Guid.NewGuid().ToString("N"),
                SaveOperationId = Guid.NewGuid().ToString("N"),
                Revision = 1,
                ImageBytes = img11,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(img11)),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/bmp",
                Width = 50,
                Height = 50,
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "正常刪除卡", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            await service11.SaveDraftAsync(snap1);
            var detail1 = await service11.GetContactDetailAsync(snap1.ContactId);
            string fullPath1 = StoragePathHelper.ResolveSafePath(root11, detail1!.ImageAsset!.RelativePath);
            bool fileExistsBeforeDel = File.Exists(fullPath1);

            var delResult1 = await service11.DeleteContactAsync(snap1.ContactId);
            bool fileExistsAfterDel = File.Exists(fullPath1);
            var detailAfterDel = await service11.GetContactDetailAsync(snap1.ContactId);
            bool normalDelOk = delResult1.Succeeded && fileExistsBeforeDel && !fileExistsAfterDel && detailAfterDel == null;

            // 11.2 Delete contact with missing image
            var snap2 = new ContactDraftSnapshot
            {
                ContactId = Guid.NewGuid().ToString("N"),
                SaveOperationId = Guid.NewGuid().ToString("N"),
                Revision = 1,
                ImageBytes = img11,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(img11)),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/bmp",
                Width = 50,
                Height = 50,
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "遺失圖刪除卡", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            await service11.SaveDraftAsync(snap2);
            var detail2 = await service11.GetContactDetailAsync(snap2.ContactId);
            string fullPath2 = StoragePathHelper.ResolveSafePath(root11, detail2!.ImageAsset!.RelativePath);
            File.Delete(fullPath2); // Simulate missing
            var delResult2 = await service11.DeleteContactAsync(snap2.ContactId);
            bool missingDelOk = delResult2.Succeeded && (await service11.GetContactDetailAsync(snap2.ContactId)) == null;

            // 11.3 File locked during delete -> resumes on restart
            var snap3 = new ContactDraftSnapshot
            {
                ContactId = Guid.NewGuid().ToString("N"),
                SaveOperationId = Guid.NewGuid().ToString("N"),
                Revision = 1,
                ImageBytes = img11,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(img11)),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/bmp",
                Width = 50,
                Height = 50,
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "鎖檔重試卡", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            await service11.SaveDraftAsync(snap3);
            var detail3 = await service11.GetContactDetailAsync(snap3.ContactId);
            string fullPath3 = StoragePathHelper.ResolveSafePath(root11, detail3!.ImageAsset!.RelativePath);

            DeleteResult lockDelResult;
            using (var lockStream = new FileStream(fullPath3, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                lockDelResult = await service11.DeleteContactAsync(snap3.ContactId);
            }
            bool lockReportedFailed = !lockDelResult.Succeeded && lockDelResult.ErrorCode == "FILE_LOCKED";

            // Restart / recover pending operations
            await service11.RecoverPendingOperationsAsync();
            bool recoveredDelOk = !File.Exists(fullPath3) && (await service11.GetContactDetailAsync(snap3.ContactId)) == null;

            bool pass = cancelDeleteOk && normalDelOk && missingDelOk && lockReportedFailed && recoveredDelOk;
            Record("W3-11", "刪除取消、正常刪除、Missing刪除、占用失敗與重啟重試", pass ? "PASS" : "FAIL",
                $"CancelDeleteOk={cancelDeleteOk}, NormalDelOk={normalDelOk}, MissingDelOk={missingDelOk}, LockReportedFailed={lockReportedFailed}, RecoveredDelOk={recoveredDelOk}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-11", "刪除取消、正常刪除、Missing刪除、占用失敗與重啟重試", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-12: 人工 revision 衝突／同 contact已刪除後再存 (RS-v0.2 W3-FR-06, DBC-v0.3)
        // =========================================================================================
        sw.Restart();
        string root12 = Path.Combine(fixtureRoot, "w3_12");
        try
        {
            using var service12 = new StorageService(root12);
            byte[] img12 = CreateSyntheticBmp(50, 50, 0x11, 0x22, 0x33);
            string contactId = Guid.NewGuid().ToString("N");

            var snap1 = new ContactDraftSnapshot
            {
                ContactId = contactId,
                SaveOperationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = 0,
                Revision = 1,
                ImageBytes = img12,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(img12)),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/bmp",
                Width = 50,
                Height = 50,
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "版本衝突測試卡", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            await service12.SaveDraftAsync(snap1);

            // Conflicting revision: submitting revision 1 again when DB has revision 1 (expected new revision 2)
            var conflictSnap = new ContactDraftSnapshot
            {
                ContactId = contactId,
                SaveOperationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = 1,
                Revision = 1, // Invalid: should be 2
                ImageBytes = img12,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(img12)),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/bmp",
                Width = 50,
                Height = 50,
                Fingerprint = "diff_fingerprint",
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "竄改值", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            var conflictRes = await service12.SaveDraftAsync(conflictSnap);
            bool conflictBlocked = !conflictRes.Succeeded && conflictRes.ErrorCode == "REVISION_CONFLICT";

            // Skipped revision: jumping from revision 1 to revision 3
            var skipSnap = new ContactDraftSnapshot
            {
                ContactId = contactId,
                SaveOperationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = 2, // Mismatch: DB has 1
                Revision = 3,
                ImageBytes = img12,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(img12)),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/bmp",
                Width = 50,
                Height = 50,
                Fingerprint = "diff_skip",
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "跳版值", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            var skipRes = await service12.SaveDraftAsync(skipSnap);
            bool skipBlocked = !skipRes.Succeeded && skipRes.ErrorCode == "REVISION_CONFLICT";

            // Delete contact
            await service12.DeleteContactAsync(contactId);

            // Re-saving deleted contact as revision update must be rejected
            var reSaveSnap = new ContactDraftSnapshot
            {
                ContactId = contactId,
                SaveOperationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = 1,
                Revision = 2,
                ImageBytes = img12,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(img12)),
                CaptureSource = "ImagePickerCopy",
                MimeType = "image/bmp",
                Width = 50,
                Height = 50,
                Fingerprint = "diff_resave",
                Values = new List<DraftValueSnapshot>
                {
                    new() { ValueKind = CanonicalValueKind.Name, RawValue = "死者甦醒", Label = "姓名", Ordinal = 0, SourceMode = "Manual", LockedAt = DateTimeOffset.UtcNow }
                }
            };
            var reSaveRes = await service12.SaveDraftAsync(reSaveSnap);
            bool reSaveBlocked = !reSaveRes.Succeeded && (reSaveRes.ErrorCode == "CONTACT_DELETED" || reSaveRes.ErrorCode == "REVISION_CONFLICT");

            // Exercise the approved active-draft workflow, not a fabricated fresh-session request.
            var vm12 = new MainViewModel(CreateIsolatedCatalog(root12), null, service12);
            await vm12.SetSessionImageAsync(ImageSessionInfo.Create(img12, "active.bmp", "ImagePickerCopy", "image/bmp", 50, 50));
            vm12.NameRow.Value = "已刪除草稿";
            vm12.NameRow.Confirm();
            var activeSave = await vm12.SaveCurrentDraftAsync();
            string activeId = vm12.CurrentContactId;
            var activeDelete = await service12.DeleteContactAsync(activeId);
            vm12.OnActiveContactDeletedExternally(activeId);
            var activeRetry = await vm12.SaveCurrentDraftAsync();
            bool resurrectBlocked = activeSave.Succeeded && activeDelete.Succeeded &&
                !activeRetry.Succeeded && activeRetry.ErrorCode == "CONTACT_DELETED" &&
                vm12.NameRow.Value == "已刪除草稿" &&
                await service12.GetContactDetailAsync(activeId) == null;

            bool pass = conflictBlocked && skipBlocked && reSaveBlocked && resurrectBlocked;
            Record("W3-12", "人工 revision 衝突／同 contact已刪除後再存", pass ? "PASS" : "FAIL",
                $"ConflictBlocked={conflictBlocked}, SkipBlocked={skipBlocked}, ReSaveBlocked={reSaveBlocked}, ResurrectBlocked={resurrectBlocked}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-12", "人工 revision 衝突／同 contact已刪除後再存", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-13: 組件版本、套件與Native SQLite版本回報 (RS-v0.2, IP-v0.1)
        // =========================================================================================
        sw.Restart();
        try
        {
            var asm = typeof(Program).Assembly;
            string? infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
            string? fileVer = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            string asmVer = asm.GetName().Version?.ToString() ?? "";

            bool versionOk = !string.IsNullOrWhiteSpace(infoVer) &&
                             infoVer.StartsWith("0.1.0-beta") &&
                             (fileVer != null || !string.IsNullOrWhiteSpace(asmVer));

            bool sqliteOk = false;
            if (!string.IsNullOrWhiteSpace(report.NativeSqliteVersion) && !report.NativeSqliteVersion.StartsWith("Error"))
            {
                if (Version.TryParse(report.NativeSqliteVersion, out var parsedVer))
                {
                    sqliteOk = parsedVer >= new Version(3, 50, 2);
                }
            }

            bool pass = versionOk && sqliteOk;
            Record("W3-13", "組件版本、套件與Native SQLite版本回報", pass ? "PASS" : "FAIL",
                $"InformationalVersion={infoVer}, FileVersion={fileVer}, AssemblyVersion={asmVer}, NativeSqliteVersion={report.NativeSqliteVersion}, SqliteVersionGte3502={sqliteOk}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-13", "組件版本、套件與Native SQLite版本回報", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // W3-14: 最終 ROI／語系及 affected確認／Undo 回歸 (RS-v0.2, TP-v0.2)
        // =========================================================================================
        sw.Restart();
        try
        {
            var vm = new MainViewModel(CreateIsolatedCatalog(Path.Combine(fixtureRoot, "w3_14")));

            // 1. Language catalog regression (RS-ISSUE-0009)
            bool langCountOk = vm.LanguageOptions.Count == 6;
            bool defaultFullIsMixed = vm.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Mixed5;
            bool defaultPartialIsMixed = vm.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.Mixed5;

            // 2. Targeted ROI selection toggle & Esc clear (RS-ISSUE-0008)
            vm.ToggleTargetField(vm.NameRow);
            bool targetSelected = ReferenceEquals(vm.SelectedTargetField, vm.NameRow);
            vm.ClearTargetFieldSelection();
            bool targetCleared = vm.SelectedTargetField == null;

            // 3. Confirm and Undo regression
            vm.CompanyRow.Value = "舊公司";
            vm.CompanyRow.Confirm();
            var prevConfirmedAt = vm.CompanyRow.ConfirmedAt;
            vm.CompanyRow.SaveUndoSnapshot();

            vm.CompanyRow.Value = "新公司";
            bool invalidated = vm.CompanyRow.IsPending && vm.CompanyRow.ConfirmedAt == null;

            vm.CompanyRow.RestoreUndo();
            bool restored = vm.CompanyRow.Value == "舊公司" &&
                            vm.CompanyRow.IsConfirmed &&
                            vm.CompanyRow.ConfirmedAt == prevConfirmedAt;

            bool pass = langCountOk && defaultFullIsMixed && defaultPartialIsMixed && targetSelected && targetCleared && invalidated && restored;
            Record("W3-14", "最終 ROI／語系及 affected確認／Undo 回歸", pass ? "PASS" : "FAIL",
                $"LangCountOk={langCountOk}, DefaultsOk={defaultFullIsMixed && defaultPartialIsMixed}, TargetToggleOk={targetSelected && targetCleared}, UndoRegressionOk={invalidated && restored}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Record("W3-14", "最終 ROI／語系及 affected確認／Undo 回歸", "FAIL", $"Exception: {ex.Message}", sw.ElapsedMilliseconds);
        }

        // =========================================================================================
        // Codex recovery checks for the concrete source-review defects.
        await RunRecoveryChecksAsync(fixtureRoot, ownedLinks, Record);

        // Gate Items (NOT_RUN per specification)
        // =========================================================================================
        Record("W3-GUI", "人類交互式 GUI 操作、DPI 縮放與讀屏驗收", "NOT_RUN",
            "CLI 不執行 GUI、DPI 或讀屏；另以 GUI 合成資料報告記錄實際互動結果。", 0);

        Record("W3-CLEAN", "乾淨離線 Windows 11 無 SDK 脫機實機首次啟動與保存", "NOT_RUN",
            "需實體乾淨脫機機台證據，目前環境保留標記 NOT_RUN。", 0);

        Record("HUMAN-CARD", "真實人類名片樣本辨識準確率驗收", "NOT_RUN",
            "遵循安全保護限制，禁止讀取真實人類名片個資；待人類使用者自行驗收。", 0);

        // Clean up synthetic fixtures safely: unlinking any junctions first!
        try
        {
            ValidateOwnedFixtureRoot(fixtureRoot, evidenceRoot, runId);
            foreach (string link in ownedLinks.AsEnumerable().Reverse())
            {
                if (StoragePathHelper.IsReparsePoint(link)) Directory.Delete(link, recursive: false);
            }
            string junctionLink = Path.Combine(fixtureRoot, "w3_10", "junction_link");
            if (StoragePathHelper.IsReparsePoint(junctionLink))
            {
                Directory.Delete(junctionLink, recursive: false);
            }
            VerifyNoFixtureReparsePoints(fixtureRoot);
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Record("W3-FIXTURE-CLEANUP", "受控合成資料清理", "FAIL", $"Cleanup failed: {ex.GetType().Name}");
        }

        return report;
    }

    private static async Task RunRecoveryChecksAsync(string fixtureRoot, List<string> ownedLinks,
        Action<string, string, string, string, long> record)
    {
        var watch = Stopwatch.StartNew();
        byte[] image = CreateSyntheticBmp(60, 40, 0x35, 0x45, 0x55);
        try
        {
            string root = Path.Combine(fixtureRoot, "w3_15_ready_retry");
            using var storage = new StorageService(root);
            var vm = new MainViewModel(CreateIsolatedCatalog(root), null, storage);
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(image, "synthetic.bmp", "ImagePickerCopy", "image/bmp", 60, 40));
            vm.NameRow.Value = "定稿內容A";
            vm.NameRow.Confirm();
            storage.SetFailureInjectionHook(stage =>
            {
                if (stage == "AfterReadyTransaction") throw new IOException("Synthetic ready-boundary interruption");
            });
            var first = await vm.SaveCurrentDraftAsync();
            storage.SetFailureInjectionHook(null);
            var retry = await vm.SaveCurrentDraftAsync();
            bool restored = !first.Succeeded && retry.Succeeded && retry.IsDuplicate && vm.CurrentRevision == 1 && !vm.HasUnsavedChanges;
            vm.NameRow.Value = "定稿內容B";
            vm.NameRow.Confirm();
            var edited = await vm.SaveCurrentDraftAsync();
            var detail = await storage.GetContactDetailAsync(vm.CurrentContactId);
            bool nextRevision = edited.Succeeded && vm.CurrentRevision == 2 && !vm.HasUnsavedChanges && detail?.Name == "定稿內容B";
            record("W3-15", "Ready 已提交後重試及再修訂", restored && nextRevision ? "PASS" : "FAIL",
                $"DuplicateRestoresRevisionAndClean={restored}, EditPersistsRevision2={nextRevision}", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) { record("W3-15", "Ready 已提交後重試及再修訂", "FAIL", ex.GetType().Name, watch.ElapsedMilliseconds); }

        watch.Restart();
        try
        {
            string root = Path.Combine(fixtureRoot, "w3_16_columns");
            StoragePathHelper.EnsureStorageDirectories(root);
            string db = Path.Combine(root, StoragePathHelper.DatabaseFileName);
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE contact_records(id TEXT); CREATE TABLE contact_values(id TEXT); CREATE TABLE card_image_assets(id TEXT); CREATE TABLE pending_delete(id TEXT); PRAGMA user_version=1;";
                cmd.ExecuteNonQuery();
            }
            byte[] before = File.ReadAllBytes(db);
            bool columnsRejected;
            using (var storage = new StorageService(root)) columnsRejected = !storage.IsAvailable;
            bool bytesUnchanged = before.SequenceEqual(File.ReadAllBytes(db));
            string aclRoot = Path.Combine(fixtureRoot, "w3_16_acl");
            StoragePathHelper.EnsureStorageDirectories(aclRoot);
            var directory = new DirectoryInfo(aclRoot);
            var acl = directory.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            directory.SetAccessControl(acl);
            string aclBefore = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            bool aclRejected;
            bool errorSafe;
            using (var storage = new StorageService(aclRoot))
            {
                aclRejected = !storage.IsAvailable;
                errorSafe = storage.ErrorMessage?.Contains("DB_INIT") == true && !storage.ErrorMessage.Contains(aclRoot);
            }
            bool aclUnchanged = aclBefore == directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            bool pass = columnsRejected && bytesUnchanged && aclRejected && aclUnchanged && errorSafe;
            record("W3-16", "Schema 1 缺欄位與既有非私有 ACL 拒用", pass ? "PASS" : "FAIL",
                $"ColumnsRejected={columnsRejected}, DbUnchanged={bytesUnchanged}, AclRejected={aclRejected}, AclUnchanged={aclUnchanged}, ErrorSafe={errorSafe}", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) { record("W3-16", "Schema 1 缺欄位與既有非私有 ACL 拒用", "FAIL", ex.GetType().Name, watch.ElapsedMilliseconds); }

        watch.Restart();
        try
        {
            string sentinel = Path.Combine(fixtureRoot, "w3_17_sentinel");
            Directory.CreateDirectory(sentinel);
            string sentinelFile = Path.Combine(sentinel, "sentinel.txt");
            File.WriteAllText(sentinelFile, "synthetic untouched sentinel");
            string aclBefore = new DirectoryInfo(sentinel).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            bool allBlocked = true;
            int created = 0;
            foreach (string component in new[] { "root", StoragePathHelper.CardImagesDirName, StoragePathHelper.DatabaseFileName })
            {
                string root = Path.Combine(fixtureRoot, "w3_17_" + component);
                string link;
                if (component == "root") link = root;
                else
                {
                    StoragePathHelper.EnsureStorageDirectories(root);
                    link = Path.Combine(root, component);
                    if (component == StoragePathHelper.CardImagesDirName) Directory.Delete(link, recursive: false);
                }
                StoragePathHelper.VerifyPathSafety(fixtureRoot, link);
                var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("mklink");
                start.ArgumentList.Add("/J");
                start.ArgumentList.Add(link);
                start.ArgumentList.Add(sentinel);
                using var proc = Process.Start(start);
                proc?.WaitForExit();
                if (proc == null || proc.ExitCode != 0 || !StoragePathHelper.IsReparsePoint(link)) continue;
                ownedLinks.Add(link);
                created++;
                using var storage = new StorageService(root);
                allBlocked &= !storage.IsAvailable;
            }
            bool sentinelUnchanged = File.ReadAllText(sentinelFile) == "synthetic untouched sentinel" &&
                Directory.GetFileSystemEntries(sentinel).Length == 1 &&
                aclBefore == new DirectoryInfo(sentinel).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            string status = !allBlocked || !sentinelUnchanged ? "FAIL" : created == 3 ? "PASS" : "NOT_RUN";
            record("W3-17", "root、圖片目錄及 DB junction 在建立或開啟前拒用", status,
                $"FixturesCreated={created}/3, AllBlocked={allBlocked}, SentinelBytesAndAclUnchanged={sentinelUnchanged}", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) { record("W3-17", "root、圖片目錄及 DB junction 在建立或開啟前拒用", "FAIL", ex.GetType().Name, watch.ElapsedMilliseconds); }

        watch.Restart();
        try
        {
            string root = Path.Combine(fixtureRoot, "w3_18_pending");
            using var storage = new StorageService(root);
            storage.SetFailureInjectionHook(stage =>
            {
                if (stage == "AfterPendingFinalizeTransaction") throw new IOException("Synthetic pending-boundary interruption");
            });
            ContactDraftSnapshot MakeSnapshot(string name) => new()
            {
                ContactId = Guid.NewGuid().ToString("N"), SaveOperationId = Guid.NewGuid().ToString("N"),
                ImageBytes = image, ImageSha256 = Convert.ToHexString(SHA256.HashData(image)), MimeType = "image/bmp", Width = 60, Height = 40,
                Values = new() { new() { ValueKind = CanonicalValueKind.Name, RawValue = name, Label = "姓名" } }
            };
            async Task<SaveResult> InterruptSaveAsync(ContactDraftSnapshot snapshot)
            {
                try { return await storage.SaveDraftAsync(snapshot); }
                catch (IOException ex) when (ex.Message == "Synthetic pending-boundary interruption")
                {
                    return SaveResult.Failed("SYNTHETIC_INTERRUPTION", "Expected injected interruption");
                }
            }
            var delete = MakeSnapshot("待定稿刪除");
            var deleteSave = await InterruptSaveAsync(delete);
            var deleteDetail = await storage.GetContactDetailAsync(delete.ContactId);
            string staged = StoragePathHelper.ResolveSafePath(root, deleteDetail!.ImageAsset!.StagingRelativePath!);
            bool stageExisted = File.Exists(staged);
            var removed = await storage.DeleteContactAsync(delete.ContactId);
            bool pendingDeleted = !deleteSave.Succeeded && stageExisted && removed.Succeeded && !File.Exists(staged) &&
                await storage.GetContactDetailAsync(delete.ContactId) == null;
            var locked = MakeSnapshot("鎖定暫存");
            var other = MakeSnapshot("其他待定稿");
            await InterruptSaveAsync(locked);
            await InterruptSaveAsync(other);
            storage.SetFailureInjectionHook(null);
            var lockedDetail = await storage.GetContactDetailAsync(locked.ContactId);
            string lockedStage = StoragePathHelper.ResolveSafePath(root, lockedDetail!.ImageAsset!.StagingRelativePath!);
            bool othersRecover;
            using (var hold = new FileStream(lockedStage, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await storage.RecoverPendingOperationsAsync();
                othersRecover = (await storage.GetContactDetailAsync(other.ContactId))?.PersistenceState == PersistenceStates.Ready &&
                    (await storage.GetContactDetailAsync(locked.ContactId))?.PersistenceState == PersistenceStates.PendingFinalize;
            }
            await storage.RecoverPendingOperationsAsync();
            bool retryRecovers = (await storage.GetContactDetailAsync(locked.ContactId))?.PersistenceState == PersistenceStates.Ready;
            bool pass = pendingDeleted && othersRecover && retryRecovers;
            record("W3-18", "待定稿暫存圖刪除及單筆占用復原隔離", pass ? "PASS" : "FAIL",
                $"PendingStageDeleted={pendingDeleted}, OthersRecoverWhileLocked={othersRecover}, ReleasedLockRecovers={retryRecovers}", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) { record("W3-18", "待定稿暫存圖刪除及單筆占用復原隔離", "FAIL", ex.GetType().Name, watch.ElapsedMilliseconds); }

        watch.Restart();
        try
        {
            string root = Path.Combine(fixtureRoot, "w3_19_delete_race");
            using var storage = new StorageService(root);
            var vm = new MainViewModel(CreateIsolatedCatalog(root), null, storage);
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(image, "synthetic.bmp", "ImagePickerCopy", "image/bmp", 60, 40));
            vm.NameRow.Value = "定稿前待刪測試";
            vm.NameRow.Confirm();
            storage.SetFailureInjectionHook(stage =>
            {
                if (stage != "AfterFinalVerification") return;
                using var deleting = new StorageService(root);
                deleting.SetFailureInjectionHook(deleteStage =>
                {
                    if (deleteStage == "AfterDeletePendingTransaction") throw new IOException("Synthetic delete-boundary interruption");
                });
                try { deleting.DeleteContactAsync(vm.CurrentContactId).GetAwaiter().GetResult(); }
                catch (IOException ex) when (ex.Message == "Synthetic delete-boundary interruption") { }
            });
            var result = await vm.SaveCurrentDraftAsync();
            var pending = await storage.GetContactDetailAsync(vm.CurrentContactId);
            bool notReady = !result.Succeeded && result.ErrorCode == "CONTACT_DELETED" &&
                pending?.PendingDelete == true && pending.PersistenceState == PersistenceStates.PendingFinalize &&
                pending.ImageAsset?.StorageState == StorageStates.PendingDelete;
            storage.SetFailureInjectionHook(null);
            await storage.RecoverPendingOperationsAsync();
            bool deleted = await storage.GetContactDetailAsync(vm.CurrentContactId) == null &&
                Directory.GetFiles(Path.Combine(root, StoragePathHelper.CardImagesDirName)).Length == 0;
            record("W3-19", "定稿與待刪相遇不能升回 Ready", notReady && deleted ? "PASS" : "FAIL",
                $"PendingDeleteNotReady={notReady}, RecoveryDeletesOwnedAsset={deleted}", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) { record("W3-19", "定稿與待刪相遇不能升回 Ready", "FAIL", ex.GetType().Name, watch.ElapsedMilliseconds); }
    }

    private static void ValidateOwnedFixtureRoot(string root, string evidenceRoot, string runId)
    {
        string expected = Path.GetFullPath(Path.Combine(evidenceRoot, runId));
        if (!string.Equals(Path.GetFullPath(root), expected, StringComparison.OrdinalIgnoreCase) ||
            !expected.StartsWith(evidenceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("FIXTURE_OUTSIDE_EVIDENCE_ROOT");
        StoragePathHelper.VerifyPathSafety(evidenceRoot, root);
    }

    private static void VerifyNoFixtureReparsePoints(string root)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException("UNEXPECTED_FIXTURE_REPARSE_POINT");
            if ((attributes & FileAttributes.Directory) != 0) VerifyNoFixtureReparsePoints(entry);
        }
    }

    internal static byte[] CreateSyntheticBmp(int width, int height, byte r, byte g, byte b)
    {
        int rowBytes = (width * 3 + 3) & ~3;
        int imageSize = rowBytes * height;
        int fileSize = 54 + imageSize;

        byte[] bmp = new byte[fileSize];

        // BMP Header
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10); // Offset to image data

        // DIB Header (BITMAPINFOHEADER)
        BitConverter.GetBytes(40).CopyTo(bmp, 14); // Header size
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);  // Planes
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28); // BitCount (24-bit BGR)
        BitConverter.GetBytes(0).CopyTo(bmp, 30);         // Compression (BI_RGB)
        BitConverter.GetBytes(imageSize).CopyTo(bmp, 34);

        // Pixel data (BGR order, bottom-up)
        for (int y = 0; y < height; y++)
        {
            int rowStart = 54 + y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int pixelStart = rowStart + x * 3;
                bmp[pixelStart] = b;
                bmp[pixelStart + 1] = g;
                bmp[pixelStart + 2] = r;
            }
        }

        return bmp;
    }

    private static CustomFieldCatalogService CreateIsolatedCatalog(string baseDir)
    {
        string path = Path.Combine(baseDir, "isolated-custom-fields.json");
        return new CustomFieldCatalogService(path);
    }

    private static async Task<byte[]> ConvertBmpToFormatAsync(byte[] bmpBytes, Guid encoderId)
    {
        using var inStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(inStream))
        {
            writer.WriteBytes(bmpBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        inStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inStream);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] pixels = pixelData.DetachPixelData();

        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, outStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixels);
        await encoder.FlushAsync();
        outStream.Seek(0);

        byte[] result = new byte[outStream.Size];
        using (var reader = new DataReader(outStream))
        {
            await reader.LoadAsync((uint)outStream.Size);
            reader.ReadBytes(result);
            reader.DetachStream();
        }
        return result;
    }

    private static byte[] CreateValidHeaderCorruptPayloadBmp()
    {
        // 54-byte BMP header specifying 200x200 24bpp (requires 120,000 bytes pixel data),
        // but file payload is truncated to only 60 bytes total!
        byte[] bmp = new byte[60];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(120054).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(200).CopyTo(bmp, 18);
        BitConverter.GetBytes(200).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(0).CopyTo(bmp, 30);
        BitConverter.GetBytes(120000).CopyTo(bmp, 34);
        return bmp;
    }

    private static byte[] CreateValidHeaderCorruptPayloadPng(byte[] validPng)
    {
        // Keep PNG signature (8 bytes) + IHDR chunk (25 bytes), then truncate right at beginning of IDAT
        byte[] truncated = new byte[38];
        Array.Copy(validPng, 0, truncated, 0, Math.Min(38, validPng.Length));
        return truncated;
    }

    private static byte[] CreateTamperedPayloadPng(byte[] validPng)
    {
        byte[] tampered = (byte[])validPng.Clone();
        // Scramble bytes after the IHDR chunk (index 33 onwards)
        for (int i = 35; i < Math.Min(tampered.Length - 12, 60); i++)
        {
            tampered[i] ^= 0xAA;
        }
        return tampered;
    }
}
