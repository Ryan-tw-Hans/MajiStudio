namespace MajiOCR_Windows.Models;

/// <summary>
/// Represents an OCR recognized candidate line with original coordinates and source tracking.
/// </summary>
public class OcrCandidateItem
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Source { get; set; } = "整圖辨識"; // "整圖辨識" or "局部辨識"
    public DateTime RecognizedAt { get; set; } = DateTime.UtcNow;

    public string? OriginalText { get; set; }
    public string? ReviewSource { get; set; }
    public bool IsReviewed => !string.IsNullOrEmpty(ReviewSource);

    public string? TraditionalSuggestion { get; set; }
    public string? SuggestionSource { get; set; }
    public bool HasSuggestion => !string.IsNullOrWhiteSpace(TraditionalSuggestion) && !string.Equals(TraditionalSuggestion, Text, StringComparison.Ordinal);

    public string FormattedConfidence => $"{Confidence:F0}%";
    public string FormattedLocation => $"X:{X}, Y:{Y} ({Width}x{Height})";
    public bool IsRegionSource => Source.StartsWith("局部辨識", StringComparison.Ordinal);
}
