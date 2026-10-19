using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MiniExcelLibs;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.Verification;

public class Issue0054TestCaseResult
{
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class Issue0054TestSuiteReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = typeof(Issue0054TestSuiteReport).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0-beta.34";
    public string IssueId { get; set; } = "ISSUE-0054";
    public string Environment { get; set; } = $"{System.Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public int TotalTests { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool OverallSuccess => FailCount == 0;
    public List<Issue0054TestCaseResult> TestCases { get; set; } = new();
}

public static class Issue0054AutomatedTestSuite
{
    public static async Task<Issue0054TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0054TestSuiteReport();

        void Record(string id, string desc, bool passed, string details, long ms = 0)
        {
            report.TestCases.Add(new Issue0054TestCaseResult
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

        string tempBaseDir = Path.Combine(Path.GetTempPath(), $"issue0054_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBaseDir);

        try
        {
            var excelCsvService = new ContactExcelCsvService();

            // Mock contact detail list
            var sampleContacts = new List<ContactDetailModel>
            {
                new()
                {
                    ContactId = Guid.NewGuid().ToString("N"),
                    Name = "林志強",
                    Company = "台灣數位科技",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                    Tags = new List<TagDefinition> { new("tag1", "核心夥伴", 1, 0) },
                    Values = new List<ContactValueRecord>
                    {
                        new() { ValueKind = CanonicalValueKind.Name, RawValue = "林志強", Label = "姓名" },
                        new() { ValueKind = CanonicalValueKind.Company, RawValue = "台灣數位科技", Label = "公司" },
                        new() { ValueKind = CanonicalValueKind.Title, RawValue = "研發副總", Label = "職稱" },
                        new() { ValueKind = CanonicalValueKind.Phone, RawValue = "0912-888-999", Label = "手機" },
                        new() { ValueKind = CanonicalValueKind.Email, RawValue = "lin@example.com", Label = "Email" },
                        new() { ValueKind = CanonicalValueKind.Address, RawValue = "台北市南港區園區街 3 號", Label = "地址" },
                        new() { ValueKind = CanonicalValueKind.Note, RawValue = "年度重點技術主管", Label = "備註" }
                    }
                },
                new()
                {
                    ContactId = Guid.NewGuid().ToString("N"),
                    Name = "陳美玲",
                    Company = "亞太金融顧問",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    Tags = new List<TagDefinition> { new("tag2", "財務顧問", 1, 0) },
                    Values = new List<ContactValueRecord>
                    {
                        new() { ValueKind = CanonicalValueKind.Name, RawValue = "陳美玲", Label = "姓名" },
                        new() { ValueKind = CanonicalValueKind.Company, RawValue = "亞太金融顧問", Label = "公司" },
                        new() { ValueKind = CanonicalValueKind.Title, RawValue = "資深總監", Label = "職稱" },
                        new() { ValueKind = CanonicalValueKind.Phone, RawValue = "02-8765-4321", Label = "電話" },
                        new() { ValueKind = CanonicalValueKind.Email, RawValue = "mei@apac-finance.example", Label = "Email" },
                        new() { ValueKind = CanonicalValueKind.Custom, RawValue = "88990011", Label = "統一編號" }
                    }
                }
            };

            // TC-0054-01: Excel (.xlsx) 匯出格式與完整欄位
            {
                var sw = Stopwatch.StartNew();
                byte[] xlsxBytes = excelCsvService.ExportToExcel(sampleContacts);
                bool validSize = xlsxBytes != null && xlsxBytes.Length > 1000;

                using var ms = new MemoryStream(xlsxBytes!);
                var rows = MiniExcel.Query(ms, useHeaderRow: true).ToList();
                bool hasTwoRows = rows.Count == 2;
                var firstRow = rows[0] as IDictionary<string, object>;
                bool matchName = firstRow != null && firstRow["姓名"]?.ToString() == "林志強";
                bool matchCompany = firstRow != null && firstRow["公司"]?.ToString() == "台灣數位科技";
                bool matchTags = firstRow != null && firstRow["分類標籤"]?.ToString() == "核心夥伴";

                sw.Stop();
                bool passed = validSize && hasTwoRows && matchName && matchCompany && matchTags;
                Record("TC-0054-01", "Excel (.xlsx) 匯出格式與完整欄位結構驗證", passed,
                    $"BytesLen={xlsxBytes?.Length}, Rows={rows.Count}, Name={firstRow?["姓名"]}, Company={firstRow?["公司"]}, Tags={firstRow?["分類標籤"]}", sw.ElapsedMilliseconds);
            }

            // TC-0054-02: 通用 CSV 匯出 UTF-8 BOM 與轉義
            {
                var sw = Stopwatch.StartNew();
                string csv = excelCsvService.ExportToFullCsv(sampleContacts);
                bool hasBom = csv.StartsWith('\uFEFF');
                bool hasHeader = csv.Contains("姓名,公司,部門,職稱,手機,公司電話,Email,地址,統一編號,網站,分類標籤,備註,建立時間");
                bool hasLin = csv.Contains("林志強,台灣數位科技,,研發副總,0912-888-999");
                bool hasMei = csv.Contains("陳美玲,亞太金融顧問,,資深總監,,02-8765-4321,mei@apac-finance.example");

                sw.Stop();
                bool passed = hasBom && hasHeader && hasLin && hasMei;
                Record("TC-0054-02", "通用 CSV 匯出 UTF-8 BOM 與轉義驗證", passed,
                    $"HasBOM={hasBom}, HasHeader={hasHeader}, HasRow1={hasLin}, HasRow2={hasMei}", sw.ElapsedMilliseconds);
            }

            // TC-0054-03: 標準 Excel / CSV 範本生成與範例資料列
            {
                var sw = Stopwatch.StartNew();
                byte[] excelTpl = excelCsvService.GenerateExcelTemplate();
                string csvTpl = excelCsvService.GenerateCsvTemplate();

                using var ms = new MemoryStream(excelTpl);
                var tplRows = MiniExcel.Query(ms, useHeaderRow: true).ToList();
                bool excelTplValid = tplRows.Count >= 2;

                bool csvTplValid = csvTpl.StartsWith('\uFEFF') && csvTpl.Contains("王小明") && csvTpl.Contains("甲式科技股份有限公司");
                sw.Stop();

                bool passed = excelTplValid && csvTplValid;
                Record("TC-0054-03", "標準 Excel / CSV 範本生成與範例資料列驗證", passed,
                    $"ExcelTplRows={tplRows.Count}, CsvTplValid={csvTplValid}", sw.ElapsedMilliseconds);
            }

            // TC-0054-04: 智慧欄位名稱對齊解析 (繁中、簡中、英文表頭識別)
            {
                var sw = Stopwatch.StartNew();
                string customCsv = "\uFEFFFull Name,Company Name,Job Title,Mobile Phone,Work Phone,E-mail,Street,Categories,Notes\n" +
                                   "John Doe,Acme Corp,CTO,0911-222-333,02-2345-6789,john@acme.com,123 Main St,Partner,VIP contact";
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(customCsv));
                var parsed = excelCsvService.ParseImportFile(ms, ".csv");

                bool countOk = parsed.Count == 1;
                var d = countOk ? parsed[0] : null;
                bool match = d != null &&
                             d.Name == "John Doe" &&
                             d.Company == "Acme Corp" &&
                             d.Title == "CTO" &&
                             d.Mobile == "0911-222-333" &&
                             d.Phone == "02-2345-6789" &&
                             d.Email == "john@acme.com" &&
                             d.Address == "123 Main St" &&
                             d.Tags.Contains("Partner") &&
                             d.Notes.Contains("VIP contact");

                sw.Stop();
                Record("TC-0054-04", "智慧欄位名稱對齊解析 (英文/同義詞表頭識別)", countOk && match,
                    $"Count={parsed.Count}, Name={d?.Name}, Company={d?.Company}, Title={d?.Title}, Mobile={d?.Mobile}, Tags={d?.DisplayTags}", sw.ElapsedMilliseconds);
            }

            // TC-0054-05: Excel (.xlsx) 檔案批次解析
            {
                var sw = Stopwatch.StartNew();
                byte[] tplBytes = excelCsvService.GenerateExcelTemplate();
                using var ms = new MemoryStream(tplBytes);
                var drafts = excelCsvService.ParseImportFile(ms, ".xlsx");

                bool countMatch = drafts.Count == 2;
                bool d1Ok = drafts.Count > 0 && drafts[0].Name == "王小明" && drafts[0].Company == "甲式科技股份有限公司" && drafts[0].Tags.Contains("VIP客戶");
                bool d2Ok = drafts.Count > 1 && drafts[1].Name == "李大華" && drafts[1].Company == "乙式電訊科技";

                sw.Stop();
                bool passed = countMatch && d1Ok && d2Ok;
                Record("TC-0054-05", "Excel (.xlsx) 檔案串流批次解析草稿驗證", passed,
                    $"DraftsCount={drafts.Count}, Draft1={drafts.FirstOrDefault()?.DisplayTitle}, Draft2={drafts.ElementAtOrDefault(1)?.DisplayTitle}", sw.ElapsedMilliseconds);
            }

            // TC-0054-06: 通用 CSV 檔案批次解析 (逗號與多標籤拆分)
            {
                var sw = Stopwatch.StartNew();
                string sampleCsv = "\uFEFF姓名,公司,職稱,電話,Email,分類標籤\n" +
                                   "\"張大偉\",\"宏遠創新, 數位部\",\"處長\",\"0933-445-566\",\"wei@example.com\",\"VIP、供應商/顧問\"";
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sampleCsv));
                var drafts = excelCsvService.ParseImportFile(ms, ".csv");

                bool ok = drafts.Count == 1 &&
                          drafts[0].Name == "張大偉" &&
                          drafts[0].Company == "宏遠創新, 數位部" &&
                          drafts[0].Tags.Count >= 3;

                sw.Stop();
                Record("TC-0054-06", "通用 CSV 檔案批次解析 (引號轉義與標籤拆分)", ok,
                    $"Drafts={drafts.Count}, Company={drafts.FirstOrDefault()?.Company}, TagsCount={drafts.FirstOrDefault()?.Tags.Count}", sw.ElapsedMilliseconds);
            }

            // 準備 SQLite 資料庫環境以驗證匯入重複裁決 (Skip / Overwrite / AlwaysAdd)
            string dbStorageRoot = Path.Combine(tempBaseDir, "db_storage");
            var storageService = new StorageService(dbStorageRoot, autoInit: true);
            if (!storageService.IsAvailable)
            {
                throw new InvalidOperationException($"StorageService 初始化失敗: {storageService.ErrorMessage}");
            }

            // 預先在資料庫建立一筆聯絡人：「張曉明」在「甲式科技」
            var initDraft = new ImportContactDraft
            {
                Name = "張曉明",
                Company = "甲式科技",
                Title = "工程師",
                Mobile = "0911-000-111",
                Email = "old_ming@example.com",
                Tags = new List<string> { "初階成員" }
            };
            var initResult = await excelCsvService.ExecuteImportAsync(
                new[] { initDraft },
                ImportDuplicateStrategy.AlwaysAdd,
                storageService);

            // TC-0054-07: 重複處理策略：跳過重複 (SkipDuplicate)
            {
                var sw = Stopwatch.StartNew();
                var duplicateDraft = new ImportContactDraft
                {
                    Name = "張曉明",
                    Company = "甲式科技",
                    Title = "資深架構師", // 新資訊，但因為是 SkipDuplicate 故應被略過
                    Mobile = "0911-000-111"
                };

                var skipResult = await excelCsvService.ExecuteImportAsync(
                    new[] { duplicateDraft },
                    ImportDuplicateStrategy.SkipDuplicate,
                    storageService);

                sw.Stop();
                bool passed = skipResult.TotalParsed == 1 &&
                              skipResult.SkippedCount == 1 &&
                              skipResult.InsertedCount == 0 &&
                              skipResult.UpdatedCount == 0;

                Record("TC-0054-07", "重複處理策略：跳過重複 (SkipDuplicate) 驗證", passed,
                    skipResult.SummaryMessage, sw.ElapsedMilliseconds);
            }

            // TC-0054-08: 重複處理策略：覆蓋更新 (OverwriteExisting)
            {
                var sw = Stopwatch.StartNew();
                var overwriteDraft = new ImportContactDraft
                {
                    Name = "張曉明",
                    Company = "甲式科技",
                    Title = "技術總監",
                    Mobile = "0911-000-111",
                    Email = "new_ming@example.com",
                    Tags = new List<string> { "核心幹部" }
                };

                var overResult = await excelCsvService.ExecuteImportAsync(
                    new[] { overwriteDraft },
                    ImportDuplicateStrategy.OverwriteExisting,
                    storageService);

                // 檢查資料庫中該聯絡人是否成功更新為「技術總監」且標籤聯集包含「初階成員」與「核心幹部」
                var paged = await storageService.GetContactsPagedAsync("張曉明", 0, 10);
                var detail = paged.Items.Count > 0 ? await storageService.GetContactDetailAsync(paged.Items[0].ContactId) : null;
                var titleVal = detail?.Values.FirstOrDefault(v => v.ValueKind == CanonicalValueKind.Title)?.RawValue;
                var emailVal = detail?.Values.FirstOrDefault(v => v.ValueKind == CanonicalValueKind.Email)?.RawValue;
                bool tagMerged = detail?.Tags.Any(t => t.DisplayName == "核心幹部") == true &&
                                 detail?.Tags.Any(t => t.DisplayName == "初階成員") == true;

                sw.Stop();
                bool passed = overResult.UpdatedCount == 1 &&
                              overResult.InsertedCount == 0 &&
                              titleVal == "技術總監" &&
                              emailVal == "new_ming@example.com" &&
                              tagMerged;

                Record("TC-0054-08", "重複處理策略：覆蓋更新 (OverwriteExisting) 欄位與標籤整併驗證", passed,
                    $"UpdatedCount={overResult.UpdatedCount}, Title={titleVal}, Email={emailVal}, TagMerged={tagMerged}", sw.ElapsedMilliseconds);
            }

            // TC-0054-09: 重複處理策略：一律新增 (AlwaysAdd)
            {
                var sw = Stopwatch.StartNew();
                var alwaysAddDraft = new ImportContactDraft
                {
                    Name = "張曉明",
                    Company = "甲式科技",
                    Title = "同名同姓新同仁",
                    Mobile = "0999-888-777"
                };

                var addResult = await excelCsvService.ExecuteImportAsync(
                    new[] { alwaysAddDraft },
                    ImportDuplicateStrategy.AlwaysAdd,
                    storageService);

                var paged = await storageService.GetContactsPagedAsync("張曉明", 0, 10);
                sw.Stop();

                bool passed = addResult.InsertedCount == 1 && paged.Items.Count >= 2;
                Record("TC-0054-09", "重複處理策略：一律新增 (AlwaysAdd) 建立獨立記錄驗證", passed,
                    $"InsertedCount={addResult.InsertedCount}, TotalWithSameName={paged.Items.Count}", sw.ElapsedMilliseconds);
            }

            // TC-0054-10: 邊界與異常防護 (空檔案、缺少必要欄位略過、安全快照備份)
            {
                var sw = Stopwatch.StartNew();
                string emptyCsv = "\uFEFF姓名,公司,職稱\n,,";
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(emptyCsv));
                var emptyDrafts = excelCsvService.ParseImportFile(ms, ".csv");

                string backupDir = Path.Combine(dbStorageRoot, "backups");
                bool backupExists = Directory.Exists(backupDir) && Directory.GetFiles(backupDir, "pre_import_*.sqlite3").Length > 0;

                sw.Stop();
                bool passed = emptyDrafts.Count == 0 && backupExists;
                Record("TC-0054-10", "邊界與異常防護 (空列自動過濾與匯入前安全快照生成) 驗證", passed,
                    $"EmptyDrafts={emptyDrafts.Count}, PreImportBackupsExists={backupExists}", sw.ElapsedMilliseconds);
            }

