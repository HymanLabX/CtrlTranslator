using System.Globalization;
using System.Text;
using System.Windows.Data;
using CtrlTranslator.Data;
using CtrlTranslator.Translation;

namespace CtrlTranslator.UI;

/// <summary>
/// 历史卡片下半截（译文）显示什么。参数 <c>collapsed</c> 是折叠态，<c>expanded</c> 是展开态。
/// </summary>
/// <remarks>
/// 普通译文：折叠时压成一行（同 <see cref="SingleLineConverter"/>），展开时原样。
/// <para>
/// 词条（<see cref="TranslationRecord.IsWord"/>）不能这么做：词典结果是好几行
/// 「音标 / 词性释义 / 例句 —— 例句译文」，压成一行就成了
/// <c>/ˌhetərə…/ n. 异质性；… 例：The heterogeneity … —— 样本的…</c> 一长串，
/// 折叠时最该一眼看到的释义反而淹在音标和例句中间。
/// 所以词条折叠时只给释义，展开时按浮窗里的顺序分行排：音标、释义、空一行、例句和译文。
/// 解析不出结构（模型没守格式）就退回普通译文的显示方式。
/// </para>
/// </remarks>
public sealed class HistoryResultConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TranslationRecord record)
        {
            return string.Empty;
        }

        bool expanded = string.Equals(parameter as string, "expanded", StringComparison.Ordinal);
        if (record.IsWord)
        {
            DictionaryEntry entry = DictionaryEntryParser.Parse(record.ResultText);
            if (entry.Senses.Count > 0)
            {
                return expanded ? FormatExpanded(entry) : FormatSenses(entry, separator: "  ");
            }
        }

        return expanded ? record.ResultText : SingleLine(record.ResultText);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("历史卡片的译文只用于显示。");
    }

    private static string FormatSenses(DictionaryEntry entry, string separator) =>
        string.Join(separator, entry.Senses.Select(sense =>
            sense.PartOfSpeech.Length > 0 ? $"{sense.PartOfSpeech} {sense.Definition}" : sense.Definition));

    private static string FormatExpanded(DictionaryEntry entry)
    {
        StringBuilder builder = new();
        if (entry.Phonetic.Length > 0)
        {
            builder.AppendLine(entry.Phonetic);
        }

        builder.Append(FormatSenses(entry, Environment.NewLine));
        foreach (DictionaryExample example in entry.Examples)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append(example.Text);
            if (example.Translation.Length > 0)
            {
                builder.AppendLine();
                builder.Append(example.Translation);
            }
        }

        return builder.ToString();
    }

    private static string SingleLine(string text) => string.Join(' ', text.Split(
        ['\r', '\n', '\t'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
