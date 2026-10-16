using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MajiOCR_Windows.Services;

/// <summary>
/// Self-contained ISO/IEC 18004 QR Code Matrix & 32-bit BMP Image Generator.
/// Generates scannable QR codes for iPhone X camera installation of the deployed Vercel PWA URL
/// without requiring external NuGet packages.
/// </summary>
public static class QrCodeBmpGenerator
{
    // Version 1..10 capabilities for Error Correction Level L (Byte Mode)
    private static readonly int[] TotalCodewordsByVersion = { 0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };
    private static readonly int[] EccCodewordsPerBlockL = { 0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18 };
    private static readonly int[] NumBlocksL = { 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4 };

    private static readonly int[][] AlignmentLocations =
    {
        Array.Empty<int>(),
        Array.Empty<int>(),
        new[] { 6, 18 },
        new[] { 6, 22 },
        new[] { 6, 26 },
        new[] { 6, 30 },
        new[] { 6, 34 },
        new[] { 6, 22, 38 },
        new[] { 6, 24, 42 },
        new[] { 6, 26, 46 },
        new[] { 6, 28, 50 }
    };

    public static bool[,] GenerateMatrix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("QR Code 內容不可為空白。", nameof(text));

        byte[] payload = Encoding.UTF8.GetBytes(text);

        // Pick smallest version (1..10) that fits Byte mode at ECC Level L
        int version = 0;
        for (int v = 1; v <= 10; v++)
        {
            int dataCapBits = (TotalCodewordsByVersion[v] - EccCodewordsPerBlockL[v] * NumBlocksL[v]) * 8;
            int lenBits = v <= 9 ? 8 : 16;
            if (4 + lenBits + payload.Length * 8 <= dataCapBits)
            {
                version = v;
                break;
            }
        }

        if (version == 0)
            throw new InvalidOperationException("網址過長，超出 QR Code Version 10 容量上限。");

        int totalCodewords = TotalCodewordsByVersion[version];
        int eccPerBlock = EccCodewordsPerBlockL[version];
        int numBlocks = NumBlocksL[version];
        int dataCodewordsCount = totalCodewords - eccPerBlock * numBlocks;

        // 1. Encode bit stream (Mode 0100 = Byte Mode)
        var bits = new List<int>();
        AppendBits(bits, 0b0100, 4);
        AppendBits(bits, payload.Length, version <= 9 ? 8 : 16);
        foreach (byte b in payload)
            AppendBits(bits, b, 8);

        int maxDataBits = dataCodewordsCount * 8;
        int terminator = Math.Min(4, maxDataBits - bits.Count);
        AppendBits(bits, 0, terminator);
        while (bits.Count % 8 != 0)
            bits.Add(0);

        byte[] dataCodewords = new byte[dataCodewordsCount];
        for (int i = 0; i < bits.Count / 8; i++)
        {
            int val = 0;
            for (int j = 0; j < 8; j++)
                val = (val << 1) | bits[i * 8 + j];
            dataCodewords[i] = (byte)val;
        }

        byte[] padBytes = { 0xEC, 0x11 };
        int padIdx = 0;
        for (int i = bits.Count / 8; i < dataCodewordsCount; i++)
        {
            dataCodewords[i] = padBytes[padIdx % 2];
            padIdx++;
        }

        // 2. Split into blocks & compute Reed-Solomon ECC
        int shortBlockDataLen = dataCodewordsCount / numBlocks;
        int numLongBlocks = dataCodewordsCount % numBlocks;
        int numShortBlocks = numBlocks - numLongBlocks;

        var dataBlocks = new byte[numBlocks][];
        var eccBlocks = new byte[numBlocks][];
        int offset = 0;
        byte[] genPoly = ReedSolomonGeneratorDivisor(eccPerBlock);

        for (int b = 0; b < numBlocks; b++)
        {
            int blockLen = shortBlockDataLen + (b >= numShortBlocks ? 1 : 0);
            dataBlocks[b] = new byte[blockLen];
            Array.Copy(dataCodewords, offset, dataBlocks[b], 0, blockLen);
            offset += blockLen;
            eccBlocks[b] = ReedSolomonComputeRemainder(dataBlocks[b], genPoly);
        }

        // 3. Interleave data and ECC codewords
        var finalBits = new List<int>();
        int maxBlockLen = shortBlockDataLen + (numLongBlocks > 0 ? 1 : 0);
        for (int i = 0; i < maxBlockLen; i++)
        {
            for (int b = 0; b < numBlocks; b++)
            {
                if (i < dataBlocks[b].Length)
                    AppendBits(finalBits, dataBlocks[b][i], 8);
            }
        }
        for (int i = 0; i < eccPerBlock; i++)
        {
            for (int b = 0; b < numBlocks; b++)
                AppendBits(finalBits, eccBlocks[b][i], 8);
        }

