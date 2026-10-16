using System.Globalization;
using System.Text;
using System.Windows.Data;
using CtrlTranslator.Data;

namespace CtrlTranslator.UI;

/// <summary>
/// 把一条翻译记录的来源信息压成一行说明，供展开的历史卡片使用。
/// </summary>
/// <remarks>
/// 这些字段单独看都不值得占一行，凑在一起才回答同一个问题：这条译文是哪儿来的。
/// 缺的字段直接不出现，而不是写「未知」—— 一行里挂三个「未知」比不写更难读。
/// </remarks>
public sealed class RecordMetaConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TranslationRecord record)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        Append(builder, record.Model);
        Append(builder, record.ProfileName);
        Append(builder, string.IsNullOrWhiteSpace(record.AppName) ? null : $"来自 {record.AppName}");
        Append(builder, record.LatencyMilliseconds is { } ms ? $"{ms / 1000d:0.0}s" : null);
        return builder.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("来源说明只用于显示。");
    }

    private static void Append(StringBuilder builder, string? part)
    {
        if (string.IsNullOrWhiteSpace(part))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(" · ");
        }

        builder.Append(part);
    }
}
