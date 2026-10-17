using System;

namespace MajiOCR_Windows.Models;

/// <summary>
/// Option item for the per-draft-row category dropdown ComboBox.
/// Contains standard kinds, active custom definitions, and the "+ 新增自訂名稱…" action item.
/// </summary>
public class DraftFieldOption
{
    public string DisplayText { get; set; } = string.Empty;
    public string ValueKind { get; set; } = CanonicalValueKind.Unclassified;
    public string? CustomFieldDefinitionId { get; set; }
    public string? CustomFieldName { get; set; }
    public bool IsActionItem { get; set; }

    public static DraftFieldOption CreateStandard(string kind)
    {
        return new DraftFieldOption
        {
            DisplayText = CanonicalValueKind.GetDisplayName(kind),
            ValueKind = kind,
            IsActionItem = false
        };
    }

    public static DraftFieldOption CreateCustom(CustomFieldDefinition def)
    {
        return new DraftFieldOption
        {
            DisplayText = $"{def.Name} (自訂)",
            ValueKind = CanonicalValueKind.Custom,
            CustomFieldDefinitionId = def.Id,
            CustomFieldName = def.Name,
            IsActionItem = false
        };
    }

    public static DraftFieldOption CreateCustom(string id, string name)
    {
        return new DraftFieldOption
        {
            DisplayText = $"{name} (自訂)",
            ValueKind = CanonicalValueKind.Custom,
            CustomFieldDefinitionId = id,
            CustomFieldName = name,
            IsActionItem = false
        };
    }

    public static DraftFieldOption CreateAction(string text = "+ 新增自訂名稱…")
    {
        return new DraftFieldOption
        {
            DisplayText = text,
            ValueKind = "__ACTION_ADD_CUSTOM__",
            IsActionItem = true
        };
    }

    public override string ToString() => DisplayText;
}
