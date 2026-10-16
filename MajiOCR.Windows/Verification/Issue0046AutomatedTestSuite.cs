using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using MajiOCR_Windows.Services.Update;

namespace MajiOCR_Windows.Verification;

public class Issue0046TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0046TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0046TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.26";
    public string IssueId { get; set; } = "ISSUE-0046";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0 && TotalTests > 0;
    public List<Issue0046TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0046AutomatedTestSuite
{
    public static Issue0046TestSuiteReport Run(string evidenceDir)
    {
        var report = new Issue0046TestSuiteReport();

        // TC-01: GitHub Releases API JSON 解析測試
        report.TestCases.Add(RunTestCase("TC-01", "驗證 UpdateManifest.Parse 支援原生 GitHub Releases API JSON 格式", () =>
        {
            string sampleGithubJson = @"
            {
              ""tag_name"": ""v0.1.0.25"",
              ""name"": ""v0.1.0.25 - 修復文字欄位 Ctrl+V 貼上提示與優化 README 渲染"",
              ""body"": ""## 🌟 MajiOCR.Windows v0.1.0.25 變更說明\n\nSHA-256: B4D567D45569632E2015AEBB40969F4990E77B27ED66BC15C61541024A091C4F"",
              ""published_at"": ""2026-10-16T22:32:35Z"",
              ""assets"": [
                {
                  ""name"": ""MajiOCR.Windows.zip"",
                  ""browser_download_url"": ""https://github.com/Ryan-tw-Hans/MajiStudio/releases/download/v0.1.0.25/MajiOCR.Windows.zip"",
                  ""size"": 108779993
                }
              ]
            }";

            var manifest = UpdateManifest.Parse(sampleGithubJson, UpdateChannel.Release);
            if (manifest.Version != "0.1.0.25") throw new InvalidOperationException($"版本應為 0.1.0.25，實際為: {manifest.Version}");
            if (manifest.FileName != "MajiOCR.Windows.zip") throw new InvalidOperationException($"檔案名稱應為 MajiOCR.Windows.zip，實際為: {manifest.FileName}");
            if (!manifest.PackageUrl.Contains("github.com/Ryan-tw-Hans/MajiStudio/releases/download/v0.1.0.25/MajiOCR.Windows.zip"))
            {
                throw new InvalidOperationException($"PackageUrl 不符: {manifest.PackageUrl}");
            }
            if (manifest.Sha256 != "B4D567D45569632E2015AEBB40969F4990E77B27ED66BC15C61541024A091C4F")
            {
                throw new InvalidOperationException($"SHA256 解析不符: {manifest.Sha256}");
            }
            if (string.IsNullOrWhiteSpace(manifest.ChangeLog))
            {
                throw new InvalidOperationException("ChangeLog (body) 不應為空");
            }
        }));

        // TC-02: 預設網址與舊 Google Drive 網址自動平滑遷移測試
        report.TestCases.Add(RunTestCase("TC-02", "驗證 DefaultReleaseManifestUrl 為 GitHub Releases 且舊 Google Drive 設定會自動遷移", () =>
        {
            var updateService = new UpdateService();
            if (!updateService.DefaultReleaseManifestUrl.Contains("api.github.com/repos/Ryan-tw-Hans/MajiStudio/releases/latest"))
            {
                throw new InvalidOperationException($"預設發布網址應為 GitHub Releases API，實際為: {updateService.DefaultReleaseManifestUrl}");
            }

            // 模擬舊使用者設定
            string tempDir = Path.Combine(Path.GetTempPath(), "MajiOCR_UpdateTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string oldSettingsPath = Path.Combine(tempDir, UpdateService.SettingsFileName);
                File.WriteAllText(oldSettingsPath, @"{ ""ReleaseManifestUrl"": ""https://drive.google.com/uc?export=download&id=OLD_GOOGLE_DRIVE_ID"" }");

                var tempService = new UpdateService(tempDir);
                var loaded = tempService.LoadSettings();

                if (!string.IsNullOrWhiteSpace(loaded.ReleaseManifestUrl) && loaded.ReleaseManifestUrl.Contains("drive.google.com"))
                {
                    throw new InvalidOperationException("舊 Google Drive 設定應被自動遷移重設為 null 或預設 GitHub Releases 網址");
                }
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }));

        // TC-03: 版本號同步 0.1.0.26
        report.TestCases.Add(RunTestCase("TC-03", "驗證 VERSION.txt、CHANGELOG.txt 與 MajiOCR.Windows.csproj 同步為 0.1.0.26", () =>
        {
            string repoRoot = FindRepoRoot();
            string verContent = File.ReadAllText(Path.Combine(repoRoot, "VERSION.txt"));
            if (!verContent.Contains("Version: 0.1.0.26")) throw new InvalidOperationException("VERSION.txt 應為 0.1.0.26");

            string clContent = File.ReadAllText(Path.Combine(repoRoot, "CHANGELOG.txt"));
            if (!clContent.Contains("## [0.1.0.26]") || !clContent.Contains("ISSUE-0046"))
            {
                throw new InvalidOperationException("CHANGELOG.txt 應包含 0.1.0.26 與 ISSUE-0046");
            }

            string csproj = File.ReadAllText(Path.Combine(repoRoot, "MajiOCR.Windows", "MajiOCR.Windows.csproj"));
            if (!csproj.Contains("<Version>0.1.0-beta.26</Version>")) throw new InvalidOperationException("csproj 版本應為 0.1.0-beta.26");
        }));

        report.TotalTests = report.TestCases.Count;
        report.PassCount = report.TestCases.Count(c => c.Passed);
        report.FailCount = report.TestCases.Count(c => !c.Passed);
        return report;
    }

    private static Issue0046TestCaseResult RunTestCase(string id, string desc, Action action)
    {
        var sw = Stopwatch.StartNew();
        var res = new Issue0046TestCaseResult { TestId = id, Description = desc };
        try
        {
            action();
            res.Passed = true;
            res.Details = "PASS";
        }
        catch (Exception ex)
        {
            res.Passed = false;
            res.Details = ex.ToString();
        }
        finally
        {
            sw.Stop();
            res.LatencyMs = sw.ElapsedMilliseconds;
        }
        return res;
    }

    private static string FindRepoRoot()
    {
        string cur = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            if (File.Exists(Path.Combine(cur, "VERSION.txt")) && File.Exists(Path.Combine(cur, "README.md")))
            {
                return cur;
            }
            string? parent = Directory.GetParent(cur)?.FullName;
            if (parent == null) break;
            cur = parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
