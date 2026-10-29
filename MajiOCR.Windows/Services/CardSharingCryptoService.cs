using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services;

public class CardSharingItem
{
    public string Name { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string TaxId { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public string Notes { get; set; } = string.Empty;
    public string? ImageBase64 { get; set; }

    // UI selection state for import
    public bool IsSelected { get; set; } = true;

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Name) ? Name : (!string.IsNullOrWhiteSpace(Company) ? Company : "未填姓名");
    public string DisplaySubtitle => !string.IsNullOrWhiteSpace(Company) ? Company : string.Empty;
    public string DisplayPhone => !string.IsNullOrWhiteSpace(Mobile) ? Mobile : Phone;
    public string DisplayTags => Tags.Count > 0 ? string.Join("、", Tags) : "未分類";
}

public class CardSharingPackage
{
    public int PackageVersion { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
    public List<CardSharingItem> Cards { get; set; } = new();
}

public interface ICardSharingCryptoService
{
    byte[] CreateEncryptedPackage(
        IReadOnlyList<ContactDetailModel> contacts,
        string password,
        bool includeTags = true,
        bool includeNotes = true,
        bool includeImages = true);

    CardSharingPackage DecryptPackage(byte[] packageBytes, string password);
}

public class CardSharingCryptoService : ICardSharingCryptoService
{
    private static readonly byte[] MagicBytes = Encoding.UTF8.GetBytes("MAJICARD"); // 8 bytes
    private const byte CurrentVersion = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12; // Standard for AES-GCM
    private const int TagSize = 16;   // 128-bit authentication tag
    private const int KeySize = 32;   // 256-bit AES
    private const int Iterations = 100_000;

    public byte[] CreateEncryptedPackage(
        IReadOnlyList<ContactDetailModel> contacts,
        string password,
        bool includeTags = true,
        bool includeNotes = true,
        bool includeImages = true)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("加密分享包必須設定密碼。", nameof(password));

        var pkg = new CardSharingPackage
        {
            PackageVersion = CurrentVersion,
            ExportedAt = DateTimeOffset.Now,
            Cards = new List<CardSharingItem>()
        };

        foreach (var c in contacts ?? Array.Empty<ContactDetailModel>())
        {
            var item = ConvertToSharingItem(c, includeTags, includeNotes, includeImages);
            pkg.Cards.Add(item);
        }

        string json = JsonSerializer.Serialize(pkg);
        byte[] plaintext = Encoding.UTF8.GetBytes(json);

        // 1. Generate random Salt & Nonce
        byte[] salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        byte[] nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        // 2. Derive 256-bit Key via PBKDF2
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

        // 3. Encrypt via AES-GCM
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // 4. Assemble package
        // [Magic: 8] [Version: 1] [Salt: 16] [Nonce: 12] [Tag: 16] [Ciphertext: N]
        using var ms = new MemoryStream();
        ms.Write(MagicBytes, 0, MagicBytes.Length);
        ms.WriteByte(CurrentVersion);
        ms.Write(salt, 0, salt.Length);
        ms.Write(nonce, 0, nonce.Length);
        ms.Write(tag, 0, tag.Length);
        ms.Write(ciphertext, 0, ciphertext.Length);

        return ms.ToArray();
    }

    public CardSharingPackage DecryptPackage(byte[] packageBytes, string password)
    {
        if (packageBytes == null || packageBytes.Length < MagicBytes.Length + 1 + SaltSize + NonceSize + TagSize)
            throw new InvalidDataException("此檔案不是有效的 MajiOCR 名片分享包或格式已損毀。");

        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("請輸入解密密碼。", nameof(password));

        using var ms = new MemoryStream(packageBytes);
        using var reader = new BinaryReader(ms);

        // 1. Verify Magic
        byte[] magic = reader.ReadBytes(MagicBytes.Length);
        for (int i = 0; i < MagicBytes.Length; i++)
        {
            if (magic[i] != MagicBytes[i])
                throw new InvalidDataException("檔案格式標頭不符，無法識別為 MajiOCR 加密分享包。");
        }

        // 2. Read Version
        byte version = reader.ReadByte();
        if (version != CurrentVersion)
            throw new NotSupportedException($"不支援的名片分享包版本 (v{version})。");

        // 3. Read Salt, Nonce, Tag
        byte[] salt = reader.ReadBytes(SaltSize);
        byte[] nonce = reader.ReadBytes(NonceSize);
        byte[] tag = reader.ReadBytes(TagSize);

        // 4. Read Ciphertext
        int cipherLen = (int)(ms.Length - ms.Position);
        byte[] ciphertext = reader.ReadBytes(cipherLen);

        // 5. Derive Key
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

        // 6. Decrypt
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("解密失敗：密碼不正確，或檔案內容已遭竄改。");
        }

