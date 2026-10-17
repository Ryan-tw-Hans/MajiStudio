using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MajiOCR_Windows.Models;
using Tesseract;

namespace MajiOCR_Windows.Services;

public class ReviewAdoptionDecision
{
    public bool ShouldAdopt { get; set; }
    public string AdoptedText { get; set; } = string.Empty;
    public float AdoptedConfidence { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Provides conservative, explainable Traditional Chinese secondary review for OCR candidates.
/// Adheres to RS-ISSUE-0007 and IP-ISSUE-0007-prototype-v0.1.
/// </summary>
public static class TraditionalChineseReviewer
{
    private static readonly HashSet<string> KnownAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "CEO", "CTO", "COO", "CFO", "CIO", "CMO", "VP", "SVP", "EVP", "AVP",
        "GM", "PM", "RD", "HR", "IT", "PR", "QA", "QC", "UI", "UX", "AI", "ML",
        "Co", "Ltd", "Inc", "Corp", "LLC", "Dr", "Mr", "Ms", "TEL", "FAX",
        "EXT", "NO", "ID", "APP"
    };

    public static bool IsCjk(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF) ||
               (c >= 0x3400 && c <= 0x4DBF) ||
               (c >= 0xF900 && c <= 0xFAFF);
    }

    public static bool IsHangul(char c)
    {
        return (c >= 0xAC00 && c <= 0xD7AF) ||
               (c >= 0x1100 && c <= 0x11FF) ||
               (c >= 0x3130 && c <= 0x318F);
    }

    public static bool IsKana(char c)
    {
        return (c >= 0x3040 && c <= 0x309F) ||
               (c >= 0x30A0 && c <= 0x30FF);
    }

    public static bool IsLatin(char c)
    {
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }

