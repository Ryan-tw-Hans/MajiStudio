using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services;

public class ImageLoadResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public ImageSessionInfo? SessionInfo { get; set; }
}

public class ImageService
{
    // Maximum limits and compression thresholds
    public const int MaxDimension = 8192;
    public const int TargetLongEdgeDimension = 2560; // Max crisp dimension for card OCR
    public const long MaxCardImageSizeBytes = 5 * 1024 * 1024; // 5 MB target max output size
    public const long MaxSafeInputSizeBytes = 100 * 1024 * 1024; // 100 MB safe limit for file input
    public const long MaxClipboardStreamBytes = 200 * 1024 * 1024; // 200 MB safe limit for uncompressed clipboard raw stream

    public static string DetectMimeType(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return "image/bmp";
        }

        return string.Empty;
    }

    public async Task<ImageLoadResult> LoadFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new ImageLoadResult { Success = false, ErrorMessage = "檔案不存在。" };
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxSafeInputSizeBytes)
            {
                return new ImageLoadResult
                {
                    Success = false,
                    ErrorMessage = $"檔案過大 ({fileInfo.Length / (1024 * 1024.0):F1} MB)，超過安全讀取上限 (100 MB)。"
                };
            }

            byte[] bytes = await File.ReadAllBytesAsync(filePath);
            string mimeType = DetectMimeType(bytes);
            if (string.IsNullOrEmpty(mimeType))
            {
                return new ImageLoadResult
                {
                    Success = false,
                    ErrorMessage = "檔案非支援的有效圖片格式 (僅支援真實 PNG、JPEG 或 BMP 內容)。"
                };
            }

            // Decode image to verify actual decodability and get dimensions
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;

            if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension)
            {
                return new ImageLoadResult
                {
                    Success = false,
                    ErrorMessage = $"圖片尺寸無效或超出上限 ({width}x{height})。"
                };
            }

            // If image is already <= 5MB and dimensions are within target limits (<= 2560px), keep as is
            if (bytes.Length <= MaxCardImageSizeBytes && width <= TargetLongEdgeDimension && height <= TargetLongEdgeDimension)
            {
                var session = ImageSessionInfo.Create(
                    bytes: bytes,
                    sourceName: $"檔案: {Path.GetFileName(filePath)}",
                    captureSource: "ImagePickerCopy",
                    mimeType: mimeType,
                    width: (int)width,
                    height: (int)height,
                    originalPath: filePath
                );

                return new ImageLoadResult { Success = true, SessionInfo = session };
            }

            // Otherwise, perform smart downscaling & compression to fit within 5MB
            var compressedResult = await CompressDecoderToTargetSizeAsync(
                decoder: decoder,
                sourceName: $"檔案: {Path.GetFileName(filePath)}",
                captureSource: "ImagePickerCopy",
                originalPath: filePath,
                preferOriginalMimeType: mimeType
            );

            return compressedResult;
        }
        catch (Exception ex)
        {
            return new ImageLoadResult
            {
                Success = false,
                ErrorMessage = $"讀取或解碼圖片失敗: {ex.Message}"
            };
        }
    }

    public async Task<ImageLoadResult> LoadFromClipboardAsync()
    {
        try
        {
            DataPackageView dataPackage;
            try
            {
                dataPackage = Clipboard.GetContent();
            }
            catch (Exception ex)
            {
                return new ImageLoadResult { Success = false, ErrorMessage = $"存取剪貼簿失敗: {ex.Message}" };
            }

            if (dataPackage == null)
            {
                return new ImageLoadResult { Success = false, ErrorMessage = "剪貼簿為空或無法讀取。" };
            }

            // Case 1: Clipboard contains Bitmap
            if (dataPackage.Contains(StandardDataFormats.Bitmap))
            {
                RandomAccessStreamReference streamRef = await dataPackage.GetBitmapAsync();
                using var stream = await streamRef.OpenReadAsync();
                if (stream.Size == 0)
                {
                    return new ImageLoadResult { Success = false, ErrorMessage = "剪貼簿圖片資料為空。" };
                }

                if (stream.Size > (ulong)MaxClipboardStreamBytes)
                {
                    return new ImageLoadResult { Success = false, ErrorMessage = $"剪貼簿原始資料過大 ({stream.Size / (1024 * 1024.0):F1} MB)，超過安全上限 (200 MB)。" };
                }

                // Decode bitmap
                var decoder = await BitmapDecoder.CreateAsync(stream);
                uint width = decoder.PixelWidth;
                uint height = decoder.PixelHeight;

                if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension)
                {
                    return new ImageLoadResult { Success = false, ErrorMessage = $"剪貼簿圖片尺寸無效 ({width}x{height})。" };
                }

                // Use smart compression pipeline to ensure <= 5MB
                var compressedResult = await CompressDecoderToTargetSizeAsync(
                    decoder: decoder,
                    sourceName: "剪貼簿貼上快照",
                    captureSource: "ClipboardImageCopy",
                    originalPath: null,
                    preferOriginalMimeType: "image/jpeg"
                );

                return compressedResult;
            }

            // Case 2: Clipboard contains file StorageItems
            if (dataPackage.Contains(StandardDataFormats.StorageItems))
            {
                var items = await dataPackage.GetStorageItemsAsync();
                if (items.Count == 0)
                {
                    return new ImageLoadResult { Success = false, ErrorMessage = "剪貼簿檔案項目為空。" };
                }

                if (items.Count > 1)
                {
                    return new ImageLoadResult { Success = false, ErrorMessage = "剪貼簿包含多個檔案，請僅複製單一名片圖片。" };
                }

                if (items[0] is StorageFile file)
                {
                    return await LoadFromFileAsync(file.Path);
                }

                return new ImageLoadResult { Success = false, ErrorMessage = "剪貼簿項目不是圖片檔案。" };
            }

            return new ImageLoadResult
            {
                Success = false,
                ErrorMessage = "剪貼簿中未偵測到有效圖片資料 (僅支援圖片或單一圖片檔案)。"
            };
        }
        catch (Exception ex)
        {
            return new ImageLoadResult
            {
                Success = false,
                ErrorMessage = $"貼上剪貼簿內容失敗: {ex.Message}"
            };
        }
    }

    private static async Task<ImageLoadResult> CompressDecoderToTargetSizeAsync(
        BitmapDecoder decoder,
        string sourceName,
        string captureSource,
        string? originalPath,
        string? preferOriginalMimeType)
    {
        uint origW = decoder.PixelWidth;
        uint origH = decoder.PixelHeight;

        // Step 1: Calculate target dimension (max 2560px)
        int targetW = (int)origW;
        int targetH = (int)origH;
        if (targetW > TargetLongEdgeDimension || targetH > TargetLongEdgeDimension)
        {
            double scale = Math.Min((double)TargetLongEdgeDimension / targetW, (double)TargetLongEdgeDimension / targetH);
            targetW = Math.Max(1, (int)Math.Round(targetW * scale));
            targetH = Math.Max(1, (int)Math.Round(targetH * scale));
        }

        // Multi-tier quality compression loop
        float[] qualityLevels = new float[] { 0.90f, 0.85f, 0.75f, 0.65f, 0.50f };
        byte[]? finalBytes = null;
        string finalMimeType = "image/jpeg";
        int finalWidth = targetW;
        int finalHeight = targetH;

        // If preferOriginalMimeType is PNG and dimensions were not oversized, try PNG first
        if (preferOriginalMimeType == "image/png" && origW <= (uint)TargetLongEdgeDimension && origH <= (uint)TargetLongEdgeDimension)
        {
            byte[] pngBytes = await EncodePixelDataAsync(decoder, (uint)targetW, (uint)targetH, BitmapEncoder.PngEncoderId, 1.0f);
            if (pngBytes.Length <= MaxCardImageSizeBytes)
            {
                finalBytes = pngBytes;
                finalMimeType = "image/png";
            }
        }

        if (finalBytes == null)
        {
            // Try JPEG encoding at descending quality levels
            int currentW = targetW;
            int currentH = targetH;

            for (int q = 0; q < qualityLevels.Length; q++)
            {
                byte[] jpegBytes = await EncodePixelDataAsync(decoder, (uint)currentW, (uint)currentH, BitmapEncoder.JpegEncoderId, qualityLevels[q]);
                if (jpegBytes.Length <= MaxCardImageSizeBytes)
                {
                    finalBytes = jpegBytes;
                    finalMimeType = "image/jpeg";
                    finalWidth = currentW;
                    finalHeight = currentH;
                    break;
                }
            }

            // If still > 5MB (very extreme case), step down dimensions by 0.7x and retry
            while (finalBytes == null && currentW > 400 && currentH > 400)
            {
                currentW = Math.Max(1, (int)Math.Round(currentW * 0.7));
                currentH = Math.Max(1, (int)Math.Round(currentH * 0.7));

                byte[] jpegBytes = await EncodePixelDataAsync(decoder, (uint)currentW, (uint)currentH, BitmapEncoder.JpegEncoderId, 0.75f);
                if (jpegBytes.Length <= MaxCardImageSizeBytes)
                {
                    finalBytes = jpegBytes;
                    finalMimeType = "image/jpeg";
                    finalWidth = currentW;
                    finalHeight = currentH;
                    break;
                }
            }
        }

        if (finalBytes == null || finalBytes.Length == 0)
        {
            return new ImageLoadResult
            {
                Success = false,
                ErrorMessage = "圖片壓縮至 5 MB 上限失敗，請確認圖檔內容。"
            };
        }

        var session = ImageSessionInfo.Create(
            bytes: finalBytes,
            sourceName: sourceName,
            captureSource: captureSource,
            mimeType: finalMimeType,
            width: finalWidth,
            height: finalHeight,
            originalPath: originalPath
        );

        return new ImageLoadResult { Success = true, SessionInfo = session };
    }

    private static async Task<byte[]> EncodePixelDataAsync(
        BitmapDecoder decoder,
        uint targetWidth,
        uint targetHeight,
        Guid encoderId,
        float quality)
    {
        var transform = new BitmapTransform();
        if (targetWidth != decoder.PixelWidth || targetHeight != decoder.PixelHeight)
        {
            transform.ScaledWidth = targetWidth;
            transform.ScaledHeight = targetHeight;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb
        );

        byte[] rawPixels = pixelData.DetachPixelData();

        using var outStream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (encoderId == BitmapEncoder.JpegEncoderId)
        {
            var propertySet = new BitmapPropertySet();
            var qualityValue = new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single);
            propertySet.Add("ImageQuality", qualityValue);
            encoder = await BitmapEncoder.CreateAsync(encoderId, outStream, propertySet);
        }
        else
        {
            encoder = await BitmapEncoder.CreateAsync(encoderId, outStream);
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            targetWidth,
            targetHeight,
            decoder.DpiX,
            decoder.DpiY,
            rawPixels
        );
        await encoder.FlushAsync();

        outStream.Seek(0);
        byte[] resultBytes = new byte[outStream.Size];
        using (var reader = new DataReader(outStream))
        {
            await reader.LoadAsync((uint)outStream.Size);
            reader.ReadBytes(resultBytes);
        }

        return resultBytes;
    }
}
