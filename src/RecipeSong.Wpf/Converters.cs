using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RecipeSong.Wpf;

/// <summary>true → Visible (Invert면 반대).</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>null·빈 문자열·0 → Collapsed.</summary>
public sealed class EmptyToCollapsed : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null or "" or 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>달력 히트 단계(0~4) → 배경색.</summary>
public sealed class HeatToBrush : IValueConverter
{
    private static readonly Brush[] Brushes =
        [Make("#1F2226"), Make("#3D2C18"), Make("#6E4721"), Make("#A96B2D"), Make("#F0A04B")];

    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        Brushes[Math.Clamp(value is int i ? i : 0, 0, 4)];

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();

    private static Brush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>이미지 URL(또는 file://) → 작게 디코딩한 BitmapImage. 실패하면 null.</summary>
public sealed class UrlToImage : IValueConverter
{
    public int DecodeWidth { get; set; } = 240;

    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string s || !Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.DecodePixelWidth = DecodeWidth;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            return img;
        }
        catch (Exception) { return null; }
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
