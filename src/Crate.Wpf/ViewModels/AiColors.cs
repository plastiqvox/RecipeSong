using System.Windows.Media;

namespace Crate.Wpf.ViewModels;

/// <summary>
/// AI 커넥터 색: Claude는 오렌지, GPT는 초록. 비싼 모델일수록 진하다.
/// Claude: Haiku → Sonnet → Opus → Fable / GPT: mini·nano → 기본 → pro
/// </summary>
public static class AiColors
{
    private static readonly Dictionary<string, Brush> Cache = [];

    public static Brush For(string label) => Get(label, 0xFF);

    /// <summary>배지 바탕: 같은 색을 옅게</summary>
    public static Brush Background(string label) => Get(label, 0x2E);

    private static Brush Get(string label, byte alpha)
    {
        var key = $"{label}|{alpha}";
        if (Cache.TryGetValue(key, out var b)) return b;
        var c = Base(label);
        b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return Cache[key] = b;
    }

    private static Color Base(string label)
    {
        var l = label.ToLowerInvariant();
        if (l.StartsWith("gpt"))
            return l.Contains("nano") || l.Contains("mini") ? Rgb(0x9C, 0xE6, 0xC6)
                : l.Contains("pro") ? Rgb(0x0F, 0x8A, 0x5C)
                : Rgb(0x2F, 0xC0, 0x88);
        return l.Contains("haiku") ? Rgb(0xFF, 0xC9, 0x8E)
            : l.Contains("opus") ? Rgb(0xF2, 0x6B, 0x0A)
            : l.Contains("fable") || l.Contains("mythos") ? Rgb(0xC4, 0x3D, 0x08)
            : Rgb(0xFF, 0x98, 0x38);  // Sonnet, 또는 모델 기록이 없던 예전 "Claude"
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>표시 순서: Claude 먼저, 비싼 모델 먼저</summary>
    public static int Rank(string label)
    {
        var l = label.ToLowerInvariant();
        int provider = l.StartsWith("gpt") ? 10 : 0;
        int tier = l.Contains("fable") ? 0 : l.Contains("opus") || l.Contains("pro") ? 1 : l.Contains("haiku") || l.Contains("mini") || l.Contains("nano") ? 3 : 2;
        return provider + tier;
    }
}
