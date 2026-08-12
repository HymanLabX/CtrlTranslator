using System.Globalization;
using System.Windows.Data;

namespace CtrlTranslator.UI;

/// <summary>
/// 把多行文本压成一行，供列表里的单行摘要使用。
/// </summary>
/// <remarks>
/// 光靠 <c>TextTrimming="CharacterEllipsis"</c> 是不够的：TextBlock 即使不换行，
/// 也照样会在 \n 处断行，于是一条划了整段的记录会把列表行撑成好几行高，
/// 整个列表的节奏就乱了。
/// </remarks>
public sealed class SingleLineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || text.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(' ', text.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("单行摘要只用于显示。");
    }
}
