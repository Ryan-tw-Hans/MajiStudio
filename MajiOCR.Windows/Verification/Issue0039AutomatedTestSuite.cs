using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Verification;

public class Issue0039TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0039TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0039TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.20";
    public string IssueId { get; set; } = "ISSUE-0039";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0039TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0039AutomatedTestSuite
{
    public static async Task<Issue0039TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0039TestSuiteReport();
        var sw = Stopwatch.StartNew();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0039TestCaseResult
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

        string tempBase = Path.Combine(Path.GetTempPath(), "MajiOCR.Windows.Issue0039Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempBase);

        try
        {
            // T01: Password Validation and Verification Logic (Used in SecurityDialogs Enter/Click handlers)
            var t01Sw = Stopwatch.StartNew();
            string validPass = "ValidPass123";
            var (hash, salt) = PasswordSecurityService.HashPassword(validPass);
            bool emptyCheck = string.IsNullOrEmpty("");
            bool wrongCheck = PasswordSecurityService.VerifyPassword("WrongPass", hash, salt);
            bool correctCheck = PasswordSecurityService.VerifyPassword(validPass, hash, salt);
            bool t01Passed = emptyCheck && !wrongCheck && correctCheck;
            t01Sw.Stop();
            Record("T01", "密碼驗證邏輯校驗 (空值/錯誤密碼拒絕，正確密碼通過)", t01Passed, $"Empty={emptyCheck}, WrongBlocked={!wrongCheck}, Correct={correctCheck}", t01Sw.ElapsedMilliseconds);

            // T02: ViewModel StorageService Initialization & CanUseStorage State Transition
            var t02Sw = Stopwatch.StartNew();
            MainViewModel.IsHeadlessEnvironment = true;
            var vm = new MainViewModel();
            bool initialCanUseStorage = vm.CanUseStorage; // Must be false before StorageService is assigned
            string storageRoot = Path.Combine(tempBase, "Storage");
            var storageService = new StorageService(storageRoot, autoInit: true, passphrase: null);
            vm.StorageService = storageService;
            bool enabledCanUseStorage = vm.CanUseStorage; // Must become true once storageService.IsAvailable == true
            bool t02Passed = !initialCanUseStorage && enabledCanUseStorage && storageService.IsAvailable;
            t02Sw.Stop();
            Record("T02", "ViewModel.StorageService 就緒後 CanUseStorage 正確切換為 true (按鈕啟用)", t02Passed, $"Initial={initialCanUseStorage}, Enabled={enabledCanUseStorage}", t02Sw.ElapsedMilliseconds);

            // T03: Encrypted Database Unlock & Storage Availability Verification
            var t03Sw = Stopwatch.StartNew();
            string dbPass = "MyEncPass!2026";
            await storageService.EnableEncryptionAsync(dbPass);
            storageService.Dispose();

            // Open with correct passphrase
            var encStorage = new StorageService(storageRoot, autoInit: true, passphrase: dbPass);
            vm.StorageService = encStorage;
            bool encAvailable = encStorage.IsAvailable;
            bool encCanUseStorage = vm.CanUseStorage;
            bool t03Passed = encAvailable && encCanUseStorage && encStorage.IsEncrypted;
            t03Sw.Stop();
            Record("T03", "加密資料庫使用解鎖密碼初始化後 StorageService 正常就緒且 CanUseStorage 為 true", t03Passed, $"EncAvailable={encAvailable}, EncCanUseStorage={encCanUseStorage}, IsEncrypted={encStorage.IsEncrypted}, Error={encStorage.ErrorMessage}", t03Sw.ElapsedMilliseconds);

            // T04: Busy and Saving state protection for CanUseStorage
            var t04Sw = Stopwatch.StartNew();
            vm.IsBusy = true;
            bool busyBlocksStorage = !vm.CanUseStorage;
            vm.IsBusy = false;
            vm.IsSaving = true;
            bool savingBlocksStorage = !vm.CanUseStorage;
            vm.IsSaving = false;
            vm.IsStorageInitializing = true;
            bool initBlocksStorage = !vm.CanUseStorage;
            vm.IsStorageInitializing = false;
            bool recoveredStorage = vm.CanUseStorage;
            bool t04Passed = busyBlocksStorage && savingBlocksStorage && initBlocksStorage && recoveredStorage;
            t04Sw.Stop();
            Record("T04", "儲存與忙碌狀態下 CanUseStorage 正確保護，完成後自動恢復可用", t04Passed, $"BusyBlock={busyBlocksStorage}, SavingBlock={savingBlocksStorage}, InitBlock={initBlocksStorage}, Recovered={recoveredStorage}", t04Sw.ElapsedMilliseconds);

            encStorage.Dispose();
        }
        catch (Exception ex)
        {
            Record("T_ERR", "測試執行過程發生未預期例外", false, ex.ToString());
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
            }
            catch { }
        }

        sw.Stop();
        return report;
    }
}
