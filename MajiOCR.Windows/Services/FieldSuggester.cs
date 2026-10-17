using System;
using System.Text.RegularExpressions;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services;

/// <summary>
/// Conservative rule-based field suggester for business card OCR text lines.
/// Conforms to RS-ISSUE-0006-v0.2 R01 and DBA-ISSUE-0006-v0.1.
/// Aligns with Android FieldSuggester logic.
/// </summary>
public static class FieldSuggester
{
    private static readonly Regex TwMobileRegex = new(@"(?:\+?886[-\s]?)?0?9\d{2}[-\s]?\d{3}[-\s]?\d{3}", RegexOptions.Compiled);
    private static readonly Regex MobilePrefixRegex = new(@"^(?:手機|行動|行動電話|Cell|Mobile|Mob)[:：\s]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhonePrefixRegex = new(@"^(?:電話|TEL|Tel|話)[:：\s]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GeneralPhoneRegex = new(@"(?:\+?\d{1,4}[-\s]?)?\(?0?\d{1,3}\)?[-\s]?\d{3,4}[-\s]?\d{4}", RegexOptions.Compiled);

    private static readonly Regex EmailRegex = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);
    private static readonly Regex WebsitePrefixRegex = new(@"^(?:https?://|www\.)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WebsiteDomainRegex = new(@"\b[a-zA-Z0-9.-]+\.(?:com|org|net|tw|io|co|info|biz|edu|gov|xyz|app|me)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NotesPrefixRegex = new(@"^(?:統編|統一編號|VAT|GUI|備註|Note|Memo)[:：\s]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CompanySuffixRegex = new(@"(?:股份有限公司|有限公司|企業社|商行|工作室|集團|協會|基金會|大學|學校|研究院|研究中心|策進會|公會|事務所|機構|Inc\.|Corp\.|LLC|Co\.,\s*Ltd\.)", RegexOptions.Compiled);
    private static readonly Regex JobTitlePrefixRegex = new(@"^(?:職稱|Title)[:：\s]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JobTitleSuffixRegex = new(@"(?:經理|主任|博士|總經理|副總經理|副總|協理|處長|組長|科長|工程師|專案經理|執行長|CEO|CTO|CFO|顧問|負責人|創辦人|特助|組員|專員)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NamePrefixRegex = new(@"^(?:姓名|Name)[:：\s]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Suggests a canonical ValueKind based on explicit tokens or formats.
    /// Falls back to Unclassified if no reliable pattern matches.
    /// </summary>
    public static string Suggest(string text)
    {
        string trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return CanonicalValueKind.Unclassified;
        }

        // 1. Email check
        if (EmailRegex.IsMatch(trimmed))
        {
            return CanonicalValueKind.Email;
        }

        // 2. Website check
        if (WebsitePrefixRegex.IsMatch(trimmed) || WebsiteDomainRegex.IsMatch(trimmed))
        {
            return CanonicalValueKind.Website;
        }

        // 3. Notes / Tax ID (must precede general phone digits)
        if (NotesPrefixRegex.IsMatch(trimmed))
        {
            return CanonicalValueKind.Note;
        }

        // 4. Address check
        if (trimmed.StartsWith("地址", StringComparison.Ordinal) ||
            trimmed.StartsWith("Add:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Addr:", StringComparison.OrdinalIgnoreCase))
        {
            return CanonicalValueKind.Address;
        }
        if (trimmed.Contains("號") && (trimmed.Contains("路") || trimmed.Contains("街") || trimmed.Contains("段") ||
                                      trimmed.Contains("大道") || trimmed.Contains("市") || trimmed.Contains("區") ||
                                      trimmed.Contains("縣") || trimmed.Contains("鄉") || trimmed.Contains("鎮")))
        {
            return CanonicalValueKind.Address;
        }

        // 5. Mobile / Phone check
        if (MobilePrefixRegex.IsMatch(trimmed) || TwMobileRegex.IsMatch(trimmed) ||
            PhonePrefixRegex.IsMatch(trimmed) || GeneralPhoneRegex.IsMatch(trimmed))
        {
            return CanonicalValueKind.Phone;
        }

        // 6. Explicit Company prefix / suffix
        if (CompanySuffixRegex.IsMatch(trimmed) || trimmed.StartsWith("公司:") || trimmed.StartsWith("公司："))
        {
            return CanonicalValueKind.Company;
        }

        // 7. Explicit Job Title prefix / suffix
        if (JobTitlePrefixRegex.IsMatch(trimmed) || JobTitleSuffixRegex.IsMatch(trimmed))
        {
            return CanonicalValueKind.Title;
        }

        // 8. Explicit Name prefix
        if (NamePrefixRegex.IsMatch(trimmed))
        {
            return CanonicalValueKind.Name;
        }

        // Conservative fallback: without reliable explicit evidence, mark as Unclassified
        return CanonicalValueKind.Unclassified;
    }
}
