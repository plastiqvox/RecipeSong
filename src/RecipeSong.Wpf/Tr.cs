using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using RecipeSong.Core;

namespace RecipeSong.Wpf;

/// <summary>XAML 번역: Text="{local:Tr '추천 설정'}" (한국어 원문이 키, RecipeSong.Core.L)</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}

/// <summary>바인딩 값 + 번역 서식: {Binding Album, Converter={StaticResource TrFmt}, ConverterParameter='좋아요: {0}'}</summary>
public sealed class TrFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => L.F((string)parameter, value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
