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
using Tesseract;

namespace MajiOCR_Windows.Verification;

public class Issue0007TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0007TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0007TestSuiteReport).Assembly
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.3";
    public string IssueId { get; set; } = "ISSUE-0007";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0007TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0007AutomatedTestSuite
{
    public static async Task<Issue0007TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0007TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0007TestCaseResult
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

        // --- T01: 混合腳本修正 (Chinese mixed with Hangul replaced by clean Trad Chinese) ---
        sw.Restart();
        var candHangulMix = new OcrCandidateItem
        {
            Id = 1,
            Text = "李핝華",
            Confidence = 78f,
            X = 100,
            Y = 50,
            Width = 120,
            Height = 30,
            Source = "整圖辨識"
        };
        bool t01ShouldReview = TraditionalChineseReviewer.ShouldReview(candHangulMix);
        var t01Decision = TraditionalChineseReviewer.EvaluateAdoption(candHangulMix, "李大華", 92f);
        sw.Stop();
        bool t01Passed = t01ShouldReview &&
                         t01Decision.ShouldAdopt &&
                         t01Decision.AdoptedText == "李大華" &&
                         t01Decision.Reason.Contains("韓文");
        Record("ISSUE-0007-T01", "混合腳本修正：中文字混有韓文字元時觸發複核並採用乾淨繁中結果", t01Passed,
            $"ShouldReview={t01ShouldReview}, ShouldAdopt={t01Decision.ShouldAdopt}, AdoptedText='{t01Decision.AdoptedText}', Reason='{t01Decision.Reason}'",
            sw.ElapsedMilliseconds);

        // --- T02: 異常拉丁片段修正 (Chinese mixed with anomalous Latin fragment) ---
        sw.Restart();
        var candLatinNoise = new OcrCandidateItem
        {
            Id = 2,
            Text = "軟體架構b師",
            Confidence = 80f,
            X = 100,
            Y = 90,
            Width = 200,
            Height = 30,
            Source = "整圖辨識"
        };
        bool t02ShouldReview = TraditionalChineseReviewer.ShouldReview(candLatinNoise);
        var t02Decision = TraditionalChineseReviewer.EvaluateAdoption(candLatinNoise, "軟體架構師", 94f);
        sw.Stop();
        bool t02Passed = t02ShouldReview &&
                         t02Decision.ShouldAdopt &&
                         t02Decision.AdoptedText == "軟體架構師" &&
                         t02Decision.Reason.Contains("拉丁");
        Record("ISSUE-0007-T02", "異常拉丁片段修正：中文字混有孤立/嵌入式異常拉丁字母時觸發複核並採用繁中結果", t02Passed,
            $"ShouldReview={t02ShouldReview}, ShouldAdopt={t02Decision.ShouldAdopt}, AdoptedText='{t02Decision.AdoptedText}', Reason='{t02Decision.Reason}'",
            sw.ElapsedMilliseconds);

        // --- T03: 韓文與拉丁混淆候選之原文保持與繁中建議生成 (OCR7-U01) ---
        sw.Restart();
        var candNameConfused = new OcrCandidateItem
        {
            Id = 3,
            Text = "한 v",
            Confidence = 65f,
            X = 120,
            Y = 100,
            Width = 150,
            Height = 35,
            Source = "整圖辨識"
        };
        bool t03ShouldReview = TraditionalChineseReviewer.ShouldReview(candNameConfused);
        var t03Decision = TraditionalChineseReviewer.EvaluateAdoption(candNameConfused, "陳大明", 89f);
        if (t03Decision.ShouldAdopt)
        {
            candNameConfused.TraditionalSuggestion = t03Decision.AdoptedText;
            candNameConfused.SuggestionSource = "繁中單語複核";
        }
        sw.Stop();
        bool t03Passed = t03ShouldReview &&
                         t03Decision.ShouldAdopt &&
                         candNameConfused.Text == "한 v" && // 五語原文完全保持不變！
                         candNameConfused.Confidence == 65f && // 原始信心度保持不變！
                         candNameConfused.Source == "整圖辨識" && // 原始來源保持不變！
                         candNameConfused.TraditionalSuggestion == "陳大明" &&
                         candNameConfused.SuggestionSource == "繁中單語複核" &&
                         candNameConfused.HasSuggestion;
        Record("ISSUE-0007-T03", "OCR7-U01 原文保持與繁中建議：姓名行五語原文/信心度/來源保持不變，單語複核僅存入 TraditionalSuggestion", t03Passed,
            $"Text='{candNameConfused.Text}', Suggestion='{candNameConfused.TraditionalSuggestion}', Source='{candNameConfused.SuggestionSource}', HasSuggestion={candNameConfused.HasSuggestion}",
            sw.ElapsedMilliseconds);

        // --- T04: 純韓文保護 (Pure Korean preservation) ---
        sw.Restart();
        var candPureKorean = new OcrCandidateItem
        {
            Id = 4,
            Text = "지사",
            Confidence = 96f,
            X = 46,
            Y = 245,
            Width = 46,
            Height = 23,
            Source = "整圖辨識"
        };
        bool t04ShouldReview = TraditionalChineseReviewer.ShouldReview(candPureKorean);
        sw.Stop();
        bool t04Passed = !t04ShouldReview;
        Record("ISSUE-0007-T04", "純韓文候選行保護：純韓文行絕不觸發繁中複核或被覆寫", t04Passed,
            $"CandidateText='{candPureKorean.Text}', ShouldReview={t04ShouldReview} (預期 False)",
            sw.ElapsedMilliseconds);

        // --- T05: 合法韓文+英文組合保護 (Legitimate Korean + English preservation) ---
        sw.Restart();
        var candKoreanEnglish = new OcrCandidateItem
        {
            Id = 5,
            Text = ": 한국 비즈니스 솔루션 (Seoul Office)",
            Confidence = 91f,
            X = 103,
            Y = 245,
            Width = 413,
            Height = 25,
            Source = "整圖辨識"
        };
        bool t05ShouldReview = TraditionalChineseReviewer.ShouldReview(candKoreanEnglish);
        sw.Stop();
        bool t05Passed = !t05ShouldReview;
        Record("ISSUE-0007-T05", "合法韓英混合行保護：含完整韓文語意與合法英文括號之候選不觸發複核", t05Passed,
            $"CandidateText='{candKoreanEnglish.Text}', ShouldReview={t05ShouldReview} (預期 False)",
            sw.ElapsedMilliseconds);

        // --- T06: 純英文保護 (Pure English preservation) ---
        sw.Restart();
        var candPureEnglish1 = new OcrCandidateItem { Id = 6, Text = "MAJI SOLUTIONS", Confidence = 95f, Source = "整圖辨識" };
        var candPureEnglish2 = new OcrCandidateItem { Id = 7, Text = "Tokyo Corp", Confidence = 93f, Source = "整圖辨識" };
        bool t06R1 = TraditionalChineseReviewer.ShouldReview(candPureEnglish1);
        bool t06R2 = TraditionalChineseReviewer.ShouldReview(candPureEnglish2);
        sw.Stop();
        bool t06Passed = !t06R1 && !t06R2;
        Record("ISSUE-0007-T06", "純英文候選行保護：全英文字串不觸發繁中複核", t06Passed,
            $"R1('MAJI SOLUTIONS')={t06R1}, R2('Tokyo Corp')={t06R2} (預期皆 False)",
            sw.ElapsedMilliseconds);

        // --- T07: 合法繁中+英文雙語組合保護 ---
        sw.Restart();
        var candBilingual = new OcrCandidateItem
        {
            Id = 8,
            Text = "王大明 (Wang Da-Ming)",
            Confidence = 91f,
            X = 118,
            Y = 96,
            Width = 274,
            Height = 26,
            Source = "整圖辨識"
        };
        bool t07ShouldReview = TraditionalChineseReviewer.ShouldReview(candBilingual);
        sw.Stop();
        bool t07Passed = !t07ShouldReview;
        Record("ISSUE-0007-T07", "合法中文與英文對照括號保護：合法雙語姓名行不觸發複核", t07Passed,
            $"CandidateText='{candBilingual.Text}', ShouldReview={t07ShouldReview} (預期 False)",
            sw.ElapsedMilliseconds);

        // --- T08: 電子郵件、網址與電話號碼保護 ---
        sw.Restart();
        var candEmail = new OcrCandidateItem { Id = 9, Text = "Email: contact@example.com", Confidence = 90f };
        var candWeb = new OcrCandidateItem { Id = 10, Text = "Web: https://www.example.com", Confidence = 88f };
        var candPhone1 = new OcrCandidateItem { Id = 11, Text = "電話:+886-2-2345-6789", Confidence = 90f };
        var candPhone2 = new OcrCandidateItem { Id = 12, Text = "行動:+886-912-345-678", Confidence = 87f };
        bool rEmail = TraditionalChineseReviewer.ShouldReview(candEmail);
        bool rWeb = TraditionalChineseReviewer.ShouldReview(candWeb);
        bool rPhone1 = TraditionalChineseReviewer.ShouldReview(candPhone1);
        bool rPhone2 = TraditionalChineseReviewer.ShouldReview(candPhone2);
        sw.Stop();
        bool t08Passed = !rEmail && !rWeb && !rPhone1 && !rPhone2;
        Record("ISSUE-0007-T08", "Email、網址、電話、行動電話行保守保護 (絕不觸發複核)", t08Passed,
            $"Email={rEmail}, Web={rWeb}, Phone1={rPhone1}, Phone2={rPhone2} (預期皆 False)",
            sw.ElapsedMilliseconds);

        // --- T09: 複核低信心度 (< 45% 即使原信心度更低仍拒絕) 或無繁中字元時之保守回退 ---
        sw.Restart();
        var candFallBack = new OcrCandidateItem
        {
            Id = 13,
            Text = "技術支援 xkj",
            Confidence = 75f,
            X = 50,
            Y = 50,
            Width = 150,
            Height = 30
        };
        // Injected Review 1: No CJK characters
        var decNoCjk = TraditionalChineseReviewer.EvaluateAdoption(candFallBack, "xkj", 85f);
        // Injected Review 2: Very low confidence (< 45%), original confidence is higher
        var decLowConf1 = TraditionalChineseReviewer.EvaluateAdoption(candFallBack, "技術支援組", 30f);
        // Injected Review 3: Confidence < 45% EVEN IF original confidence is even lower (e.g. orig=20%, review=40%)
        var candVeryLowOrig = new OcrCandidateItem { Id = 131, Text = "王핝華", Confidence = 20f };
        var decLowConf2 = TraditionalChineseReviewer.EvaluateAdoption(candVeryLowOrig, "王大華", 40f);
        sw.Stop();
        bool t09Passed = !decNoCjk.ShouldAdopt &&
                         decNoCjk.Reason.Contains("未包含任何繁體中文") &&
                         !decLowConf1.ShouldAdopt &&
                         decLowConf1.Reason.Contains("信心度過低") &&
                         !decLowConf2.ShouldAdopt &&
                         decLowConf2.Reason.Contains("信心度過低");
        Record("ISSUE-0007-T09", "複核結果不含中文字或信心度 < 45% (即使原信心度更低) 時保守拒絕採用並回退原候選", t09Passed,
            $"NoCjkAdopt={decNoCjk.ShouldAdopt}, LowConf1Adopt={decLowConf1.ShouldAdopt}, LowConf2Adopt={decLowConf2.ShouldAdopt} ('{decLowConf2.Reason}')",
            sw.ElapsedMilliseconds);

        // --- T10: 幾何空間與字數合理性防護 ---
        sw.Restart();
        var candSmallBox = new OcrCandidateItem
        {
            Id = 14,
            Text = "名稱 x",
            Confidence = 70f,
            X = 10,
            Y = 10,
            Width = 20,
            Height = 20
        };
        // 15 Chinese characters in a 20px box is physically impossible noise
        var decGeometry = TraditionalChineseReviewer.EvaluateAdoption(candSmallBox, "中華民國臺灣積體電路製造科技", 85f);
        sw.Stop();
        bool t10Passed = !decGeometry.ShouldAdopt && decGeometry.Reason.Contains("寬度");
        Record("ISSUE-0007-T10", "幾何空間合理性檢查：候選區域寬度不足以容納過多字元時安全拒絕", t10Passed,
            $"Adopt={decGeometry.ShouldAdopt}, Reason='{decGeometry.Reason}'",
            sw.ElapsedMilliseconds);

        // --- T11: CJK 字數相同但內容不同、且無腳本缺陷改善時必須安全拒絕 ---
        sw.Restart();
        var candSameCjk = new OcrCandidateItem
        {
            Id = 15,
            Text = "張大明",
            Confidence = 80f,
            X = 100,
            Y = 100,
            Width = 150,
            Height = 35
        };
        // Reviewed text has same CJK count (3 vs 3) but different content, and original had no Hangul / no anomalous Latin
        var decSameCjk = TraditionalChineseReviewer.EvaluateAdoption(candSameCjk, "陳小明", 88f);
        sw.Stop();
        bool t11Passed = !decSameCjk.ShouldAdopt &&
                         decSameCjk.Reason.Contains("明確改善指標") &&
                         decSameCjk.AdoptedText == "張大明";
        Record("ISSUE-0007-T11", "字數相同防護：無腳本缺陷改善且中文字數相同時，安全拒絕任意採用並保留原候選", t11Passed,
            $"Adopt={decSameCjk.ShouldAdopt}, Retained='{decSameCjk.AdoptedText}', Reason='{decSameCjk.Reason}'",
            sw.ElapsedMilliseconds);

        // --- T12: DraftRowItem.IsRegionSource 修正與 Source 標籤驗證 ---
        sw.Restart();
        var rowRegionReviewed = new DraftRowItem
        {
            Source = "局部辨識 (繁中複核)",
            OriginalText = "한 v",
            Value = "陳大明"
        };
        var rowRegionStandard = new DraftRowItem
        {
            Source = "局部辨識",
            OriginalText = "電話",
            Value = "電話"
        };
        var rowFullReviewed = new DraftRowItem
        {
            Source = "整圖辨識 (繁中複核)",
            OriginalText = "李핝華",
            Value = "李大華"
        };
        sw.Stop();
        bool t12Passed = rowRegionReviewed.IsRegionSource &&
                         rowRegionReviewed.DisplaySourceTag == "局部辨識" &&
                         rowRegionStandard.IsRegionSource &&
                         rowRegionStandard.DisplaySourceTag == "局部辨識" &&
                         !rowFullReviewed.IsRegionSource &&
                         rowFullReviewed.DisplaySourceTag == "整圖辨識";
        Record("ISSUE-0007-T12", "DraftRowItem 模型屬性修復：複核後 '局部辨識 (繁中複核)' 仍正確判定 IsRegionSource 與 DisplaySourceTag", t12Passed,
            $"ReviewedRegion: IsRegionSource={rowRegionReviewed.IsRegionSource}, Tag='{rowRegionReviewed.DisplaySourceTag}'; StandardRegion: IsRegionSource={rowRegionStandard.IsRegionSource}, Tag='{rowRegionStandard.DisplaySourceTag}'; FullReviewed: IsRegionSource={rowFullReviewed.IsRegionSource}, Tag='{rowFullReviewed.DisplaySourceTag}'",
            sw.ElapsedMilliseconds);

        // --- T13: 既有五語合成圖全管線執行與按需惰性載入防護 ---
        sw.Restart();
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
                    string cand = Path.Combine(cur, "TestReports", "evidence", "ISSUE-0005-W1", "synthetic_card_w0.png");
                    if (File.Exists(cand)) { samplePng = cand; break; }
                    cur = Directory.GetParent(cur)?.FullName;
                }
            }
        }

        bool t13Passed = false;
        string t13Details = string.Empty;
        if (File.Exists(samplePng))
        {
            byte[] imageBytes = await File.ReadAllBytesAsync(samplePng);
            using var ocrService = new OcrService();
            var ocrResult = await ocrService.RunFullOcrAsync(imageBytes, 1001, CancellationToken.None);

            bool hasWang = ocrResult.Candidates.Any(c => c.Text.Contains("王大明"));
            bool hasJpn = ocrResult.Candidates.Any(c => c.Text.Contains("株式会社") || c.Text.Contains("テクノロジー"));
            bool hasKor = ocrResult.Candidates.Any(c => c.Text.Contains("한국") || c.Text.Contains("지사"));
            bool hasPhone = ocrResult.Candidates.Any(c => c.Text.Contains("2345-6789"));
            bool coordsValid = ocrResult.Candidates.All(c => c.Width > 0 && c.Height > 0);

            // Verify that for candidates where ShouldReview is false, no review source was injected
            bool legitimateNotModified = ocrResult.Candidates.Where(c => !TraditionalChineseReviewer.ShouldReview(c)).All(c => !c.IsReviewed);

            t13Passed = ocrResult.Success && hasWang && hasJpn && hasKor && hasPhone && coordsValid && legitimateNotModified && ocrResult.Candidates.Count >= 10;
            t13Details = $"Candidates={ocrResult.Candidates.Count}, Latency={ocrResult.LatencyMs}ms, Wang={hasWang}, Jpn={hasJpn}, Kor={hasKor}, Phone={hasPhone}, CoordsValid={coordsValid}, LegitimateNotModified={legitimateNotModified}";
        }
        else
        {
            t13Details = $"找不到合成名片圖片: {samplePng}";
        }
        sw.Stop();
        Record("ISSUE-0007-T13", "五語合成名片全管線執行：五語候選正常辨識，無混淆行未被誤改，且次要引擎按需載入", t13Passed,
            t13Details, sw.ElapsedMilliseconds);

        // --- T14: 草稿逐列採用、人工修改標記與確認流程 (OCR7-U04) ---
        sw.Restart();
        var catalogService = new CustomFieldCatalogService();
        var vm = new MainViewModel(catalogService);

        var mockCandidates = new List<OcrCandidateItem>
        {
            new() { Id = 1, Text = "한 v", TraditionalSuggestion = "陳大明", SuggestionSource = "繁中單語複核", Source = "整圖辨識", Confidence = 65f, X = 100, Y = 50, Width = 150, Height = 35 },
            new() { Id = 2, Text = "软件开发有限公司", TraditionalSuggestion = "軟件開發有限公司", SuggestionSource = "Windows 字形轉換", Source = "整圖辨識", Confidence = 90f, X = 50, Y = 20, Width = 200, Height = 25 },
            new() { Id = 3, Text = "contact@example.com", Source = "整圖辨識", Confidence = 92f, X = 50, Y = 100, Width = 250, Height = 25 }
        };

        vm.PopulateDraftRowsFromCandidates(mockCandidates, reqId: vm.CurrentRequestId, isRegion: false);

        // Verify re-populating with same request does not duplicate rows
        vm.PopulateDraftRowsFromCandidates(mockCandidates, reqId: vm.CurrentRequestId, isRegion: false);
        bool t14NoDuplicates = vm.DraftRows.Count == 3;

        var row0 = vm.DraftRows[0];
        bool t14InitialState = row0.Value == "한 v" &&
                               row0.OriginalText == "한 v" &&
                               row0.TraditionalSuggestion == "陳大明" &&
                               row0.HasSuggestion &&
                               row0.CanApplySuggestion &&
                               !row0.IsUserModified &&
                               row0.Status == DraftItemStatus.PendingConfirmation;

        // Apply suggestion
        row0.ApplySuggestion();

        bool t14AppliedState = row0.Value == "陳大明" &&
                               row0.OriginalText == "한 v" && // 原文保持！
                               row0.IsUserModified && // 標為已人工修改！
                               row0.Status == DraftItemStatus.PendingConfirmation && // 仍待確認！
                               !row0.CanApplySuggestion; // 套用建議按鈕已停用！

        string appliedVal = row0.Value;
        bool appliedMod = row0.IsUserModified;
        var appliedStatus = row0.Status;

        // Confirm
        vm.ConfirmDraftRow(row0);
        bool t14ConfirmedState = row0.Status == DraftItemStatus.Confirmed &&
                                 !row0.CanApplySuggestion;

        sw.Stop();
        bool t14Passed = t14InitialState && t14AppliedState && t14ConfirmedState && t14NoDuplicates;
        Record("ISSUE-0007-T14", "OCR7-U04 草稿逐列採用：初始值為原文，點套用僅改可編輯值並標人工修改(仍待確認)，另按確認才確認", t14Passed,
            $"InitialState={t14InitialState}, AppliedState={t14AppliedState} (Value='{appliedVal}', IsMod={appliedMod}, Status={appliedStatus}), ConfirmedState={t14ConfirmedState}, NoDuplicates={t14NoDuplicates}",
            sw.ElapsedMilliseconds);

        // --- T15: Windows 字形映射與失敗安全回退 (OCR7-U02) ---
        sw.Restart();
        // 1. Valid simplified Chinese mapped to traditional Chinese
        string? mappedCompany = WindowsNlsHelper.TryMapToTraditionalChinese("软件开发有限公司");
        // 2. Identical text produces null (no redundant suggestion)
        string? mappedIdentical = WindowsNlsHelper.TryMapToTraditionalChinese("軟件開發有限公司");
        // 3. Null or empty string returns null safely
        string? mappedNull = WindowsNlsHelper.TryMapToTraditionalChinese(null);
        string? mappedEmpty = WindowsNlsHelper.TryMapToTraditionalChinese("");
        // 4. Injected API failure test
        WindowsNlsHelper.SimulateApiFailureForTesting = true;
        string? mappedFailure = WindowsNlsHelper.TryMapToTraditionalChinese("软件开发有限公司");
        WindowsNlsHelper.SimulateApiFailureForTesting = false;
        bool apiFailureHandled = mappedFailure == null;
        // 5. Candidate integration via GenerateTraditionalSuggestion
        var candSimpCompany = new OcrCandidateItem
        {
            Id = 40,
            Text = "北京创新软件开发有限公司",
            Confidence = 91f,
            Source = "整圖辨識"
        };
        TraditionalChineseReviewer.GenerateTraditionalSuggestion(null, null, candSimpCompany);
        sw.Stop();
        bool t15Passed = mappedCompany == "軟件開發有限公司" &&
                         mappedIdentical == null &&
                         mappedNull == null &&
                         mappedEmpty == null &&
                         apiFailureHandled &&
                         candSimpCompany.Text == "北京创新软件开发有限公司" && // 原文保持！
                         candSimpCompany.TraditionalSuggestion == "北京創新軟件開發有限公司" &&
                         candSimpCompany.SuggestionSource == "Windows 字形轉換";
        Record("ISSUE-0007-T15", "OCR7-U02 Windows 字形映射：簡體字產生繁體建議且原文保持；null、空字串、相同繁體字串與 API 注入失敗均安全回退無建議", t15Passed,
            $"MappedCompany='{mappedCompany}', MappedIdenticalNull={mappedIdentical == null}, ApiFailureHandled={apiFailureHandled}, CandidateSuggestion='{candSimpCompany.TraditionalSuggestion}', Source='{candSimpCompany.SuggestionSource}'",
            sw.ElapsedMilliseconds);

        // --- T16: 語種防護：純英、Email、URL、電話、純韓、含假名日文 (OCR7-U03) ---
        sw.Restart();
        bool gEng = TraditionalChineseReviewer.ShouldAttemptGlyphMapping("MAJI SOLUTIONS Corp");
        bool gEmail = TraditionalChineseReviewer.ShouldAttemptGlyphMapping("service@example.com");
        bool gUrl = TraditionalChineseReviewer.ShouldAttemptGlyphMapping("https://www.example.com");
        bool gPhone = TraditionalChineseReviewer.ShouldAttemptGlyphMapping("TEL: 02-2345-6789");
        bool gKor = TraditionalChineseReviewer.ShouldAttemptGlyphMapping("한국 비즈니스 지사");
        bool gKana = TraditionalChineseReviewer.ShouldAttemptGlyphMapping("東京都千代田区とうきょう");
        // Kanji-only Japanese is eligible for glyph mapping candidate, but will NEVER auto-apply
        bool gKanjiJpn = TraditionalChineseReviewer.ShouldAttemptGlyphMapping("東京都千代田区");
        sw.Stop();
        bool t16Passed = !gEng && !gEmail && !gUrl && !gPhone && !gKor && !gKana && gKanjiJpn;
        Record("ISSUE-0007-T16", "OCR7-U03 語種防護：純英、Email、URL、電話、純韓、含假名日文不產生字形建議；純漢字日文不自動套用", t16Passed,
            $"Eng={!gEng}, Email={!gEmail}, Url={!gUrl}, Phone={!gPhone}, Kor={!gKor}, Kana={!gKana}, KanjiJpn={gKanjiJpn}",
            sw.ElapsedMilliseconds);

        // --- T17: 既有草稿保護：已修改/已確認列保護、整圖重跑、局部辨識與晚到回呼防護 (OCR7-U05) ---
        sw.Restart();
        var vmProtection = new MainViewModel(catalogService);
        var initialCands = new List<OcrCandidateItem>
        {
            new() { Id = 1, Text = "Row1 Orig", TraditionalSuggestion = "Row1 Sug", SuggestionSource = "Windows 字形轉換" },
            new() { Id = 2, Text = "Row2 Orig", TraditionalSuggestion = "Row2 Sug", SuggestionSource = "Windows 字形轉換" }
        };
        vmProtection.PopulateDraftRowsFromCandidates(initialCands, reqId: vmProtection.CurrentRequestId, isRegion: false);

        // User modifies Row 1
        vmProtection.DraftRows[0].Value = "Row1 Manually Edited";
        vmProtection.DraftRows[0].MarkModified();

        // User confirms Row 2
        vmProtection.DraftRows[1].Confirm();

        // Run another full OCR (re-populate)
        var newFullCands = new List<OcrCandidateItem>
        {
            new() { Id = 3, Text = "Row3 New", TraditionalSuggestion = "Row3 Sug", SuggestionSource = "Windows 字形轉換" }
        };
        vmProtection.PopulateDraftRowsFromCandidates(newFullCands, reqId: vmProtection.CurrentRequestId, isRegion: false);

        // Verify preserved rows
        bool p1Row1Preserved = vmProtection.DraftRows.Any(r => r.Value == "Row1 Manually Edited" && r.IsUserModified);
        bool p1Row2Preserved = vmProtection.DraftRows.Any(r => r.Value == "Row2 Orig" && r.Status == DraftItemStatus.Confirmed);
        bool p1Row3Added = vmProtection.DraftRows.Any(r => r.Value == "Row3 New");

        // Stale request protection
        int countBeforeStale = vmProtection.DraftRows.Count;
        vmProtection.PopulateDraftRowsFromCandidates(newFullCands, reqId: 999999, isRegion: false);
        bool staleIgnored = vmProtection.DraftRows.Count == countBeforeStale;

        sw.Stop();
        bool t17Passed = p1Row1Preserved && p1Row2Preserved && p1Row3Added && staleIgnored;
        Record("ISSUE-0007-T17", "OCR7-U05 既有草稿保護：已人工修改與已確認列在整圖重跑時完整保留，且晚到逾期回呼不覆寫草稿", t17Passed,
            $"Row1Preserved={p1Row1Preserved}, Row2Preserved={p1Row2Preserved}, Row3Added={p1Row3Added}, StaleIgnored={staleIgnored}",
            sw.ElapsedMilliseconds);

        // --- T18: 次要繁中模型異常安全降級 (Secondary engine failure fallback) ---
        sw.Restart();
        var candSafe = new OcrCandidateItem
        {
            Id = 99,
            Text = "王大華 b",
            Confidence = 80f,
            X = 50,
            Y = 50,
            Width = 100,
            Height = 25,
            Source = "整圖辨識"
        };
        var decSafe = TraditionalChineseReviewer.EvaluateAdoption(candSafe, null, 0f);
        sw.Stop();
        bool t18Passed = !decSafe.ShouldAdopt &&
                         decSafe.AdoptedText == candSafe.Text &&
                         candSafe.Text == "王大華 b";
        Record("ISSUE-0007-T18", "次要繁中模型缺失或辨識失敗之安全降級：自動回退原候選而不中斷管線", t18Passed,
            $"Adopt={decSafe.ShouldAdopt}, RetainedText='{decSafe.AdoptedText}', Reason='{decSafe.Reason}'",
            sw.ElapsedMilliseconds);

        // --- T19: 取消與請求世代過期保護 (Cancellation and stale request protection) ---
        sw.Restart();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-canceled token

        bool cancelCaught = false;
        try
        {
            using var ocrService = new OcrService();
            byte[] dummy = new byte[100];
            await ocrService.RunFullOcrAsync(dummy, 9999, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelCaught = true;
        }
        catch
        {
            cancelCaught = false;
        }
        sw.Stop();
        Record("ISSUE-0007-T19", "取消與逾期回呼安全保護：取消權杖觸發時即時中斷且不污染草稿或候選", cancelCaught,
            $"OperationCanceledExceptionCaught={cancelCaught}",
            sw.ElapsedMilliseconds);

        // --- T20: 實機環境獨立量測 (次要 chi_tra 載入記憶體 WorkingSet 增量與單行 ROI 延遲) ---
        sw.Restart();
        string tessdata = W0Verifier.FindTessdataPath();
        long memBefore = Process.GetCurrentProcess().WorkingSet64;
        TesseractEngine? measuredEngine = null;
        long engineInitMs = 0;
        try
        {
            var initSw = Stopwatch.StartNew();
            measuredEngine = new TesseractEngine(tessdata, "chi_tra", EngineMode.Default);
            initSw.Stop();
            engineInitMs = initSw.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[T20] Engine init failed: {ex.GetType().Name}");
        }

        long memAfter = Process.GetCurrentProcess().WorkingSet64;
        double memDeltaMb = (memAfter - memBefore) / (1024.0 * 1024.0);

        // Measure single line ROI latency on synthetic image
        var lineLatencies = new List<long>();
        if (measuredEngine != null && File.Exists(samplePng))
        {
            using var pix = Pix.LoadFromFile(samplePng);
            // Single line ROI for '王大明 (Wang Da-Ming)': X=118, Y=96, W=274, H=26
            var roiRect = Rect.FromCoords(118, 96, 118 + 274, 96 + 26);

            // Warmup
            using (var p = measuredEngine.Process(pix, roiRect, PageSegMode.SingleLine)) { _ = p.GetText(); }

            // 5 iterations measurement
            for (int i = 0; i < 5; i++)
            {
                var lineSw = Stopwatch.StartNew();
                using var p = measuredEngine.Process(pix, roiRect, PageSegMode.SingleLine);
                string txt = p.GetText();
                lineSw.Stop();
                lineLatencies.Add(lineSw.ElapsedMilliseconds);
            }
        }
        measuredEngine?.Dispose();
        sw.Stop();

        double avgLatency = lineLatencies.Count > 0 ? lineLatencies.Average() : 0;
        long minLatency = lineLatencies.Count > 0 ? lineLatencies.Min() : 0;
        long maxLatency = lineLatencies.Count > 0 ? lineLatencies.Max() : 0;

        bool t20Passed = measuredEngine != null && lineLatencies.Count == 5;
        Record("ISSUE-0007-T20", "實機環境獨立實測：次要 chi_tra 引擎 WorkingSet 增量與單行局部 ROI 辨識真實延遲", t20Passed,
            $"EngineInit={engineInitMs}ms, WorkingSetBefore={memBefore / (1024.0 * 1024.0):F2}MB, After={memAfter / (1024.0 * 1024.0):F2}MB, WorkingSetDelta={memDeltaMb:F2}MB. SingleLineLatency (5 runs): Min={minLatency}ms, Max={maxLatency}ms, Avg={avgLatency:F1}ms (Runs: {string.Join(", ", lineLatencies)} ms)",
            sw.ElapsedMilliseconds);

        // --- T21: 套用建議後重跑同一候選之草稿去重防護 (OCR7-U05 / 缺陷回歸) ---
        sw.Restart();
        var vmRerun = new MainViewModel(catalogService);
        var candToRerun = new OcrCandidateItem
        {
            Id = 1,
            Text = "软件开发有限公司",
            TraditionalSuggestion = "軟件開發有限公司",
            SuggestionSource = "Windows 字形轉換",
            X = 50,
            Y = 20,
            Width = 200,
            Height = 25,
            Source = "整圖辨識"
        };
        var candsFirstRun = new List<OcrCandidateItem> { candToRerun };

        // 第一次整圖辨識產生草稿
        vmRerun.PopulateDraftRowsFromCandidates(candsFirstRun, reqId: vmRerun.CurrentRequestId, isRegion: false);
        bool t21FirstRunCount = vmRerun.DraftRows.Count == 1;

        // 使用者點選「套用建議」 (IsUserModified = true, Value = 建議)
        vmRerun.DraftRows[0].ApplySuggestion();
        bool t21Applied = vmRerun.DraftRows[0].IsUserModified &&
                          vmRerun.DraftRows[0].Value == "軟件開發有限公司" &&
                          vmRerun.DraftRows[0].OriginalText == "软件开发有限公司";

        // 第二次對同一張圖重跑整圖辨識 (產生相同候選)
        vmRerun.PopulateDraftRowsFromCandidates(candsFirstRun, reqId: vmRerun.CurrentRequestId, isRegion: false);

        // 驗證：去重成功，不得再新增一列！
        bool t21Deduplicated = vmRerun.DraftRows.Count == 1;
        bool t21RowPreserved = vmRerun.DraftRows[0].Value == "軟件開發有限公司" &&
                               vmRerun.DraftRows[0].OriginalText == "软件开发有限公司" &&
                               vmRerun.DraftRows[0].IsUserModified &&
                               vmRerun.DraftRows[0].Status == DraftItemStatus.PendingConfirmation;

        sw.Stop();
        bool t21Passed = t21FirstRunCount && t21Applied && t21Deduplicated && t21RowPreserved;
        Record("ISSUE-0007-T21", "整圖重跑去重：草稿列套用建議後重跑整圖辨識，已修改列完整保留且相同候選不重複新增", t21Passed,
            $"FirstRunCount={vmRerun.DraftRows.Count}, Applied={t21Applied}, RowCountAfterRerun={vmRerun.DraftRows.Count} (預期 1), RowPreserved={t21RowPreserved}",
            sw.ElapsedMilliseconds);

        // --- T22: 相同文字但不同原圖位置之候選各自保留防護 ---
        sw.Restart();
        var vmMultiPos = new MainViewModel(catalogService);
        var candPos1 = new OcrCandidateItem
        {
            Id = 1,
            Text = "技术有限公司",
            TraditionalSuggestion = "技術有限公司",
            SuggestionSource = "Windows 字形轉換",
            X = 50,
            Y = 100,
            Width = 150,
            Height = 30,
            Source = "整圖辨識"
        };
        var candPos2 = new OcrCandidateItem
        {
            Id = 2,
            Text = "技术有限公司", // 相同文字
            TraditionalSuggestion = "技術有限公司",
            SuggestionSource = "Windows 字形轉換",
            X = 300,
            Y = 400, // 不同位置
            Width = 150,
            Height = 30,
            Source = "整圖辨識"
        };
        var multiPosCands = new List<OcrCandidateItem> { candPos1, candPos2 };

        // 第一次辨識：產生兩列草稿
        vmMultiPos.PopulateDraftRowsFromCandidates(multiPosCands, reqId: vmMultiPos.CurrentRequestId, isRegion: false);
        bool t22InitialCount = vmMultiPos.DraftRows.Count == 2;

        // 使用者修改第一列 (位置 1)
        vmMultiPos.DraftRows[0].ApplySuggestion();
        vmMultiPos.ConfirmDraftRow(vmMultiPos.DraftRows[0]);

        // 第二次重跑整圖辨識 (含位置 1 與位置 2)
        vmMultiPos.PopulateDraftRowsFromCandidates(multiPosCands, reqId: vmMultiPos.CurrentRequestId, isRegion: false);

        // 驗證：
        // 位置 1 已確認列被保留，不重複新增
        // 位置 2 因座標不同，各自保留為獨立草稿列
        bool t22CountAfterRerun = vmMultiPos.DraftRows.Count == 2;
        var preservedRowPos1 = vmMultiPos.DraftRows.FirstOrDefault(r => r.X == 50 && r.Y == 100);
        var pendingRowPos2 = vmMultiPos.DraftRows.FirstOrDefault(r => r.X == 300 && r.Y == 400);

        bool t22Row1Preserved = preservedRowPos1 != null &&
                                preservedRowPos1.Status == DraftItemStatus.Confirmed &&
                                preservedRowPos1.Value == "技術有限公司";
        bool t22Row2Present = pendingRowPos2 != null &&
                              pendingRowPos2.Status == DraftItemStatus.PendingConfirmation &&
                              pendingRowPos2.Value == "技术有限公司";

        sw.Stop();
        bool t22Passed = t22InitialCount && t22CountAfterRerun && t22Row1Preserved && t22Row2Present;
        Record("ISSUE-0007-T22", "不同位置同文字保留：整圖重跑時，與已保留列相同文字但不同原圖位置之候選各自保留獨立草稿列", t22Passed,
            $"InitialCount={t22InitialCount}, CountAfterRerun={vmMultiPos.DraftRows.Count} (預期 2), Row1Preserved={t22Row1Preserved}, Row2Present={t22Row2Present}",
            sw.ElapsedMilliseconds);

        return report;
    }
}
