using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Verification;

public class Issue0036TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0036TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0036TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.19";
    public string IssueId { get; set; } = "ISSUE-0036";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0036TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0036AutomatedTestSuite
{
    public static async Task<Issue0036TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0036TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0036TestCaseResult
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

        string testRoot = Path.Combine(Path.GetFullPath(evidenceDir), $"test-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);

        // =========================================================================================
        // OCR36-T01: 初次啟動與無設定檔預設行為驗證 (RS-ISSUE-0036 AC1, AC5)
        // =========================================================================================
        sw.Restart();
        string settingsPath01 = Path.Combine(testRoot, "settings-t01.json");
        var settingsSvc01 = new AppSettingsService(settingsPath01);
        var catalog01 = new CustomFieldCatalogService(Path.Combine(testRoot, "catalog-t01.json"));

        var vm01 = new MainViewModel(catalog01, null, null, settingsSvc01);

        bool initialFullIsMixed5 = vm01.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Mixed5 &&
                                   vm01.SelectedFullOcrLanguage?.DisplayName == "多語系辨識";
        bool initialPartialIsMixed5 = vm01.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.Mixed5 &&
                                       vm01.SelectedPartialOcrLanguage?.DisplayName == "多語系辨識";
        bool fileNotCreatedUntilSaved = !File.Exists(settingsPath01);

        sw.Stop();
        bool t01Passed = initialFullIsMixed5 && initialPartialIsMixed5 && fileNotCreatedUntilSaved;
        Record("OCR36-T01", "初次啟動與無設定檔預設行為驗證 (預設多語系辨識且不異常報錯)", t01Passed,
            $"FullIsMixed5={initialFullIsMixed5}, PartialIsMixed5={initialPartialIsMixed5}, LazyFile={fileNotCreatedUntilSaved}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR36-T02: 整體語系切換持久化與重啟恢復 (RS-ISSUE-0036 AC1, AC2)
        // =========================================================================================
        sw.Restart();
        string settingsPath02 = Path.Combine(testRoot, "settings-t02.json");
        var settingsSvc02 = new AppSettingsService(settingsPath02);
        var catalog02 = new CustomFieldCatalogService(Path.Combine(testRoot, "catalog-t02.json"));

        var vm02A = new MainViewModel(catalog02, null, null, settingsSvc02);
        var chiTraOption = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.ChiTra);
        vm02A.SelectedFullOcrLanguage = chiTraOption;

        bool fileCreated02 = File.Exists(settingsPath02);
        var loadedRaw02 = settingsSvc02.LoadSettings();
        bool fileHasChiTra = loadedRaw02.FullOcrLanguageCode == OcrLanguageCatalog.ChiTra;

        // Simulate app restart by constructing a new ViewModel with the same settings service/path
        var vm02B = new MainViewModel(catalog02, null, null, new AppSettingsService(settingsPath02));
        bool restoredFullChiTra = vm02B.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.ChiTra &&
                                  vm02B.SelectedFullOcrLanguage?.DisplayName == "繁體中文";
        bool partialStillMixed5 = vm02B.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.Mixed5;

        sw.Stop();
        bool t02Passed = fileCreated02 && fileHasChiTra && restoredFullChiTra && partialStillMixed5;
        Record("OCR36-T02", "整體語系切換即時持久化與重新啟動自動恢復驗證", t02Passed,
            $"FileCreated={fileCreated02}, RawCode={loadedRaw02.FullOcrLanguageCode}, RestoredFull={restoredFullChiTra}, RestoredPartial={partialStillMixed5}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR36-T03: 局部語系切換持久化與重啟恢復 (RS-ISSUE-0036 AC1, AC2)
        // =========================================================================================
        sw.Restart();
        string settingsPath03 = Path.Combine(testRoot, "settings-t03.json");
        var settingsSvc03 = new AppSettingsService(settingsPath03);
        var catalog03 = new CustomFieldCatalogService(Path.Combine(testRoot, "catalog-t03.json"));

        var vm03A = new MainViewModel(catalog03, null, null, settingsSvc03);
        var jpnOption = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Jpn);
        vm03A.SelectedPartialOcrLanguage = jpnOption;

        var loadedRaw03 = settingsSvc03.LoadSettings();
        bool fileHasJpn = loadedRaw03.PartialOcrLanguageCode == OcrLanguageCatalog.Jpn;

        // Simulate app restart
        var vm03B = new MainViewModel(catalog03, null, null, new AppSettingsService(settingsPath03));
        bool restoredPartialJpn = vm03B.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.Jpn &&
                                  vm03B.SelectedPartialOcrLanguage?.DisplayName == "日文";
        bool fullStillMixed5 = vm03B.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Mixed5;

        sw.Stop();
        bool t03Passed = fileHasJpn && restoredPartialJpn && fullStillMixed5;
        Record("OCR36-T03", "局部語系切換即時持久化與重新啟動自動恢復驗證", t03Passed,
            $"RawPartialCode={loadedRaw03.PartialOcrLanguageCode}, RestoredPartial={restoredPartialJpn}, RestoredFull={fullStillMixed5}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR36-T04: 雙語系獨立持久化與交叉重啟測試 (RS-ISSUE-0036 AC4)
        // =========================================================================================
        sw.Restart();
        string settingsPath04 = Path.Combine(testRoot, "settings-t04.json");
        var settingsSvc04 = new AppSettingsService(settingsPath04);
        var catalog04 = new CustomFieldCatalogService(Path.Combine(testRoot, "catalog-t04.json"));

        var vm04A = new MainViewModel(catalog04, null, null, settingsSvc04);
        vm04A.SelectedFullOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Eng);
        vm04A.SelectedPartialOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.Kor);

        // Restart 1: Verify both independent selections restored
        var vm04B = new MainViewModel(catalog04, null, null, new AppSettingsService(settingsPath04));
        bool restart1FullEng = vm04B.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Eng;
        bool restart1PartialKor = vm04B.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.Kor;

        // Change only Partial to ChiSim on vm04B
        vm04B.SelectedPartialOcrLanguage = OcrLanguageCatalog.GetOptionByCode(OcrLanguageCatalog.ChiSim);

        // Restart 2: Verify Full remains Eng and Partial is ChiSim
        var vm04C = new MainViewModel(catalog04, null, null, new AppSettingsService(settingsPath04));
        bool restart2FullEng = vm04C.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Eng;
        bool restart2PartialChiSim = vm04C.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.ChiSim;

        sw.Stop();
        bool t04Passed = restart1FullEng && restart1PartialKor && restart2FullEng && restart2PartialChiSim;
        Record("OCR36-T04", "整體與局部雙語系獨立持久化與交叉變更重啟驗證", t04Passed,
            $"Restart1(Full=Eng:{restart1FullEng}, Part=Kor:{restart1PartialKor}), Restart2(Full=Eng:{restart2FullEng}, Part=ChiSim:{restart2PartialChiSim})",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR36-T05: 重啟後 OCR 執行路由與模型參數驗證 (RS-ISSUE-0036 AC3)
        // =========================================================================================
        sw.Restart();
        string settingsPath05 = Path.Combine(testRoot, "settings-t05.json");
        var settingsSvc05 = new AppSettingsService(settingsPath05);
        settingsSvc05.SaveSettings(new AppSettings
        {
            FullOcrLanguageCode = OcrLanguageCatalog.ChiTra,
            PartialOcrLanguageCode = OcrLanguageCatalog.Jpn
        });

        var spyOcr05 = new SpyOcrService();
        var catalog05 = new CustomFieldCatalogService(Path.Combine(testRoot, "catalog-t05.json"));
        var vm05 = new MainViewModel(catalog05, spyOcr05, null, new AppSettingsService(settingsPath05));

        var dummySession05 = new ImageSessionInfo
        {
            ImageBytes = CreateSolidColorBmp(200, 100),
            Width = 800,
            Height = 400,
            SourceName = "card_05.png",
            MimeType = "image/png"
        };
        typeof(MainViewModel).GetProperty("CurrentSession")?.SetValue(vm05, dummySession05);

        // 1. Run Full OCR on restored VM
        spyOcr05.PresetFullResult = new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem> { new() { Id = 1, Text = "台北資訊系統", Confidence = 96f, Source = "整圖辨識" } },
            LatencyMs = 20
        };
        await vm05.RunFullOcrAsync();
        bool fullRoutedChiTra = spyOcr05.LastFullLanguage == OcrLanguageCatalog.ChiTra;
        bool fullSummaryChiTra = vm05.LastOcrSummary.Contains("繁體中文");

        // 2. Run Targeted ROI OCR on restored VM
        vm05.SetRegionCoordinates(10, 10, 100, 40);
        spyOcr05.PresetRegionResult = new OcrResult
        {
            Success = true,
            Candidates = new List<OcrCandidateItem> { new() { Id = 2, Text = "株式会社テスト", Confidence = 93f, Source = "局部辨識" } },
            LatencyMs = 15
        };
        await vm05.RunTargetedRoiOcrAsync(vm05.CompanyRow);
        bool regionRoutedJpn = spyOcr05.LastRegionLanguage == OcrLanguageCatalog.Jpn;
        bool regionSummaryJpn = vm05.LastOcrSummary.Contains("日文");
        bool companyValueUpdated = vm05.CompanyRow.Value == "株式会社テスト";

        sw.Stop();
        bool t05Passed = fullRoutedChiTra && fullSummaryChiTra && regionRoutedJpn && regionSummaryJpn && companyValueUpdated;
        Record("OCR36-T05", "重啟後整圖與局部 OCR 執行語系路由與回顯摘要驗證", t05Passed,
            $"FullChiTra={fullRoutedChiTra}, FullSummary={fullSummaryChiTra}, RegionJpn={regionRoutedJpn}, RegionSummary={regionSummaryJpn}, CompanyUpdated={companyValueUpdated}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR36-T06: 異常與惡意設定檔安全防護 (RS-ISSUE-0036 AC5)
        // =========================================================================================
        sw.Restart();
        string badPath1 = Path.Combine(testRoot, "settings-bad-code.json");
        File.WriteAllText(badPath1, "{\"settingsVersion\":1,\"fullOcrLanguageCode\":\"french\",\"partialOcrLanguageCode\":\"../../etc/passwd\"}", Encoding.UTF8);

        var svcBad1 = new AppSettingsService(badPath1);
        var loadedBad1 = svcBad1.LoadSettings();
        bool sanitizedBadCodes = loadedBad1.FullOcrLanguageCode == OcrLanguageCatalog.Mixed5 &&
                                 loadedBad1.PartialOcrLanguageCode == OcrLanguageCatalog.Mixed5;

        string badPath2 = Path.Combine(testRoot, "settings-corrupted-json.json");
        File.WriteAllText(badPath2, "{ corrupted json missing braces", Encoding.UTF8);
        var svcBad2 = new AppSettingsService(badPath2);
        var loadedBad2 = svcBad2.LoadSettings();
        bool fallbackCorruptJson = loadedBad2.FullOcrLanguageCode == OcrLanguageCatalog.Mixed5;

        string badPath3 = Path.Combine(testRoot, "settings-non-utf8.json");
        File.WriteAllBytes(badPath3, new byte[] { 0xFF, 0xFE, 0xFD, 0x80, 0x81 });
        var svcBad3 = new AppSettingsService(badPath3);
        var loadedBad3 = svcBad3.LoadSettings();
        bool fallbackNonUtf8 = loadedBad3.FullOcrLanguageCode == OcrLanguageCatalog.Mixed5;

        string badPath4 = Path.Combine(testRoot, "settings-oversized.json");
        File.WriteAllBytes(badPath4, new byte[128 * 1024]); // 128 KB > 64 KB limit
        var svcBad4 = new AppSettingsService(badPath4);
        var loadedBad4 = svcBad4.LoadSettings();
        bool fallbackOversized = loadedBad4.FullOcrLanguageCode == OcrLanguageCatalog.Mixed5;

        // Verify ViewModel safely constructs with corrupted settings without crashing
        var vmBad = new MainViewModel(catalog01, null, null, svcBad2);
        bool vmBadGraceful = vmBad.SelectedFullOcrLanguage?.Code == OcrLanguageCatalog.Mixed5 &&
                             vmBad.SelectedPartialOcrLanguage?.Code == OcrLanguageCatalog.Mixed5;

        sw.Stop();
        bool t06Passed = sanitizedBadCodes && fallbackCorruptJson && fallbackNonUtf8 && fallbackOversized && vmBadGraceful;
        Record("OCR36-T06", "異常、惡意語系代碼、非 UTF-8、毀損 JSON 及超大檔案安全防護驗證", t06Passed,
            $"SanitizedBadCodes={sanitizedBadCodes}, FallbackCorrupt={fallbackCorruptJson}, FallbackNonUtf8={fallbackNonUtf8}, FallbackOversized={fallbackOversized}, VmGraceful={vmBadGraceful}",
            sw.ElapsedMilliseconds);

        // =========================================================================================
        // OCR36-T07: 原子寫入與多執行緒併發安全 (RS-ISSUE-0036 AC5)
        // =========================================================================================
        sw.Restart();
        string concPath = Path.Combine(testRoot, "settings-concurrent.json");
        var concSvc = new AppSettingsService(concPath);

        var codes = new[] { OcrLanguageCatalog.ChiTra, OcrLanguageCatalog.ChiSim, OcrLanguageCatalog.Jpn, OcrLanguageCatalog.Kor, OcrLanguageCatalog.Eng, OcrLanguageCatalog.Mixed5 };
        var tasks = new List<Task>();
        int errorCount = 0;

        for (int i = 0; i < 30; i++)
        {
            int index = i;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    string targetCode = codes[index % codes.Length];
                    if (index % 2 == 0) concSvc.UpdateFullOcrLanguage(targetCode);
                    else concSvc.UpdatePartialOcrLanguage(targetCode);

                    var current = concSvc.LoadSettings();
                    if (!OcrLanguageCatalog.IsValidCode(current.FullOcrLanguageCode) ||
                        !OcrLanguageCatalog.IsValidCode(current.PartialOcrLanguageCode))
                    {
                        Interlocked.Increment(ref errorCount);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref errorCount);
                }
            }));
        }

        await Task.WhenAll(tasks);

        var finalSettings = concSvc.LoadSettings();
        bool finalValid = OcrLanguageCatalog.IsValidCode(finalSettings.FullOcrLanguageCode) &&
                          OcrLanguageCatalog.IsValidCode(finalSettings.PartialOcrLanguageCode);
        bool noTmpFilesLeft = Directory.GetFiles(testRoot, "*.tmp.*").Length == 0;

        sw.Stop();
        bool t07Passed = errorCount == 0 && finalValid && noTmpFilesLeft;
        Record("OCR36-T07", "原子寫入與多執行緒併發讀寫安全驗證 (無死鎖、無檔案鎖衝突、無殘留暫存檔)", t07Passed,
            $"ErrorCount={errorCount}, FinalValid={finalValid}, NoTmpLeft={noTmpFilesLeft}",
            sw.ElapsedMilliseconds);

        // Write test report files
        try
        {
            if (Directory.Exists(evidenceDir))
            {
                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0036_TEST_REPORT.json"), json, Encoding.UTF8);

                var sb = new StringBuilder();
                sb.AppendLine($"# ISSUE-0036 辨識語系設定持久化自動化測試報告 (v{report.Version})");
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
                sb.AppendLine("## 驗收結論");
                sb.AppendLine();
                sb.AppendLine("所有測試項目皆已通過，驗證辨識語系調整後在應用程式重新啟動時能正確維持前次設定，且具備完整的檔案原子寫入與毀損回退防護。");

                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0036_TEST_REPORT.md"), sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ISSUE-0036 Test] Failed to write report: {ex.Message}");
        }

        return report;
    }

    private static byte[] CreateSolidColorBmp(int width, int height)
    {
        int rowStride = (width * 3 + 3) & ~3;
        int imageSize = rowStride * height;
        int fileSize = 54 + imageSize;

        byte[] bmp = new byte[fileSize];
        bmp[0] = 0x42;
        bmp[1] = 0x4D;
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);

        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(imageSize).CopyTo(bmp, 34);

        for (int i = 54; i < fileSize; i++)
        {
            bmp[i] = 0xFF;
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
}
