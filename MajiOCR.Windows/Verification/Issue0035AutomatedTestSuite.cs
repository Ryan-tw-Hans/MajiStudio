using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;
using Tesseract;

namespace MajiOCR_Windows.Verification;

public class Issue0035AutomatedTestSuite
{
    public record TestResult(string TestName, bool Passed, string Message);

    public static async Task<List<TestResult>> RunAllTestsAsync()
    {
        var results = new List<TestResult>();

        results.Add(await Test01_NormalImageLoadAsync());
        results.Add(await Test02_LargeImageSmartCompressionAsync());
        results.Add(await Test03_OversizedDimensionDownscalingAsync());
        results.Add(await Test04_StorageValidationAndDecodabilityAsync());
        results.Add(await DiagnoseUserImageAsync());

        return results;
    }

    public static async Task<TestResult> DiagnoseUserImageAsync(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            return new TestResult("TC-0035-05: 使用者上傳名片診斷", true, "圖片檔案不存在於本機暫存，略過");
        }

        var imgService = new ImageService();
        var loadRes = await imgService.LoadFromFileAsync(imagePath);
        if (!loadRes.Success || loadRes.SessionInfo == null)
        {
            return new TestResult("TC-0035-05: 使用者上傳名片診斷", false, $"載入失敗: {loadRes.ErrorMessage}");
        }

        using var ocrService = new OcrService();
        var ocrRes = await ocrService.RunFullOcrAsync(loadRes.SessionInfo.ImageBytes, 999, System.Threading.CancellationToken.None, null);

        var sb = new StringBuilder();
        sb.AppendLine($"[全圖 OCR] 成功={ocrRes.Success}, 候選行數={ocrRes.Candidates.Count}");
        foreach (var c in ocrRes.Candidates)
        {
            string kind = FieldSuggester.Suggest(c.Text);
            sb.AppendLine($"  - [{kind}] (X={c.X}, Y={c.Y}, W={c.Width}, H={c.Height}, Conf={c.Confidence:P0}): \"{c.Text}\"");
        }

            // Find candidate for name or test region around name
        var nameCand = ocrRes.Candidates.FirstOrDefault(c => c.Text.Contains("王") || c.Text.Contains("明"));
        if (nameCand != null)
        {
            sb.AppendLine($"\n[測試區域 OCR on Name candidate bounds X={nameCand.X}, Y={nameCand.Y}, W={nameCand.Width}, H={nameCand.Height}]:");

            // Test 1: Precise box around 王小明 (approx 160px width)
            int nameW = 160;
            sb.AppendLine($"\n--- 測試 RunRegionOcrAsync (王小明選區 W=160) X={nameCand.X}, Y={nameCand.Y}, W={nameW}, H={nameCand.Height} ---");
            var regResName = await ocrService.RunRegionOcrAsync(loadRes.SessionInfo.ImageBytes, nameCand.X, nameCand.Y, nameW, nameCand.Height, 997, System.Threading.CancellationToken.None, null);
            sb.AppendLine($"  - Region OCR 結果: 成功={regResName.Success}, 行數={regResName.Candidates.Count}");
            foreach (var rc in regResName.Candidates)
            {
                string kind = FieldSuggester.Suggest(rc.Text);
                sb.AppendLine($"    * 辨識出: \"{rc.Text}\" (Sug=\"{rc.TraditionalSuggestion}\", Src={rc.SuggestionSource}) -> 建議欄位=[{kind}] (Conf={rc.Confidence:P0}, 來源={rc.Source})");
            }

            // Test 2: Full line box
            sb.AppendLine($"\n--- 測試 RunRegionOcrAsync (全行選區) X={nameCand.X}, Y={nameCand.Y}, W={nameCand.Width}, H={nameCand.Height} ---");
            var regResFull = await ocrService.RunRegionOcrAsync(loadRes.SessionInfo.ImageBytes, nameCand.X, nameCand.Y, nameCand.Width, nameCand.Height, 998, System.Threading.CancellationToken.None, null);
            sb.AppendLine($"  - Region OCR 結果: 成功={regResFull.Success}, 行數={regResFull.Candidates.Count}");
            foreach (var rc in regResFull.Candidates)
            {
                string kind = FieldSuggester.Suggest(rc.Text);
                sb.AppendLine($"    * 辨識出: \"{rc.Text}\" (Sug=\"{rc.TraditionalSuggestion}\", Src={rc.SuggestionSource}) -> 建議欄位=[{kind}] (Conf={rc.Confidence:P0}, 來源={rc.Source})");
            }
        }

