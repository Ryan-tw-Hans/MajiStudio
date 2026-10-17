using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MajiOCR_Windows.Services;

/// <summary>
/// Provides Windows DPAPI (CurrentUser scope via crypt32.dll) encryption and decryption
/// for local runtime credentials (e.g., Google OAuth ClientSecret and Vercel Access Token),
/// with transparent migration support for legacy plaintext values (ISSUE-0050).
/// </summary>
public static class CredentialProtectionHelper
{
    public const string Prefix = "dpapi:";
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private static readonly byte[] ApplicationEntropy =
        Encoding.UTF8.GetBytes("MajiOCR.Windows.DPAPI.Entropy.v1");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static bool IsProtected(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.StartsWith(Prefix, StringComparison.Ordinal);
    }

    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return plainText;
        }

        if (IsProtected(plainText))
        {
            return plainText;
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = ProtectBytes(plainBytes);
        return Prefix + Convert.ToBase64String(cipherBytes);
    }

    public static string? Unprotect(string? storedValue)
    {
        return UnprotectWithMetadata(storedValue).PlainText;
    }

    public static (string? PlainText, bool WasLegacyPlainText, bool DecryptionFailed) UnprotectWithMetadata(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return (storedValue, false, false);
        }

        if (!IsProtected(storedValue))
        {
            // Legacy plaintext value: return as-is and flag for automatic upgrade
            return (storedValue, true, false);
        }

        string base64Part = storedValue.Substring(Prefix.Length).Trim();
        if (string.IsNullOrEmpty(base64Part))
        {
            return (null, false, true);
        }

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(base64Part);
            byte[] plainBytes = UnprotectBytes(cipherBytes);
            string plainText = Encoding.UTF8.GetString(plainBytes);
            return (plainText, false, false);
        }
        catch
        {
            // Corrupted ciphertext or encrypted under a different Windows user/machine
            return (null, false, true);
        }
    }

    private static byte[] ProtectBytes(byte[] plainBytes)
    {
        GCHandle dataHandle = GCHandle.Alloc(plainBytes, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(ApplicationEntropy, GCHandleType.Pinned);
        DATA_BLOB outBlob = default;

        try
        {
            var inBlob = new DATA_BLOB
            {
                cbData = plainBytes.Length,
                pbData = dataHandle.AddrOfPinnedObject()
            };
            var entropyBlob = new DATA_BLOB
            {
                cbData = ApplicationEntropy.Length,
                pbData = entropyHandle.AddrOfPinnedObject()
            };

            if (!CryptProtectData(
                    ref inBlob,
                    "MajiOCR.Windows Credential",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN,
                    out outBlob))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"DPAPI CryptProtectData failed with Win32 error {err}.");
            }

            byte[] result = new byte[outBlob.cbData];
            if (outBlob.cbData > 0 && outBlob.pbData != IntPtr.Zero)
            {
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            }
            return result;
        }
        finally
        {
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
            if (dataHandle.IsAllocated) dataHandle.Free();
            if (entropyHandle.IsAllocated) entropyHandle.Free();
        }
    }

    private static byte[] UnprotectBytes(byte[] cipherBytes)
    {
        GCHandle dataHandle = GCHandle.Alloc(cipherBytes, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(ApplicationEntropy, GCHandleType.Pinned);
        DATA_BLOB outBlob = default;

        try
        {
            var inBlob = new DATA_BLOB
            {
                cbData = cipherBytes.Length,
                pbData = dataHandle.AddrOfPinnedObject()
            };
            var entropyBlob = new DATA_BLOB
            {
                cbData = ApplicationEntropy.Length,
                pbData = entropyHandle.AddrOfPinnedObject()
            };

            if (!CryptUnprotectData(
                    ref inBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN,
                    out outBlob))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"DPAPI CryptUnprotectData failed with Win32 error {err}.");
            }

            byte[] result = new byte[outBlob.cbData];
            if (outBlob.cbData > 0 && outBlob.pbData != IntPtr.Zero)
            {
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            }
            return result;
        }
        finally
        {
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
            if (dataHandle.IsAllocated) dataHandle.Free();
            if (entropyHandle.IsAllocated) entropyHandle.Free();
        }
    }
}
