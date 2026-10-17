using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace MajiOCR_Windows.Verification;

public class Issue0030AutomatedTestSuite
{
    public record TestResult(string TestName, bool Passed, string Message);

    public static async Task<List<TestResult>> RunAllTestsAsync()
    {
        var results = new List<TestResult>();

        results.Add(Test01_GuideResourcesEmbedded());
        results.Add(Test02_GuideStepsContentAndStructure());
        results.Add(Test03_GuideFallbackFiles());
        results.Add(Test04_GuideViewModelAndStepLogic());

        return await Task.FromResult(results);
    }

    public static TestResult Test01_GuideResourcesEmbedded()
    {
        var asm = typeof(GoogleGuideDialog).Assembly;
        string[] resourceNames = ["MajiOCR.Guide.Step1", "MajiOCR.Guide.Step2", "MajiOCR.Guide.Step3"];

        var missing = new List<string>();
        var details = new List<string>();

        foreach (var res in resourceNames)
        {
            using var stream = asm.GetManifestResourceStream(res);
            if (stream == null || stream.Length < 1024)
            {
                missing.Add(res);
            }
            else
            {
                details.Add($"{res}: {stream.Length} bytes");
            }
        }

        if (missing.Count == 0)
        {
            return new TestResult("TC-0030-01: Google Guide 3 步驟官方截圖資源嵌入驗證", true, $"所有 3 張引導圖片皆成功嵌入組件：{string.Join(", ", details)}");
        }
        return new TestResult("TC-0030-01: Google Guide 3 步驟官方截圖資源嵌入驗證", false, $"缺少或損毀之資源：{string.Join(", ", missing)}");
    }

    public static TestResult Test02_GuideStepsContentAndStructure()
    {
        var steps = GoogleGuideDialog.Steps;
        if (steps.Length != 3)
        {
            return new TestResult("TC-0030-02: Google Guide 步驟結構完整性", false, $"預期 3 個步驟，實際為 {steps.Length}");
        }

        for (int i = 0; i < 3; i++)
        {
            var step = steps[i];
            if (step.StepNumber != i + 1 ||
                string.IsNullOrWhiteSpace(step.Title) ||
                string.IsNullOrWhiteSpace(step.DetailedExplanation) ||
                string.IsNullOrWhiteSpace(step.ResourceName))
            {
                return new TestResult("TC-0030-02: Google Guide 步驟結構完整性", false, $"步驟 {i + 1} 內容缺少必要欄位");
            }
        }

        return new TestResult("TC-0030-02: Google Guide 步驟結構完整性", true, "3 個引導步驟之標題、說明與資源定義完整無誤");
    }

    public static TestResult Test03_GuideFallbackFiles()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] files = ["step1_create_oauth.png", "step2_select_desktop.png", "step3_copy_client_id.png"];
        var checkedPaths = new List<string>();

        foreach (var file in files)
        {
            string p1 = Path.Combine(baseDir, "Assets", "GoogleGuide", file);
            string p2 = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "GoogleGuide", file);
            string p3 = Path.Combine(Directory.GetCurrentDirectory(), "MajiOCR.Windows", "Assets", "GoogleGuide", file);

            bool exists = File.Exists(p1) || File.Exists(p2) || File.Exists(p3);
            if (!exists)
            {
                return new TestResult("TC-0030-03: 實體檔案備援檢查", false, $"實體檔案 {file} 未找到");
            }
            checkedPaths.Add(file);
        }

        return new TestResult("TC-0030-03: 實體檔案備援檢查", true, $"實體引導圖檔備援存在：{string.Join(", ", checkedPaths)}");
    }

    public static TestResult Test04_GuideViewModelAndStepLogic()
    {
        try
        {
            var steps = GoogleGuideDialog.Steps;
            bool valid = steps.Length == 3 &&
                         steps[0].ResourceName == "MajiOCR.Guide.Step1" &&
                         steps[1].ResourceName == "MajiOCR.Guide.Step2" &&
                         steps[2].ResourceName == "MajiOCR.Guide.Step3";

            if (valid)
            {
                return new TestResult("TC-0030-04: 圖文教學步驟邏輯與資源映射驗證", true, "3 步驟資源名稱與邏輯定義完全匹配 (Step1/Step2/Step3 官方截圖)");
            }
            return new TestResult("TC-0030-04: 圖文教學步驟邏輯與資源映射驗證", false, "步驟資源映射不符合預期");
        }
        catch (Exception ex)
        {
            return new TestResult("TC-0030-04: 圖文教學步驟邏輯與資源映射驗證", false, $"驗證失敗：{ex.Message}");
        }
    }
}
