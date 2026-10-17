using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Converters;

public class DraftStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ConfirmedBrush = new(Windows.UI.Color.FromArgb(255, 16, 124, 65));    // Green
    private static readonly SolidColorBrush ModifiedBrush = new(Windows.UI.Color.FromArgb(255, 0, 120, 212));      // Blue
    private static readonly SolidColorBrush PendingBrush = new(Windows.UI.Color.FromArgb(255, 202, 80, 16));      // Amber / Orange

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DraftRowItem row)
        {
            if (row.Status == DraftItemStatus.Confirmed) return ConfirmedBrush;
            if (row.IsUserModified) return ModifiedBrush;
            return PendingBrush;
        }

        if (value is DraftItemStatus status && status == DraftItemStatus.Confirmed)
        {
            return ConfirmedBrush;
        }

        return PendingBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