            storageService.Dispose();
        }
        catch (Exception ex)
        {
            Record("TC-FATAL", "測試執行過程發生未預期例外", false, ex.ToString());
        }
        finally
        {
            try { Directory.Delete(tempBaseDir, true); } catch { }
        }

        // 保存證據報表
        try
        {
            Directory.CreateDirectory(evidenceDir);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string reportFile = Path.Combine(evidenceDir, "Issue0054_Automated_Test_Report.json");
            await File.WriteAllTextAsync(reportFile, json, Encoding.UTF8);

            var sb = new StringBuilder();
            sb.AppendLine($"# ISSUE-0054 自動化測試驗證報告");
            sb.AppendLine();
            sb.AppendLine($"- 驗證時間: {report.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"- 軟體版本: {report.Version}");
            sb.AppendLine($"- 執行環境: {report.Environment}");
            sb.AppendLine($"- 測試總數: {report.TotalTests}");
            sb.AppendLine($"- 通過數 (PASS): {report.PassCount}");
            sb.AppendLine($"- 失敗數 (FAIL): {report.FailCount}");
            sb.AppendLine($"- 綜合判定: {(report.OverallSuccess ? "PASS" : "FAIL")}");
            sb.AppendLine();
            sb.AppendLine("| 測試編號 | 測試項目描述 | 結果 | 延遲 (ms) | 驗證詳情 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var tc in report.TestCases)
            {
                sb.AppendLine($"| `{tc.TestId}` | {tc.Description} | **{(tc.Passed ? "PASS" : "FAIL")}** | {tc.LatencyMs} | {tc.Details.Replace("|", "\\|")} |");
            }

            string mdFile = Path.Combine(evidenceDir, "Issue0054_Automated_Test_Report.md");
            await File.WriteAllTextAsync(mdFile, sb.ToString(), Encoding.UTF8);
        }
        catch { }

        return report;
    }
}