        // 4. Construct QR matrix
        int size = version * 4 + 17;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];

        DrawFunctionPatterns(version, size, modules, isFunction);
        DrawCodewords(size, modules, isFunction, finalBits);

        // Apply Mask 0 ((row + col) % 2 == 0) & Format Info for Level L (01) + Mask 0 (000)
        ApplyMask(size, modules, isFunction, 0);
        DrawFormatBits(size, modules, isFunction, mask: 0);

        return modules;
    }

    public static byte[] GenerateBmpBytes(string text, int scale = 8, int quietZoneModules = 4)
    {
        bool[,] matrix = GenerateMatrix(text);
        int qrSize = matrix.GetLength(0);
        int totalModules = qrSize + quietZoneModules * 2;
        int imgSize = totalModules * Math.Max(2, scale);

        int rowStride = imgSize * 4;
        int pixelDataSize = rowStride * imgSize;
        int fileSize = 54 + pixelDataSize;

        using var ms = new MemoryStream(fileSize);
        using var bw = new BinaryWriter(ms);

        // BMP Header (14 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write(0);
        bw.Write(54);

        // DIB Header BITMAPINFOHEADER (40 bytes)
        bw.Write(40);
        bw.Write(imgSize);
        bw.Write(imgSize); // standard bottom-up DIB (positive height)
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0);
        bw.Write(pixelDataSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);

        int s = Math.Max(2, scale);
        for (int y = imgSize - 1; y >= 0; y--)
        {
            int modY = y / s - quietZoneModules;
            for (int x = 0; x < imgSize; x++)
            {
                int modX = x / s - quietZoneModules;
                bool dark = modX >= 0 && modX < qrSize && modY >= 0 && modY < qrSize && matrix[modY, modX];
                if (dark)
                {
                    bw.Write((byte)0x2A); // B
                    bw.Write((byte)0x17); // G
                    bw.Write((byte)0x0F); // R (#0F172A dark slate)
                    bw.Write((byte)0xFF); // A
                }
                else
                {
                    bw.Write((byte)0xFF);
                    bw.Write((byte)0xFF);
                    bw.Write((byte)0xFF);
                    bw.Write((byte)0xFF);
                }
            }
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static void AppendBits(List<int> list, int val, int numBits)
    {
        for (int i = numBits - 1; i >= 0; i--)
            list.Add((val >>> i) & 1);
    }

    private static void DrawFunctionPatterns(int version, int size, bool[,] modules, bool[,] isFunction)
    {
        // Timing patterns
        for (int i = 0; i < size; i++)
        {
            SetFunctionModule(modules, isFunction, 6, i, i % 2 == 0);
            SetFunctionModule(modules, isFunction, i, 6, i % 2 == 0);
        }

        // 3 Finder patterns
        DrawFinderPattern(size, modules, isFunction, 3, 3);
        DrawFinderPattern(size, modules, isFunction, size - 4, 3);
        DrawFinderPattern(size, modules, isFunction, 3, size - 4);

        // Alignment patterns
        int[] align = AlignmentLocations[version];
        for (int i = 0; i < align.Length; i++)
        {
            for (int j = 0; j < align.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == align.Length - 1) || (i == align.Length - 1 && j == 0))
                    continue;
                DrawAlignmentPattern(modules, isFunction, align[i], align[j]);
            }
        }

        // Reserve Format Info & Dark Module
        DrawFormatBits(size, modules, isFunction, 0);

        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++)
                rem = (rem << 1) ^ ((rem >>> 11) * 0x1F25);
            int bits = (version << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >>> i) & 1) != 0;
                int a = size - 11 + i % 3;
                int b = i / 3;
                SetFunctionModule(modules, isFunction, a, b, bit);
                SetFunctionModule(modules, isFunction, b, a, bit);
            }
        }
    }

    private static void DrawFinderPattern(int size, bool[,] modules, bool[,] isFunction, int centerX, int centerY)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int x = centerX + dx;
                int y = centerY + dy;
                if (x >= 0 && x < size && y >= 0 && y < size)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunctionModule(modules, isFunction, y, x, dist != 2 && dist != 4);
                }
            }
        }
    }

    private static void DrawAlignmentPattern(bool[,] modules, bool[,] isFunction, int centerX, int centerY)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunctionModule(modules, isFunction, centerY + dy, centerX + dx, dist != 1);
            }
        }
    }

    private static void DrawFormatBits(int size, bool[,] modules, bool[,] isFunction, int mask)
    {
        // ECC Level L = 1 (binary 01)
        int data = (1 << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
            rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;

        // First copy (around top-left finder pattern: col 8 vertical & row 8 horizontal)
        for (int i = 0; i <= 5; i++)
            SetFunctionModule(modules, isFunction, i, 8, ((bits >>> i) & 1) != 0);
        SetFunctionModule(modules, isFunction, 7, 8, ((bits >>> 6) & 1) != 0);
        SetFunctionModule(modules, isFunction, 8, 8, ((bits >>> 7) & 1) != 0);
        SetFunctionModule(modules, isFunction, 8, 7, ((bits >>> 8) & 1) != 0);
        for (int i = 9; i < 15; i++)
            SetFunctionModule(modules, isFunction, 8, 14 - i, ((bits >>> i) & 1) != 0);

        // Second copy (top-right row 8 horizontal & bottom-left col 8 vertical)
        for (int i = 0; i < 8; i++)
            SetFunctionModule(modules, isFunction, 8, size - 1 - i, ((bits >>> i) & 1) != 0);
        for (int i = 8; i < 15; i++)
            SetFunctionModule(modules, isFunction, size - 15 + i, 8, ((bits >>> i) & 1) != 0);

        // Always-dark module at (row = size - 8, col = 8)
        SetFunctionModule(modules, isFunction, size - 8, 8, true);
    }

    /// <summary>
    /// Standard ISO/IEC 18004 verifier/decoder that reads both Format Information copies,
    /// verifies BCH (15,5) format bits & Reed-Solomon ECC codewords, and decodes the UTF-8 payload.
    /// </summary>
    public static bool TryVerifyAndDecodeMatrix(bool[,] modules, out string decodedText, out string error)
    {
        decodedText = string.Empty;
        error = string.Empty;

        int size = modules.GetLength(0);
        if (size != modules.GetLength(1) || size < 21 || (size - 17) % 4 != 0)
        {
            error = $"Invalid QR matrix size: {size}";
            return false;
        }

        int version = (size - 17) / 4;

        // 1. Read Format Info Copy 1 (top-left) using standard ISO/IEC 18004 coordinates
        int copy1 = 0;
        for (int i = 0; i <= 5; i++)
            if (modules[i, 8]) copy1 |= (1 << i);
        if (modules[7, 8]) copy1 |= (1 << 6);
        if (modules[8, 8]) copy1 |= (1 << 7);
        if (modules[8, 7]) copy1 |= (1 << 8);
        for (int i = 9; i < 15; i++)
            if (modules[8, 14 - i]) copy1 |= (1 << i);

        // 2. Read Format Info Copy 2 (top-right + bottom-left) using standard ISO/IEC 18004 coordinates
        int copy2 = 0;
        for (int i = 0; i < 8; i++)
            if (modules[8, size - 1 - i]) copy2 |= (1 << i);
        for (int i = 8; i < 15; i++)
            if (modules[size - 15 + i, 8]) copy2 |= (1 << i);

        if (copy1 != copy2)
        {
            error = $"Format Info mismatch: Copy1=0x{copy1:X4}, Copy2=0x{copy2:X4}";
            return false;
        }

        if (!modules[size - 8, 8])
        {
            error = "Dark module at (size-8, 8) is not dark.";
            return false;
        }

        int unmaskedFormat = copy1 ^ 0x5412;
        int formatData = unmaskedFormat >>> 10;
        int bchRem = unmaskedFormat;
        for (int i = 14; i >= 10; i--)
        {
            if (((bchRem >>> i) & 1) != 0)
                bchRem ^= (0x537 << (i - 10));
        }
        if (bchRem != 0)
        {
            error = $"Format BCH checksum failed: raw=0x{copy1:X4}, rem=0x{bchRem:X4}";
            return false;
        }

        int mask = formatData & 0b111;
        var dummyModules = new bool[size, size];
        var isFunction = new bool[size, size];
        DrawFunctionPatterns(version, size, dummyModules, isFunction);

        var rawBits = new List<int>();
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (!isFunction[y, x])
                    {
                        bool maskBit = mask == 0 && (y + x) % 2 == 0;
                        bool bit = modules[y, x] ^ maskBit;
                        rawBits.Add(bit ? 1 : 0);
                    }
                }
            }
        }

        int totalCodewords = TotalCodewordsByVersion[version];
        int eccPerBlock = EccCodewordsPerBlockL[version];
        int numBlocks = NumBlocksL[version];
        int dataCodewordsCount = totalCodewords - eccPerBlock * numBlocks;

        byte[] allCodewords = new byte[totalCodewords];
        for (int i = 0; i < totalCodewords; i++)
        {
            int val = 0;
            for (int j = 0; j < 8; j++)
                val = (val << 1) | rawBits[i * 8 + j];
            allCodewords[i] = (byte)val;
        }

        // De-interleave blocks and verify Reed-Solomon ECC
        int shortBlockDataLen = dataCodewordsCount / numBlocks;
        int numLongBlocks = dataCodewordsCount % numBlocks;
        int numShortBlocks = numBlocks - numLongBlocks;
        int maxBlockLen = shortBlockDataLen + (numLongBlocks > 0 ? 1 : 0);

        var dataBlocks = new byte[numBlocks][];
        var eccBlocks = new byte[numBlocks][];
        for (int b = 0; b < numBlocks; b++)
        {
            int blockLen = shortBlockDataLen + (b >= numShortBlocks ? 1 : 0);
            dataBlocks[b] = new byte[blockLen];
            eccBlocks[b] = new byte[eccPerBlock];
        }

        int cwIdx = 0;
        for (int i = 0; i < maxBlockLen; i++)
        {
            for (int b = 0; b < numBlocks; b++)
            {
                if (i < dataBlocks[b].Length)
                    dataBlocks[b][i] = allCodewords[cwIdx++];
            }
        }
        for (int i = 0; i < eccPerBlock; i++)
        {
            for (int b = 0; b < numBlocks; b++)
                eccBlocks[b][i] = allCodewords[cwIdx++];
        }

        byte[] genPoly = ReedSolomonGeneratorDivisor(eccPerBlock);
        var dataStream = new List<byte>();
        for (int b = 0; b < numBlocks; b++)
        {
            byte[] expectedEcc = ReedSolomonComputeRemainder(dataBlocks[b], genPoly);
            for (int i = 0; i < eccPerBlock; i++)
            {
                if (expectedEcc[i] != eccBlocks[b][i])
                {
                    error = $"Reed-Solomon ECC mismatch in block {b} at byte {i}";
                    return false;
                }
            }
            dataStream.AddRange(dataBlocks[b]);
        }

        var dataBits = new List<int>();
        foreach (byte b in dataStream)
            AppendBits(dataBits, b, 8);

        int mode = 0;
        for (int i = 0; i < 4; i++)
            mode = (mode << 1) | dataBits[i];
        if (mode != 0b0100)
        {
            error = $"Unexpected mode indicator: {mode}";
            return false;
        }

        int lenBitsCount = version <= 9 ? 8 : 16;
        int payloadLen = 0;
        for (int i = 0; i < lenBitsCount; i++)
            payloadLen = (payloadLen << 1) | dataBits[4 + i];

        byte[] payloadBytes = new byte[payloadLen];
        int bitStart = 4 + lenBitsCount;
        for (int i = 0; i < payloadLen; i++)
        {
            int b = 0;
            for (int j = 0; j < 8; j++)
                b = (b << 1) | dataBits[bitStart + i * 8 + j];
            payloadBytes[i] = (byte)b;
        }

        decodedText = Encoding.UTF8.GetString(payloadBytes);
        return true;
    }

    private static void DrawCodewords(int size, bool[,] modules, bool[,] isFunction, List<int> bits)
    {
        int i = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (!isFunction[y, x] && i < bits.Count)
                    {
                        modules[y, x] = bits[i] != 0;
                        i++;
                    }
                }
            }
        }
    }

    private static void ApplyMask(int size, bool[,] modules, bool[,] isFunction, int mask)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!isFunction[y, x] && mask == 0 && (y + x) % 2 == 0)
                {
                    modules[y, x] = !modules[y, x];
                }
            }
        }
    }

    private static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int row, int col, bool isDark)
    {
        modules[row, col] = isDark;
        isFunction[row, col] = true;
    }

    private static byte[] ReedSolomonGeneratorDivisor(int degree)
    {
        byte[] result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = GfMultiply(result[j], root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }
            root = GfMultiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
    {
        byte[] result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
                result[i] ^= GfMultiply(divisor[i], factor);
        }
        return result;
    }

    private static byte GfMultiply(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >>> 7) * 0x11D);
            z ^= ((y >>> i) & 1) * x;
        }
        return (byte)z;
    }
}
