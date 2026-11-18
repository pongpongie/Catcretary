using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Catcretary.Views;

/// <summary>"#RRGGBB" 문자열을 Brush 로. Plane 이 내려주는 state color 를 그대로 쓰기 위한 것.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        }
        catch { }
        return new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
