using System;

namespace MajiOCR_Windows.Models;

/// <summary>
/// Snapshot of a draft row's prior state before an explicitly targeted ROI OCR replacement.
/// Used to provide one-field Undo/復原 functionality conforming to RS-ISSUE-0008-v0.1.
/// </summary>
public class DraftRowSnapshot
{
    public string Value { get; init; } = string.Empty;
    public string OriginalText { get; init; } = string.Empty;
    public DraftItemStatus Status { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public bool IsUserModified { get; init; }
    public string Source { get; init; } = string.Empty;
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? TraditionalSuggestion { get; init; }
    public string? SuggestionSource { get; init; }
}
