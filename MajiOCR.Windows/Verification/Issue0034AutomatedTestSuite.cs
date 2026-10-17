using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MajiOCR_Windows.Services;

namespace MajiOCR_Windows.Verification;

public class Issue0034AutomatedTestSuite
{
    public record TestResult(string TestName, bool Passed, string Message);

    public static async Task<List<TestResult>> RunAllTestsAsync()
    {
        var results = new List<TestResult>();

        results.Add(await Test01_ProgressableStreamContentAsync());
        results.Add(await Test02_MockTokenUploadAndResultHandlingAsync());

        return results;
    }

    public static async Task<TestResult> Test01_ProgressableStreamContentAsync()
    {
        byte[] dummyData = Encoding.UTF8.GetBytes(new string('A', 256 * 1024)); // 256KB
        using var ms = new MemoryStream(dummyData);

        var progressReports = new List<BackupProgress>();
        var progress = new Progress<BackupProgress>(p => progressReports.Add(p));

        var streamContent = new ProgressableStreamContent(ms, dummyData.Length, progress, startPercentage: 50, endPercentage: 95);

        using var targetStream = new MemoryStream();
        await streamContent.CopyToAsync(targetStream);

        bool lengthMatches = targetStream.Length == dummyData.Length;
        bool hasProgress = progressReports.Count > 0;

        if (lengthMatches && hasProgress)
        {
            return new TestResult("TC-0034-01: ProgressableStreamContent 串流傳輸與進度回報校驗", true, $"成功傳輸 {targetStream.Length} bytes，觸發 {progressReports.Count} 次進度回報，最後進度={progressReports[^1].Percentage}%");
        }

        return new TestResult("TC-0034-01: ProgressableStreamContent 串流傳輸與進度回報校驗", false, $"傳輸異常：lengthMatches={lengthMatches}, hasProgress={hasProgress}");
    }

    public static async Task<TestResult> Test02_MockTokenUploadAndResultHandlingAsync()
    {
        var manifest = new BackupManifest(
            BackupId: "MajiOCR_Backup_20260929_120000_windows",
            BackupVersion: "1.0",
            AppVersion: "0.1.0-beta.18",
            Platform: "windows",
            CreatedAtUtc: DateTime.UtcNow,
            ContactCount: 10,
            ImageCount: 10,
            SqliteSha256: "testsha",
            Description: "測試備份"
        );

        string tempZip = Path.Combine(Path.GetTempPath(), $"test_zip_{Guid.NewGuid():N}.zip");
        await File.WriteAllBytesAsync(tempZip, new byte[] { 0x50, 0x4B, 0x05, 0x06 }); // empty zip

        try
        {
            var driveService = new GoogleDriveBackupService();
            var result = await driveService.UploadBackupToDriveAsync(tempZip, manifest, "mock_token_123");

            if (result.Success && !string.IsNullOrEmpty(result.FileId) && result.Manifest?.ContactCount == 10)
            {
                return new TestResult("TC-0034-02: GoogleDriveBackupService 上傳流程與 Mock Token 測試", true, $"上傳成功，FileId={result.FileId}，筆數={result.Manifest.ContactCount}");
            }

            return new TestResult("TC-0034-02: GoogleDriveBackupService 上傳流程與 Mock Token 測試", false, $"上傳失敗：{result.Message}");
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }
}
