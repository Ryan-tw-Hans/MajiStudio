using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MajiOCR_Windows.Converters;

public class BoolToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RegionBrush = new(Windows.UI.Color.FromArgb(255, 216, 59, 1)); // Amber/Orange
    private static readonly SolidColorBrush FullBrush = new(Windows.UI.Color.FromArgb(255, 0, 120, 212));    // Blue

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isRegion && isRegion)
        {
            return RegionBrush;
        }
        return FullBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
