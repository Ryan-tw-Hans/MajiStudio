using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MajiOCR_Windows.Services.Storage;

public static class StoragePathHelper
{
    public const string CardImagesDirName = "card-images";
    public const string PendingDirName = ".pending";
    public const string DatabaseFileName = "contacts.sqlite3";

    public static string GetDefaultStorageRoot()
    {
        // This WinUI bundle extracts all content; BaseDirectory points into the extraction cache.
        string? executable = Environment.ProcessPath;
        string? directory = executable == null ? null : Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(directory)) throw new SecurityException("無法確認主程式目錄。");
        return Path.GetFullPath(directory);
    }

    public static void EnsureStorageDirectories(string storageRoot, bool applicationDirectory = false)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot));

        // 1. Verify existing path components from volume root down to root for reparse points
        string check = root;
        while (!string.IsNullOrEmpty(check))
        {
            if (IsReparsePoint(check))
            {
                throw new SecurityException("儲存目錄組件不安全，拒絕存取。");
            }
            string? parent = Path.GetDirectoryName(check);
            if (string.IsNullOrEmpty(parent) || parent.Equals(check, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            check = parent;
        }

        // 2. Create storage root if not exists and apply strict private ACL
        if (!Directory.Exists(root))
        {
            if (applicationDirectory) throw new DirectoryNotFoundException("主程式目錄不存在。");
            Directory.CreateDirectory(root);
            if (IsReparsePoint(root)) throw new SecurityException("儲存根目錄不安全。");
            ApplyUserAndAdminAcl(root);
        }
        else
        {
            if (IsReparsePoint(root))
            {
                throw new SecurityException($"儲存根目錄不可為 Reparse Point: 「{root}」。");
            }
            if (!applicationDirectory) VerifyAclProtection(root);
        }

        // 3. Create and verify subdirectories
        string imagesDir = Path.Combine(root, CardImagesDirName);
        VerifyPathSafety(root, imagesDir);
        if (!Directory.Exists(imagesDir))
        {
            Directory.CreateDirectory(imagesDir);
        }
        if (applicationDirectory)
        {
            ApplyUserAndAdminAcl(imagesDir);
        }
        if (IsReparsePoint(imagesDir))
        {
            throw new SecurityException($"圖片子目錄不可為 Reparse Point: 「{imagesDir}」。");
        }
        VerifyAclProtection(imagesDir, requireProtected: applicationDirectory);

        string pendingDir = Path.Combine(root, PendingDirName);
        VerifyPathSafety(root, pendingDir);
        if (!Directory.Exists(pendingDir))
        {
            Directory.CreateDirectory(pendingDir);
        }
        if (applicationDirectory)
        {
            ApplyUserAndAdminAcl(pendingDir);
        }
        if (IsReparsePoint(pendingDir))
        {
            throw new SecurityException($"暫存子目錄不可為 Reparse Point: 「{pendingDir}」。");
        }
        VerifyAclProtection(pendingDir, requireProtected: applicationDirectory);

        // 4. Verify DB path
        string dbPath = Path.Combine(root, DatabaseFileName);
        VerifyPathSafety(root, dbPath, checkReparse: true);
        if (applicationDirectory) EnsurePrivateDatabaseFiles(root, includeAuxiliary: false);
    }

    // Each file is first created inside the private pending directory, then renamed on the
    // same volume so no inherited public ACL is exposed while SQLite creates its sidecars.
    public static void EnsurePrivateDatabaseFiles(string storageRoot, bool includeAuxiliary = true)
    {
        string[] suffixes = includeAuxiliary ? new[] { "", "-wal", "-shm", "-journal" } : new[] { "" };
        foreach (string suffix in suffixes)
        {
            string target = Path.Combine(storageRoot, DatabaseFileName + suffix);
            VerifyPathSafety(storageRoot, target);
            if (!File.Exists(target))
            {
                string temporary = Path.Combine(storageRoot, PendingDirName, Guid.NewGuid().ToString("N") + ".tmp");
                VerifyPathSafety(storageRoot, temporary);
                using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                ProtectDataFile(temporary);
                try { File.Move(temporary, target, overwrite: false); }
                catch (IOException) when (File.Exists(target))
                {
                    VerifyPathSafety(storageRoot, temporary);
                    File.Delete(temporary); // Only our newly-created, empty, UUID staging file.
                }
            }
            VerifyPathSafety(storageRoot, target);
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("資料庫或暫存檔為唯讀。");
            ProtectDataFile(target);
        }
    }

    public static void EnsurePrivateDataDirectory(string storageRoot, string directoryPath)
    {
        VerifyPathSafety(storageRoot, directoryPath);
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
            VerifyPathSafety(storageRoot, directoryPath);
            ApplyUserAndAdminAcl(directoryPath);
        }
        VerifyAclProtection(directoryPath, requireProtected: false);
        if (!new DirectoryInfo(directoryPath).GetAccessControl().AreAccessRulesProtected)
            ApplyUserAndAdminAcl(directoryPath);
        VerifyAclProtection(directoryPath);
    }

    public static void ProtectDataFile(string filePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (IsReparsePoint(filePath)) throw new SecurityException("資料檔不可為 Reparse Point。");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new SecurityException("無法確認 Windows 使用者。");
        foreach (var sid in new[] { user,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(filePath).SetAccessControl(security);
    }

    public static void VerifyAclProtection(string directoryPath, bool requireProtected = true)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            var dSecurity = dirInfo.GetAccessControl(AccessControlSections.Access);
            if (requireProtected && !dSecurity.AreAccessRulesProtected)
            {
                throw new SecurityException($"儲存目錄 ACL 未受保護 (AreAccessRulesProtected=false): 「{directoryPath}」，拒絕使用未受保護之目錄以避免未授權存取。");
            }
            string? currentUser = WindowsIdentity.GetCurrent().User?.Value;
            if (currentUser == null) throw new SecurityException("無法確認 Windows 使用者。");
            var allowed = new[]
            {
                currentUser,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
            };
            foreach (FileSystemAccessRule rule in dSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    !Array.Exists(allowed, sid => sid == rule.IdentityReference.Value))
                    throw new SecurityException("儲存目錄允許非授權使用者存取，拒絕使用。");
            }
        }
        catch (SecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SecurityException($"無法驗證儲存目錄 ACL 權限: 「{directoryPath}」- {ex.Message}", ex);
        }
    }

    private static void ApplyUserAndAdminAcl(string directoryPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            var dSecurity = new DirectorySecurity();
            dSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                dSecurity.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            dSecurity.AddAccessRule(new FileSystemAccessRule(
                adminSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            dSecurity.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(dSecurity);
        }
        catch (Exception ex)
        {
            throw new SecurityException($"無法設定私有目錄存取權限 (ACL): {ex.Message}，為保護個資拒絕使用未保護之目錄。", ex);
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            // Fail closed: if attributes cannot be read or access is denied, treat as suspicious/reparse
            return true;
        }
    }

    public static bool IsGuidN(string stem)
    {
        if (string.IsNullOrEmpty(stem) || stem.Length != 32) return false;
        return Guid.TryParseExact(stem, "N", out _);
    }

    public static bool IsOwnedAssetPath(string storageRoot, string filePath, out string? sanitizedRelativePath, string? expectedAssetId = null)
    {
        sanitizedRelativePath = null;
        try
        {
            string canonicalRoot = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string canonicalFile = Path.GetFullPath(filePath);

            if (!canonicalFile.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            VerifyPathSafety(canonicalRoot, canonicalFile);

            string relative = canonicalFile.Substring(canonicalRoot.Length + 1);
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2) return false;

            string folder = parts[0];
            string fileName = parts[1];
            string stem = Path.GetFileNameWithoutExtension(fileName);

            if (!IsGuidN(stem)) return false;

            if (!string.IsNullOrEmpty(expectedAssetId) && !stem.Equals(expectedAssetId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (folder.Equals(CardImagesDirName, StringComparison.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp") return false;

                sanitizedRelativePath = Path.Combine(CardImagesDirName, fileName);
                return true;
            }
            else if (folder.Equals(PendingDirName, StringComparison.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (ext != ".tmp") return false;

                sanitizedRelativePath = Path.Combine(PendingDirName, fileName);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void VerifyPathSafety(string storageRoot, string targetPath, bool checkReparse = true)
    {
        string canonicalRoot = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string canonicalTarget = Path.GetFullPath(targetPath);

        // 1. Boundary check: canonicalTarget must start with canonicalRoot
        if (!canonicalTarget.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !canonicalTarget.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("存取路徑越界 (Path traversal out of bounds): 拒絕存取受控資料目錄外部路徑。");
        }

        // 2. Reparse point check on existing components from root down to target
        if (checkReparse)
        {
            string current = canonicalTarget;
            while (!string.IsNullOrEmpty(current))
            {
                if (IsReparsePoint(current))
                {
                    throw new SecurityException($"路徑包含 Reparse Point (Symlink/Junction): 拒絕存取「{current}」。");
                }

                if (current.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
        }
    }

    public static string ResolveSafePath(string storageRoot, string relativePath, bool checkReparse = true)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("相對路徑不可為空。", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new SecurityException("禁止使用絕對路徑作為受控儲存相對路徑。");
        }

        string normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        string combined = Path.Combine(storageRoot, normalized);
        VerifyPathSafety(storageRoot, combined, checkReparse);
        return combined;
    }
}
