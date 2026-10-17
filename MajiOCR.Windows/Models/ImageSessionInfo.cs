using System.Security.Cryptography;

namespace MajiOCR_Windows.Models;

/// <summary>
/// Session image metadata conforming to DBC-ISSUE-0005-v0.2 asset model.
/// </summary>
public class ImageSessionInfo
{
    public string SourceName { get; set; } = string.Empty;
    public string? OriginalFilePath { get; set; }
    public string CaptureSource { get; set; } = "ImagePickerCopy"; // "ImagePickerCopy" or "ClipboardImageCopy"
    public string MimeType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public long ByteCount { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
    public DateTime LoadedAt { get; set; } = DateTime.UtcNow;

    public static ImageSessionInfo Create(byte[] bytes, string sourceName, string captureSource, string mimeType, int width, int height, string? originalPath = null)
    {
        string hash;
        using (var sha = SHA256.Create())
        {
            byte[] hashBytes = sha.ComputeHash(bytes);
            hash = Convert.ToHexString(hashBytes);
        }

        return new ImageSessionInfo
        {
            SourceName = sourceName,
            OriginalFilePath = originalPath,
            CaptureSource = captureSource,
            MimeType = mimeType,
            Width = width,
            Height = height,
            ByteCount = bytes.Length,
            Sha256 = hash,
            ImageBytes = bytes,
            LoadedAt = DateTime.UtcNow
        };
    }
}
