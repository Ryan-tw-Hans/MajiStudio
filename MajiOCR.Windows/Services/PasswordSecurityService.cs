using System;
using System.Security.Cryptography;
using System.Text;

namespace MajiOCR_Windows.Services;

/// <summary>
/// Provides secure password hashing, salt generation, verification, and key derivation
/// for MajiOCR.Windows user master password and SQLCipher database encryption.
/// Conforms to RS-ISSUE-0038, AR-ISSUE-0038, and DBA-ISSUE-0038.
/// </summary>
public static class PasswordSecurityService
{
    public const int SaltByteSize = 16;
    public const int HashByteSize = 32;
    public const int Pbkdf2Iterations = 100_000;
    public const int MinPasswordLength = 6;
    public const int MaxPasswordLength = 128;

    /// <summary>
    /// Validates password length and format.
    /// </summary>
    public static bool ValidatePassword(string? password, out string errorMessage)
    {
        if (string.IsNullOrEmpty(password))
        {
            errorMessage = "密碼不可為空白。";
            return false;
        }

        if (password.Length < MinPasswordLength)
        {
            errorMessage = $"密碼長度至少需為 {MinPasswordLength} 個字元。";
            return false;
        }

        if (password.Length > MaxPasswordLength)
        {
            errorMessage = $"密碼長度不可超過 {MaxPasswordLength} 個字元。";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    /// <summary>
    /// Computes a PBKDF2-SHA256 hash for a given password using a newly generated cryptographic salt.
    /// </summary>
    public static (string Hash, string Salt) HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be empty.", nameof(password));

        byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltByteSize);
        byte[] hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(password),
            salt: saltBytes,
            iterations: Pbkdf2Iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: HashByteSize);

        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    /// <summary>
    /// Verifies a candidate password against stored Base64 hash and salt using constant-time comparison.
    /// </summary>
    public static bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(storedSalt))
            return false;

        try
        {
            byte[] saltBytes = Convert.FromBase64String(storedSalt);
            byte[] expectedHashBytes = Convert.FromBase64String(storedHash);

            byte[] actualHashBytes = Rfc2898DeriveBytes.Pbkdf2(
                password: Encoding.UTF8.GetBytes(password),
                salt: saltBytes,
                iterations: Pbkdf2Iterations,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: expectedHashBytes.Length);

            return CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes);
        }
        catch
        {
            return false;
        }
    }
}
