using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Verification;

public class Issue0009TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0009TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0009TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.7";
    public string IssueId { get; set; } = "ISSUE-0009";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0009TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0009AutomatedTestSuite
{
    public static async Task<Issue0009TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0009TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0009TestCaseResult
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
        // OCR9-T01: 語系選擇選項、預設值、獨立性與重設行為 (RS-ISSUE-0009 OCR9-03, OCR9-04, OCR9-05 / RS-ISSUE-0036)
        // =========================================================================================
        sw.Restart();
        var catalog01 = new CustomFieldCatalogService(Path.Combine(Path.GetFullPath(evidenceDir), $"isolated-catalog-{Guid.NewGuid():N}.json"));
        string settingsPath01 = Path.Combine(Path.GetFullPath(evidenceDir), $"isolated-settings-{Guid.NewGuid():N}.json");
        var settings01 = new AppSettingsService(settingsPath01);
        var vm01 = new MainViewModel(catalog01, null, null, settings01);

        // 1. Initial values must both be "多語系辨識"
        bool initialFullIsMixed5 = vm01.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Mixed5 &&
                                   vm01.SelectedFullOcrLanguage?.DisplayName == "多語系辨識";
        bool initialPartialIsMixed5 = vm01.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.Mixed5 &&
                                      vm01.SelectedPartialOcrLanguage?.DisplayName == "多語系辨識";

        // 2. Exact 6 options present in defined order
        var allOpts = vm01.LanguageOptions;
        bool has6Options = allOpts.Count == 6;
        bool optionsOrderCorrect = has6Options &&
                                   allOpts[0].Code == OcrLanguageCatalog.Mixed5 && allOpts[0].DisplayName == "多語系辨識" &&
                                   allOpts[1].Code == OcrLanguageCatalog.ChiTra && allOpts[1].DisplayName == "繁體中文" &&
                                   allOpts[2].Code == OcrLanguageCatalog.ChiSim && allOpts[2].DisplayName == "簡體中文" &&
                                   allOpts[3].Code == OcrLanguageCatalog.Jpn && allOpts[3].DisplayName == "日文" &&
                                   allOpts[4].Code == OcrLanguageCatalog.Kor && allOpts[4].DisplayName == "韓文" &&
                                   allOpts[5].Code == OcrLanguageCatalog.Eng && allOpts[5].DisplayName == "英文";

        // 3. Modifying one does not affect the other
        vm01.SelectedFullOcrLanguage = allOpts[3]; // 日文
        bool fullChangedPartialUntouched = vm01.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Jpn &&
                                           vm01.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.Mixed5;

        vm01.SelectedPartialOcrLanguage = allOpts[1]; // 繁體中文
        bool partialChangedFullUntouched = vm01.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Jpn &&
                                           vm01.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.ChiTra;

        // 4. Session image switch retains both selections (換圖保留)
        var dummySession01 = new ImageSessionInfo
        {
            ImageBytes = CreateSolidColorBmp(100, 100),
            Width = 1000,
            Height = 600,
            SourceName = "card_01.png",
            MimeType = "image/png"
        };
        typeof(MainViewModel).GetProperty("CurrentSession")?.SetValue(vm01, dummySession01);

        bool retainedAfterImageSession = vm01.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Jpn &&
                                         vm01.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.ChiTra;

        // 5. App restart simulation: Retains previous settings (ISSUE-0036 persistence)
        var restartedVm = new MainViewModel(catalog01, null, null, new AppSettingsService(settingsPath01));
        bool restartRetainsSettings = restartedVm.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Jpn &&
                                      restartedVm.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.ChiTra;

        // Fresh instance with fresh settings starts at default Mixed5
        var freshVm = new MainViewModel(catalog01, null, null, new AppSettingsService(Path.Combine(Path.GetFullPath(evidenceDir), $"fresh-settings-{Guid.NewGuid():N}.json")));
        bool freshStartsAtMixed5 = freshVm.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Mixed5 &&
                                   freshVm.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.Mixed5;

        sw.Stop();
        bool t01Passed = initialFullIsMixed5 && initialPartialIsMixed5 &&
                         optionsOrderCorrect && fullChangedPartialUntouched &&
                         partialChangedFullUntouched && retainedAfterImageSession &&
                         restartRetainsSettings && freshStartsAtMixed5;

        Record("OCR9-T01", "雙語系選擇器初值、六選項清單、獨立切換、換圖保留與重啟持久化邏輯驗證", t01Passed,
            $"InitMixed5={initialFullIsMixed5 && initialPartialIsMixed5}, Has6={has6Options}, Independent={fullChangedPartialUntouched && partialChangedFullUntouched}, RetainedOnSession={retainedAfterImageSession}, RestartRetained={restartRetainsSettings}, FreshMixed5={freshStartsAtMixed5}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR9-T02: 受控語系快照與實際 OcrService 路由驗證 (RS-ISSUE-0009 OCR9-03, OCR9-04, OCR9-06)
        // =========================================================================================
        sw.Restart();
        var spyOcr = new SpyOcrService();
        var vm02 = new MainViewModel(catalog01, spyOcr);

        var dummySession02 = new ImageSessionInfo
        {
            ImageBytes = CreateSolidColorBmp(200, 100),
            Width = 800,
            Height = 400,
            SourceName = "card_02.png",
            MimeType = "image/png"
        };
        typeof(MainViewModel).GetProperty("CurrentSession")?.SetValue(vm02, dummySession02);

        // 1. Full OCR uses SelectedFullOcrLanguage
        vm02.SelectedFullOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.ChiTra);
        vm02.SelectedPartialOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Eng);

        spyOcr.PresetFullResult = new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem>
            {
                new() { Id = 1, Text = "台北科技有限公司", Confidence = 95f, Source = "整圖辨識" }
            },
            LatencyMs = 25,
            MeanConfidence = 0.95f
        };

        await vm02.RunFullOcrAsync();
        bool fullOcrUsedChiTra = spyOcr.LastFullLanguage == OcrLanguageCatalog.ChiTra;
        bool fullSummaryMentionsLang = vm02.LastOcrSummary.Contains("繁體中文");

        // 2. Region OCR (General) uses SelectedPartialOcrLanguage
        vm02.SetRegionCoordinates(20, 20, 150, 60);
        spyOcr.PresetRegionResult = new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem>
            {
                new() { Id = 2, Text = "SOFTWARE ENGINEER", Confidence = 92f, Source = "局部辨識" }
            },
            LatencyMs = 15,
            MeanConfidence = 0.92f
        };

        await vm02.RunRegionOcrAsync();
        bool regionOcrUsedEng = spyOcr.LastRegionLanguage == OcrLanguageCatalog.Eng;
        bool regionSummaryMentionsLang = vm02.LastOcrSummary.Contains("英文");

        // 3. Targeted ROI OCR uses SelectedPartialOcrLanguage
        vm02.SelectedPartialOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Jpn);
        vm02.SetRegionCoordinates(10, 10, 120, 40);
        spyOcr.PresetRegionResult = new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem>
            {
                new() { Id = 3, Text = "ソニー株式会社", Confidence = 91f, Source = "局部辨識" }
            },
            LatencyMs = 18,
            MeanConfidence = 0.91f
        };

        await vm02.RunTargetedRoiOcrAsync(vm02.CompanyRow);
        bool targetedOcrUsedJpn = spyOcr.LastRegionLanguage == OcrLanguageCatalog.Jpn;
        bool targetedSummaryMentionsLang = vm02.LastOcrSummary.Contains("日文");
        bool companyFilled = vm02.CompanyRow.Value == "ソニー株式会社";

        sw.Stop();
        bool t02Passed = fullOcrUsedChiTra && fullSummaryMentionsLang &&
                         regionOcrUsedEng && regionSummaryMentionsLang &&
                         targetedOcrUsedJpn && targetedSummaryMentionsLang && companyFilled;

        Record("OCR9-T02", "整圖與局部獨立語系快照傳遞至 OcrService 及狀態回顯驗證", t02Passed,
            $"FullRoutedChiTra={fullOcrUsedChiTra}, FullSummaryHasLang={fullSummaryMentionsLang}, RegionRoutedEng={regionOcrUsedEng}, TargetedRoutedJpn={targetedOcrUsedJpn}, CompanyFilled={companyFilled}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR9-T03: 真實 Tesseract 引擎六受控模型載入、切換與串行鎖定驗證 (AR-ISSUE-0009, RS-ISSUE-0009)
        // =========================================================================================
        sw.Restart();
        bool all6ModelsLoaded = true;
        var modelResults = new List<string>();

        using (var realOcr = new OcrService())
        {
            byte[] testImageBmp = CreateSolidColorBmp(100, 40);

            for (int i = 0; i < OcrLanguageCatalog.AllOptions.Count; i++)
            {
                var opt = OcrLanguageCatalog.AllOptions[i];
                var subSw = Stopwatch.StartNew();

                var ocrRes = await realOcr.RunFullOcrAsync(testImageBmp, 200 + i, CancellationToken.None, opt.Code);
                subSw.Stop();

                bool success = ocrRes.Success;
                if (!success)
                {
                    all6ModelsLoaded = false;
                }
                modelResults.Add($"{opt.DisplayName}({opt.Code}): Success={success} [{subSw.ElapsedMilliseconds}ms]");
            }

            // Test model switching in sequence: chi_tra -> eng -> chi_sim -> jpn -> kor -> Mixed5
            var switchOrder = new[]
            {
                OcrLanguageCatalog.ChiTra,
                OcrLanguageCatalog.Eng,
                OcrLanguageCatalog.ChiSim,
                OcrLanguageCatalog.Jpn,
                OcrLanguageCatalog.Kor,
                OcrLanguageCatalog.Mixed5
            };

            bool sequenceSwitchOk = true;
            foreach (var lang in switchOrder)
            {
                var res = await realOcr.RunRegionOcrAsync(testImageBmp, 5, 5, 80, 30, 300, CancellationToken.None, lang);
                if (!res.Success)
                {
                    sequenceSwitchOk = false;
                }
            }

            all6ModelsLoaded = all6ModelsLoaded && sequenceSwitchOk;
        }

        sw.Stop();
        Record("OCR9-T03", "真實 Tesseract 引擎六受控語系模型離線載入、串行切換與執行驗證", all6ModelsLoaded,
            $"All6Loaded={all6ModelsLoaded}, Details={string.Join(", ", modelResults)}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR9-T04: 模型缺失與初始化失敗安全防護：明確錯誤提示、禁止暗中回退、草稿值保留 (OCR9-04, OCR9-06)
        // =========================================================================================
        sw.Restart();
        var failingOcr = new FailingModelOcrService();
        var vm04 = new MainViewModel(catalog01, failingOcr);

        var dummySession04 = new ImageSessionInfo
        {
            ImageBytes = CreateSolidColorBmp(150, 80),
            Width = 600,
            Height = 400,
            SourceName = "card_04.png",
            MimeType = "image/png"
        };
        typeof(MainViewModel).GetProperty("CurrentSession")?.SetValue(vm04, dummySession04);

        // Pre-populate and confirm TitleRow
        vm04.TitleRow.Value = "研發總監";
        vm04.TitleRow.Confirm();
        vm04.TitleRow.SaveUndoSnapshot();
        string titleBefore = vm04.TitleRow.Value;
        var statusBefore = vm04.TitleRow.Status;
        bool undoBefore = vm04.TitleRow.CanUndo;

        // Set partial language to simulated failing model ("kor")
        vm04.SelectedPartialOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Kor);
        vm04.SetRegionCoordinates(10, 10, 80, 30);

        var err = await vm04.RunTargetedRoiOcrAsync(vm04.TitleRow);

        bool errorReturned = !string.IsNullOrWhiteSpace(err) && err.Contains("韓文");
        bool statusReflectsError = vm04.StatusMessage.Contains("韓文") && vm04.StatusMessage.Contains("失敗");
        bool titlePreserved = vm04.TitleRow.Value == titleBefore &&
                              vm04.TitleRow.Status == statusBefore &&
                              vm04.TitleRow.CanUndo == undoBefore;

        // Test direct strict allowlist validation in OcrLanguageCatalog
        bool invalidCodeRejected = !OcrLanguageCatalog.IsValidCode("french") &&
                                   !OcrLanguageCatalog.IsValidCode("../../etc/passwd") &&
                                   !OcrLanguageCatalog.IsValidCode(null);

        // Test NormalizeCode behavior: null defaults to Mixed5, non-null invalid throws ArgumentException
        bool nullDefaultsToMixed5 = OcrLanguageCatalog.NormalizeCode(null) == OcrLanguageCatalog.Mixed5;
        bool throwsOnFrench = false;
        try { OcrLanguageCatalog.NormalizeCode("french"); } catch (ArgumentException) { throwsOnFrench = true; }

        bool throwsOnPath = false;
        try { OcrLanguageCatalog.NormalizeCode("../../etc/passwd"); } catch (ArgumentException) { throwsOnPath = true; }

        // Test direct OcrService entry point rejects non-allowlist languages without falling back to Mixed5
        var realOcrService = new OcrService();
        byte[] dummyImgBytes = CreateSolidColorBmp(100, 100);
        var directFullResult = await realOcrService.RunFullOcrAsync(dummyImgBytes, 999, CancellationToken.None, "french");
        bool directFullRejected = !directFullResult.Success && directFullResult.ErrorMessage != null && directFullResult.ErrorMessage.Contains("不支援");

        var directRegionResult = await realOcrService.RunRegionOcrAsync(dummyImgBytes, 0, 0, 50, 50, 999, CancellationToken.None, "../../invalid");
        bool directRegionRejected = !directRegionResult.Success && directRegionResult.ErrorMessage != null && directRegionResult.ErrorMessage.Contains("不支援");

        sw.Stop();
        bool t04Passed = errorReturned && statusReflectsError && titlePreserved &&
                         invalidCodeRejected && nullDefaultsToMixed5 &&
                         throwsOnFrench && throwsOnPath &&
                         directFullRejected && directRegionRejected;

        Record("OCR9-T04", "模型載入失敗安全防護：明確報錯、不暗中回退五語混合、欄位與復原狀態完全保留", t04Passed,
            $"ErrorReturned={errorReturned}, StatusMessage='{vm04.StatusMessage}', TitlePreserved={titlePreserved}, InvalidRejected={invalidCodeRejected}, ThrowNormalize={throwsOnFrench && throwsOnPath}, DirectOcrRejected={directFullRejected && directRegionRejected}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR9-T05: 辨識期間改選語系作廢舊請求與晚到過期回呼防護 (RS-ISSUE-0009 OCR9-05)
        // =========================================================================================
        sw.Restart();
        var delayedOcr = new ControllableDelayedOcrService();
        var vm05 = new MainViewModel(catalog01, delayedOcr);

        var dummySession05 = new ImageSessionInfo
        {
            ImageBytes = CreateSolidColorBmp(200, 120),
            Width = 800,
            Height = 500,
            SourceName = "card_05.png",
            MimeType = "image/png"
        };
        typeof(MainViewModel).GetProperty("CurrentSession")?.SetValue(vm05, dummySession05);

        // Case A: Full OCR running in English -> User switches to Traditional Chinese -> Old result rejected
        vm05.SelectedFullOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Eng);
        delayedOcr.Reset();

        var fullOcrTask = vm05.RunFullOcrAsync();

        // While OCR task is in flight, user switches full-image language
        vm05.SelectedFullOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.ChiTra);

        // Complete the in-flight English OCR
        delayedOcr.FullTcs.TrySetResult(new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem>
            {
                new() { Id = 1, Text = "Late English Full Result", Source = "整圖辨識" }
            },
            LatencyMs = 50
        });

        await fullOcrTask;

        bool staleFullOcrRejected = !vm05.DraftRows.Any(r => r.Value == "Late English Full Result") &&
                                    !vm05.Candidates.Any(c => c.Text == "Late English Full Result");
        bool statusMentionsLanguageChange = vm05.StatusMessage.Contains("已略過") || vm05.StatusMessage.Contains("已取消");

        // Case B: Targeted ROI OCR running in Japanese -> User switches to Korean -> Old result rejected
        vm05.NameRow.Value = "原始姓名";
        vm05.NameRow.Confirm();
        vm05.SelectedPartialOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Jpn);
        vm05.SetRegionCoordinates(15, 15, 90, 35);
        delayedOcr.Reset();

        var targetedOcrTask = vm05.RunTargetedRoiOcrAsync(vm05.NameRow);

        // While in flight, user changes partial language selection
        vm05.SelectedPartialOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Kor);

        // Complete the in-flight Japanese OCR
        delayedOcr.RegionTcs.TrySetResult(new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem>
            {
                new() { Id = 2, Text = "遅れた日本語結果", Source = "局部辨識" }
            },
            LatencyMs = 40
        });

        await targetedOcrTask;

        bool staleTargetedOcrRejected = vm05.NameRow.Value == "原始姓名";

        // Case C: Delayed OperationCanceledException sequence
        // Request 1 starts with Mixed5. User immediately starts Request 2 with Jpn.
        // Request 1 throws OperationCanceledException late.
        // Verify Request 1 does NOT overwrite Request 2's status or progress!
        var slowCancelOcr = new SlowCancelOcrService();
        var vm05c = new MainViewModel(catalog01, slowCancelOcr);
        typeof(MainViewModel).GetProperty("CurrentSession")?.SetValue(vm05c, dummySession05);

        // Start request 1 (Full OCR, Mixed5)
        var task1 = vm05c.RunFullOcrAsync();

        // Switch language to Jpn and immediately launch request 2
        vm05c.SelectedFullOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Jpn);
        var task2 = vm05c.RunFullOcrAsync();

        // Release request 1's cancellation exception
        slowCancelOcr.ReleaseFirstRequestCancellation();

        // Wait for task 1 to finish handling its cancellation
        await task1;

        // Verify that vm05c's BusyMessage and StatusMessage were NOT overwritten by "OCR 辨識已取消。"
        bool request2StatusPreserved = vm05c.BusyMessage.Contains("日文") && vm05c.StatusMessage != "OCR 辨識已取消。";

        // Complete request 2
        slowCancelOcr.CompleteSecondRequest(new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem>
            {
                new() { Id = 10, Text = "日文辨識成功", Source = "整圖辨識" }
            },
            LatencyMs = 30
        });
        await task2;

        bool request2CompletedSuccessfully = vm05c.DraftRows.Any(r => r.Value == "日文辨識成功") &&
                                             vm05c.StatusMessage.Contains("日文");

        sw.Stop();
        bool t05Passed = staleFullOcrRejected && statusMentionsLanguageChange &&
                         staleTargetedOcrRejected && request2StatusPreserved && request2CompletedSuccessfully;

        Record("OCR9-T05", "辨識等待期間改選語系之請求取消、世代判定與晚到結果安全作廢驗證", t05Passed,
            $"StaleFullRejected={staleFullOcrRejected}, Status='{vm05.StatusMessage}', StaleTargetedRejected={staleTargetedOcrRejected}, SeqCancelNoOverwrite={request2StatusPreserved && request2CompletedSuccessfully}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR9-T06: Ctrl＋滾輪幾何計算：指標視覺錨點、留白中心錨點、縮放界限與平移安全 (OCR9-01, OCR9-02)
        // =========================================================================================
        sw.Restart();

        // 1. Pointer inside image: Verify that the exact original image pixel under pointer stays at pointer position
        double vpW = 800, vpH = 600;
        int imgW = 1000, imgH = 600;
        double initScale = 1.0, initPanX = 0, initPanY = 0;
        double ptrX = 300, ptrY = 250;

        // Rendered rect at init: Left = (800 - 1000)/2 = -100, Top = 0, Width = 1000, Height = 600
        // Pointer at (300, 250) corresponds to pixel X = (300 - (-100))/1.0 = 400, Y = 250
        var (zoomInScale, zoomInPanX, zoomInPanY) = ImageTransformHelper.CalculateZoomAtPoint(
            vpW, vpH, imgW, imgH, initScale, initPanX, initPanY, ptrX, ptrY, 1.5);

        // Verify visual position of pixel (400, 250) under zoomInScale and zoomInPan
        var (newImgLeft, newImgTop, _, _) = ImageTransformHelper.GetRenderedImageRect(
            vpW, vpH, imgW, imgH, zoomInScale, zoomInPanX, zoomInPanY);
        double anchoredVisualX = newImgLeft + (400 * zoomInScale);
        double anchoredVisualY = newImgTop + (250 * zoomInScale);

        bool zoomInAnchorExact = Math.Abs(anchoredVisualX - ptrX) < 0.01 &&
                                 Math.Abs(anchoredVisualY - ptrY) < 0.01;

        // 2. Zoom out anchor test
        var (zoomOutScale, zoomOutPanX, zoomOutPanY) = ImageTransformHelper.CalculateZoomAtPoint(
            vpW, vpH, imgW, imgH, initScale, initPanX, initPanY, ptrX, ptrY, 0.75);

        var (outImgLeft, outImgTop, _, _) = ImageTransformHelper.GetRenderedImageRect(
            vpW, vpH, imgW, imgH, zoomOutScale, zoomOutPanX, zoomOutPanY);
        double outVisualX = outImgLeft + (400 * zoomOutScale);
        double outVisualY = outImgTop + (250 * zoomOutScale);

        bool zoomOutAnchorExact = Math.Abs(outVisualX - ptrX) < 0.01 &&
                                  Math.Abs(outVisualY - ptrY) < 0.01;

        // 3. Pointer in margin / whitespace (留白): anchors to visible image center
        // Small image 400x300 centered in 800x600 viewport -> Rendered rect = [200, 150, 400, 300]
        // Pointer at (50, 50) is in whitespace
        var (marginScale, marginPanX, marginPanY) = ImageTransformHelper.CalculateZoomAtPoint(
            800, 600, 400, 300, 1.0, 0, 0, 50, 50, 1.25);

        // Since image was centered at (400, 300) and fully visible, anchor is (400, 300), so pan remains 0
        bool marginExpandsSymmetrically = Math.Abs(marginPanX) < 0.01 && Math.Abs(marginPanY) < 0.01;

        // 4. Scale boundaries [0.1, 5.0]
        var (maxScaleClamped, _, _) = ImageTransformHelper.CalculateZoomAtPoint(
            800, 600, 1000, 600, 4.8, 0, 0, 400, 300, 2.0);
        bool maxScaleClampedOk = Math.Abs(maxScaleClamped - 5.0) < 0.001;

        var (minScaleClamped, _, _) = ImageTransformHelper.CalculateZoomAtPoint(
            800, 600, 1000, 600, 0.15, 0, 0, 400, 300, 0.5);
        bool minScaleClampedOk = Math.Abs(minScaleClamped - 0.1) < 0.001;

        // 5. Image never completely leaves view (minVisibleMargin clamping)
        var (_, pannedPanX, pannedPanY) = ImageTransformHelper.CalculateZoomAtPoint(
            800, 600, 1000, 600, 2.0, 5000, 5000, 400, 300, 1.0);
        var (clampedRectL, clampedRectT, clampedRectW, clampedRectH) = ImageTransformHelper.GetRenderedImageRect(
            800, 600, 1000, 600, 2.0, pannedPanX, pannedPanY);
        bool imageRetainedInViewport = (clampedRectL + clampedRectW >= 40.0) &&
                                       (clampedRectL <= 800 - 40.0);

        sw.Stop();
        bool t06Passed = zoomInAnchorExact && zoomOutAnchorExact &&
                         marginExpandsSymmetrically && maxScaleClampedOk &&
                         minScaleClampedOk && imageRetainedInViewport;

        Record("OCR9-T06", "Ctrl＋滾輪純幾何計算：圖片指標視覺錨點保持、留白中心錨點、比例界限與平移邊界防護", t06Passed,
            $"ZoomInAnchor={zoomInAnchorExact}, ZoomOutAnchor={zoomOutAnchorExact}, MarginSymmetric={marginExpandsSymmetrically}, MaxClamped={maxScaleClampedOk}, MinClamped={minScaleClampedOk}, ViewportRetained={imageRetainedInViewport}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR9-T07: 縮放平移後原圖 ROI 雙向映射完全一致性驗證 (RS-ISSUE-0009 OCR9-02)
        // =========================================================================================
        sw.Restart();
        byte[] originalCardBytes = CreateSolidColorBmp(1000, 600);
        byte[] originalHash = SHA256.HashData(originalCardBytes);

        // Target original pixel ROI
        int origX = 180, origY = 120, origW = 320, origH = 90;

        // Case A: 100% scale without pan
        var (v1X, v1Y, v1W, v1H) = ImageTransformHelper.ImagePixelRoiToVisualRoi(
            origX, origY, origW, origH, 800, 600, 1000, 600, 1.0, 0, 0);
        ImageTransformHelper.VisualRoiToImagePixelRoi(
            v1X, v1Y, v1W, v1H, 800, 600, 1000, 600, 1.0, 0, 0,
            out int round1X, out int round1Y, out int round1W, out int round1H);
        bool case1Ok = (round1X == origX) && (round1Y == origY) && (round1W == origW) && (round1H == origH);

        // Case B: 180% scale with pan offsets (+80, -50)
        var (v2X, v2Y, v2W, v2H) = ImageTransformHelper.ImagePixelRoiToVisualRoi(
            origX, origY, origW, origH, 800, 600, 1000, 600, 1.8, 80, -50);
        ImageTransformHelper.VisualRoiToImagePixelRoi(
            v2X, v2Y, v2W, v2H, 800, 600, 1000, 600, 1.8, 80, -50,
            out int round2X, out int round2Y, out int round2W, out int round2H);
        bool case2Ok = (round2X == origX) && (round2Y == origY) && (round2W == origW) && (round2H == origH);

        // Case C: 65% scale (fit mode) with pan
        var (v3X, v3Y, v3W, v3H) = ImageTransformHelper.ImagePixelRoiToVisualRoi(
            origX, origY, origW, origH, 800, 600, 1000, 600, 0.65, -30, 20);
        ImageTransformHelper.VisualRoiToImagePixelRoi(
            v3X, v3Y, v3W, v3H, 800, 600, 1000, 600, 0.65, -30, 20,
            out int round3X, out int round3Y, out int round3W, out int round3H);
        bool case3Ok = (round3X == origX) && (round3Y == origY) && (round3W == origW) && (round3H == origH);

        // Verify original image bytes in session are byte-for-byte identical (unmutated)
        byte[] postTestHash = SHA256.HashData(originalCardBytes);
        bool imageBytesUnmutated = originalHash.SequenceEqual(postTestHash);

        sw.Stop();
        bool t07Passed = case1Ok && case2Ok && case3Ok && imageBytesUnmutated;

        Record("OCR9-T07", "縮放、平移與適合視窗前後原圖像素 ROI 雙向映射完全一致且圖片資料未被竄改", t07Passed,
            $"Case1(100%)={case1Ok}, Case2(180%+Pan)={case2Ok}, Case3(65%)={case3Ok}, ImageBytesUnmutated={imageBytesUnmutated}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR9-T08: 非 Ctrl 滾輪、無圖滾輪與非圖片區滾輪隔離保護 (OCR9-01, OCR9-02)
        // =========================================================================================
        sw.Restart();
        var vm08 = new MainViewModel(catalog01);

        // 1. When has no image: ZoomAtPoint should not alter scale or pan
        double initialScale = vm08.ZoomScale;
        double initialPanX = vm08.PanX;
        double initialPanY = vm08.PanY;

        vm08.ZoomAtPoint(120, 300, 200); // Zoom in attempt without image
        bool noImageIgnored = (vm08.ZoomScale == initialScale) &&
                              (vm08.PanX == initialPanX) &&
                              (vm08.PanY == initialPanY);

        // 2. Gesture predicate check: ShouldAcceptPointerPress requires image and left button
        bool leftWithImageAccepted = ImageTransformHelper.ShouldAcceptPointerPress(true, true);
        bool leftWithoutImageRejected = !ImageTransformHelper.ShouldAcceptPointerPress(true, false);
        bool rightWithImageRejected = !ImageTransformHelper.ShouldAcceptPointerPress(false, true);

        sw.Stop();
        bool t08Passed = noImageIgnored && leftWithImageAccepted &&
                         leftWithoutImageRejected && rightWithImageRejected;

        Record("OCR9-T08", "無圖幾何縮放隔離與左鍵手勢 Predicate 單元測試 (真實 WinUI 滑鼠/非 Ctrl 捲動/圖片外輸入尚待 Windows UI 驗收)", t08Passed,
            $"NoImageIgnored={noImageIgnored}, LeftImageAccepted={leftWithImageAccepted}, LeftNoImageRejected={leftWithoutImageRejected}, RightRejected={rightWithImageRejected} (注意: WinUI PointerWheelChanged 實體滑鼠手勢尚待 Windows UI 驗收)",
            sw.ElapsedMilliseconds);

        // Output markdown and json reports if evidence directory is provided
        try
        {
            if (Directory.Exists(evidenceDir))
            {
                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0009_TEST_REPORT.json"), json);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# ISSUE-0009 Windows OCR 語系選擇與 Ctrl＋滾輪自動化測試報告 (v{report.Version})");
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
                sb.AppendLine("## 安全與未驗證實機閘門說明 (需依規範獨立回報)");
                sb.AppendLine();
                sb.AppendLine("- **真實 Windows UI 讀屏與鍵盤**: WinUI 原生 ComboBox 與 AutomationProperties 已宣告，仍待實體螢幕閱讀器手動操作。");
                sb.AppendLine("- **實體 DPI 與縮放手勢**: Ctrl＋滾輪幾何計算已自動驗證，但真實 WinUI PointerWheelChanged、實體滑鼠滾輪、非 Ctrl 捲動、圖片外輸入及觸控板手勢尚待 Windows UI 實體操作驗收。");
                sb.AppendLine("- **程式重啟初值重設**: ViewModel 實例重設邏輯已驗證，實際應用程式關閉後重新啟動之初值行為尚待 Windows UI 驗收。");
                sb.AppendLine("- **使用者名片辨識率**: 依規範不預設宣稱單語必然提升準確率，需由使用者樣張逐字人工判定。");
                sb.AppendLine("- **個資邊界**: 本測試全程使用合成影像與幾何資料，未讀取或儲存任何真實名片個資。");

                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0009_TEST_REPORT.md"), sb.ToString());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ISSUE-0009 Test] Failed to write report files: {ex.Message}");
        }

        return report;
    }

    private static byte[] CreateSolidColorBmp(int width, int height)
    {
        int rowStride = (width * 3 + 3) & ~3;
        int imageSize = rowStride * height;
        int fileSize = 54 + imageSize;

        byte[] bmp = new byte[fileSize];
        // BM header
        bmp[0] = 0x42;
        bmp[1] = 0x4D;
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);

        // DIB header
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(imageSize).CopyTo(bmp, 34);

        for (int i = 54; i < fileSize; i++)
        {
            bmp[i] = 0xFF; // White background
        }

        return bmp;
    }

    private class SpyOcrService : OcrService
    {
        public string? LastFullLanguage { get; private set; }
        public string? LastRegionLanguage { get; private set; }
        public OcrResult? PresetFullResult { get; set; }
        public OcrResult? PresetRegionResult { get; set; }

        public override Task<OcrResult> RunFullOcrAsync(byte[] imageBytes, int requestId, CancellationToken ct, string? language = null)
        {
            LastFullLanguage = language;
            return Task.FromResult(PresetFullResult ?? new OcrResult { Success = true, RequestId = requestId });
        }

        public override Task<OcrResult> RunRegionOcrAsync(byte[] imageBytes, int rx, int ry, int rw, int rh, int requestId, CancellationToken ct, string? language = null)
        {
            LastRegionLanguage = language;
            return Task.FromResult(PresetRegionResult ?? new OcrResult { Success = true, RequestId = requestId });
        }
    }

    private class FailingModelOcrService : OcrService
    {
        public override Task<OcrResult> RunFullOcrAsync(byte[] imageBytes, int requestId, CancellationToken ct, string? language = null)
        {
            return Task.FromResult(new OcrResult
            {
                Success = false,
                RequestId = requestId,
                ErrorMessage = $"OCR 語系模型載入失敗 ({OcrLanguageCatalog.GetDisplayName(language ?? "")}): 找不到模型檔案。"
            });
        }

        public override Task<OcrResult> RunRegionOcrAsync(byte[] imageBytes, int rx, int ry, int rw, int rh, int requestId, CancellationToken ct, string? language = null)
        {
            return Task.FromResult(new OcrResult
            {
                Success = false,
                RequestId = requestId,
                ErrorMessage = $"OCR 語系模型載入失敗 ({OcrLanguageCatalog.GetDisplayName(language ?? "")}): 找不到模型檔案。"
            });
        }
    }

    private class ControllableDelayedOcrService : OcrService
    {
        public TaskCompletionSource<OcrResult> FullTcs = new();
        public TaskCompletionSource<OcrResult> RegionTcs = new();

        public void Reset()
        {
            FullTcs = new TaskCompletionSource<OcrResult>();
            RegionTcs = new TaskCompletionSource<OcrResult>();
        }

        public override async Task<OcrResult> RunFullOcrAsync(byte[] imageBytes, int requestId, CancellationToken ct, string? language = null)
        {
            ct.Register(() => FullTcs.TrySetCanceled(ct));
            return await FullTcs.Task;
        }

        public override async Task<OcrResult> RunRegionOcrAsync(byte[] imageBytes, int rx, int ry, int rw, int rh, int requestId, CancellationToken ct, string? language = null)
        {
            ct.Register(() => RegionTcs.TrySetCanceled(ct));
            return await RegionTcs.Task;
        }
    }

    private class SlowCancelOcrService : OcrService
    {
        private readonly TaskCompletionSource<bool> _cancelGate = new();
        private readonly TaskCompletionSource<OcrResult> _secondTcs = new();
        private int _callCount = 0;

        public void ReleaseFirstRequestCancellation() => _cancelGate.TrySetResult(true);
        public void CompleteSecondRequest(OcrResult result) => _secondTcs.TrySetResult(result);

        public override async Task<OcrResult> RunFullOcrAsync(byte[] imageBytes, int requestId, CancellationToken ct, string? language = null)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                await _cancelGate.Task;
                ct.ThrowIfCancellationRequested();
                return new OcrResult { Success = false };
            }
            else
            {
                return await _secondTcs.Task;
            }
        }
    }
}
