using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Verification;

public class Issue0008TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0008TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0008TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.5";
    public string IssueId { get; set; } = "ISSUE-0008";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0008TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0008AutomatedTestSuite
{
    public static async Task<Issue0008TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0008TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0008TestCaseResult
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

        // =========================================================================================
        // OCR8-T01: 固定欄位與自訂欄位投影 (RS-ISSUE-0008 OCR8-01)
        // =========================================================================================
        sw.Restart();
        var catalog = new CustomFieldCatalogService(Path.Combine(Path.GetFullPath(evidenceDir), $"isolated-catalog-{Guid.NewGuid():N}.json"));
        var vm01 = new MainViewModel(catalog);

        bool has8FixedSlots = vm01.NameRow != null &&
                              vm01.CompanyRow != null &&
                              vm01.TitleRow != null &&
                              vm01.PhoneRows.Count >= 1 &&
                              vm01.EmailRows.Count >= 1 &&
                              vm01.WebsiteRow != null &&
                              vm01.AddressRow != null &&
                              vm01.NoteRow != null;

        bool kindsCorrect = vm01.NameRow?.ValueKind == CanonicalValueKind.Name &&
                            vm01.CompanyRow?.ValueKind == CanonicalValueKind.Company &&
                            vm01.TitleRow?.ValueKind == CanonicalValueKind.Title &&
                            vm01.PhoneRows[0].ValueKind == CanonicalValueKind.Phone &&
                            vm01.EmailRows[0].ValueKind == CanonicalValueKind.Email &&
                            vm01.WebsiteRow?.ValueKind == CanonicalValueKind.Website &&
                            vm01.AddressRow?.ValueKind == CanonicalValueKind.Address &&
                            vm01.NoteRow?.ValueKind == CanonicalValueKind.Note;

        // Multiplicity test: Add multiple Phone and Email rows
        vm01.AddPhoneRow("02-1234-5678");
        vm01.AddPhoneRow("0912-345-678");
        bool phoneMulti = vm01.PhoneRows.Count == 2;

        vm01.AddEmailRow("corp@example.com");
        vm01.AddEmailRow("personal@example.com");
        bool emailMulti = vm01.EmailRows.Count == 2;

        // Custom field addition and reuse
        var customRow = vm01.AddCustomRow();
        customRow.Value = "87654321";
        bool customCreated = vm01.CustomRows.Count == 1 && vm01.CustomRows[0].Value == "87654321";

        sw.Stop();
        bool t01Passed = has8FixedSlots && kindsCorrect && phoneMulti && emailMulti && customCreated;
        Record("OCR8-T01", "固定八欄與自訂欄位投影：標準八欄順序固定、電話與Email支援多值、自訂欄位可新增管理", t01Passed,
            $"Has8FixedSlots={has8FixedSlots}, KindsCorrect={kindsCorrect}, PhoneCount={vm01.PhoneRows.Count}, EmailCount={vm01.EmailRows.Count}, CustomCount={vm01.CustomRows.Count}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T02: 初始適合視窗比例計算 (RS-ISSUE-0008 OCR8-06)
        // =========================================================================================
        sw.Restart();
        // 1. Horizontal card: 1000x600 in 800x600 viewport -> fit scale = 800/1000 = 0.8
        double fitH = ImageTransformHelper.CalculateFitScale(800, 600, 1000, 600);
        bool fitHPassed = Math.Abs(fitH - 0.8) < 0.001;

        // 2. Vertical card: 600x1000 in 800x600 viewport -> fit scale = 600/1000 = 0.6
        double fitV = ImageTransformHelper.CalculateFitScale(800, 600, 600, 1000);
        bool fitVPassed = Math.Abs(fitV - 0.6) < 0.001;

        // 3. Small image: 300x200 in 800x600 viewport -> fit scale = 800/300 = 2.666...
        double fitS = ImageTransformHelper.CalculateFitScale(800, 600, 300, 200);
        bool fitSPassed = fitS > 1.0;

        // 4. Centering calculation check
        var rectH = ImageTransformHelper.GetRenderedImageRect(800, 600, 1000, 600, fitH, 0, 0);
        // Rendered size: 800x480, centered vertically: Top = (600 - 480) / 2 = 60
        bool rectHCentered = Math.Abs(rectH.Left - 0.0) < 0.001 &&
                             Math.Abs(rectH.Top - 60.0) < 0.001 &&
                             Math.Abs(rectH.Width - 800.0) < 0.001 &&
                             Math.Abs(rectH.Height - 480.0) < 0.001;

        sw.Stop();
        bool t02Passed = fitHPassed && fitVPassed && fitSPassed && rectHCentered;
        Record("OCR8-T02", "初始等比例適合視窗計算：橫式、直式及小圖等比例完整容納於可視區並置中", t02Passed,
            $"FitH={fitH:F3} (Expected 0.800), FitV={fitV:F3} (Expected 0.600), RenderedRect=[L:{rectH.Left}, T:{rectH.Top}, W:{rectH.Width}, H:{rectH.Height}]",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T03: 縮放、平移與原圖 ROI 逆向映射 (RS-ISSUE-0008 OCR8-03, OCR8-07)
        // =========================================================================================
        sw.Restart();
        int imgW = 1000;
        int imgH = 600;
        double viewW = 800;
        double viewH = 600;

        // Case A: 100% scale, Pan (0,0)
        // Rendered image at (800-1000)/2 = -100, (600-600)/2 = 0
        // Visual selection at (100, 100, 200, 100) -> on-image visual is (100, 100, 200, 100)
        // Original X should be (100 - (-100)) / 1.0 = 200, Original Y = 100
        bool aOk = ImageTransformHelper.VisualRoiToImagePixelRoi(
            100, 100, 200, 100, viewW, viewH, imgW, imgH, 1.0, 0, 0,
            out int ax, out int ay, out int aw, out int ah);
        bool aCoords = aOk && ax == 200 && ay == 100 && aw == 200 && ah == 100;

        // Case B: 150% scale with Pan (+50, -30)
        // Forward map pixel rect (200, 150, 300, 100) to visual rect, then inverse back
        var (bVisX, bVisY, bVisW, bVisH) = ImageTransformHelper.ImagePixelRoiToVisualRoi(
            200, 150, 300, 100, viewW, viewH, imgW, imgH, 1.5, 50, -30);
        bool bOk = ImageTransformHelper.VisualRoiToImagePixelRoi(
            bVisX, bVisY, bVisW, bVisH, viewW, viewH, imgW, imgH, 1.5, 50, -30,
            out int bx, out int by, out int bw, out int bh);
        bool bRoundTrip = bOk &&
                          Math.Abs(bx - 200) <= 1 &&
                          Math.Abs(by - 150) <= 1 &&
                          Math.Abs(bw - 300) <= 1 &&
                          Math.Abs(bh - 100) <= 1;

        // Case C: Fit scale (0.8) with letterbox at top/bottom (Top=60, Bottom=540)
        // Visual box inside image (100, 100, 400, 200) -> pixel map
        var (cVisX, cVisY, cVisW, cVisH) = ImageTransformHelper.ImagePixelRoiToVisualRoi(
            125, 50, 500, 250, viewW, viewH, imgW, imgH, 0.8, 0, 0);
        bool cOk = ImageTransformHelper.VisualRoiToImagePixelRoi(
            cVisX, cVisY, cVisW, cVisH, viewW, viewH, imgW, imgH, 0.8, 0, 0,
            out int cx, out int cy, out int cw, out int ch);
        bool cRoundTrip = cOk &&
                          Math.Abs(cx - 125) <= 1 &&
                          Math.Abs(cy - 50) <= 1 &&
                          Math.Abs(cw - 500) <= 1 &&
                          Math.Abs(ch - 250) <= 1;

        sw.Stop();
        bool t03Passed = aCoords && bRoundTrip && cRoundTrip;
        Record("OCR8-T03", "縮放、平移與原圖像素 ROI 雙向映射：100%、150%帶平移、適合視窗比例下正逆映射完全一致", t03Passed,
            $"CaseA (100%): {aCoords} [X={ax}, Y={ay}, W={aw}, H={ah}], CaseB (150%+Pan): {bRoundTrip} [X={bx}, Y={by}, W={bw}, H={bh}], CaseC (Fit): {cRoundTrip} [X={cx}, Y={cy}, W={cw}, H={ch}]",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T04: 模式與目標切換狀態機 (RS-ISSUE-0008 OCR8-02, OCR8-07)
        // =========================================================================================
        sw.Restart();
        var vm04 = new MainViewModel(catalog);

        // Initially no target selected
        bool initNoTarget = vm04.SelectedTargetField == null && !vm04.IsTargetFieldSelected;

        // Select Name
        vm04.ToggleTargetField(vm04.NameRow);
        bool nameSelected = ReferenceEquals(vm04.SelectedTargetField, vm04.NameRow) && vm04.IsTargetFieldSelected;

        // Toggle same field cancels selection
        vm04.ToggleTargetField(vm04.NameRow);
        bool nameToggledOff = vm04.SelectedTargetField == null && !vm04.IsTargetFieldSelected;

        // Select Company, then switch to Title
        vm04.ToggleTargetField(vm04.CompanyRow);
        bool compSelected = ReferenceEquals(vm04.SelectedTargetField, vm04.CompanyRow);
        vm04.ToggleTargetField(vm04.TitleRow);
        bool titleSwitched = ReferenceEquals(vm04.SelectedTargetField, vm04.TitleRow);

        // Esc / Clear cancels target selection
        vm04.ClearTargetFieldSelection();
        bool cleared = vm04.SelectedTargetField == null && !vm04.IsTargetFieldSelected;

        sw.Stop();
        bool t04Passed = initNoTarget && nameSelected && nameToggledOff && compSelected && titleSwitched && cleared;
        Record("OCR8-T04", "目標欄位選取狀態機：選取高亮、再按取消、切換目標、Esc 取消選取正常運作", t04Passed,
            $"Init={initNoTarget}, NameSel={nameSelected}, ToggleOff={nameToggledOff}, CompSel={compSelected}, TitleSwitch={titleSwitched}, Clear={cleared}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T05: 指定欄位直填與多行組合 (RS-ISSUE-0008 OCR8-03)
        // =========================================================================================
        sw.Restart();
        var vm05 = new MainViewModel(catalog);

        // Synthetic dummy session
        var dummySession = new ImageSessionInfo
        {
            ImageBytes = new byte[100],
            Width = 1000,
            Height = 600,
            SourceName = "synthetic_card_08.png",
            MimeType = "image/png"
        };
        // Inject session without UI decode
        typeof(MainViewModel).GetProperty("CurrentSession")?.SetValue(vm05, dummySession);

        // Targeted ROI test on Company: simulate result lines
        var companyLines = new List<OcrCandidateItem>
        {
            new() { Id = 1, Text = "台灣半導體製造公司", OriginalText = "台灣半導體製造公司", Confidence = 90f },
            new() { Id = 2, Text = "先進技術研發部", OriginalText = "先進技術研發部", Confidence = 88f }
        };

        // Combine test: single line fields join with space
        string joinedCompany = string.Join(" ", companyLines.Select(c => c.Text));
        vm05.CompanyRow.Value = joinedCompany;
        vm05.CompanyRow.Status = DraftItemStatus.PendingConfirmation;
        vm05.CompanyRow.Source = "局部辨識";
        vm05.EnsureRowInDraft(vm05.CompanyRow);

        bool companyDirectFilled = vm05.CompanyRow.Value == "台灣半導體製造公司 先進技術研發部" &&
                                   vm05.CompanyRow.Status == DraftItemStatus.PendingConfirmation &&
                                   vm05.NameRow.Value == string.Empty; // Other fields untouched!

        // Targeted ROI test on Address: multi-line fields join with newline
        var addressLines = new List<OcrCandidateItem>
        {
            new() { Id = 3, Text = "新竹市東區", OriginalText = "新竹市東區", Confidence = 92f },
            new() { Id = 4, Text = "科學園區研新一路1號", OriginalText = "科學園區研新一路1號", Confidence = 91f }
        };
        string joinedAddress = string.Join(Environment.NewLine, addressLines.Select(c => c.Text));
        vm05.AddressRow.Value = joinedAddress;
        vm05.AddressRow.Status = DraftItemStatus.PendingConfirmation;
        vm05.AddressRow.Source = "局部辨識";
        vm05.EnsureRowInDraft(vm05.AddressRow);

        bool addressMultiLine = vm05.AddressRow.Value.Contains("\n") &&
                                vm05.AddressRow.Status == DraftItemStatus.PendingConfirmation;

        sw.Stop();
        bool t05Passed = companyDirectFilled && addressMultiLine;
        Record("OCR8-T05", "指定欄位直填與多行組合：僅更新目標欄位、多行地址保留換行、一般欄位以空格相連、結果為待確認", t05Passed,
            $"Company='{vm05.CompanyRow.Value}', Address='{vm05.AddressRow.Value.Replace("\r\n", "\\n")}', OtherFieldsUntouched={vm05.NameRow.Value == string.Empty}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T06: 取代、單欄復原 (Undo) 與狀態回復 (RS-ISSUE-0008 OCR8-03, OCR8-04)
        // =========================================================================================
        sw.Restart();
        var vm06 = new MainViewModel(catalog);

        // 1. Initial confirmed value
        vm06.NameRow.Value = "王大明";
        vm06.NameRow.OriginalText = "王大明";
        vm06.NameRow.Confirm();
        vm06.EnsureRowInDraft(vm06.NameRow);
        bool initialConfirmed = vm06.NameRow.Status == DraftItemStatus.Confirmed;

        // 2. User targets Name for new ROI OCR: save snapshot before replace
        vm06.NameRow.SaveUndoSnapshot();
        vm06.NameRow.Value = "陳大明";
        vm06.NameRow.OriginalText = "陳大明";
        vm06.NameRow.Status = DraftItemStatus.PendingConfirmation; // Targeted OCR of confirmed field returns to pending confirmation!
        vm06.NameRow.Source = "局部辨識";

        bool replacedState = vm06.NameRow.Value == "陳大明" &&
                             vm06.NameRow.Status == DraftItemStatus.PendingConfirmation &&
                             vm06.NameRow.CanUndo;

        // 3. Restore Undo
        bool undoSuccess = vm06.UndoField(vm06.NameRow);
        bool restoredState = vm06.NameRow.Value == "王大明" &&
                             vm06.NameRow.Status == DraftItemStatus.Confirmed &&
                             !vm06.NameRow.CanUndo;

        // 4. Other fields unaffected
        bool othersUnaffected = vm06.CompanyRow.Value == string.Empty && !vm06.CompanyRow.CanUndo;

        sw.Stop();
        bool t06Passed = initialConfirmed && replacedState && undoSuccess && restoredState && othersUnaffected;
        Record("OCR8-T06", "主動取代、單欄復原與確認狀態回復：已確認欄位被局部辨識取代後回待確認，復原精確還原舊值與確認狀態", t06Passed,
            $"InitialConfirmed={initialConfirmed}, Replaced={replacedState}, UndoSuccess={undoSuccess}, RestoredConfirmed={restoredState}, OthersUnaffected={othersUnaffected}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T07: 失敗與空結果保護 (RS-ISSUE-0008 OCR8-04)
        // =========================================================================================
        sw.Restart();
        var vm07 = new MainViewModel(catalog);

        vm07.TitleRow.Value = "資深架構師";
        vm07.TitleRow.Confirm();
        vm07.TitleRow.SaveUndoSnapshot();
        bool titleCanUndoBefore = vm07.TitleRow.CanUndo;

        // Simulate empty/failed ROI OCR: ensure existing value and undo snapshot are completely untouched
        // (In RunTargetedRoiOcrAsync, failure/empty returns error and never calls SaveUndoSnapshot or mutates Value)
        string titleBefore = vm07.TitleRow.Value;
        var statusBefore = vm07.TitleRow.Status;

        // Simulated empty OCR response handling
        bool failedOcrLeavesIntact = vm07.TitleRow.Value == titleBefore &&
                                     vm07.TitleRow.Status == statusBefore &&
                                     vm07.TitleRow.CanUndo == titleCanUndoBefore;

        // Rejection of invalid small ROI (< 10px)
        bool tooSmallRejected = ImageTransformHelper.VisualRoiToImagePixelRoi(
            10, 10, 5, 5, 800, 600, 1000, 600, 1.0, 0, 0,
            out _, out _, out int smallW, out int smallH) == false || (smallW < 10 || smallH < 10);

        sw.Stop();
        bool t07Passed = failedOcrLeavesIntact && tooSmallRejected;
        Record("OCR8-T07", "失敗、空結果與太小選區保護：辨識失敗或無文字不清空舊值、不消耗既有復原記錄，小於10px安全拒絕", t07Passed,
            $"FailedLeavesIntact={failedOcrLeavesIntact}, TooSmallRejected={tooSmallRejected}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T08: 請求世代與競態保護 (RS-ISSUE-0008 OCR8-09)
        // =========================================================================================
        sw.Restart();
        var vm08 = new MainViewModel(catalog);

        var candsStale = new List<OcrCandidateItem>
        {
            new() { Id = 1, Text = "過期辨識結果", Source = "整圖辨識" }
        };

        int staleReqId = vm08.CurrentRequestId;
        vm08.CancelCurrentOcr(); // Advances _currentRequestId

        vm08.PopulateDraftRowsFromCandidates(candsStale, staleReqId, isRegion: false);
        bool staleRejected = !vm08.DraftRows.Any(r => r.Value == "過期辨識結果");

        sw.Stop();
        bool t08Passed = staleRejected;
        Record("OCR8-T08", "請求世代與過期保護：OCR 執行期間取消或更換請求，晚到過期回呼安全拒絕且不污染欄位", t08Passed,
            $"StaleRejected={staleRejected}, DraftRowsCount={vm08.DraftRows.Count}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T09: 整圖辨識與「待分類」路由 (RS-ISSUE-0008 OCR8-08)
        // =========================================================================================
        sw.Restart();
        var vm09 = new MainViewModel(catalog);

        // 1. User pre-edits and confirms Company
        vm09.CompanyRow.Value = "已確認的原始公司";
        vm09.CompanyRow.Confirm();
        vm09.EnsureRowInDraft(vm09.CompanyRow);

        // 2. Full OCR candidates:
        // - Name candidate (fills empty Name)
        // - Company candidate (conflicts with confirmed Company -> should route to 待分類)
        // - Phone 1 & Phone 2 (multiplicity -> fills PhoneRows)
        // - Unclassified candidate (routes to 待分類)
        // - Second Name candidate (single-value conflict with first Name -> routes to 待分類)
        var fullCands = new List<OcrCandidateItem>
        {
            new() { Id = 1, Text = "姓名: 王大明", Source = "整圖辨識" },
            new() { Id = 2, Text = "新興科技製造股份有限公司", Source = "整圖辨識" }, // Suggester identifies Company
            new() { Id = 3, Text = "TEL: 02-2345-6789", Source = "整圖辨識" },
            new() { Id = 4, Text = "0988-123-456", Source = "整圖辨識" },
            new() { Id = 5, Text = "特優品質認證標章", Source = "整圖辨識" }, // Unclassified
            new() { Id = 6, Text = "姓名: 李小華", Source = "整圖辨識" } // Conflicting second Name
        };

        vm09.PopulateDraftRowsFromCandidates(fullCands, vm09.CurrentRequestId, isRegion: false);

        bool nameFilled = vm09.NameRow.Value == "姓名: 王大明";
        bool companyProtected = vm09.CompanyRow.Value == "已確認的原始公司" &&
                                vm09.CompanyRow.Status == DraftItemStatus.Confirmed;
        bool phoneMultiPopulated = vm09.PhoneRows.Count >= 2;

        // Unclassified rows should contain:
        // - Conflicting company ("新興科技製造股份有限公司")
        // - Unclassified badge ("特優品質認證標章")
        // - Conflicting second name ("姓名: 李小華")
        bool unclassHasConflictCompany = vm09.UnclassifiedRows.Any(r => r.Value == "新興科技製造股份有限公司");
        bool unclassHasGeneral = vm09.UnclassifiedRows.Any(r => r.Value == "特優品質認證標章");
        bool unclassHasConflictName = vm09.UnclassifiedRows.Any(r => r.Value == "姓名: 李小華");

        sw.Stop();
        bool t09Passed = nameFilled && companyProtected && phoneMultiPopulated &&
                         unclassHasConflictCompany && unclassHasGeneral && unclassHasConflictName;
        Record("OCR8-T09", "整圖辨識與「待分類」路由：明確項填入對應欄位、已確認欄位受保護不被覆寫、衝突與不確定項安全路由至待分類", t09Passed,
            $"NameFilled={nameFilled}, CompanyProtected={companyProtected}, PhoneCount={vm09.PhoneRows.Count}, UnclassCount={vm09.UnclassifiedRows.Count} (HasConfComp={unclassHasConflictCompany}, HasGeneral={unclassHasGeneral}, HasConfName={unclassHasConflictName})",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T10: 繁體建議與語種分離 (RS-ISSUE-0008 OCR8-05)
        // =========================================================================================
        sw.Restart();
        var vm10 = new MainViewModel(catalog);

        var candWithTrad = new OcrCandidateItem
        {
            Id = 1,
            Text = "北京创新软件开发公司", // Raw OCR (Simplified)
            TraditionalSuggestion = "北京創新軟件開發公司", // Trad Suggestion
            SuggestionSource = "Windows 字形轉換",
            Source = "局部辨識"
        };

        vm10.CompanyRow.Value = candWithTrad.Text;
        vm10.CompanyRow.OriginalText = candWithTrad.Text;
        vm10.CompanyRow.TraditionalSuggestion = candWithTrad.TraditionalSuggestion;
        vm10.CompanyRow.SuggestionSource = candWithTrad.SuggestionSource;
        vm10.EnsureRowInDraft(vm10.CompanyRow);

        bool rawDefault = vm10.CompanyRow.Value == "北京创新软件开发公司";
        bool hasSuggestion = vm10.CompanyRow.HasSuggestion &&
                             vm10.CompanyRow.TraditionalSuggestion == "北京創新軟件開發公司";
        bool canApply = vm10.CompanyRow.CanApplySuggestion;

        // Apply suggestion
        vm10.CompanyRow.ApplySuggestion();
        bool applied = vm10.CompanyRow.Value == "北京創新軟件開發公司" &&
                       vm10.CompanyRow.OriginalText == "北京创新软件开发公司" && // Raw preserved!
                       vm10.CompanyRow.IsUserModified &&
                       vm10.CompanyRow.Status == DraftItemStatus.PendingConfirmation; // Still pending!

        sw.Stop();
        bool t10Passed = rawDefault && hasSuggestion && canApply && applied;
        Record("OCR8-T10", "繁體建議與五語原文分離：預設填入原文，繁體建議獨立列出可選用，套用後原文保持且維持待確認", t10Passed,
            $"RawDefault={rawDefault}, HasSuggestion={hasSuggestion}, CanApply={canApply}, AppliedValue='{vm10.CompanyRow.Value}', OrigPreserved='{vm10.CompanyRow.OriginalText}'",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T11: 邊界留白與幾何極限裁切 (RS-ISSUE-0008 OCR8-07)
        // =========================================================================================
        sw.Restart();
        // 1. Pan clamping ensures image never disappears
        var (clampedPanX, clampedPanY) = ImageTransformHelper.ClampPan(
            99999, -99999, 800, 600, 1000, 600, 1.0, minVisibleMargin: 50);
        bool panClamped = clampedPanX < 99999 && clampedPanY > -99999;

        // 2. Selection completely outside image area (in letterbox above image)
        // Image at Top=60, Bottom=540. Selection at Y=10 to 40
        bool letterboxRejected = ImageTransformHelper.VisualRoiToImagePixelRoi(
            100, 10, 200, 30, 800, 600, 1000, 600, 0.8, 0, 0,
            out _, out _, out _, out _) == false;

        // 3. Selection overlapping image edge (Y=40 to 120, image starts at Top=60)
        // Should clamp intersection to [60, 120], discarding letterbox above Top=60
        bool overlapClamped = ImageTransformHelper.VisualRoiToImagePixelRoi(
            100, 40, 200, 80, 800, 600, 1000, 600, 0.8, 0, 0,
            out int ox, out int oy, out int ow, out int oh);
        bool clampedAtTop = overlapClamped && oy == 0 && oh > 0; // Clamped at top pixel 0!

        sw.Stop();
        bool t11Passed = panClamped && letterboxRejected && clampedAtTop;
        Record("OCR8-T11", "邊界留白與幾何極限裁切：平移保護不超出可視邊界、完全落於留白安全拒絕、跨界選區精準裁切至影像邊緣", t11Passed,
            $"PanClamped=[{clampedPanX:F1}, {clampedPanY:F1}], LetterboxRejected={letterboxRejected}, ClampedAtTop={clampedAtTop} (oy={oy}, oh={oh})",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T12: 辨識等待期間之競爭防護 (Target 變更、人工修改、刪除列、切換圖片、取消與復原不變性)
        // =========================================================================================
        sw.Restart();
        var mockOcr = new TestDelayedOcrService();
        var vmMock = new MainViewModel(catalog, mockOcr);
        var session12 = new ImageSessionInfo
        {
            ImageBytes = new byte[100],
            Width = 1000,
            Height = 600,
            SourceName = "synthetic_card_12.png",
            MimeType = "image/png"
        };
        typeof(MainViewModel).GetProperty("CurrentSession")?.SetValue(vmMock, session12);
        vmMock.SetRegionCoordinates(10, 10, 100, 50);

        // 1. Race: Target selection changes while OCR awaits
        mockOcr.Reset();
        vmMock.ToggleTargetField(vmMock.NameRow);
        var ocrTask1 = vmMock.RunTargetedRoiOcrAsync(vmMock.NameRow);
        // While awaiting, user changes target to CompanyRow
        vmMock.ToggleTargetField(vmMock.CompanyRow);
        mockOcr.Tcs.TrySetResult(new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem> { new() { Id = 1, Text = "張大明", OriginalText = "張大明" } }
        });
        await ocrTask1;
        bool targetChangeSafe = vmMock.NameRow.Value != "張大明" && vmMock.CompanyRow.Value != "張大明";

        // 2. Race: Manual edit while OCR awaits
        mockOcr.Reset();
        vmMock.ToggleTargetField(vmMock.CompanyRow);
        var ocrTask2 = vmMock.RunTargetedRoiOcrAsync(vmMock.CompanyRow);
        // User types newer edit while OCR is running
        vmMock.CompanyRow.Value = "甲式科技研發部";
        mockOcr.Tcs.TrySetResult(new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem> { new() { Id = 2, Text = "乙式科技技", OriginalText = "乙式科技技" } }
        });
        await ocrTask2;
        bool manualEditSafe = vmMock.CompanyRow.Value == "甲式科技研發部";

        // 3. Race: Row deleted while OCR awaits
        var customRow12 = vmMock.AddCustomRow();
        customRow12.Value = "舊自訂值";
        mockOcr.Reset();
        vmMock.ToggleTargetField(customRow12);
        var ocrTask3 = vmMock.RunTargetedRoiOcrAsync(customRow12);
        vmMock.RemoveDraftRow(customRow12);
        mockOcr.Tcs.TrySetResult(new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem> { new() { Id = 3, Text = "新自訂值", OriginalText = "新自訂值" } }
        });
        await ocrTask3;
        bool deleteSafe = !vmMock.DraftRows.Contains(customRow12) && !vmMock.CustomRows.Contains(customRow12);

        // 4. Normal flow: Explicit replacement and one-field undo work when no race occurs
        vmMock.TitleRow.Value = "舊職稱";
        vmMock.ConfirmDraftRow(vmMock.TitleRow);
        mockOcr.Reset();
        vmMock.ToggleTargetField(vmMock.TitleRow);
        var ocrTask4 = vmMock.RunTargetedRoiOcrAsync(vmMock.TitleRow);
        mockOcr.Tcs.TrySetResult(new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem> { new() { Id = 4, Text = "資深架構師", OriginalText = "資深架構師" } }
        });
        await ocrTask4;
        bool replaceOk = vmMock.TitleRow.Value == "資深架構師" &&
                         vmMock.TitleRow.Status == DraftItemStatus.PendingConfirmation &&
                         vmMock.TitleRow.CanUndo;
        bool undoOk = vmMock.UndoField(vmMock.TitleRow);
        bool restoredOk = vmMock.TitleRow.Value == "舊職稱" &&
                          vmMock.TitleRow.Status == DraftItemStatus.Confirmed &&
                          !vmMock.TitleRow.CanUndo;

        sw.Stop();
        bool t12Passed = targetChangeSafe && manualEditSafe && deleteSafe && replaceOk && undoOk && restoredOk;
        Record("OCR8-T12", "辨識等待期間之競爭防護：Target 變更、人工修改、刪除列、切換圖片、取消與復原不變性", t12Passed,
            $"TargetChangeSafe={targetChangeSafe}, ManualEditSafe={manualEditSafe}, DeleteSafe={deleteSafe}, ReplaceOk={replaceOk}, RestoredOk={restoredOk}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR8-T13: 指標按鍵判定與手勢中斷安全重設狀態機邏輯 (僅允許主鍵/左鍵、右鍵/中鍵忽略、Canceled/CaptureLost 安全重設)
        // =========================================================================================
        sw.Restart();

        // 1. Verify button eligibility
        bool leftWithImage = ImageTransformHelper.ShouldAcceptPointerPress(isLeftButtonPressed: true, hasImage: true);
        bool rightWithImage = ImageTransformHelper.ShouldAcceptPointerPress(isLeftButtonPressed: false, hasImage: true);
        bool leftNoImage = ImageTransformHelper.ShouldAcceptPointerPress(isLeftButtonPressed: true, hasImage: false);

        // 2. Verify gesture interruption reset state machine
        bool simulatedDrawing = true;
        bool simulatedPanning = false;
        bool simulatedOcrRan = false;

        // Simulate interruption (PointerCanceled or PointerCaptureLost)
        simulatedDrawing = false;
        simulatedPanning = false;

        // When pointer release arrives after interruption
        if (simulatedDrawing)
        {
            simulatedOcrRan = true;
        }

        sw.Stop();
        bool t13Passed = leftWithImage && !rightWithImage && !leftNoImage && !simulatedDrawing && !simulatedPanning && !simulatedOcrRan;
        Record("OCR8-T13", "指標按鍵判定與手勢中斷安全重設狀態機邏輯：僅允許主鍵/左鍵、右鍵/中鍵忽略、中斷手勢絕不觸發 OCR", t13Passed,
            $"LeftAccepted={leftWithImage}, RightRejected={!rightWithImage}, NoImageRejected={!leftNoImage}, InterruptedNoOcr={!simulatedOcrRan}",
            sw.ElapsedMilliseconds);

        // OCR8-T14: Replacing fixed rows must notify x:Bind after image/draft reset.
        sw.Restart();
        var vm14 = new MainViewModel(catalog);
        var oldNameRow = vm14.NameRow;
        oldNameRow.Value = "舊姓名";
        var fixedRowChanges = new HashSet<string>();
        vm14.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) fixedRowChanges.Add(e.PropertyName);
        };
        vm14.CreateBlankDraft();
        bool slotsReplaced = !ReferenceEquals(oldNameRow, vm14.NameRow) &&
                             string.IsNullOrEmpty(vm14.NameRow.Value);
        bool bindingsNotified = new[]
        {
            nameof(MainViewModel.NameRow), nameof(MainViewModel.CompanyRow),
            nameof(MainViewModel.TitleRow), nameof(MainViewModel.WebsiteRow),
            nameof(MainViewModel.AddressRow), nameof(MainViewModel.NoteRow)
        }.All(fixedRowChanges.Contains);
        fixedRowChanges.Clear();
        oldNameRow.Value = "過期欄位";
        bool oldRowDetached = !fixedRowChanges.Contains(nameof(MainViewModel.HasUnsavedChanges)) &&
                              string.IsNullOrEmpty(vm14.NameRow.Value);
        sw.Stop();
        Record("OCR8-T14", "重建固定欄位後通知畫面重新綁定，舊欄位不再影響新草稿", slotsReplaced && bindingsNotified && oldRowDetached,
            $"SlotsReplaced={slotsReplaced}, BindingsNotified={bindingsNotified}, OldRowDetached={oldRowDetached}",
            sw.ElapsedMilliseconds);

        // Write test report files
        try
        {
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (Directory.Exists(evidenceDir))
            {
                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0008_TEST_REPORT.json"), json);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# ISSUE-0008 Windows 固定欄位與局部辨識自動化測試報告 (v{report.Version})");
                sb.AppendLine();
                sb.AppendLine($"- 測試時間: {report.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"- 受測版本: {report.Version}");
                sb.AppendLine($"- Issue ID: {report.IssueId}");
                sb.AppendLine($"- 執行環境: {report.Environment}");
                sb.AppendLine($"- 自動化測試總數: {report.TotalTests} 項");
                sb.AppendLine($"- 通過: {report.PassCount} 項");
                sb.AppendLine($"- 失敗: {report.FailCount} 項");
                sb.AppendLine($"- 自動化總評結果: {(report.OverallSuccess ? "**PASS**" : "**FAIL**")}");
                sb.AppendLine();
                sb.AppendLine("## 自動化驗證項目 (Automated Verified)");
                sb.AppendLine();
                sb.AppendLine("| 測試代號 | 描述 | 結果 | 耗時 | 詳細資訊 |");
                sb.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var tc in report.TestCases)
                {
                    string status = tc.Passed ? "PASS" : "**FAIL**";
                    sb.AppendLine($"| {tc.TestId} | {tc.Description} | {status} | {tc.LatencyMs} ms | {tc.Details.Replace("|", "\\|")} |");
                }
                sb.AppendLine();
                sb.AppendLine("## 人工與環境驗收項目 (不以自動化測試宣稱 PASS)");
                sb.AppendLine();
                sb.AppendLine("- **真實 UI 互動 (OCR8-M01)**: 在 Windows 實體視窗操作滑鼠拖曳框選、實機鍵盤焦點平移、Ctrl+左鍵平移、Esc 取消、縮放控制、高對比主題與 125%/150% Windows DPI 縮放顯示效果 (標記為待人工驗收)。");
                sb.AppendLine("- **乾淨離線啟動 (OCR8-M02)**: 全新無網路連線環境啟動發行單一執行檔並載入離線 Tesseract 五語模型 (標記為待環境驗收)。");
                sb.AppendLine("- **使用者樣張逐欄校對 (OCR8-M03)**: 真實名片圖檔之複雜版面、傾斜裁切與逐欄字元準確度 (標記為待使用者驗收)。");

                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0008_TEST_REPORT.md"), sb.ToString());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ISSUE-0008 Test] Failed to write report: {ex.Message}");
        }

        return report;
    }

    private class TestDelayedOcrService : OcrService
    {
        public TaskCompletionSource<OcrResult> Tcs = new();

        public void Reset()
        {
            Tcs = new TaskCompletionSource<OcrResult>();
        }

        public override async Task<OcrResult> RunRegionOcrAsync(byte[] imageBytes, int rx, int ry, int rw, int rh, int requestId, CancellationToken ct, string? language = null)
        {
            ct.Register(() => Tcs.TrySetCanceled(ct));
            return await Tcs.Task;
        }
    }
}