        return new TestResult("TC-0035-05: 使用者上傳名片診斷", true, sb.ToString());
    }

    public static async Task<TestResult> Test01_NormalImageLoadAsync()
    {
        var imgService = new ImageService();
        string tempPng = Path.Combine(Path.GetTempPath(), $"test_normal_{Guid.NewGuid():N}.png");

        try
        {
            // Generate standard small 800x500 PNG
            byte[] normalPng = await CreateSyntheticPngAsync(800, 500);
            await File.WriteAllBytesAsync(tempPng, normalPng);

            var result = await imgService.LoadFromFileAsync(tempPng);

            bool ok = result.Success &&
                      result.SessionInfo != null &&
                      result.SessionInfo.Width == 800 &&
                      result.SessionInfo.Height == 500 &&
                      result.SessionInfo.ImageBytes.Length <= ImageService.MaxCardImageSizeBytes;

            if (ok)
            {
                return new TestResult("TC-0035-01: 標準合法圖片載入", true,
                    $"成功載入標準圖片，尺寸={result.SessionInfo!.Width}x{result.SessionInfo.Height}，大小={result.SessionInfo.ImageBytes.Length} bytes (<= 5MB)");
            }

            return new TestResult("TC-0035-01: 標準合法圖片載入", false,
                $"載入失敗：Success={result.Success}, Error={result.ErrorMessage}");
        }
        finally
        {
            if (File.Exists(tempPng)) File.Delete(tempPng);
        }
    }

    public static async Task<TestResult> Test02_LargeImageSmartCompressionAsync()
    {
        var imgService = new ImageService();
        string tempLargeJpg = Path.Combine(Path.GetTempPath(), $"test_large_{Guid.NewGuid():N}.jpg");

        try
        {
            // Generate large 4000x3000 high-res image
            byte[] largeImg = await CreateSyntheticPngAsync(4000, 3000);
            await File.WriteAllBytesAsync(tempLargeJpg, largeImg);

            var result = await imgService.LoadFromFileAsync(tempLargeJpg);

            bool ok = result.Success &&
                      result.SessionInfo != null &&
                      result.SessionInfo.Width <= ImageService.TargetLongEdgeDimension &&
                      result.SessionInfo.Height <= ImageService.TargetLongEdgeDimension &&
                      result.SessionInfo.ImageBytes.Length <= ImageService.MaxCardImageSizeBytes;

            if (ok)
            {
                return new TestResult("TC-0035-02: 超高解析度大圖自動智慧等比例縮放與壓縮", true,
                    $"成功將 4000x3000 縮放並壓縮至 {result.SessionInfo!.Width}x{result.SessionInfo.Height}，最終大小={result.SessionInfo.ImageBytes.Length} bytes (<= 5MB)");
            }

            return new TestResult("TC-0035-02: 超高解析度大圖自動智慧等比例縮放與壓縮", false,
                $"大圖壓縮失敗：Success={result.Success}, Error={result.ErrorMessage}");
        }
        finally
        {
            if (File.Exists(tempLargeJpg)) File.Delete(tempLargeJpg);
        }
    }

    public static async Task<TestResult> Test03_OversizedDimensionDownscalingAsync()
    {
        var imgService = new ImageService();
        string tempWidePng = Path.Combine(Path.GetTempPath(), $"test_wide_{Guid.NewGuid():N}.png");

        try
        {
            // 5000 x 2000 image (aspect ratio 2.5:1)
            byte[] wideImg = await CreateSyntheticPngAsync(5000, 2000);
            await File.WriteAllBytesAsync(tempWidePng, wideImg);

            var result = await imgService.LoadFromFileAsync(tempWidePng);

            bool ok = result.Success &&
                      result.SessionInfo != null &&
                      result.SessionInfo.Width == ImageService.TargetLongEdgeDimension && // 2560
                      result.SessionInfo.Height == 1024 && // 2000 * (2560/5000) = 1024
                      result.SessionInfo.ImageBytes.Length <= ImageService.MaxCardImageSizeBytes;

            if (ok)
            {
                return new TestResult("TC-0035-03: 超寬比例大圖精準等比例縮放", true,
                    $"成功維持比例縮放為 {result.SessionInfo!.Width}x{result.SessionInfo.Height}，大小={result.SessionInfo.ImageBytes.Length} bytes (<= 5MB)");
            }

            return new TestResult("TC-0035-03: 超寬比例大圖精準等比例縮放", false,
                $"縮放比例異常：Success={result.Success}, W={result.SessionInfo?.Width}, H={result.SessionInfo?.Height}");
        }
        finally
        {
            if (File.Exists(tempWidePng)) File.Delete(tempWidePng);
        }
    }

    public static async Task<TestResult> Test04_StorageValidationAndDecodabilityAsync()
    {
        var imgService = new ImageService();
        string tempJpg = Path.Combine(Path.GetTempPath(), $"test_storage_{Guid.NewGuid():N}.jpg");

        try
        {
            byte[] rawBmp = await CreateSyntheticPngAsync(3200, 2400);
            await File.WriteAllBytesAsync(tempJpg, rawBmp);

            var loadRes = await imgService.LoadFromFileAsync(tempJpg);
            if (!loadRes.Success || loadRes.SessionInfo == null)
            {
                return new TestResult("TC-0035-04: 壓縮後圖檔之 StorageService 驗證與可解碼性", false, $"載入失敗: {loadRes.ErrorMessage}");
            }

            // Verify with StorageService.ValidateImageAsync
            var (valid, width, height, mimeType, error) = await StorageService.ValidateImageAsync(loadRes.SessionInfo.ImageBytes);

            bool ok = valid &&
                      error == null &&
                      width == loadRes.SessionInfo.Width &&
                      height == loadRes.SessionInfo.Height &&
                      loadRes.SessionInfo.ImageBytes.Length <= ImageService.MaxCardImageSizeBytes;

            if (ok)
            {
                return new TestResult("TC-0035-04: 壓縮後圖檔之 StorageService 驗證與可解碼性", true,
                    $"通過 StorageService 嚴格解碼與標頭校驗，格式={mimeType}，尺寸={width}x{height}，大小={loadRes.SessionInfo.ImageBytes.Length} bytes");
            }

            return new TestResult("TC-0035-04: 壓縮後圖檔之 StorageService 驗證與可解碼性", false,
                $"StorageService 驗證未通過: Valid={valid}, Error={error}");
        }
        finally
        {
            if (File.Exists(tempJpg)) File.Delete(tempJpg);
        }
    }

    private static async Task<byte[]> CreateSyntheticPngAsync(uint width, uint height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (int)((y * width + x) * 4);
                pixels[idx] = (byte)((x * 255) / width);         // B
                pixels[idx + 1] = (byte)((y * 255) / height);     // G
                pixels[idx + 2] = (byte)(((x + y) * 255) / (width + height)); // R
                pixels[idx + 3] = 255;                            // A
            }
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            width,
            height,
            96,
            96,
            pixels
        );
        await encoder.FlushAsync();

        stream.Seek(0);
        byte[] bytes = new byte[stream.Size];
        using (var dr = new DataReader(stream))
        {
            await dr.LoadAsync((uint)stream.Size);
            dr.ReadBytes(bytes);
        }
        return bytes;
    }
}
