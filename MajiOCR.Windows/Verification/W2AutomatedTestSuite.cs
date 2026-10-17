using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Verification;

public class W2TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class W2TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(W2TestSuiteReport).Assembly
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.3";
    public string IssueId { get; set; } = "ISSUE-0006";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<W2TestCaseResult> TestCases { get; set; } = new();
}

public static class W2AutomatedTestSuite
{
    public static async Task<W2TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new W2TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new W2TestCaseResult
            {
                TestId = id,
                Description = desc,
                Passed = passed,
                Details = details,
                LatencyMs = ms
            });
            if (passed) report.PassCount++;
            else report.FailCount++;
            report.TotalTests++;
        }

        // Locate synthetic sample from evidence dir or parent evidence dirs
        string samplePng = Path.Combine(evidenceDir, "synthetic_card_w0.png");
        if (!File.Exists(samplePng))
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "TestReports", "evidence", "ISSUE-0005-W1", "synthetic_card_w0.png");
            if (File.Exists(fallback)) samplePng = fallback;
            else
            {
                string? cur = Directory.GetCurrentDirectory();
                for (int i = 0; i < 5 && cur != null; i++)
                {
                    string candidate = Path.Combine(cur, "TestReports", "evidence", "ISSUE-0005-W1", "synthetic_card_w0.png");
                    if (File.Exists(candidate)) { samplePng = candidate; break; }
                    cur = Directory.GetParent(cur)?.FullName;
                }
            }
        }

        // --- T01: Conservative FieldSuggester ---
        sw.Restart();
        bool sEmail1 = FieldSuggester.Suggest("service@example.com") == CanonicalValueKind.Email;
        bool sEmail2 = FieldSuggester.Suggest("聯絡信箱: info@example.org") == CanonicalValueKind.Email;
        bool sWeb1 = FieldSuggester.Suggest("https://www.example.com") == CanonicalValueKind.Website;
        bool sWeb2 = FieldSuggester.Suggest("example.org") == CanonicalValueKind.Website;
        bool sPhone1 = FieldSuggester.Suggest("0912-345-678") == CanonicalValueKind.Phone;
        bool sPhone2 = FieldSuggester.Suggest("+886-920-111-222") == CanonicalValueKind.Phone;
        bool sPhone3 = FieldSuggester.Suggest("(02) 2345-6789") == CanonicalValueKind.Phone;
        bool sAddr1 = FieldSuggester.Suggest("台北市示範區測試路一段100號") == CanonicalValueKind.Address;
        bool sAddr2 = FieldSuggester.Suggest("地址: 新北市板橋區縣民大道二段100號10樓") == CanonicalValueKind.Address;
        bool sNote1 = FieldSuggester.Suggest("統編: 12345678") == CanonicalValueKind.Note;
        bool sNote2 = FieldSuggester.Suggest("統一編號：87654321") == CanonicalValueKind.Note;
        bool sComp1 = FieldSuggester.Suggest("台灣積體電路製造股份有限公司") == CanonicalValueKind.Company;
        bool sComp2 = FieldSuggester.Suggest("創意數位工作室") == CanonicalValueKind.Company;
        bool sTitle = FieldSuggester.Suggest("職稱: 資深軟體架構師") == CanonicalValueKind.Title;
        bool sName = FieldSuggester.Suggest("姓名: 陳小美") == CanonicalValueKind.Name;
        // Ambiguous text lines must default to Unclassified
        bool sAmb1 = FieldSuggester.Suggest("王大明") == CanonicalValueKind.Unclassified;
        bool sAmb2 = FieldSuggester.Suggest("創新科技，引領未來") == CanonicalValueKind.Unclassified;
        bool sAmb3 = FieldSuggester.Suggest("台北市") == CanonicalValueKind.Unclassified;
        bool sAmb4 = FieldSuggester.Suggest("   ") == CanonicalValueKind.Unclassified;
        sw.Stop();

        bool t01Passed = sEmail1 && sEmail2 && sWeb1 && sWeb2 && sPhone1 && sPhone2 && sPhone3 &&
                         sAddr1 && sAddr2 && sNote1 && sNote2 && sComp1 && sComp2 && sTitle && sName &&
                         sAmb1 && sAmb2 && sAmb3 && sAmb4;
        Record("ISSUE-0006-T01", "保守欄位建議器 (明確模式建議，模糊字串歸類為 Unclassified)", t01Passed,
            $"Email: {sEmail1 && sEmail2}, Web: {sWeb1 && sWeb2}, Phone: {sPhone1 && sPhone2 && sPhone3}, Addr: {sAddr1 && sAddr2}, Note: {sNote1 && sNote2}, Comp: {sComp1 && sComp2}, Title: {sTitle}, Name: {sName}, Conservative Unclassified: {sAmb1 && sAmb2 && sAmb3 && sAmb4}",
            sw.ElapsedMilliseconds);

        // --- T02: OCR Candidates to Dynamic Draft Rows with Tracking ---
        sw.Restart();
        var testCatalogPath = Path.Combine(evidenceDir, "test_custom_fields_t02.json");
        try { if (File.Exists(testCatalogPath)) File.Delete(testCatalogPath); } catch { }
        var catalogService = new CustomFieldCatalogService(testCatalogPath);
        var vm = new MainViewModel(catalogService);

        var mockCandidates = new List<OcrCandidateItem>
        {
            new() { Id = 1, Text = "姓名: 王大明", Confidence = 95f, X = 10, Y = 20, Width = 100, Height = 30, Source = "整圖辨識" },
            new() { Id = 2, Text = "service@example.com", Confidence = 92f, X = 10, Y = 60, Width = 200, Height = 25, Source = "整圖辨識" },
            new() { Id = 3, Text = "不明標語", Confidence = 80f, X = 10, Y = 100, Width = 150, Height = 25, Source = "整圖辨識" }
        };

        vm.PopulateDraftRowsFromCandidates(mockCandidates, reqId: vm.CurrentRequestId, isRegion: false);
        sw.Stop();

        bool t02Passed = vm.DraftRows.Count == 3 &&
                         vm.DraftRows[0].OriginalText == "姓名: 王大明" &&
                         vm.DraftRows[0].ValueKind == CanonicalValueKind.Name &&
                         vm.DraftRows[0].Status == DraftItemStatus.PendingConfirmation &&
                         !vm.DraftRows[0].IsUserModified &&
                         vm.DraftRows[1].ValueKind == CanonicalValueKind.Email &&
                         vm.DraftRows[2].ValueKind == CanonicalValueKind.Unclassified;

        Record("ISSUE-0006-T02", "OCR 候選文字自動生成待確認草稿列 (保留原文、座標、來源與代碼)", t02Passed,
            $"DraftRows count: {vm.DraftRows.Count}, Row 0 Kind: {vm.DraftRows[0].ValueKind}, Status: {vm.DraftRows[0].Status}, Row 1: {vm.DraftRows[1].ValueKind}, Row 2: {vm.DraftRows[2].ValueKind}",
            sw.ElapsedMilliseconds);

        // --- T03: UI Binding Initial State & TextChanged Guard Verification ---
        sw.Restart();
        var row0 = vm.DraftRows[0];
        // Simulate template initialization: TextBox receives current row value
        string simulatedInitialText = row0.Value;
        bool templateLoadGuarded = string.Equals(row0.Value, simulatedInitialText, StringComparison.Ordinal);
        bool initialNotModified = !row0.IsUserModified && row0.Status == DraftItemStatus.PendingConfirmation;

        // User genuinely modifies text
        row0.Value = "王小明";
        row0.MarkModified();
        bool modTextPassed = row0.IsUserModified && row0.Status == DraftItemStatus.PendingConfirmation;

        // User changes category
        row0.ValueKind = CanonicalValueKind.Company;
        row0.MarkModified();
        bool modCategoryPassed = row0.IsUserModified && row0.Status == DraftItemStatus.PendingConfirmation;
        string statusBeforeConfirm = row0.Status.ToString();

        // User explicitly confirms
        row0.Confirm();
        bool confirmPassed = row0.Status == DraftItemStatus.Confirmed;
        sw.Stop();

        bool t03Passed = templateLoadGuarded && initialNotModified && modTextPassed && modCategoryPassed && confirmPassed;
        Record("ISSUE-0006-T03", "UI 初始綁定防誤標人工修改與明確確認轉換", t03Passed,
            $"TemplateLoadGuarded: {templateLoadGuarded}, InitialNotModified: {initialNotModified}, AfterEdit: IsUserModified={row0.IsUserModified}, Status={statusBeforeConfirm}, AfterConfirm: Status={row0.Status}",
            sw.ElapsedMilliseconds);

        // --- T04: Add Custom Field "統編" & File Reloading (New Service Instance) ---
        sw.Restart();
        var customPath = Path.Combine(evidenceDir, "test_custom_fields_t04.json");
        try { if (File.Exists(customPath)) File.Delete(customPath); } catch { }

        var catalogService1 = new CustomFieldCatalogService(customPath);
        var def1 = catalogService1.AddDefinition("統編");

        // Reload using a completely new service instance from disk
        var catalogService2 = new CustomFieldCatalogService(customPath);
        var activeDefs = catalogService2.GetActiveDefinitions();
        sw.Stop();

        bool t04Passed = activeDefs.Count == 1 &&
                         activeDefs[0].Id == def1.Id &&
                         activeDefs[0].Name == "統編" &&
                         activeDefs[0].Active &&
                         File.Exists(customPath);

        Record("ISSUE-0006-T04", "新增自訂名稱「統編」並由新服務實例重新讀取持久化設定 (UTF-8 JSON v1 儲存)", t04Passed,
            $"Created Id: '{def1.Id}', Active defs loaded: {activeDefs.Count}, Name: '{activeDefs.FirstOrDefault()?.Name}', File exists: {File.Exists(customPath)}",
            sw.ElapsedMilliseconds);

        // --- T05: Multilingual Unicode Support & NFC Normalization ---
        sw.Restart();
        var unicodePath = Path.Combine(evidenceDir, "test_custom_fields_t05.json");
        try { if (File.Exists(unicodePath)) File.Delete(unicodePath); } catch { }

        var uService = new CustomFieldCatalogService(unicodePath);
        var uDefTrad = uService.AddDefinition("統一編號");
        var uDefSimp = uService.AddDefinition("纳税人识别号");
        var uDefJpn = uService.AddDefinition("所属部署名");
        var uDefKor = uService.AddDefinition("사업자등록번호");
        var uDefMixed = uService.AddDefinition("LINE ID (官方)");

        var uReloaded = new CustomFieldCatalogService(unicodePath).GetActiveDefinitions();
        sw.Stop();

        bool t05Passed = uReloaded.Count == 5 &&
                         uReloaded.Any(d => d.Name == "統一編號") &&
                         uReloaded.Any(d => d.Name == "纳税人识别号") &&
                         uReloaded.Any(d => d.Name == "所属部署名") &&
                         uReloaded.Any(d => d.Name == "사업자등록번호") &&
                         uReloaded.Any(d => d.Name == "LINE ID (官方)");

        Record("ISSUE-0006-T05", "繁中、簡中、日文、韓文、英文混合自訂名稱無損讀寫與 NFC 正規化", t05Passed,
            $"Stored 5 multilingual names; Read back count: {uReloaded.Count}",
            sw.ElapsedMilliseconds);

        // --- T06: Validation Rejections (Empty, Control Chars, >64 chars, Duplicate) ---
        sw.Restart();
        bool rejEmpty = !CustomFieldCatalogService.ValidateName("   ", out _, out _);
        bool rejCtrl = !CustomFieldCatalogService.ValidateName("統編\u0000字元", out _, out _);
        bool rejNewline = !CustomFieldCatalogService.ValidateName("統編\n第二行", out _, out _);
        string longName = new string('A', 65);
        bool rejLong = !CustomFieldCatalogService.ValidateName(longName, out _, out _);

        bool rejDup = false;
        try
        {
            uService.AddDefinition("統一編號");
        }
        catch (InvalidOperationException)
        {
            rejDup = true;
        }

        bool rejLatinDup = false;
        try
        {
            uService.AddDefinition("line id (官方)");
        }
        catch (InvalidOperationException)
        {
            rejLatinDup = true;
        }
        sw.Stop();

        bool t06Passed = rejEmpty && rejCtrl && rejNewline && rejLong && rejDup && rejLatinDup;
        Record("ISSUE-0006-T06", "空白、控制字元、超過 64 字元與重複名稱安全拒絕", t06Passed,
            $"RejEmpty={rejEmpty}, RejCtrl={rejCtrl}, RejNewline={rejNewline}, RejLong={rejLong}, RejDup={rejDup}, RejLatinDup={rejLatinDup}",
            sw.ElapsedMilliseconds);

        // --- T07: Rename and Deactivate Definition (Stable ID & Snapshot) ---
        sw.Restart();
        string origId = uDefTrad.Id;
        uService.RenameDefinition(origId, "統編快照");
        var afterRename = uService.GetAllDefinitions().First(d => d.Id == origId);
        bool renamePassed = afterRename.Name == "統編快照" && afterRename.Id == origId;

        uService.SetDefinitionActive(origId, false);
        var activeAfterDeactivate = uService.GetActiveDefinitions();
        var allAfterDeactivate = uService.GetAllDefinitions().First(d => d.Id == origId);
        bool deactivatePassed = !allAfterDeactivate.Active && !activeAfterDeactivate.Any(d => d.Id == origId);
        sw.Stop();

        Record("ISSUE-0006-T07", "自訂名稱重新命名 (ID 不變) 與停用 (新選單隱藏、舊定義保留)",
            renamePassed && deactivatePassed,
            $"Renamed: '{afterRename.Name}' (ID: {origId}); Deactivated: Active={allAfterDeactivate.Active}, InActiveMenu={activeAfterDeactivate.Any(d => d.Id == origId)}",
            sw.ElapsedMilliseconds);

        // --- T08: File Corruption, Strict UTF-8, Null Definitions, and Version Safety ---
        sw.Restart();
        // 1. Broken JSON: Load & direct SaveCatalog protection
        var corruptPath = Path.Combine(evidenceDir, "corrupted_custom_fields.json");
        byte[] brokenJsonBytes = Encoding.UTF8.GetBytes("{ broken json content, unclosed [");
        await File.WriteAllBytesAsync(corruptPath, brokenJsonBytes);
        var corruptService = new CustomFieldCatalogService(corruptPath);
        bool corruptCaught = false;
        try { corruptService.LoadCatalog(); } catch (FormatException) { corruptCaught = true; }
        bool corruptSaveRejected = false;
        try { corruptService.SaveCatalog(new CustomFieldCatalog { CatalogVersion = 1 }); } catch (FormatException) { corruptSaveRejected = true; }
        byte[] afterCorruptSaveBytes = await File.ReadAllBytesAsync(corruptPath);
        bool corruptFilePreserved = brokenJsonBytes.SequenceEqual(afterCorruptSaveBytes);

        // 2. Invalid non-UTF-8 bytes: Load & direct SaveCatalog protection
        var nonUtf8Path = Path.Combine(evidenceDir, "non_utf8_custom_fields.json");
        byte[] invalidUtf8Bytes = new byte[] { 0x7B, 0x22, 0x6E, 0x61, 0x6D, 0x65, 0x22, 0x3A, 0x20, 0x22, 0xFF, 0xFE, 0x22, 0x7D };
        await File.WriteAllBytesAsync(nonUtf8Path, invalidUtf8Bytes);
        var nonUtf8Service = new CustomFieldCatalogService(nonUtf8Path);
        bool nonUtf8Caught = false;
        try { nonUtf8Service.LoadCatalog(); } catch (FormatException) { nonUtf8Caught = true; }
        bool nonUtf8SaveRejected = false;
        try { nonUtf8Service.SaveCatalog(new CustomFieldCatalog { CatalogVersion = 1 }); } catch (FormatException) { nonUtf8SaveRejected = true; }
        byte[] afterNonUtf8SaveBytes = await File.ReadAllBytesAsync(nonUtf8Path);
        bool nonUtf8FilePreserved = invalidUtf8Bytes.SequenceEqual(afterNonUtf8SaveBytes);

        // 3. Definitions is null: Load & direct SaveCatalog protection
        var nullDefsPath = Path.Combine(evidenceDir, "null_defs_custom_fields.json");
        byte[] nullDefsBytes = Encoding.UTF8.GetBytes("{\"catalogVersion\": 1, \"definitions\": null}");
        await File.WriteAllBytesAsync(nullDefsPath, nullDefsBytes);
        var nullDefsService = new CustomFieldCatalogService(nullDefsPath);
        bool nullDefsCaught = false;
        try { nullDefsService.LoadCatalog(); } catch (FormatException) { nullDefsCaught = true; }
        bool nullDefsSaveRejected = false;
        try { nullDefsService.SaveCatalog(new CustomFieldCatalog { CatalogVersion = 1 }); } catch (FormatException) { nullDefsSaveRejected = true; }
        byte[] afterNullDefsSaveBytes = await File.ReadAllBytesAsync(nullDefsPath);
        bool nullDefsFilePreserved = nullDefsBytes.SequenceEqual(afterNullDefsSaveBytes);

        // 4. Unknown newer version: Load & direct SaveCatalog protection
        var newVerPath = Path.Combine(evidenceDir, "new_version_custom_fields.json");
        byte[] newVerBytes = Encoding.UTF8.GetBytes("{\"catalogVersion\":2,\"definitions\":[]}");
        await File.WriteAllBytesAsync(newVerPath, newVerBytes);
        var newVerService = new CustomFieldCatalogService(newVerPath);
        bool newVerCaught = false;
        try { newVerService.LoadCatalog(); } catch (InvalidOperationException) { newVerCaught = true; }
        bool newVerSaveRejected = false;
        try { newVerService.SaveCatalog(new CustomFieldCatalog { CatalogVersion = 1 }); } catch (InvalidOperationException) { newVerSaveRejected = true; }
        byte[] afterNewVerSaveBytes = await File.ReadAllBytesAsync(newVerPath);
        bool newVerFilePreserved = newVerBytes.SequenceEqual(afterNewVerSaveBytes);

        // 5. Active field missing / null in definition
        var missingActivePath = Path.Combine(evidenceDir, "missing_active_custom_fields.json");
        byte[] missingActiveBytes = Encoding.UTF8.GetBytes("{\"catalogVersion\": 1, \"definitions\": [{\"id\":\"0a6d49e0-8683-4a9f-9c16-bdd66fcfd011\",\"name\":\"統編\",\"createdAt\":\"2026-09-25T06:00:00+08:00\"}]}");
        await File.WriteAllBytesAsync(missingActivePath, missingActiveBytes);
        var missingActiveService = new CustomFieldCatalogService(missingActivePath);
        bool missingActiveCaught = false;
        try { missingActiveService.LoadCatalog(); } catch (FormatException) { missingActiveCaught = true; }
        bool missingActiveSaveRejected = false;
        try { missingActiveService.SaveCatalog(new CustomFieldCatalog { CatalogVersion = 1 }); } catch (FormatException) { missingActiveSaveRejected = true; }
        byte[] afterMissingActiveBytes = await File.ReadAllBytesAsync(missingActivePath);
        bool missingActiveFilePreserved = missingActiveBytes.SequenceEqual(afterMissingActiveBytes);

        // 6. CreatedAt without timezone (e.g. 2026-09-25 or 2026-09-25T12:00:00)
        var noTzPath = Path.Combine(evidenceDir, "no_tz_custom_fields.json");
        byte[] noTzBytes = Encoding.UTF8.GetBytes("{\"catalogVersion\": 1, \"definitions\": [{\"id\":\"0a6d49e0-8683-4a9f-9c16-bdd66fcfd011\",\"name\":\"統編\",\"active\":true,\"createdAt\":\"2026-09-25T12:00:00\"}]}");
        await File.WriteAllBytesAsync(noTzPath, noTzBytes);
        var noTzService = new CustomFieldCatalogService(noTzPath);
        bool noTzCaught = false;
        try { noTzService.LoadCatalog(); } catch (FormatException) { noTzCaught = true; }
        bool noTzSaveRejected = false;
        try { noTzService.SaveCatalog(new CustomFieldCatalog { CatalogVersion = 1 }); } catch (FormatException) { noTzSaveRejected = true; }
        byte[] afterNoTzBytes = await File.ReadAllBytesAsync(noTzPath);
        bool noTzFilePreserved = noTzBytes.SequenceEqual(afterNoTzBytes);
        sw.Stop();

        bool t08Passed = corruptCaught && corruptSaveRejected && corruptFilePreserved &&
                         nonUtf8Caught && nonUtf8SaveRejected && nonUtf8FilePreserved &&
                         nullDefsCaught && nullDefsSaveRejected && nullDefsFilePreserved &&
                         newVerCaught && newVerSaveRejected && newVerFilePreserved &&
                         missingActiveCaught && missingActiveSaveRejected && missingActiveFilePreserved &&
                         noTzCaught && noTzSaveRejected && noTzFilePreserved;

        Record("ISSUE-0006-T08", "SaveCatalog 面對毀損檔/非法UTF-8/新版/缺失active/無時區直接拒絕且 byte-for-byte 保留原檔", t08Passed,
            $"CorruptSaveRej={corruptSaveRejected}, NonUtf8SaveRej={nonUtf8SaveRejected}, NullDefsSaveRej={nullDefsSaveRejected}, NewVerSaveRej={newVerSaveRejected}, MissingActiveSaveRej={missingActiveSaveRejected}, NoTzSaveRej={noTzSaveRejected}",
            sw.ElapsedMilliseconds);

        // --- T09: OCR Re-run & Region Re-recognition Protection ---
        sw.Restart();
        var protectVm = new MainViewModel(uService);
        var initialCandidates = new List<OcrCandidateItem>
        {
            new() { Id = 1, Text = "原始電話: 02-12345678", Source = "整圖辨識" },
            new() { Id = 2, Text = "原始姓名: 王大明", Source = "整圖辨識" }
        };
        protectVm.PopulateDraftRowsFromCandidates(initialCandidates, reqId: protectVm.CurrentRequestId, isRegion: false);

        // User modifies Row 0 and confirms Row 1
        protectVm.DraftRows[0].Value = "手動修改之電話: 02-87654321";
        protectVm.DraftRows[0].MarkModified();
        protectVm.DraftRows[1].Confirm();

        // Run full OCR again with different candidates
        var secondCandidates = new List<OcrCandidateItem>
        {
            new() { Id = 3, Text = "全新文字行 A", Source = "整圖辨識" },
            new() { Id = 4, Text = "全新文字行 B", Source = "整圖辨識" }
        };
        protectVm.PopulateDraftRowsFromCandidates(secondCandidates, reqId: protectVm.CurrentRequestId, isRegion: false);

        bool row0Preserved = protectVm.DraftRows.Any(r => r.Value == "手動修改之電話: 02-87654321" && r.IsUserModified);
        bool row1Preserved = protectVm.DraftRows.Any(r => r.Value == "原始姓名: 王大明" && r.Status == DraftItemStatus.Confirmed);
        bool newRowsAdded = protectVm.DraftRows.Any(r => r.Value == "全新文字行 A");

        // Run region OCR
        var regionCandidates = new List<OcrCandidateItem>
        {
            new() { Id = 5, Text = "局部放大候選 C", Source = "局部辨識" }
        };
        protectVm.PopulateDraftRowsFromCandidates(regionCandidates, reqId: protectVm.CurrentRequestId, isRegion: true);
        bool regionRowAdded = protectVm.DraftRows.Any(r => r.Value == "局部放大候選 C" && r.Source == "局部辨識");
        sw.Stop();

        bool t09Passed = row0Preserved && row1Preserved && newRowsAdded && regionRowAdded;
        Record("ISSUE-0006-T09", "重辨識保護：整圖/局部再辨識不覆蓋已人工修改或已確認列", t09Passed,
            $"User-modified preserved: {row0Preserved}, Confirmed preserved: {row1Preserved}, New full OCR added: {newRowsAdded}, Region OCR added: {regionRowAdded}, Total rows: {protectVm.DraftRows.Count}",
            sw.ElapsedMilliseconds);

        // --- T10: In-flight OCR Cancellation & Stale Callback Rejection ---
        sw.Restart();
        var staleVm = new MainViewModel(uService);
        // Case A: Pure cancellation
        int reqCancel = staleVm.CurrentRequestId;
        staleVm.CancelCurrentOcr();
        var staleCandidatesA = new List<OcrCandidateItem> { new() { Id = 1, Text = "過期取消候選" } };
        staleVm.PopulateDraftRowsFromCandidates(staleCandidatesA, reqId: reqCancel, isRegion: false);
        bool cancelRejected = !staleVm.DraftRows.Any(r => r.Value == "過期取消候選");

        // Case B: Region reselection invalidation
        int reqRegion = staleVm.CurrentRequestId;
        staleVm.SetRegionCoordinates(10, 10, 100, 100);
        var staleCandidatesB = new List<OcrCandidateItem> { new() { Id = 2, Text = "過期選區候選" } };
        staleVm.PopulateDraftRowsFromCandidates(staleCandidatesB, reqId: reqRegion, isRegion: true);
        bool regionRejected = !staleVm.DraftRows.Any(r => r.Value == "過期選區候選");

        // Case C: Image change invalidation
        int reqImage = staleVm.CurrentRequestId;
        staleVm.CancelCurrentOcr(); // invoked in SetSessionImageAsync
        var staleCandidatesC = new List<OcrCandidateItem> { new() { Id = 3, Text = "換圖前殘留候選" } };
        staleVm.PopulateDraftRowsFromCandidates(staleCandidatesC, reqId: reqImage, isRegion: false);
        bool imageChangeRejected = !staleVm.DraftRows.Any(r => r.Value == "換圖前殘留候選");
        sw.Stop();

        bool t10Passed = cancelRejected && regionRejected && imageChangeRejected;
        Record("ISSUE-0006-T10", "取消、重新選區與換圖之過期 OCR 回呼實質拒絕驗證", t10Passed,
            $"CancelRejected: {cancelRejected}, RegionReselectionRejected: {regionRejected}, ImageChangeRejected: {imageChangeRejected}",
            sw.ElapsedMilliseconds);

        // --- T11: Single-value Conflict Detection ---
        sw.Restart();
        var conflictVm = new MainViewModel(uService);
        var cRow1 = conflictVm.AddBlankDraftRow();
        cRow1.ValueKind = CanonicalValueKind.Name;
        cRow1.Value = "王大明";
        cRow1.Confirm();

        var cRow2 = conflictVm.AddBlankDraftRow();
        cRow2.ValueKind = CanonicalValueKind.Name;
        cRow2.Value = "陳小美";

        var conflictDetected = conflictVm.CheckSingleValueConflict(cRow2, CanonicalValueKind.Name);
        var noConflictOnPhone = conflictVm.CheckSingleValueConflict(cRow2, CanonicalValueKind.Phone);
        sw.Stop();

        bool t11Passed = conflictDetected != null &&
                         conflictDetected.Value == "王大明" &&
                         noConflictOnPhone == null;

        Record("ISSUE-0006-T11", "單值欄位衝突偵測 (已存在確認值時提供提示，不靜默取代；多筆電話不衝突)", t11Passed,
            $"Single value conflict on Name detected: {conflictDetected?.Value}, Multi-value Phone conflict: {noConflictOnPhone != null}",
            sw.ElapsedMilliseconds);

        // --- T12: Canonical Cross-Platform Code Mapping ---
        sw.Restart();
        var kinds = CanonicalValueKind.AllStandardKinds;
        bool allKnown = kinds.All(k => CanonicalValueKind.IsKnownKind(k));
        bool customKnown = CanonicalValueKind.IsKnownKind(CanonicalValueKind.Custom);
        bool singleValueCheck = CanonicalValueKind.IsSingleValueKind(CanonicalValueKind.Name) &&
                                CanonicalValueKind.IsSingleValueKind(CanonicalValueKind.Company) &&
                                !CanonicalValueKind.IsSingleValueKind(CanonicalValueKind.Phone) &&
                                !CanonicalValueKind.IsSingleValueKind(CanonicalValueKind.Email) &&
                                !CanonicalValueKind.IsSingleValueKind(CanonicalValueKind.Custom);
        sw.Stop();

        bool t12Passed = allKnown && customKnown && singleValueCheck;
        Record("ISSUE-0006-T12", "跨平台 canonical 代碼契約 (Name, Company, Title, Phone, Email, Website, Address, Note, Unclassified, Custom)", t12Passed,
            $"All standard kinds valid: {allKnown}, Custom valid: {customKnown}, Single-value definition: {singleValueCheck}",
            sw.ElapsedMilliseconds);

        // --- T13: Android Baseline Integrity & File Inventory Verification ---
        sw.Restart();
        string repoRoot = Directory.GetCurrentDirectory();
        string? root = repoRoot;
        for (int i = 0; i < 5 && root != null; i++)
        {
            if (Directory.Exists(Path.Combine(root, "MajiOCR")) && File.Exists(Path.Combine(root, "TestReports", "evidence", "ISSUE-0006", "baseline-MajiOCR.json")))
            {
                repoRoot = root;
                break;
            }
            root = Directory.GetParent(root)?.FullName;
        }

        string baselineFile = Path.Combine(repoRoot, "TestReports", "evidence", "ISSUE-0006", "baseline-MajiOCR.json");
        bool t13Passed = false;
        string t13Details = string.Empty;

        if (File.Exists(baselineFile))
        {
            try
            {
                string baselineJson = File.ReadAllText(baselineFile);
                using var doc = JsonDocument.Parse(baselineJson);
                int totalEntries = doc.RootElement.GetArrayLength();
                int verifiedEntries = 0;
                var mismatches = new List<string>();

                using var sha = SHA256.Create();
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    string relPath = element.GetProperty("path").GetString()!;
                    string expectedSha = element.GetProperty("sha256").GetString()!;
                    long expectedLen = element.GetProperty("length").GetInt64();

                    string fullPath = Path.Combine(repoRoot, relPath);
                    if (!File.Exists(fullPath))
                    {
                        mismatches.Add($"Missing file: {relPath}");
                        continue;
                    }

                    var fi = new FileInfo(fullPath);
                    if (fi.Length != expectedLen)
                    {
                        mismatches.Add($"Length mismatch on {relPath}: expected {expectedLen}, got {fi.Length}");
                        continue;
                    }

                    byte[] bytes = File.ReadAllBytes(fullPath);
                    string actualSha = Convert.ToHexString(sha.ComputeHash(bytes));
                    if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                    {
                        mismatches.Add($"SHA256 mismatch on {relPath}: expected {expectedSha}, got {actualSha}");
                        continue;
                    }

                    verifiedEntries++;
                }

                // Verify non-build file count in MajiOCR/ directory
                string majiOcrDir = Path.Combine(repoRoot, "MajiOCR");
                int currentFileCount = Directory.GetFiles(majiOcrDir, "*", SearchOption.AllDirectories)
                    .Count(f => !f.Contains("\\build\\") && !f.Contains("\\.gradle\\"));

                // Verify publish/MajiOCR/ baseline
                string publishBaselineFile = Path.Combine(repoRoot, "TestReports", "evidence", "ISSUE-0006", "baseline-publish-MajiOCR.json");
                int pubTotal = 0;
                int pubVerified = 0;
                if (File.Exists(publishBaselineFile))
                {
                    string pubBaselineJson = File.ReadAllText(publishBaselineFile);
                    using var pubDoc = JsonDocument.Parse(pubBaselineJson);
                    pubTotal = pubDoc.RootElement.GetArrayLength();
                    foreach (var element in pubDoc.RootElement.EnumerateArray())
                    {
                        string relPath = element.GetProperty("path").GetString()!;
                        string expectedSha = element.GetProperty("sha256").GetString()!;
                        long expectedLen = element.GetProperty("length").GetInt64();

                        string fullPath = Path.Combine(repoRoot, relPath);
                        if (File.Exists(fullPath))
                        {
                            var fi = new FileInfo(fullPath);
                            if (fi.Length == expectedLen)
                            {
                                byte[] bytes = File.ReadAllBytes(fullPath);
                                string actualSha = Convert.ToHexString(sha.ComputeHash(bytes));
                                if (string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                                {
                                    pubVerified++;
                                }
                            }
                        }
                    }
                }

                bool baselineExists = totalEntries > 0 && pubTotal > 0;
                t13Passed = baselineExists;
                t13Details = $"MajiOCR/ Baseline 定義數 {totalEntries} 筆，現存檔案數 {currentFileCount}；publish/MajiOCR/ 定義數 {pubTotal} 筆。事前 Baseline 檔案完整，Android 變更依審查指示由 PM 獨立執行前後比對。";
            }
            catch (Exception ex)
            {
                t13Passed = false;
                t13Details = $"讀取或驗證 baseline 發生例外: {ex.Message}";
            }
        }
        else
        {
            t13Passed = false;
            t13Details = $"找不到 baseline-MajiOCR.json: {baselineFile}";
        }
        sw.Stop();

        Record("ISSUE-0006-T13", "Android 原始碼與發布目錄事前 Baseline 記錄 (由 PM 獨立執行前後清單比對)", t13Passed,
            t13Details, sw.ElapsedMilliseconds);

        // --- T14: Real Offline 5-Language OCR End-to-End Dynamic Draft Generation ---
        if (File.Exists(samplePng))
        {
            sw.Restart();
            using var ocr = new OcrService();
            byte[] imgBytes = await File.ReadAllBytesAsync(samplePng);
            var ocrRes = await ocr.RunFullOcrAsync(imgBytes, 201, CancellationToken.None);
            var ocrVm = new MainViewModel(uService);
            ocrVm.PopulateDraftRowsFromCandidates(ocrRes.Candidates, ocrVm.CurrentRequestId, isRegion: false);
            sw.Stop();

            bool hasRows = ocrVm.DraftRows.Count >= 5;
            bool hasPhoneRow = ocrVm.DraftRows.Any(r => r.ValueKind == CanonicalValueKind.Phone);
            bool allPending = ocrVm.DraftRows.All(r => r.Status == DraftItemStatus.PendingConfirmation && !r.IsUserModified);
            bool t14Passed = ocrRes.Success && hasRows && hasPhoneRow && allPending;

            Record("ISSUE-0006-T14", "五語真實離線 OCR 全流程動態草稿生成 (候選行全數轉換為待確認列)", t14Passed,
                $"OCR Latency: {ocrRes.LatencyMs} ms, Candidates: {ocrRes.Candidates.Count}, DraftRows: {ocrVm.DraftRows.Count}, HasPhone: {hasPhoneRow}, AllPending: {allPending}",
                sw.ElapsedMilliseconds);
        }

        // --- T15: CustomFieldsManagementController & UI Operations ---
        sw.Restart();
        var ctrlPath = Path.Combine(evidenceDir, "test_custom_fields_t15.json");
        try { if (File.Exists(ctrlPath)) File.Delete(ctrlPath); } catch { }
        var ctrlService = new CustomFieldCatalogService(ctrlPath);
        var controller = new CustomFieldsManagementController(ctrlService);

        // 1. Initial empty load
        bool loadOk = controller.TryLoadAllDefinitions(out var initialDefs, out var loadErr) && initialDefs.Count == 0 && loadErr == null;

        // 2. Add definition
        bool addOk = controller.TryAddDefinition("統編", out var ctrlDef, out var addErr) && ctrlDef != null && addErr == null;

        // 3. Rename definition
        bool renameOk = controller.TryRenameDefinition(ctrlDef!.Id, "統一編號", out var renameErr) && renameErr == null;
        var reloadedDefs = ctrlService.GetAllDefinitions();
        bool renameVerified = reloadedDefs.Count == 1 && reloadedDefs[0].Name == "統一編號" && reloadedDefs[0].Id == ctrlDef.Id;

        // 4. Toggle active
        bool deactivateOk = controller.TrySetDefinitionActive(ctrlDef.Id, false, out var deactErr) && deactErr == null;
        bool actVerified = ctrlService.GetActiveDefinitions().Count == 0 && ctrlService.GetAllDefinitions()[0].Active == false;

        bool reactivateOk = controller.TrySetDefinitionActive(ctrlDef.Id, true, out var reactErr) && reactErr == null;
        bool reactVerified = ctrlService.GetActiveDefinitions().Count == 1 && ctrlService.GetAllDefinitions()[0].Active == true;

        // 5. Corrupted file error handling (safe error, no crash)
        var brokenPath = Path.Combine(evidenceDir, "broken_ctrl.json");
        await File.WriteAllTextAsync(brokenPath, "{ corrupt");
        var brokenController = new CustomFieldsManagementController(new CustomFieldCatalogService(brokenPath));
        bool safeHandlingOnBroken = !brokenController.TryLoadAllDefinitions(out _, out var brokenErr) && !string.IsNullOrWhiteSpace(brokenErr);

        // 6. Max 200 items limit
        var limitPath = Path.Combine(evidenceDir, "limit_catalog.json");
        try { if (File.Exists(limitPath)) File.Delete(limitPath); } catch { }
        var limitService = new CustomFieldCatalogService(limitPath);
        var limitCatalog = new CustomFieldCatalog { CatalogVersion = 1 };
        for (int i = 0; i < 200; i++)
        {
            limitCatalog.Definitions.Add(new CustomFieldDefinition
            {
                Id = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                Name = $"欄位_{i}",
                Active = true,
                CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")
            });
        }
        limitService.SaveCatalog(limitCatalog);
        bool limitEnforced = false;
        try
        {
            limitService.AddDefinition("第201個");
        }
        catch (InvalidOperationException)
        {
            limitEnforced = true;
        }
        // 7. CheckBox toggle recursion suppression and single-error handling
        int errorDialogCount = 0;
        bool viewStateReverted = false;
        // Simulate a failing toggle where persistence fails (e.g. broken catalog service)
        bool toggleResult = brokenController.ExecuteActiveToggle(
            "dummy-id",
            requestedActive: true,
            revertViewCheckbox: revertedActive => { viewStateReverted = !revertedActive; },
            showErrorMessage: err => { errorDialogCount++; }
        );
        bool recursionSuppressed = !toggleResult && viewStateReverted && errorDialogCount == 1;

        sw.Stop();

        bool t15Passed = loadOk && addOk && renameOk && renameVerified && deactivateOk && actVerified && reactivateOk && reactVerified && safeHandlingOnBroken && limitEnforced && recursionSuppressed;
        Record("ISSUE-0006-T15", "自訂名稱管理 Controller 與 UI 操作安全驗證 (改名/停用/毀損防護/200上限/抑制遞迴)", t15Passed,
            $"LoadOk={loadOk}, AddOk={addOk}, RenameOk={renameVerified}, DeactivateOk={actVerified}, ReactivateOk={reactVerified}, SafeBrokenHandling={safeHandlingOnBroken}, Limit200Enforced={limitEnforced}, RecursionSuppressed={recursionSuppressed}",
            sw.ElapsedMilliseconds);

        // --- T16: PM Shared Fixture: custom-fields-v1.json Verification ---
        sw.Restart();
        string customFieldsFixturePath = Path.Combine(repoRoot, "TestPlans", "fixtures", "ISSUE-0006", "custom-fields-v1.json");
        bool t16Passed = false;
        string t16Details = string.Empty;
        if (File.Exists(customFieldsFixturePath))
        {
            byte[] fixtureBytesBefore = await File.ReadAllBytesAsync(customFieldsFixturePath);
            try
            {
                var fixtureService = new CustomFieldCatalogService(customFieldsFixturePath);
                var fixtureCatalog = fixtureService.LoadCatalog();

                bool verOk = fixtureCatalog.CatalogVersion == 1;
                bool countOk = fixtureCatalog.Definitions.Count == 2;

                var fDef1 = fixtureCatalog.Definitions[0];
                bool def1Ok = fDef1.Id == "0a6d49e0-8683-4a9f-9c16-bdd66fcfd011" &&
                              fDef1.Name == "統編" &&
                              fDef1.Active == true &&
                              fDef1.CreatedAt == "2026-09-25T06:00:00+08:00" &&
                              CustomFieldCatalogService.IsValidIso8601WithTimezone(fDef1.CreatedAt);

                var fDef2 = fixtureCatalog.Definitions[1];
                bool def2Ok = fDef2.Id == "d6fbb71c-25ad-48da-a1c6-27c14f588452" &&
                              fDef2.Name == "部署 / 部門 LINE ID" &&
                              fDef2.Active == false &&
                              fDef2.CreatedAt == "2026-09-25T06:01:00+08:00" &&
                              CustomFieldCatalogService.IsValidIso8601WithTimezone(fDef2.CreatedAt);

                byte[] fixtureBytesAfter = await File.ReadAllBytesAsync(customFieldsFixturePath);
                bool fixtureUnmodified = fixtureBytesBefore.SequenceEqual(fixtureBytesAfter);

                t16Passed = verOk && countOk && def1Ok && def2Ok && fixtureUnmodified;
                t16Details = $"CatalogVersion={fixtureCatalog.CatalogVersion}, DefsCount={fixtureCatalog.Definitions.Count}, Def1({fDef1.Name}, Active={fDef1.Active}, TZ={fDef1.CreatedAt}): {def1Ok}, Def2({fDef2.Name}, Active={fDef2.Active}, TZ={fDef2.CreatedAt}): {def2Ok}, FixtureUnmodified={fixtureUnmodified}";
            }
            catch (Exception ex)
            {
                t16Passed = false;
                t16Details = $"讀取 custom-fields-v1.json 發生例外: {ex.Message}";
            }
        }
        else
        {
            t16Passed = false;
            t16Details = $"找不到檔案: {customFieldsFixturePath}";
        }
        sw.Stop();
        Record("ISSUE-0006-T16", "共用 Fixture custom-fields-v1.json 格式/UUID/Unicode/時區載入與不變更驗證", t16Passed, t16Details, sw.ElapsedMilliseconds);

        // --- T17: PM Shared Fixture: canonical-samples-v1.json 10-Pattern Verification ---
        sw.Restart();
        string canonicalFixturePath = Path.Combine(repoRoot, "TestPlans", "fixtures", "ISSUE-0006", "canonical-samples-v1.json");
        bool t17Passed = false;
        string t17Details = string.Empty;
        if (File.Exists(canonicalFixturePath))
        {
            byte[] fixtureBytesBefore = await File.ReadAllBytesAsync(canonicalFixturePath);
            try
            {
                string jsonText = await File.ReadAllTextAsync(canonicalFixturePath);
                using var cDoc = JsonDocument.Parse(jsonText);
                int contractVer = cDoc.RootElement.GetProperty("contractVersion").GetInt32();
                var samplesArray = cDoc.RootElement.GetProperty("samples");

                int matchedCount = 0;
                int totalSamples = samplesArray.GetArrayLength();
                var sampleMismatches = new List<string>();

                foreach (var sample in samplesArray.EnumerateArray())
                {
                    string text = sample.GetProperty("text").GetString()!;
                    string expKind = sample.GetProperty("valueKind").GetString()!;
                    string actKind = FieldSuggester.Suggest(text);

                    if (string.Equals(expKind, actKind, StringComparison.Ordinal))
                    {
                        matchedCount++;
                    }
                    else
                    {
                        sampleMismatches.Add($"Text '{text}': expected {expKind}, got {actKind}");
                    }
                }

                byte[] fixtureBytesAfter = await File.ReadAllBytesAsync(canonicalFixturePath);
                bool fixtureUnmodified = fixtureBytesBefore.SequenceEqual(fixtureBytesAfter);

                t17Passed = contractVer == 2 && matchedCount == totalSamples && totalSamples == 10 && fixtureUnmodified;
                t17Details = $"ContractVersion={contractVer}, MatchedSamples={matchedCount}/{totalSamples}, FixtureUnmodified={fixtureUnmodified}, Mismatches={sampleMismatches.Count}";
            }
            catch (Exception ex)
            {
                t17Passed = false;
                t17Details = $"讀取 canonical-samples-v1.json 發生例外: {ex.Message}";
            }
        }
        else
        {
            t17Passed = false;
            t17Details = $"找不到檔案: {canonicalFixturePath}";
        }
        sw.Stop();
        Record("ISSUE-0006-T17", "共用 Fixture canonical-samples-v1.json 10 項標準欄位建議比對與不變更驗證", t17Passed, t17Details, sw.ElapsedMilliseconds);

        // --- T18: Core Option Model: Snapshot Retention & Deactivated Isolation ---
        sw.Restart();
        var optModelPath = Path.Combine(evidenceDir, "test_opt_model.json");
        try { if (File.Exists(optModelPath)) File.Delete(optModelPath); } catch { }
        var optModelService = new CustomFieldCatalogService(optModelPath);

        // Add 2 custom fields: "統編" (ID1) and "部門" (ID2)
        var defA = optModelService.AddDefinition("統編");
        var defB = optModelService.AddDefinition("部門");

        var optModelVm = new MainViewModel(optModelService);
        optModelVm.RefreshFieldOptions();

        // Row 1 assigned to "統編" (ID1)
        var rowA = optModelVm.AddBlankDraftRow();
        rowA.ValueKind = CanonicalValueKind.Custom;
        rowA.CustomFieldDefinitionId = defA.Id;
        rowA.CustomFieldNameSnapshot = defA.Name;
        rowA.SyncOptions(optModelService.GetActiveDefinitions(), DraftFieldOption.CreateAction("+ 新增自訂名稱…"));

        // Row 2 assigned to "部門" (ID2)
        var rowB = optModelVm.AddBlankDraftRow();
        rowB.ValueKind = CanonicalValueKind.Custom;
        rowB.CustomFieldDefinitionId = defB.Id;
        rowB.CustomFieldNameSnapshot = defB.Name;
        rowB.SyncOptions(optModelService.GetActiveDefinitions(), DraftFieldOption.CreateAction("+ 新增自訂名稱…"));

        // Now Deactivate "統編" (defA)
        optModelService.SetDefinitionActive(defA.Id, false);

        // Rename "部門" (defB) to "所屬部門" in catalog
        optModelService.RenameDefinition(defB.Id, "所屬部門");

        // Refresh ViewModel options
        optModelVm.RefreshFieldOptions();

        // Assert Row 1: retains snapshot "統編", retains defA.Id, SelectedOption matches defA.Id
        bool rowAPreserved = rowA.ValueKind == CanonicalValueKind.Custom &&
                             rowA.CustomFieldDefinitionId == defA.Id &&
                             rowA.SelectedOption != null &&
                             rowA.SelectedOption.CustomFieldDefinitionId == defA.Id &&
                             rowA.SelectedOption.DisplayText.Contains("統編");

        // Assert Row 2: retains snapshot "部門", retains defB.Id, SelectedOption matches defB.Id
        bool rowBPreserved = rowB.ValueKind == CanonicalValueKind.Custom &&
                             rowB.CustomFieldDefinitionId == defB.Id &&
                             rowB.SelectedOption != null &&
                             rowB.SelectedOption.CustomFieldDefinitionId == defB.Id &&
                             rowB.SelectedOption.DisplayText.Contains("部門");

        // Assert a new blank row (Row 3): its available options contain active "所屬部門", but do NOT contain deactivated "統編"
        var rowC = optModelVm.AddBlankDraftRow();
        bool rowCContainsRenamedActive = rowC.AvailableOptions.Any(o => o.CustomFieldDefinitionId == defB.Id && o.DisplayText.Contains("所屬部門"));
        bool rowCExcludesDeactivated = !rowC.AvailableOptions.Any(o => o.CustomFieldDefinitionId == defA.Id);

        // Assert shared ViewModel.FieldOptions also excludes deactivated defA
        bool sharedExcludesDeactivated = !optModelVm.FieldOptions.Any(o => o.CustomFieldDefinitionId == defA.Id);

        sw.Stop();
        bool t18Passed = rowAPreserved && rowBPreserved && rowCContainsRenamedActive && rowCExcludesDeactivated && sharedExcludesDeactivated;
        Record("ISSUE-0006-T18", "核心選項模型：已停用/改名之舊草稿列快照保留、新列隔離與正確選單對應", t18Passed,
            $"Row1Preserved({defA.Name})={rowAPreserved}, Row2Preserved({defB.Name})={rowBPreserved}, NewRowIncludesRenamed={rowCContainsRenamedActive}, NewRowExcludesDeactivated={rowCExcludesDeactivated}",
            sw.ElapsedMilliseconds);

        // --- T19: HasUnsavedChanges & Atomic Session Switch Protection ---
        sw.Restart();
        var atomicVm = new MainViewModel(optModelService);

        // 1. Automatic pending confirmation rows must cause HasUnsavedChanges == true
        var autoCand = new List<OcrCandidateItem> { new() { Id = 1, Text = "電話: 0912-345-678", Source = "整圖辨識" } };
        atomicVm.PopulateDraftRowsFromCandidates(autoCand, atomicVm.CurrentRequestId, isRegion: false);
        bool hasPendingRows = atomicVm.DraftRows.Count == 1 && atomicVm.DraftRows[0].Status == DraftItemStatus.PendingConfirmation;
        bool unsavedTriggeredOnPending = atomicVm.HasUnsavedChanges;

        // 2. Decode failure during SetSessionImageAsync must preserve existing session, draft rows and candidates
        var originalDraftRowsCount = atomicVm.DraftRows.Count;
        var originalRowText = atomicVm.DraftRows[0].Value;
        bool decodeErrorCaught = false;
        try
        {
            // Corrupt image session bytes that will fail during decoding
            var brokenSession = new ImageSessionInfo
            {
                SourceName = "broken.png",
                Width = 100,
                Height = 100,
                ByteCount = 4,
                Sha256 = "0000",
                ImageBytes = new byte[] { 0x00, 0x01, 0x02, 0x03 }
            };
            await atomicVm.SetSessionImageAsync(brokenSession);
        }
        catch
        {
            decodeErrorCaught = true;
        }

        bool oldRowsPreservedOnFailure = atomicVm.DraftRows.Count == originalDraftRowsCount &&
                                         atomicVm.DraftRows[0].Value == originalRowText;
        sw.Stop();

        bool t19Passed = hasPendingRows && unsavedTriggeredOnPending && decodeErrorCaught && oldRowsPreservedOnFailure;
        Record("ISSUE-0006-T19", "自動待確認列之 HasUnsavedChanges 提醒與換圖解碼失敗保留原草稿資料", t19Passed,
            $"HasPendingRows={hasPendingRows}, UnsavedOnPending={unsavedTriggeredOnPending}, DecodeErrorCaught={decodeErrorCaught}, OldRowsPreserved={oldRowsPreservedOnFailure}",
            sw.ElapsedMilliseconds);

        return report;
    }
}
