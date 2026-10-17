using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Verification;

public sealed class PortableProbeReport
{
    public string Version { get; set; } = Program.GetAppVersion();
    public string StorageRoot { get; set; } = "";
    public string DatabasePath { get; set; } = "";
    public bool Available { get; set; }
    public bool Saved { get; set; }
    public bool ReadBack { get; set; }
    public bool ImageVerified { get; set; }
    public bool AuxiliaryFilesProtected { get; set; }
    public string? ErrorCode { get; set; }
}

public static class Issue0014AutomatedTestSuite
{
    public static async Task<PortableProbeReport> ProbeAsync(bool seed)
    {
        MainViewModel.IsHeadlessEnvironment = true;
        using var storage = new StorageService();
        var report = new PortableProbeReport { StorageRoot = storage.StorageRoot, DatabasePath = storage.DatabasePath,
            Available = storage.IsAvailable, ErrorCode = storage.IsAvailable ? null : "STORAGE_UNAVAILABLE" };
        if (!storage.IsAvailable) return report;
        if (seed)
        {
            var vm = new MainViewModel(new CustomFieldCatalogService(Path.Combine(storage.StorageRoot, "probe-fields.json")), null, storage);
            await vm.SetSessionImageAsync(ImageSessionInfo.Create(Bmp(), "synthetic.bmp", "ImagePickerCopy", "image/bmp", 2, 2));
            vm.NameRow.Value = "portable.synthetic"; vm.NameRow.Confirm();
            StoragePathHelper.EnsurePrivateDatabaseFiles(storage.StorageRoot);
            using var keeper = new SqliteConnection(Conn(storage.DatabasePath, true));
            keeper.Open(); Sql(keeper, "SELECT count(*) FROM contact_records;");
            var saved = await vm.SaveCurrentDraftAsync(); report.Saved = saved.Succeeded;
            report.AuxiliaryFilesProtected = new[] { "-wal", "-shm" }.All(suffix =>
                File.Exists(storage.DatabasePath + suffix) && new FileInfo(storage.DatabasePath + suffix).GetAccessControl().AreAccessRulesProtected);
        }
        var page = await storage.GetContactsPagedAsync(null, 0);
        if (page.TotalCount == 1)
        {
            var detail = await storage.GetContactDetailAsync(page.Items[0].ContactId);
            report.ReadBack = detail?.Name == "portable.synthetic";
            report.ImageVerified = detail?.VerifiedImageBytes != null && detail.VerifiedImageBytes.SequenceEqual(Bmp());
        }
        return report;
    }

