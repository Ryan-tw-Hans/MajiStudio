using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MajiOCR_Windows.Verification;

namespace MajiOCR_Windows;

public static class Program
{
    // Explicit diagnostic launch only; ordinary launches use the actual EXE directory.
    public static string? GuiVerificationRoot { get; private set; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    public static string GetAppVersion()
    {
        var infoVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        return !string.IsNullOrWhiteSpace(infoVersion) ? infoVersion : "0.1.0-beta.19";
    }

    public static string ResolveEvidenceDir(string[] args, string defaultSubDir = "ISSUE-0006")
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--evidence-dir", StringComparison.OrdinalIgnoreCase))
            {
                string custom = args[i + 1];
                if (!Directory.Exists(custom)) Directory.CreateDirectory(custom);
                return custom;
            }
        }

        string current = Directory.GetCurrentDirectory();
        string checkCurrent = Path.Combine(current, "TestReports", "evidence", defaultSubDir);
        if (Directory.Exists(checkCurrent)) return checkCurrent;

        string checkBase = Path.Combine(AppContext.BaseDirectory, "TestReports", "evidence", defaultSubDir);
        if (Directory.Exists(checkBase)) return checkBase;

        string? dir = current;
        for (int i = 0; i < 5 && dir != null; i++)
        {
            string p = Path.Combine(dir, "TestReports", "evidence", defaultSubDir);
            if (Directory.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName;
        }

        string fallback = Path.Combine(current, "TestReports", "evidence", defaultSubDir);
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static int RunW0(string[] args, string evidenceDir)
    {
        Console.WriteLine("[MajiOCR.Windows CLI] Running W0 Technical Verification...");
        var result = W0Verifier.RunVerification(null, evidenceDir);
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);

        try
        {
            if (Directory.Exists(evidenceDir))
            {
                File.WriteAllText(Path.Combine(evidenceDir, "W0_VERIFICATION_REPORT.json"), json, Encoding.UTF8);

                var sb = new StringBuilder();
                sb.AppendLine("# ISSUE-0005 W0 技術驗證報告 (無個資)");
                sb.AppendLine();
                sb.AppendLine($"- 驗證時間: {result.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"- 作業系統: {result.OsVersion}");
                sb.AppendLine($"- 處理器架構: {result.ProcessArchitecture}");
                sb.AppendLine($"- .NET 執行階段: {result.DotNetVersion}");
                sb.AppendLine($"- Tessdata 路徑: {result.TessdataPath}");
                sb.AppendLine($"- 五語模型獨立載入: eng={result.SingleLanguageModelLoad.GetValueOrDefault("eng")}, chi_tra={result.SingleLanguageModelLoad.GetValueOrDefault("chi_tra")}, chi_sim={result.SingleLanguageModelLoad.GetValueOrDefault("chi_sim")}, jpn={result.SingleLanguageModelLoad.GetValueOrDefault("jpn")}, kor={result.SingleLanguageModelLoad.GetValueOrDefault("kor")}");
                sb.AppendLine($"- 五語聯合載入: {result.CombinedFiveLanguageLoad} (耗時: {result.EngineInitTimeMs} ms)");
                sb.AppendLine($"- 全圖 OCR 通過: {result.FullImagePassed} (耗時: {result.FullImageLatencyMs} ms, 平均信心度: {result.FullImageConfidence:P1})");
                sb.AppendLine($"- 局部區域 OCR 通過: {result.RegionOcrPassed} (耗時: {result.RegionOcrLatencyMs} ms, 選框: X={result.RegionBox.X}, Y={result.RegionBox.Y}, W={result.RegionBox.Width}, H={result.RegionBox.Height})");
                sb.AppendLine($"- 記憶體使用 (Working Set): {result.PeakMemoryBytes / (1024 * 1024.0):F2} MB");
                sb.AppendLine($"- W0 綜合判定: {(result.OverallW0Passed ? "PASS" : "FAIL")}");
                sb.AppendLine($"- 結論: {result.Conclusion}");
                sb.AppendLine();
                sb.AppendLine("## 辨識行摘要");
                foreach (var line in result.ExtractedLines)
                {
                    sb.AppendLine($"- [{line.X},{line.Y},{line.Width},{line.Height}] ({line.Confidence:P0}): {line.Text}");
                }
                File.WriteAllText(Path.Combine(evidenceDir, "W0_VERIFICATION_REPORT.md"), sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[W0] Failed to write report: {ex.Message}");
        }

        return result.OverallW0Passed ? 0 : 1;
    }

    private static int RunW1(string[] args, string evidenceDir)
    {
        Console.WriteLine("[MajiOCR.Windows CLI] Running W1 Automated Test Suite...");
        var testTask = Task.Run(async () => await W1AutomatedTestSuite.RunAllTestsAsync(evidenceDir));
        testTask.Wait();
        var testReport = testTask.Result;

        string json = JsonSerializer.Serialize(testReport, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);

        try
        {
            if (Directory.Exists(evidenceDir))
            {
                File.WriteAllText(Path.Combine(evidenceDir, "W1_TEST_REPORT.json"), json, Encoding.UTF8);

                var sb = new StringBuilder();
                sb.AppendLine("# ISSUE-0005 W1 自動化測試報告");
                sb.AppendLine();
                sb.AppendLine($"- 測試時間: {testReport.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"- 受測版本: {testReport.Version}");
                sb.AppendLine($"- 執行環境: {testReport.Environment}");
                sb.AppendLine($"- 自動化測試總數: {testReport.TotalTests} 項");
                sb.AppendLine($"- 通過: {testReport.PassCount} 項");
                sb.AppendLine($"- 失敗: {testReport.FailCount} 項");
                sb.AppendLine($"- 自動化總評結果: {(testReport.OverallSuccess ? "PASS" : "FAIL")}");
                sb.AppendLine();
                sb.AppendLine("## 自動化驗證項目 (Automated Verified)");
                sb.AppendLine();
                sb.AppendLine("| 測試代號 | 描述 | 結果 | 耗時 | 詳細資訊 |");
                sb.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var tc in testReport.TestCases)
                {
                    string status = tc.Passed ? "PASS" : "**FAIL**";
                    sb.AppendLine($"| {tc.TestId} | {tc.Description} | {status} | {tc.LatencyMs} ms | {tc.Details.Replace("|", "\\|")} |");
                }
                sb.AppendLine();
                sb.AppendLine("## 環境與實機閘門項目 (需實體/乾淨機手動驗證，不宣稱 PASS)");
                sb.AppendLine();
                sb.AppendLine("- **W1-T14 (無障礙與不同 DPI 實機)**: 未在真實高對比/無障礙螢幕閱讀器下做實體人類操作 (標記為未驗證)。");
                sb.AppendLine("- **W1-T15 (乾淨 Win11 x64 斷網首次啟動)**: 單一 EXE 封裝完成且本機執行通過，但乾淨脫機實體機首次啟動尚未執行 (標記為未驗證)。");
                sb.AppendLine("- **人類 UI 體驗與名片準確率驗收**: 需等待人類使用者以實際名片進行驗收 (標記為待驗收)。");

                File.WriteAllText(Path.Combine(evidenceDir, "W1_TEST_REPORT.md"), sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[W1 Test] Failed to write report: {ex.Message}");
        }

        return testReport.OverallSuccess ? 0 : 1;
    }

    private static int RunW2(string[] args, string evidenceDir)
    {
        Console.WriteLine($"[MajiOCR.Windows CLI] Running ISSUE-0006 Automated Test Suite (Windows {GetAppVersion()})...");
        var testTask = Task.Run(async () => await W2AutomatedTestSuite.RunAllTestsAsync(evidenceDir));
        testTask.Wait();
        var testReport = testTask.Result;

        string json = JsonSerializer.Serialize(testReport, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);

        try
        {
            if (Directory.Exists(evidenceDir))
            {
                File.WriteAllText(Path.Combine(evidenceDir, "W2_TEST_REPORT.json"), json, Encoding.UTF8);
                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0006_TEST_REPORT.json"), json, Encoding.UTF8);

                var sb = new StringBuilder();
                sb.AppendLine($"# ISSUE-0006 Windows 自動化測試報告 (v{testReport.Version})");
                sb.AppendLine();
                sb.AppendLine($"- 測試時間: {testReport.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"- 受測版本: {testReport.Version}");
                sb.AppendLine($"- Issue ID: {testReport.IssueId}");
                sb.AppendLine($"- 執行環境: {testReport.Environment}");
                sb.AppendLine($"- 自動化測試總數: {testReport.TotalTests} 項");
                sb.AppendLine($"- 通過: {testReport.PassCount} 項");
                sb.AppendLine($"- 失敗: {testReport.FailCount} 項");
                sb.AppendLine($"- 自動化總評結果: {(testReport.OverallSuccess ? "**PASS**" : "**FAIL**")}");
                sb.AppendLine();
                sb.AppendLine("## 自動化驗證項目 (Automated Verified)");
                sb.AppendLine();
                sb.AppendLine("| 測試代號 | 描述 | 結果 | 耗時 | 詳細資訊 |");
                sb.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var tc in testReport.TestCases)
                {
                    string status = tc.Passed ? "PASS" : "**FAIL**";
                    sb.AppendLine($"| {tc.TestId} | {tc.Description} | {status} | {tc.LatencyMs} ms | {tc.Details.Replace("|", "\\|")} |");
                }
                sb.AppendLine();
                sb.AppendLine("## 環境與實機閘門項目 (需實體/乾淨機手動驗證，不宣稱 PASS)");
                sb.AppendLine();
                sb.AppendLine("- **T13 (真實 Windows 11 乾淨離線啟動)**: 單一 EXE 封裝完成且本機執行通過，但乾淨脫機實體機首次啟動尚未執行 (標記為未驗證)。");
                sb.AppendLine("- **T14 (無障礙與不同 DPI 實機)**: 未在真實高對比/無障礙螢幕閱讀器下做實體人類操作 (標記為未驗證)。");
                sb.AppendLine("- **人類名片準確率驗收**: 需等待人類使用者以實際名片進行驗收 (標記為待驗收)。");

                File.WriteAllText(Path.Combine(evidenceDir, "W2_TEST_REPORT.md"), sb.ToString(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0006_TEST_REPORT.md"), sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[W2 Test] Failed to write report: {ex.Message}");
        }

        return testReport.OverallSuccess ? 0 : 1;
    }

    private static int RunIssue0007(string[] args, string evidenceDir)
    {
        Console.WriteLine($"[MajiOCR.Windows CLI] Running ISSUE-0007 Automated Test Suite (Windows {GetAppVersion()})...");
        var testTask = Task.Run(async () => await Issue0007AutomatedTestSuite.RunAllTestsAsync(evidenceDir));
        testTask.Wait();
        var testReport = testTask.Result;

        string json = JsonSerializer.Serialize(testReport, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);

        try
        {
            if (Directory.Exists(evidenceDir))
            {
                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0007_TEST_REPORT.json"), json, Encoding.UTF8);

                var sb = new StringBuilder();
                sb.AppendLine("# ISSUE-0007 Windows OCR 繁中複核原型自動化測試報告");
                sb.AppendLine();
                sb.AppendLine($"- 測試時間: {testReport.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"- 受測版本: {testReport.Version}");
                sb.AppendLine($"- Issue ID: {testReport.IssueId}");
                sb.AppendLine($"- 執行環境: {testReport.Environment}");
                sb.AppendLine($"- 自動化測試總數: {testReport.TotalTests} 項");
                sb.AppendLine($"- 通過: {testReport.PassCount} 項");
                sb.AppendLine($"- 失敗: {testReport.FailCount} 項");
                sb.AppendLine($"- 自動化總評結果: {(testReport.OverallSuccess ? "**PASS**" : "**FAIL**")}");
                sb.AppendLine();
                sb.AppendLine("## 自動化驗證項目 (Automated Verified)");
                sb.AppendLine();
                sb.AppendLine("| 測試代號 | 描述 | 結果 | 耗時 | 詳細資訊 |");
                sb.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var tc in testReport.TestCases)
                {
                    string status = tc.Passed ? "PASS" : "**FAIL**";
                    sb.AppendLine($"| {tc.TestId} | {tc.Description} | {status} | {tc.LatencyMs} ms | {tc.Details.Replace("|", "\\|")} |");
                }
                sb.AppendLine();
                sb.AppendLine("## 安全與驗證邊界說明");
                sb.AppendLine();
                sb.AppendLine("- 本測試僅以無個資合成名片與注入之假候選驗證繁中複核與採用規則。");
                sb.AppendLine("- 未讀取、儲存或洩漏任何真實名片樣本或個人資料。");
                sb.AppendLine("- 本測試 PASS 僅代表後端原型之規則符合規格，不代表真實名片之人工準確率驗收。");

                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0007_TEST_REPORT.md"), sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ISSUE-0007 Test] Failed to write report: {ex.Message}");
        }

        return testReport.OverallSuccess ? 0 : 1;
    }

    private static int RunIssue0008(string[] args, string evidenceDir)
    {
        Console.WriteLine($"[MajiOCR.Windows CLI] Running ISSUE-0008 Automated Test Suite (Windows {GetAppVersion()})...");
        var testTask = Task.Run(async () => await Issue0008AutomatedTestSuite.RunAllTestsAsync(evidenceDir));
        testTask.Wait();
        var testReport = testTask.Result;

        string json = JsonSerializer.Serialize(testReport, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);

        return testReport.OverallSuccess ? 0 : 1;
    }

    private static int RunIssue0009(string[] args, string evidenceDir)
    {
        Console.WriteLine($"[MajiOCR.Windows CLI] Running ISSUE-0009 Automated Test Suite (Windows {GetAppVersion()})...");
        var testTask = Task.Run(async () => await Issue0009AutomatedTestSuite.RunAllTestsAsync(evidenceDir));
        testTask.Wait();
        var testReport = testTask.Result;

        string json = JsonSerializer.Serialize(testReport, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);

        return testReport.OverallSuccess ? 0 : 1;
    }

    private static int RunIssue0011(string[] args, string evidenceDir)
    {
        Console.WriteLine($"[MajiOCR.Windows CLI] Running ISSUE-0011 Automated Test Suite (Windows {GetAppVersion()})...");
        var testTask = Task.Run(async () => await Issue0011AutomatedTestSuite.RunAllTestsAsync(evidenceDir));
        testTask.Wait();
        var testReport = testTask.Result;

        string json = JsonSerializer.Serialize(testReport, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);

        try
        {
            if (Directory.Exists(evidenceDir))
            {
                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0011_TEST_REPORT.json"), json, Encoding.UTF8);

                var sb = new StringBuilder();
                sb.AppendLine($"# ISSUE-0011 Windows W3 本機名片儲存自動化測試報告 (v{testReport.Version})");
                sb.AppendLine();
                sb.AppendLine($"- 測試時間: {testReport.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"- 受測版本: {testReport.Version}");
                sb.AppendLine($"- Issue ID: {testReport.IssueId}");
                sb.AppendLine($"- 執行環境: {testReport.Environment}");
                sb.AppendLine($"- Native SQLite 版本: {testReport.NativeSqliteVersion}");
                sb.AppendLine($"- 測試項目總數: {testReport.TotalTests} 項");
                sb.AppendLine($"- 自動化通過: {testReport.PassCount} 項");
                sb.AppendLine($"- 自動化失敗: {testReport.FailCount} 項");
                sb.AppendLine($"- 實體/人工未執行 (Gate): {testReport.NotRunCount} 項");
                sb.AppendLine($"- 自動化總評結果: {(testReport.OverallSuccess ? "**PASS**" : "**FAIL**")}");
                sb.AppendLine();
                sb.AppendLine("## 自動化驗證項目 (Automated Verified)");
                sb.AppendLine();
                sb.AppendLine("| 測試代號 | 描述 | 結果 | 耗時 | 詳細資訊 |");
                sb.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var tc in testReport.TestCases)
                {
                    sb.AppendLine($"| {tc.TestId} | {tc.Description} | {tc.Status} | {tc.LatencyMs} ms | {tc.Details.Replace("|", "\\|")} |");
                }
                sb.AppendLine();
                sb.AppendLine("## 安全與驗證邊界說明");
                sb.AppendLine();
                sb.AppendLine("- 本測試完全使用臨時注入的測試根目錄 (synthetic fixture root) 與記憶體/合成圖片。");
                sb.AppendLine("- 未讀取真實名片、圖片、剪貼簿、正式 LocalAppData 聯絡人或外部憑證。");
                sb.AppendLine("- 未啟動任何真實 WinUI 3 視窗與 GUI 操作 (W3-GUI, W3-CLEAN, HUMAN-CARD 均安全標記為 NOT_RUN)。");

                File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0011_TEST_REPORT.md"), sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ISSUE-0011 Test] Failed to write report: {ex.Message}");
        }

        return testReport.OverallSuccess ? 0 : 1;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (Array.Exists(args, a => a.Equals("--w0-verify", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0005-W1");
            int code = RunW0(args, evidenceDir);
            Environment.Exit(code);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--w1-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0005-W1");
            int code = RunW1(args, evidenceDir);
            Environment.Exit(code);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--w2-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-issue-0006", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0006");
            int code = RunW2(args, evidenceDir);
            Environment.Exit(code);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--w3-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-issue-0007", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0007-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0007");
            int code = RunIssue0007(args, evidenceDir);
            Environment.Exit(code);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--w4-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-issue-0008", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0008-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-roi", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0008");
            int code = RunIssue0008(args, evidenceDir);
            Environment.Exit(code);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--w5-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-issue-0009", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0009-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-language", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0009");
            int code = RunIssue0009(args, evidenceDir);
            Environment.Exit(code);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-storage", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0011-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-issue-0011", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0011");
            int code = RunIssue0011(args, evidenceDir);
            Environment.Exit(code);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-tags", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir=ResolveEvidenceDir(args,"ISSUE-0012");
            var report=Task.Run(()=>Issue0012AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
            Directory.CreateDirectory(evidenceDir);
            File.WriteAllText(Path.Combine(evidenceDir,"ISSUE-0012_TEST_REPORT.json"),json,Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess?0:1);return;
        }

        if (Array.Exists(args, a => a.Equals("--test-confirmation", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0013");
            var report = Task.Run(() => Issue0013AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0013_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--copy-legacy-storage", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidence = ResolveEvidenceDir(args, "ISSUE-0014");
            try
            {
                var report = Services.Storage.StorageCopyService.Copy(Services.Storage.StorageCopyService.GetLegacyRoot(), Services.Storage.StoragePathHelper.GetDefaultStorageRoot());
                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(evidence, "STORAGE_COPY_REPORT.json"), json, Encoding.UTF8);
                Console.WriteLine(json); Environment.Exit(0);
            }
            catch (Exception ex)
            {
                string json = JsonSerializer.Serialize(new { IssueId = "ISSUE-0014", Version = GetAppVersion(), Succeeded = false,
                    ErrorType = ex.GetType().Name, Error = ex.Message });
                File.WriteAllText(Path.Combine(evidence, "STORAGE_COPY_REPORT.json"), json, Encoding.UTF8);
                Console.WriteLine(json); Environment.Exit(1);
            }
            return;
        }
        if (Array.Exists(args, a => a.Equals("--portable-probe", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidence = ResolveEvidenceDir(args, "ISSUE-0014");
            var report = Task.Run(() => Issue0014AutomatedTestSuite.ProbeAsync(Array.Exists(args, a => a == "--seed"))).GetAwaiter().GetResult();
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidence, "PORTABLE_PROBE.json"), json, Encoding.UTF8);
            Console.WriteLine(json); Environment.Exit(report.Available && report.ReadBack && report.ImageVerified ? 0 : 1); return;
        }
        if (Array.Exists(args, a => a.Equals("--test-portable", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidence = ResolveEvidenceDir(args, "ISSUE-0014");
            var report = Task.Run(() => Issue0014AutomatedTestSuite.RunAllTestsAsync(evidence)).GetAwaiter().GetResult();
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidence, "ISSUE-0014_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json); Environment.Exit(report.OverallSuccess ? 0 : 1); return;
        }

        if (Array.Exists(args, a => a.Equals("--test-table", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-issue-0015", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0015-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0015");
            var report = Task.Run(() => Issue0015AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0015_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0016", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0016-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-search-reload", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0016");
            var report = Task.Run(() => Issue0016AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0016_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0017", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0017-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-tag-catalog", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0017");
            var report = Task.Run(() => Issue0017AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0017_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0020", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0020-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-tag-sync-picker", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0020");
            var report = Task.Run(() => Issue0020AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0020_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0021", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0021-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-dialog-width", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0021");
            var report = Task.Run(() => Issue0021AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0021_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0022", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0022-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-export-sync", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0022");
            var testResults = Task.Run(() => Issue0022AutomatedTestSuite.RunAllTestsAsync()).GetAwaiter().GetResult();
            bool allPassed = testResults.All(t => t.Passed);
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(new { IssueId = "ISSUE-0022", Version = GetAppVersion(), OverallSuccess = allPassed, TestCases = testResults }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0022_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(allPassed ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0024", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0024-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-autofit-columns", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0024");
            var report = Task.Run(() => Issue0024AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0024_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0026", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0026-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-google-browser-login", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0026");
            var testResults = Task.Run(() => Issue0026AutomatedTestSuite.RunAllTestsAsync()).GetAwaiter().GetResult();
            bool allPassed = testResults.All(t => t.Passed);
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(new { IssueId = "ISSUE-0026", Version = GetAppVersion(), OverallSuccess = allPassed, TestCases = testResults }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0026_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(allPassed ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0030", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0030-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-google-guide", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0030");
            var testResults = Task.Run(() => Issue0030AutomatedTestSuite.RunAllTestsAsync()).GetAwaiter().GetResult();
            bool allPassed = testResults.All(t => t.Passed);
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(new { IssueId = "ISSUE-0030", Version = GetAppVersion(), OverallSuccess = allPassed, TestCases = testResults }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0030_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(allPassed ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0033", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0033-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-google-drive", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0033");
            var testResults = Task.Run(() => Issue0033AutomatedTestSuite.RunAllTestsAsync()).GetAwaiter().GetResult();
            bool allPassed = testResults.All(t => t.Passed);
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(new { IssueId = "ISSUE-0033", Version = GetAppVersion(), OverallSuccess = allPassed, TestCases = testResults }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0033_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(allPassed ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0034", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0034-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-google-drive-resumable", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0034");
            var testResults = Task.Run(() => Issue0034AutomatedTestSuite.RunAllTestsAsync()).GetAwaiter().GetResult();
            bool allPassed = testResults.All(t => t.Passed);
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(new { IssueId = "ISSUE-0034", Version = GetAppVersion(), OverallSuccess = allPassed, TestCases = testResults }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0034_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(allPassed ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0035", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0035-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-image-compression", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0035");
            var testResults = Task.Run(() => Issue0035AutomatedTestSuite.RunAllTestsAsync()).GetAwaiter().GetResult();
            bool allPassed = testResults.All(t => t.Passed);
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(new { IssueId = "ISSUE-0035", Version = GetAppVersion(), OverallSuccess = allPassed, TestCases = testResults }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0035_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(allPassed ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0036", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0036-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-language-persistence", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0036");
            var report = Task.Run(() => Issue0036AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0036_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0037", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0037-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-multilang-rename", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0037");
            var report = Task.Run(() => Issue0037AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0037_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0038", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0038-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-password-encryption", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0038");
            var report = Task.Run(() => Issue0038AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0038_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0039", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0039-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-unlock-button-fix", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0039");
            var report = Task.Run(() => Issue0039AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0039_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0040", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0040-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-oauth-credentials-fix", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0040");
            var report = Task.Run(() => Issue0040AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0040_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0041", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0041-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-backup-restore-encryption", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0041");
            var report = Task.Run(() => Issue0041AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0041_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0043", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0043-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-customfields-sqlite", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0043");
            var report = Task.Run(() => Issue0043AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0043_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0044", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0044-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-ios-pwa-vercel", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0044");
            var report = Task.Run(() => Issue0044AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0044_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0045", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0045-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0045");
            var report = Issue0045AutomatedTestSuite.Run(evidenceDir);
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0045_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0046", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0046-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0046");
            var report = Issue0046AutomatedTestSuite.Run(evidenceDir);
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0046_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0050", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0050-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0050");
            var report = Task.Run(() => Issue0050AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0050_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0051", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0051-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-portable-settings", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0051");
            var report = Task.Run(() => Issue0051AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0051_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0052", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0052-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-db-merge", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0052");
            var report = Task.Run(() => Issue0052AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0052_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0053", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0053-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-local-db-sync", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0053");
            var report = Task.Run(() => Issue0053AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0053_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0054", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0054-test", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--test-excel-csv-exchange", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0054");
            var report = Task.Run(() => Issue0054AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0054_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0059", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0059-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0059");
            var report = Task.Run(() => Issue0059AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0059_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0060", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0060-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0060");
            var report = Task.Run(() => Issue0060AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0060_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        if (Array.Exists(args, a => a.Equals("--test-issue-0061", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--issue-0061-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string evidenceDir = ResolveEvidenceDir(args, "ISSUE-0061");
            var report = Task.Run(() => Issue0061AutomatedTestSuite.RunAllTestsAsync(evidenceDir)).GetAwaiter().GetResult();
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(evidenceDir, "ISSUE-0061_TEST_REPORT.json"), json, Encoding.UTF8);
            Console.WriteLine(json);
            Environment.Exit(report.OverallSuccess ? 0 : 1);
            return;
        }

        int guiRootIndex = Array.FindIndex(args, a => a.Equals("--gui-test-root", StringComparison.OrdinalIgnoreCase));
        if (guiRootIndex >= 0)
        {
            if (guiRootIndex + 1 >= args.Length || !Path.IsPathRooted(args[guiRootIndex + 1]))
            {
                Environment.Exit(2);
                return;
            }
            GuiVerificationRoot = Path.GetFullPath(args[guiRootIndex + 1]);
        }

        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
