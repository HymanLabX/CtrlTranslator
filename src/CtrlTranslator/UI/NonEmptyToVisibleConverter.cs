using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CtrlTranslator.UI;

/// <summary>
/// 字符串非空则显示，为空则整行折叠。
/// </summary>
/// <remarks>
/// 用在深度翻译的例句上：模型偶尔会漏掉「—— 中文意思」那一行。
/// 不折叠的话，那条例句下面会留一道莫名其妙的空白，看着像渲染坏了。
/// </remarks>
public sealed class NonEmptyToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string text && text.Trim().Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("这个转换器只用于显示。");
    }
}
