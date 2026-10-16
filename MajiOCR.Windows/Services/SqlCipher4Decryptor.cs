using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MajiOCR_Windows.Services;

/// <summary>
/// Cross-platform SQLCipher 4 (SQLCipher-AES256) page-level decryptor and HMAC-SHA512 verifier.
/// Conforms to DBA-ISSUE-0044-v0.1 and mirrors MajiOCR.Web/js/sqlcipher-decryptor.js.
/// Decrypts an encrypted SQLCipher 4 database buffer into a standard "SQLite format 3\0" buffer
/// with 48-byte page reserve, verifiable by any standard SQLite engine or B-Tree parser.
/// </summary>
public static class SqlCipher4Decryptor
{
    public const int PageSize = 4096;
    public const int ReserveSize = 80; // 16-byte IV + 64-byte HMAC-SHA512 (SQLCipher 4 default)
    public const int SaltSize = 16;
    public const int IvSize = 16;
    public const int HmacSize = 64;
    public const int KdfIterations = 256_000;
    public const int HmacKdfIterations = 2;

    private static readonly byte[] SqliteHeaderMagic = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public static bool IsPlainSqliteBuffer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) return false;
        return bytes.Slice(0, 16).SequenceEqual(SqliteHeaderMagic);
    }

    public static byte[] DecryptDatabase(byte[] encryptedBytes, string passphrase)
    {
        if (encryptedBytes == null)
            throw new ArgumentNullException(nameof(encryptedBytes));

        if (IsPlainSqliteBuffer(encryptedBytes))
        {
            byte[] copy = new byte[encryptedBytes.Length];
            Buffer.BlockCopy(encryptedBytes, 0, copy, 0, encryptedBytes.Length);
            return copy;
        }

        if (string.IsNullOrEmpty(passphrase))
            throw new CryptographicException("PASSWORD_REQUIRED: 此備份資料庫受 SQLCipher-AES256 主密碼保護，請輸入主密碼。");

        if (encryptedBytes.Length < PageSize || encryptedBytes.Length % PageSize != 0)
            throw new InvalidOperationException($"資料庫檔案長度異常 ({encryptedBytes.Length} bytes)，非有效的 SQLCipher 4096-byte 分頁檔案。");

        byte[] passBytes = Encoding.UTF8.GetBytes(passphrase);
        byte[] kdfSalt = new byte[SaltSize];
        Buffer.BlockCopy(encryptedBytes, 0, kdfSalt, 0, SaltSize);

        // 1. Derive 32-byte AES-256 encryption key (PBKDF2-HMAC-SHA512, 256,000 iterations)
        byte[] encKey = Rfc2898DeriveBytes.Pbkdf2(
            password: passBytes,
            salt: kdfSalt,
            iterations: KdfIterations,
            hashAlgorithm: HashAlgorithmName.SHA512,
            outputLength: 32);

        // 2. Derive 32-byte HMAC key (PBKDF2-HMAC-SHA512, 2 iterations, salt XOR 0x3a)
        byte[] hmacSalt = new byte[SaltSize];
        for (int i = 0; i < SaltSize; i++)
        {
            hmacSalt[i] = (byte)(kdfSalt[i] ^ 0x3a);
        }

        byte[] hmacKey = Rfc2898DeriveBytes.Pbkdf2(
            password: encKey,
            salt: hmacSalt,
            iterations: HmacKdfIterations,
            hashAlgorithm: HashAlgorithmName.SHA512,
            outputLength: 32);

        int totalPages = encryptedBytes.Length / PageSize;
        byte[] output = new byte[encryptedBytes.Length];

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = encKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var hmac = new HMACSHA512(hmacKey);

        int activeReserveSize = ReserveSize;
        int activeHmacSize = HmacSize;

        for (int pageNo = 1; pageNo <= totalPages; pageNo++)
        {
            int pageStart = (pageNo - 1) * PageSize;
            ReadOnlySpan<byte> pageSpan = encryptedBytes.AsSpan(pageStart, PageSize);

            if (IsAllZeros(pageSpan))
                continue;

            int cipherOffset = pageNo == 1 ? SaltSize : 0;
            int cipherEnd = PageSize - activeReserveSize;
            int cipherLen = cipherEnd - cipherOffset;

            ReadOnlySpan<byte> ciphertext = pageSpan.Slice(cipherOffset, cipherLen);
            ReadOnlySpan<byte> iv = pageSpan.Slice(cipherEnd, IvSize);
            ReadOnlySpan<byte> storedHmac = pageSpan.Slice(cipherEnd + IvSize, activeHmacSize);

            byte[] hmacInput = new byte[cipherLen + IvSize + 4];
            ciphertext.CopyTo(hmacInput.AsSpan(0, cipherLen));
            iv.CopyTo(hmacInput.AsSpan(cipherLen, IvSize));
            BinaryPrimitives.WriteUInt32LittleEndian(hmacInput.AsSpan(cipherLen + IvSize, 4), (uint)pageNo);

            byte[] fullHmac = hmac.ComputeHash(hmacInput);
            bool hmacValid = CryptographicOperations.FixedTimeEquals(fullHmac.AsSpan(0, activeHmacSize), storedHmac);

            // Fallback check on Page 1 for 48-byte reserve (16-byte IV + 32-byte truncated HMAC)
            if (!hmacValid && pageNo == 1 && activeReserveSize == 80)
            {
                const int altReserve = 48;
                const int altHmac = 32;
                int altEnd = PageSize - altReserve;
                int altLen = altEnd - cipherOffset;
                ReadOnlySpan<byte> altCipher = pageSpan.Slice(cipherOffset, altLen);
                ReadOnlySpan<byte> altIv = pageSpan.Slice(altEnd, IvSize);
                ReadOnlySpan<byte> altStoredHmac = pageSpan.Slice(altEnd + IvSize, altHmac);

                byte[] altInput = new byte[altLen + IvSize + 4];
                altCipher.CopyTo(altInput.AsSpan(0, altLen));
                altIv.CopyTo(altInput.AsSpan(altLen, IvSize));
                BinaryPrimitives.WriteUInt32LittleEndian(altInput.AsSpan(altLen + IvSize, 4), 1u);

                byte[] altFullHmac = hmac.ComputeHash(altInput);
                if (CryptographicOperations.FixedTimeEquals(altFullHmac.AsSpan(0, altHmac), altStoredHmac))
                {
                    activeReserveSize = altReserve;
                    activeHmacSize = altHmac;
                    cipherEnd = altEnd;
                    cipherLen = altLen;
                    iv = altIv;
                    hmacValid = true;
                }
            }

            if (!hmacValid)
            {
                if (pageNo == 1)
                    throw new CryptographicException("INVALID_PASSWORD: 主密碼錯誤或資料庫無法解密 (SQLCipher Page 1 HMAC 驗證失敗)。");
                throw new CryptographicException($"CORRUPTED_PAGE: 資料庫第 {pageNo} 頁完整性校驗失敗。");
            }

            aes.IV = iv.ToArray();
            using var decryptor = aes.CreateDecryptor();
            byte[] plaintext = decryptor.TransformFinalBlock(encryptedBytes, pageStart + cipherOffset, cipherLen);

            if (pageNo == 1)
            {
                Buffer.BlockCopy(SqliteHeaderMagic, 0, output, 0, 16);
                Buffer.BlockCopy(plaintext, 0, output, 16, plaintext.Length);
                output[20] = (byte)activeReserveSize;
            }
            else
            {
                Buffer.BlockCopy(plaintext, 0, output, pageStart, plaintext.Length);
            }
        }

        return output;
    }

    private static bool IsAllZeros(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != 0) return false;
        }
        return true;
    }
}