    /// <summary>
    /// Checks if a string contains anomalous Latin fragments (isolated single letters,
    /// Latin directly adjacent to CJK, or consonant clusters without vowels).
    /// Legitimate parenthesized English, emails, URLs, and phone numbers are excluded.
    /// </summary>
    public static bool HasAnomalousLatin(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Negative filters for email and URLs
        if (text.Contains('@') ||
            text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("www.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stripped = StripLegitimateParentheses(text);
        var matches = Regex.Matches(stripped, @"[a-zA-Z]+");
        if (matches.Count == 0) return false;

        foreach (Match m in matches)
        {
            string latin = m.Value;

            if (KnownAcronyms.Contains(latin)) continue;

            // Single isolated Latin letter (e.g. 'b', 'v', 'a')
            if (latin.Length == 1) return true;

            // Latin directly adjacent to CJK character without whitespace or punctuation
            int startIdx = m.Index;
            int endIdx = m.Index + m.Length;
            if (startIdx > 0 && IsCjk(stripped[startIdx - 1])) return true;
            if (endIdx < stripped.Length && IsCjk(stripped[endIdx])) return true;

            // Consonant cluster without vowels (e.g. 'xkj', 'krpt')
            bool hasVowel = latin.Any(c => "aeiouyAEIOUY".Contains(c));
            if (!hasVowel) return true;
        }

        return false;
    }

    private static string StripLegitimateParentheses(string text)
    {
        return Regex.Replace(text, @"[\(（\[][^\)）\]]*[\)）\]]", m =>
        {
            string inner = m.Value.Trim('(', ')', '（', '）', '[', ']');
            var words = Regex.Matches(inner, @"[a-zA-Z]+");
            if (words.Count > 0 && words.All(w => w.Value.Any(c => "aeiouyAEIOUY".Contains(c)) || KnownAcronyms.Contains(w.Value)))
            {
                return " ";
            }
            return m.Value;
        });
    }

    private static bool IsLegitimateKoreanEnglish(string text)
    {
        int hangulCount = text.Count(IsHangul);
        int cjkCount = text.Count(IsCjk);
        int latinCount = text.Count(IsLatin);

        if (hangulCount >= 2 && cjkCount == 0 && latinCount > 0)
        {
            var latinWords = Regex.Matches(text, @"[a-zA-Z]+");
            if (latinWords.Count > 0 && latinWords.All(w => w.Value.Length >= 2 && (w.Value.Any(c => "aeiouyAEIOUY".Contains(c)) || KnownAcronyms.Contains(w.Value))))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Determines whether an OCR candidate line is eligible for Traditional Chinese secondary review.
    /// Follows conservative boundaries: pure Korean, pure English, Japanese, Email, URL, Phone are excluded.
    /// </summary>
    public static bool ShouldReview(OcrCandidateItem candidate)
    {
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.Text)) return false;

        string text = candidate.Text.Trim();

        // 1. Negative Filter: Email
        if (text.Contains('@') && Regex.IsMatch(text, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"))
        {
            return false;
        }
        if (FieldSuggester.Suggest(text) == CanonicalValueKind.Email)
        {
            return false;
        }

        // 2. Negative Filter: Website / URL
        if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
            FieldSuggester.Suggest(text) == CanonicalValueKind.Website)
        {
            return false;
        }

        // 3. Negative Filter: Phone number
        if (FieldSuggester.Suggest(text) == CanonicalValueKind.Phone)
        {
            return false;
        }
        int digitCount = text.Count(char.IsDigit);
        int nonWhitespaceCount = text.Count(c => !char.IsWhiteSpace(c));
        if (digitCount >= 6 && nonWhitespaceCount > 0 && ((float)digitCount / nonWhitespaceCount) >= 0.5f)
        {
            return false;
        }
        if (text.StartsWith("電話", StringComparison.Ordinal) ||
            text.StartsWith("行動", StringComparison.Ordinal) ||
            text.StartsWith("TEL", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("FAX", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 4. Negative Filter: Japanese Kana
        if (text.Any(IsKana))
        {
            return false;
        }

        int cjkCount = text.Count(IsCjk);
        int hangulCount = text.Count(IsHangul);
        int latinCount = text.Count(IsLatin);

        // 5. Negative Filter: Pure Korean
        if (hangulCount > 0 && cjkCount == 0 && latinCount == 0)
        {
            return false;
        }

        // 6. Filter: Pure English (exclude legitimate English words, but allow anomalous short garbled Latin)
        if (latinCount > 0 && cjkCount == 0 && hangulCount == 0)
        {
            if (HasAnomalousLatin(text) && (candidate.Confidence < 0.60f || text.Length <= 8))
            {
                return true;
            }
            return false;
        }

        // 7. Negative Filter: Legitimate Korean + English phrase (e.g. ": 한국 비즈니스 솔루션 (Seoul Office)")
        if (IsLegitimateKoreanEnglish(text))
        {
            return false;
        }

        // 8. Positive Trigger: CJK mixed with Hangul (script confusion)
        if (cjkCount > 0 && hangulCount > 0)
        {
            return true;
        }

        // 9. Positive Trigger: CJK mixed with anomalous Latin fragments
        if (cjkCount > 0 && HasAnomalousLatin(text))
        {
            return true;
        }

        // 10. Positive Trigger: Short Hangul + Latin mix that is not legitimate Korean + English
        // (Handles failure mode where Chinese name was recognized as Hangul + Latin due to multi-model confusion)
        if (hangulCount > 0 && latinCount > 0 && cjkCount == 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates whether a Traditional Chinese review result should replace the original candidate.
    /// Conservative criteria: must have CJK, must not have Hangul, must meet confidence & geometry checks,
    /// and must demonstrably improve script purity or character completeness.
    /// </summary>
    public static ReviewAdoptionDecision EvaluateAdoption(
        OcrCandidateItem original,
        string? reviewedText,
        float reviewedConfidence)
    {
        var decision = new ReviewAdoptionDecision
        {
            ShouldAdopt = false,
            AdoptedText = original.Text,
            AdoptedConfidence = original.Confidence,
            Reason = "保持原始候選"
        };

        if (string.IsNullOrWhiteSpace(reviewedText))
        {
            decision.Reason = "繁中複核結果為空，回退原候選";
            return decision;
        }

        string cleanReviewed = reviewedText.Trim();

        if (string.Equals(cleanReviewed, original.Text, StringComparison.Ordinal))
        {
            decision.Reason = "複核文字與原候選相同，無需替換";
            return decision;
        }

        int reviewCjkCount = cleanReviewed.Count(IsCjk);
        if (reviewCjkCount == 0)
        {
            decision.Reason = "複核文字未包含任何繁體中文字元，拒絕採用";
            return decision;
        }

        int reviewHangulCount = cleanReviewed.Count(IsHangul);
        if (reviewHangulCount > 0)
        {
            decision.Reason = "複核文字仍含韓文字元，拒絕採用";
            return decision;
        }

        if (reviewedConfidence < 45.0f)
        {
            decision.Reason = $"複核信心度過低 ({reviewedConfidence:F1}% < 45%)，拒絕採用";
            return decision;
        }

        if (original.Height > 0 && original.Width > 0)
        {
            float minExpectedWidth = reviewCjkCount * (original.Height * 0.30f);
            if (original.Width < minExpectedWidth && original.Width < 25)
            {
                decision.Reason = $"複核字數 ({reviewCjkCount} 字) 與候選區域寬度 ({original.Width}px) 不符，拒絕採用";
                return decision;
            }
        }

        int origCjkCount = original.Text.Count(IsCjk);
        int origHangulCount = original.Text.Count(IsHangul);
        bool origHasAnomalousLatin = HasAnomalousLatin(original.Text);
        bool reviewHasAnomalousLatin = HasAnomalousLatin(cleanReviewed);

        bool improvesKorean = origHangulCount > 0 && reviewHangulCount == 0;
        bool improvesLatin = origHasAnomalousLatin && !reviewHasAnomalousLatin;
        bool improvesCjk = reviewCjkCount > origCjkCount && !reviewHasAnomalousLatin;

        // 必須具備明確改善指標 (消除韓文混淆、消除異常拉丁，或在無異常拉丁下增加中文字數) 才採用
        if (!improvesKorean && !improvesLatin && !improvesCjk)
        {
            decision.Reason = "複核結果未呈現明確改善指標 (未消除韓文混淆、未消除異常拉丁且未增加繁體字數)，保留原候選";
            return decision;
        }

        decision.ShouldAdopt = true;
        decision.AdoptedText = cleanReviewed;
        decision.AdoptedConfidence = reviewedConfidence;
        decision.Reason = improvesKorean
            ? "消除韓文混淆並以繁中候選取代"
            : (improvesLatin ? "消除異常拉丁片段並以繁中候選取代" : "提升繁體中文字數與完整度");

        return decision;
    }

    /// <summary>
    /// Checks if a candidate line is eligible for Windows NLS traditional Chinese glyph mapping.
    /// Excludes pure English, Email, URL, Phone, pure Korean, Japanese Kana, and whitespace.
    /// Requires at least one CJK character.
    /// </summary>
    public static bool ShouldAttemptGlyphMapping(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();

        // 1. Negative Filter: Japanese Kana (hiragana or katakana)
        if (trimmed.Any(IsKana))
        {
            return false;
        }

        // 2. Negative Filter: Hangul
        if (trimmed.Any(IsHangul))
        {
            return false;
        }

        // 3. Must contain at least one CJK character
        if (!trimmed.Any(IsCjk))
        {
            return false;
        }

        // 4. Negative Filter: Email
        if (trimmed.Contains('@') && Regex.IsMatch(trimmed, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"))
        {
            return false;
        }
        if (FieldSuggester.Suggest(trimmed) == CanonicalValueKind.Email)
        {
            return false;
        }

        // 5. Negative Filter: Website / URL
        if (trimmed.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
            FieldSuggester.Suggest(trimmed) == CanonicalValueKind.Website)
        {
            return false;
        }

        // 6. Negative Filter: Phone number
        if (FieldSuggester.Suggest(trimmed) == CanonicalValueKind.Phone)
        {
            return false;
        }
        int digitCount = trimmed.Count(char.IsDigit);
        int nonWhitespaceCount = trimmed.Count(c => !char.IsWhiteSpace(c));
        if (digitCount >= 6 && nonWhitespaceCount > 0 && ((float)digitCount / nonWhitespaceCount) >= 0.5f)
        {
            return false;
        }
        if (trimmed.StartsWith("電話", StringComparison.Ordinal) ||
            trimmed.StartsWith("行動", StringComparison.Ordinal) ||
            trimmed.StartsWith("TEL", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("FAX", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Executes single-line ROI crop OCR with the secondary chi_tra engine without adopting or modifying candidate.
    /// </summary>
    public static bool TryReviewWithChiTra(
        TesseractEngine chiTraEngine,
        Pix pix,
        OcrCandidateItem candidate,
        CancellationToken ct,
        out string reviewedText,
        out float reviewedConfidence)
    {
        reviewedText = string.Empty;
        reviewedConfidence = 0f;

        ct.ThrowIfCancellationRequested();

        int padY = Math.Max(2, (int)(candidate.Height * 0.12));
        int padX = Math.Max(3, (int)(candidate.Height * 0.15));

        int x1 = Math.Clamp(candidate.X - padX, 0, pix.Width - 1);
        int y1 = Math.Clamp(candidate.Y - padY, 0, pix.Height - 1);
        int x2 = Math.Clamp(candidate.X + candidate.Width + padX, x1 + 1, pix.Width);
        int y2 = Math.Clamp(candidate.Y + candidate.Height + padY, y1 + 1, pix.Height);

        if (x2 - x1 < 10 || y2 - y1 < 10)
        {
            return false;
        }

        var roiRect = Rect.FromCoords(x1, y1, x2, y2);
        ct.ThrowIfCancellationRequested();

        try
        {
            using (var page = chiTraEngine.Process(pix, roiRect, PageSegMode.SingleLine))
            {
                ct.ThrowIfCancellationRequested();
                reviewedText = page.GetText()?.Trim() ?? string.Empty;
                reviewedConfidence = page.GetMeanConfidence() * 100f;
            }

            if (string.IsNullOrWhiteSpace(reviewedText))
            {
                ct.ThrowIfCancellationRequested();
                using (var page = chiTraEngine.Process(pix, roiRect, PageSegMode.SingleBlock))
                {
                    ct.ThrowIfCancellationRequested();
                    reviewedText = page.GetText()?.Trim() ?? string.Empty;
                    reviewedConfidence = page.GetMeanConfidence() * 100f;
                }
            }

            return !string.IsNullOrWhiteSpace(reviewedText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TraditionalChineseReviewer] ChiTra crop review failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Generates Traditional Chinese suggestion according to ISSUE-0007 specification.
    /// Strictly preserves original candidate Text, Coordinates, Confidence, and Source.
    /// Priority 1: Secondary chi_tra single-line review (when ShouldReview is true and chiTraEngine is available).
    /// Priority 2: Windows NLS LCMapStringEx traditional Chinese glyph mapping (when ShouldAttemptGlyphMapping is true).
    /// Populates candidate.TraditionalSuggestion and candidate.SuggestionSource without modifying original Text.
    /// </summary>
    public static void GenerateTraditionalSuggestion(
        TesseractEngine? chiTraEngine,
        Pix? pix,
        OcrCandidateItem candidate,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Ensure candidate.OriginalText is recorded as its original text
        if (string.IsNullOrEmpty(candidate.OriginalText))
        {
            candidate.OriginalText = candidate.Text;
        }

        // Priority 1: 繁中單語複核 (chi_tra crop OCR)
        if (chiTraEngine != null && pix != null && ShouldReview(candidate))
        {
            if (TryReviewWithChiTra(chiTraEngine, pix, candidate, ct, out var reviewedText, out var reviewedConf))
            {
                var decision = EvaluateAdoption(candidate, reviewedText, reviewedConf);
                if (decision.ShouldAdopt)
                {
                    candidate.TraditionalSuggestion = decision.AdoptedText;
                    candidate.SuggestionSource = "繁中單語複核";
                    return;
                }
            }
        }

        // Priority 2: Windows 字形轉換 (LCMapStringEx)
        if (ShouldAttemptGlyphMapping(candidate.Text))
        {
            string? mapped = WindowsNlsHelper.TryMapToTraditionalChinese(candidate.Text);
            if (!string.IsNullOrWhiteSpace(mapped) && !string.Equals(mapped, candidate.Text, StringComparison.Ordinal))
            {
                candidate.TraditionalSuggestion = mapped;
                candidate.SuggestionSource = "Windows 字形轉換";
                return;
            }
        }

        // No valid suggestion
        candidate.TraditionalSuggestion = null;
        candidate.SuggestionSource = null;
    }

    /// <summary>
    /// Executes Traditional Chinese suggestion evaluation for a candidate line.
    /// Preserves original candidate Text and populates TraditionalSuggestion.
    /// </summary>
    public static bool ReviewCandidateLine(
        TesseractEngine chiTraEngine,
        Pix pix,
        OcrCandidateItem candidate,
        CancellationToken ct)
    {
        GenerateTraditionalSuggestion(chiTraEngine, pix, candidate, ct);
        return candidate.HasSuggestion;
    }
}

/// <summary>
/// Helper for Windows Native NLS API (LCMapStringEx) character mapping.
/// Adheres strictly to official Unicode/buffer length contracts and safe error fallback.
/// </summary>
public static class WindowsNlsHelper
{
    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

    internal static bool SimulateApiFailureForTesting { get; set; } = false;

    [System.Runtime.InteropServices.DllImport("Kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int LCMapStringEx(
        string? lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        [System.Runtime.InteropServices.Out] char[]? lpDestStr,
        int cchDest,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr sortHandle);

    /// <summary>
    /// Maps simplified Chinese characters to traditional Chinese characters using Windows Native NLS API.
    /// Returns null if conversion produces no change, fails, or introduces replacement characters.
    /// Never throws exceptions; falls back safely to null.
    /// </summary>
    public static string? TryMapToTraditionalChinese(string? src)
    {
        if (string.IsNullOrEmpty(src)) return null;

        if (SimulateApiFailureForTesting)
        {
            Debug.WriteLine("[WindowsNlsHelper] LCMapStringEx mapping failed: SimulatedApiException");
            return null;
        }

        try
        {
            int destSize = LCMapStringEx(
                null,
                LCMAP_TRADITIONAL_CHINESE,
                src,
                src.Length,
                null,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (destSize <= 0)
            {
                return null;
            }

            char[] destBuf = new char[destSize];
            int result = LCMapStringEx(
                null,
                LCMAP_TRADITIONAL_CHINESE,
                src,
                src.Length,
                destBuf,
                destSize,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (result <= 0)
            {
                return null;
            }

            string mapped = new string(destBuf, 0, result);

            // If mapped string is identical to original, no suggestion is needed
            if (string.Equals(mapped, src, StringComparison.Ordinal))
            {
                return null;
            }

            // Question mark replacement check: reject if mapping introduces replacement characters
            int origQMarks = src.Count(c => c == '?' || c == '？' || c == '\uFFFD');
            int mappedQMarks = mapped.Count(c => c == '?' || c == '？' || c == '\uFFFD');
            if (mappedQMarks > origQMarks)
            {
                return null;
            }

            return mapped;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsNlsHelper] LCMapStringEx mapping failed: {ex.GetType().Name}");
            return null;
        }
    }
}