    public static async Task<Issue0011TestSuiteReport> RunAllTestsAsync(string evidenceDir)
    {
        var report = new Issue0011TestSuiteReport { IssueId = "ISSUE-0014" };
        string baseDir = StoragePathHelper.GetDefaultStorageRoot();
        string root = Path.Combine(baseDir, "portable-probes-" + Guid.NewGuid().ToString("N"));
        string marker = Path.Combine(root, ".owned-synthetic-fixture");
        Directory.CreateDirectory(root); File.WriteAllText(marker, root);
        string a = Path.Combine(root, "a"), b = Path.Combine(root, "b"), c = Path.Combine(root, "c");
        foreach (string dir in new[] { a, b, c }) Directory.CreateDirectory(dir);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("PROBE_EXE_MISSING");
        string aExe = Path.Combine(a, "MajiOCR.Windows.exe"), bExe = Path.Combine(b, "MajiOCR.Windows.exe");
        File.Copy(executable, aExe); File.Copy(executable, bExe);
        string aAcl = Acl(a), bAcl = Acl(b);
        void Record(string id, string description, bool passed, string details) => report.TestCases.Add(
            new Issue0011TestCaseResult { TestId = id, Description = description, Status = passed ? "PASS" : "FAIL", Details = details });
        try
        {
            var seed = await RunProbe(aExe, true, evidenceDir, "seed");
            bool defaultRoot = seed.ExitCode == 0 && seed.Report.Available && seed.Report.Saved && seed.Report.ReadBack && seed.Report.ImageVerified &&
                Path.TrimEndingDirectorySeparator(seed.Report.StorageRoot) == a && seed.Report.DatabasePath == Path.Combine(a, "contacts.sqlite3");
            Record("STORE14-T01", "不同CWD實際EXE預設同層保存與圖片讀回", defaultRoot, $"ActualDefaultRoot={seed.Report.StorageRoot}, SeedAndRead={defaultRoot}");
            string sourceDb = Path.Combine(a, "contacts.sqlite3");
            using (var writer = new SqliteConnection(Conn(sourceDb)))
            {
                writer.Open(); Sql(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE copy_probe(payload TEXT); INSERT INTO copy_probe VALUES('wal-committed');");
                bool walExisted = new FileInfo(sourceDb + "-wal").Length > 0;
                string before = StorageCopyService.Hash(sourceDb);
                var copy = StorageCopyService.Copy(a, b);
                using var copied = new SqliteConnection(Conn(Path.Combine(b, "contacts.sqlite3"), true));
                copied.Open();
                using var query = copied.CreateCommand(); query.CommandText = "SELECT payload FROM copy_probe;";
                bool walCopied = Convert.ToString(query.ExecuteScalar()) == "wal-committed";
                Record("COPY14-T01", "一致性備份包含未checkpoint的已提交WAL且來源保留", walExisted && walCopied && copy.Succeeded && copy.VerifiedAssets == 1 && before == StorageCopyService.Hash(sourceDb),
                    $"WalExisted={walExisted}, WalCopied={walCopied}, VerifiedAssets={copy.VerifiedAssets}, SourceBytesUnchanged={before == StorageCopyService.Hash(sourceDb)}");
            }
            var read = await RunProbe(bExe, false, evidenceDir, "relocated");
            Record("STORE14-T02", "另一EXE目錄重開相對圖片与名片讀回", read.ExitCode == 0 && read.Report.ReadBack && read.Report.ImageVerified && Path.TrimEndingDirectorySeparator(read.Report.StorageRoot) == b,
                $"RelocatedRoot={read.Report.StorageRoot}, ReadBack={read.Report.ReadBack}, ImageVerified={read.Report.ImageVerified}");
            bool protectedData = new FileInfo(Path.Combine(b, "contacts.sqlite3")).GetAccessControl().AreAccessRulesProtected &&
                new DirectoryInfo(Path.Combine(b, "card-images")).GetAccessControl().AreAccessRulesProtected;
            Record("STORE14-T03", "資料獨立私有ACL且兩個EXE父目錄ACL不改", protectedData && seed.Report.AuxiliaryFilesProtected && aAcl == Acl(a) && bAcl == Acl(b),
                $"DataProtected={protectedData}, ActiveAuxProtected={seed.Report.AuxiliaryFilesProtected}, ParentAUnchanged={aAcl == Acl(a)}, ParentBUnchanged={bAcl == Acl(b)}");
            string bDb = Path.Combine(b, "contacts.sqlite3"); string targetHash = StorageCopyService.Hash(bDb);
            bool repeatedBlocked = false;
            try { StorageCopyService.Copy(a, b); } catch (InvalidOperationException ex) { repeatedBlocked = ex.Message == "COPY_DESTINATION_HAS_DATA"; }
            Record("COPY14-T02", "已有目的資料拒覆蓋且目標DB原bytes保持", repeatedBlocked && targetHash == StorageCopyService.Hash(bDb), $"RepeatBlocked={repeatedBlocked}");
            File.SetAttributes(bDb, File.GetAttributes(bDb) | FileAttributes.ReadOnly);
            var readOnly = await RunProbe(bExe, false, evidenceDir, "read-only");
            File.SetAttributes(bDb, File.GetAttributes(bDb) & ~FileAttributes.ReadOnly);
            Record("STORE14-T04", "唯讀同層DB安全拒用且不改root或fallback", readOnly.ExitCode != 0 && !readOnly.Report.Available && Path.TrimEndingDirectorySeparator(readOnly.Report.StorageRoot) == b,
                $"ReadOnlyBlocked={!readOnly.Report.Available}, RootRemainsExecutable={Path.TrimEndingDirectorySeparator(readOnly.Report.StorageRoot) == b}");
            string unsupported = Path.Combine(root, "unsupported"); StoragePathHelper.EnsureStorageDirectories(unsupported);
            string unknownDb = Path.Combine(unsupported, "contacts.sqlite3");
            using (var newer = new SqliteConnection(Conn(unknownDb))) { newer.Open(); Sql(newer, "PRAGMA user_version=999;"); }
            string unknownHash = StorageCopyService.Hash(unknownDb); bool unknownBlocked = false;
            try { StorageCopyService.Copy(unsupported, c); } catch (InvalidOperationException ex) { unknownBlocked = ex.Message == "COPY_SOURCE_SCHEMA_UNSUPPORTED"; }
            Record("COPY14-T03", "未知較新schema拒複製、不寫目標且source bytes保持", unknownBlocked && !File.Exists(Path.Combine(c, "contacts.sqlite3")) && unknownHash == StorageCopyService.Hash(unknownDb), $"UnknownSchemaBlocked={unknownBlocked}");
        }
        catch (Exception ex) { Record("STORE14-UNEXPECTED", "可攜式整合測試例外", false, ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            try
            {
                string fullRoot = Path.GetFullPath(root);
                if (!fullRoot.StartsWith(baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "portable-probes-", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(marker) || File.ReadAllText(marker) != root || StoragePathHelper.IsReparsePoint(root))
                    throw new InvalidOperationException("FIXTURE_BOUNDARY_INVALID");
                ValidateTree(root);
                Directory.Delete(fullRoot, recursive: true); // Verified fresh owned UUID fixture only.
            }
            catch (Exception ex) { Record("STORE14-CLEANUP", "受控可攜式fixture清理", false, ex.GetType().Name); }
        }
        report.TotalTests = report.TestCases.Count; report.PassCount = report.TestCases.Count(x => x.Status == "PASS");
        report.FailCount = report.TestCases.Count(x => x.Status == "FAIL"); return report;
    }
    private static void ValidateTree(string root)
    {
        foreach (string item in Directory.EnumerateFileSystemEntries(root))
        { if (StoragePathHelper.IsReparsePoint(item)) throw new InvalidOperationException("FIXTURE_REPARSE");
          if (Directory.Exists(item)) ValidateTree(item); }
    }
    private static async Task<(int ExitCode, PortableProbeReport Report)> RunProbe(string exe, bool seed, string cwd, string name)
    {
        string evidence = Path.Combine(cwd, name); Directory.CreateDirectory(evidence);
        var start = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "--portable-probe", seed ? "--seed" : "--read", "--evidence-dir", evidence }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PROBE_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        File.WriteAllText(Path.Combine(evidence, "stdout.log"), await stdout); File.WriteAllText(Path.Combine(evidence, "stderr.log"), await stderr);
        var report = JsonSerializer.Deserialize<PortableProbeReport>(File.ReadAllText(Path.Combine(evidence, "PORTABLE_PROBE.json"))) ?? throw new InvalidOperationException("PROBE_REPORT_MISSING");
        return (process.ExitCode, report);
    }
    private static string Conn(string db, bool readOnly = false) => new SqliteConnectionStringBuilder { DataSource = db, Pooling = false, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate }.ConnectionString;
    private static string Acl(string directory) => new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    private static void Sql(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private static byte[] Bmp()
    {
        byte[] b = new byte[70]; b[0] = 66; b[1] = 77; BitConverter.GetBytes(70).CopyTo(b, 2); BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14); BitConverter.GetBytes(2).CopyTo(b, 18); BitConverter.GetBytes(2).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26); BitConverter.GetBytes((short)24).CopyTo(b, 28); BitConverter.GetBytes(16).CopyTo(b, 34); return b;
    }
}
