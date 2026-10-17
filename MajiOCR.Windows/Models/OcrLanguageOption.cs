using System;
using System.Collections.Generic;
using System.Linq;

namespace MajiOCR_Windows.Models;

/// <summary>
/// Represents a selectable OCR language option conforming to RS-ISSUE-0009 and AR-ISSUE-0009.
/// </summary>
public class OcrLanguageOption
{
    public string DisplayName { get; }
    public string Code { get; }

    public OcrLanguageOption(string displayName, string code)
    {
        DisplayName = displayName;
        Code = code;
    }

    public override string ToString() => DisplayName;

    public override bool Equals(object? obj) =>
        obj is OcrLanguageOption other && Code == other.Code;

    public override int GetHashCode() => Code.GetHashCode();
}

/// <summary>
/// Strict allowlist and catalog of supported OCR language models conforming to RS-ISSUE-0009 OCR9-06.
/// Strictly restricts language parameter to 6 controlled keys:
/// "chi_tra+chi_sim+jpn+kor+eng", "chi_tra", "chi_sim", "jpn", "kor", "eng".
/// </summary>
public static class OcrLanguageCatalog
{
    public const string Mixed5 = "chi_tra+chi_sim+jpn+kor+eng";
    public const string ChiTra = "chi_tra";
    public const string ChiSim = "chi_sim";
    public const string Jpn = "jpn";
    public const string Kor = "kor";
    public const string Eng = "eng";

    public static readonly IReadOnlyList<OcrLanguageOption> AllOptions = new[]
    {
        new OcrLanguageOption("多語系辨識", Mixed5),
        new OcrLanguageOption("繁體中文", ChiTra),
        new OcrLanguageOption("簡體中文", ChiSim),
        new OcrLanguageOption("日文", Jpn),
        new OcrLanguageOption("韓文", Kor),
        new OcrLanguageOption("英文", Eng),
    };

    public static readonly IReadOnlyDictionary<string, string> DisplayNameToCodeMap =
        AllOptions.ToDictionary(o => o.DisplayName, o => o.Code);

    public static readonly IReadOnlyDictionary<string, string> CodeToDisplayNameMap =
        AllOptions.ToDictionary(o => o.Code, o => o.DisplayName);

    public static bool IsValidCode(string? code) =>
        code is Mixed5 or ChiTra or ChiSim or Jpn or Kor or Eng;

    public static string NormalizeCode(string? code)
    {
        if (code == null) return Mixed5;
        if (IsValidCode(code)) return code;
        throw new ArgumentException($"不支援的 OCR 語系代碼: '{code}'。僅允許六項受控語系 (多語系辨識、繁中、簡中、日文、韓文、英文)。", nameof(code));
    }

    public static string GetDisplayName(string code)
    {
        return CodeToDisplayNameMap.TryGetValue(code, out var name) ? name : "多語系辨識";
    }

    public static OcrLanguageOption GetOptionByCode(string? code)
    {
        string norm = NormalizeCode(code);
        return AllOptions.FirstOrDefault(o => o.Code == norm) ?? AllOptions[0];
    }
}