        // 7. Parse JSON
        string json = Encoding.UTF8.GetString(plaintext);
        var pkg = JsonSerializer.Deserialize<CardSharingPackage>(json);
        if (pkg == null)
            throw new InvalidDataException("無法解析名片分享包內容。");

        return pkg;
    }

    private static CardSharingItem ConvertToSharingItem(
        ContactDetailModel c,
        bool includeTags,
        bool includeNotes,
        bool includeImages)
    {
        var item = new CardSharingItem
        {
            Name = c.Name ?? string.Empty,
            Company = c.Company ?? string.Empty,
            Title = string.Empty,
            Department = string.Empty,
            Mobile = string.Empty,
            Phone = string.Empty,
            Email = string.Empty,
            Address = string.Empty,
            TaxId = string.Empty,
            Website = string.Empty,
            Notes = string.Empty,
            Tags = new List<string>()
        };

        if (includeTags && c.Tags != null)
        {
            foreach (var t in c.Tags)
            {
                if (!string.IsNullOrWhiteSpace(t.DisplayName))
                    item.Tags.Add(t.DisplayName.Trim());
            }
        }

        var notesList = new List<string>();

        foreach (var v in c.Values ?? new List<ContactValueRecord>())
        {
            if (string.IsNullOrWhiteSpace(v.RawValue)) continue;

            switch (v.ValueKind)
            {
                case CanonicalValueKind.Name:
                    if (string.IsNullOrEmpty(item.Name)) item.Name = v.RawValue;
                    break;
                case CanonicalValueKind.Company:
                    if (string.IsNullOrEmpty(item.Company)) item.Company = v.RawValue;
                    break;
                case CanonicalValueKind.Title:
                    if (string.IsNullOrEmpty(item.Title)) item.Title = v.RawValue;
                    break;
                case CanonicalValueKind.Phone:
                    if (v.RawValue.StartsWith("09") || v.RawValue.StartsWith("+8869"))
                    {
                        if (string.IsNullOrEmpty(item.Mobile)) item.Mobile = v.RawValue;
                        else if (string.IsNullOrEmpty(item.Phone)) item.Phone = v.RawValue;
                        else if (includeNotes) notesList.Add($"電話: {v.RawValue}");
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(item.Phone)) item.Phone = v.RawValue;
                        else if (string.IsNullOrEmpty(item.Mobile)) item.Mobile = v.RawValue;
                        else if (includeNotes) notesList.Add($"電話: {v.RawValue}");
                    }
                    break;
                case CanonicalValueKind.Email:
                    if (string.IsNullOrEmpty(item.Email)) item.Email = v.RawValue;
                    else if (includeNotes) notesList.Add($"Email: {v.RawValue}");
                    break;
                case CanonicalValueKind.Address:
                    if (string.IsNullOrEmpty(item.Address)) item.Address = v.RawValue;
                    else if (includeNotes) notesList.Add($"地址: {v.RawValue}");
                    break;
                case CanonicalValueKind.Website:
                    if (string.IsNullOrEmpty(item.Website)) item.Website = v.RawValue;
                    break;
                case CanonicalValueKind.Note:
                    if (includeNotes) notesList.Add(v.RawValue);
                    break;
                default:
                    if (v.Label != null && (v.Label.Contains("統一編號") || v.Label.Contains("統編")))
                    {
                        if (string.IsNullOrEmpty(item.TaxId)) item.TaxId = v.RawValue;
                    }
                    else if (v.Label != null && v.Label.Contains("部門"))
                    {
                        if (string.IsNullOrEmpty(item.Department)) item.Department = v.RawValue;
                    }
                    else if (includeNotes)
                    {
                        notesList.Add($"{v.Label}: {v.RawValue}");
                    }
                    break;
            }
        }

        if (includeNotes && notesList.Count > 0)
        {
            item.Notes = string.Join("; ", notesList);
        }

        if (includeImages && c.VerifiedImageBytes != null && c.VerifiedImageBytes.Length > 0)
        {
            item.ImageBase64 = Convert.ToBase64String(c.VerifiedImageBytes);
        }

        return item;
    }
}
