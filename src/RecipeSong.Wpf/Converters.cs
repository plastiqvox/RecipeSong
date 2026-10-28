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

    /// <summary>
    /// 받은 커버를 주소별로 같이 쓴다. 필터를 바꿀 때마다 카드를 새로 만들어 같은 커버를 수십 장씩 다시 받다가
    /// 일부가 실패해 빈칸으로 남았다. 실패한 것은 빼 두어 다음에 그릴 때 다시 받는다.
    /// </summary>
    private static readonly Dictionary<string, BitmapImage> Loaded = [];

    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string s || !Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        var key = $"{DecodeWidth}|{s}";
        if (Loaded.TryGetValue(key, out var hit)) return hit;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.DecodePixelWidth = DecodeWidth;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            if (img.IsDownloading)
            {
                img.DownloadFailed += (_, _) => Loaded.Remove(key);
                img.DecodeFailed += (_, _) => Loaded.Remove(key);
            }
            if (Loaded.Count > 1500) Loaded.Clear();  // 연 보기를 오래 넘겨도 메모리가 끝없이 늘지 않게
            Loaded[key] = img;
            return img;
        }
        catch (Exception) { return null; }
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
