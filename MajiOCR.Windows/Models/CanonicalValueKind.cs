using System;
using System.Collections.Generic;

namespace MajiOCR_Windows.Models;

/// <summary>
/// Canonical serialization codes for business card value kinds across Android and Windows.
/// Conforms to RS-ISSUE-0006-v0.2 and DBA-ISSUE-0006-v0.1.
/// </summary>
public static class CanonicalValueKind
{
    public const string Name = "Name";
    public const string Company = "Company";
    public const string Title = "Title";
    public const string Phone = "Phone";
    public const string Email = "Email";
    public const string Website = "Website";
    public const string Address = "Address";
    public const string Note = "Note";
    public const string Unclassified = "Unclassified";
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<string> AllStandardKinds = new[]
    {
        Name,
        Company,
        Title,
        Phone,
        Email,
        Website,
        Address,
        Note,
        Unclassified
    };

    /// <summary>
    /// Returns human-readable UI display label for the given canonical code.
    /// </summary>
    public static string GetDisplayName(string valueKind, string? customName = null)
    {
        return valueKind switch
        {
            Name => "姓名",
            Company => "公司",
            Title => "職稱",
            Phone => "電話",
            Email => "Email",
            Website => "網站",
            Address => "地址",
            Note => "備註",
            Unclassified => "未分類",
            Custom => string.IsNullOrWhiteSpace(customName) ? "自訂" : $"{customName} (自訂)",
            _ => valueKind // Preserve unknown code
        };
    }

    /// <summary>
    /// Checks if the value kind typically allows only a single value per business card.
    /// Multiple Phone, Email, and Custom fields can coexist without conflict.
    /// </summary>
    public static bool IsSingleValueKind(string valueKind)
    {
        return valueKind is Name or Company or Title or Website or Address or Note;
    }

    /// <summary>
    /// Validates whether the given code is a recognized canonical code.
    /// </summary>
    public static bool IsKnownKind(string valueKind)
    {
        return valueKind is Name or Company or Title or Phone or Email or Website or Address or Note or Unclassified or Custom;
    }
}
