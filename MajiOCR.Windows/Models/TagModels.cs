using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MajiOCR_Windows.Models;

public record TagDefinition(string TagId, string DisplayName, int Revision, int UsageCount = 0)
{
    public string CatalogCaption => $"{DisplayName}（{UsageCount} 張名片）";
}
public record TagChoice(string? TagId, string DisplayName)
{
    public string Token => TagId is not null ? "id:" + TagId : "new:" + TagNames.Key(DisplayName);
    public static string Fingerprint(IEnumerable<TagChoice> choices) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            choices.Select(c => c.Token).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)))));
}
public static class TagNames
{
    public static string Display(string name)
    {
        if (name is null || name.Any(char.IsControl)) throw new InvalidOperationException("標籤不可包含換行或控制字元。");
        string display = name.Trim().Normalize(NormalizationForm.FormC);
        if (display.Length == 0) throw new InvalidOperationException("請輸入標籤名稱。");
        return display;
    }
    public static string Key(string name) => Display(name).ToUpperInvariant();
}
public record TagState(int Revision, IReadOnlyList<TagDefinition> Tags, string SaveFingerprint, IReadOnlyDictionary<string,string>? ResolvedTokens = null);
public record TagMutationResult(bool Succeeded, string? Error, TagState? State = null, TagDefinition? Tag = null);
public record TagDeletePreview(TagDefinition Tag, int ContactCount, string MembershipToken, bool Busy);
public record ContactQuery(string? Text, IReadOnlyList<string> TagIds, bool MatchAll = false, bool Untagged = false, string OrderBy = "CreatedAt", bool IsDescending = true);
