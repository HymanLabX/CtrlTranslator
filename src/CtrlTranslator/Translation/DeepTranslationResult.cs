using System.Text;

namespace CtrlTranslator.Translation;

/// <summary>深度翻译给出的一条例句。<see cref="Translation"/> 可能为空（模型漏给了译文行）。</summary>
public sealed record DeepExample(string Text, string Translation);

/// <summary>深度翻译给出的一条搭配。<see cref="Note"/> 可能为空（模型没写说明）。</summary>
public sealed record DeepCollocation(string Text, string Note);

/// <summary>
/// 深度翻译的结构化结果：译文 + 例句 + 搭配三节。
/// </summary>
public sealed class DeepTranslationResult
{
    public static readonly DeepTranslationResult Empty = new();

    public string Translation { get; init; } = string.Empty;

    public IReadOnlyList<DeepExample> Examples { get; init; } = [];

    public IReadOnlyList<DeepCollocation> Collocations { get; init; } = [];

    public bool HasTranslation => Translation.Length > 0;

    public bool HasExamples => Examples.Count > 0;

    public bool HasCollocations => Collocations.Count > 0;
}

/// <summary>
/// 把模型吐出来的三节文本拆成 <see cref="DeepTranslationResult"/>。
/// </summary>
/// <remarks>
/// <para>
/// 设计前提：<b>模型迟早会不守格式</b>。所以这里的每一步都是"尽力而为、失败也不炸"：
/// 认不出小节标题时，整段文字会落进「译文」——用户至少还看得到翻译结果，
/// 而不是对着一个空白页面发懵。
/// </para>
/// <para>
/// 之所以用 <c>【】</c> 标题而不是 JSON：流式输出时 JSON 在收完最后一个括号之前都不可解析，
/// 没法边收边显示；而且不少模型爱自作主张裹一层 ```json 围栏。标题行既能流式增量解析，
/// 模型也不容易写错。
/// </para>
/// <para>
/// ⚠️ 这里的三个标题常量必须和 <see cref="Settings.BuiltInModes"/> 里 <c>DeepOutputFormat</c>
/// 写给模型的标题<b>逐字一致</b>。
/// </para>
/// </remarks>
internal static class DeepResultParser
{
    private const string TranslationHeader = "译文";
    private const string ExamplesHeader = "例句";
    private const string CollocationsHeader = "搭配";

    /// <summary>例句译文行的行首标记。模型常把全角破折号写成别的样子，这里都认。</summary>
    private static readonly string[] TranslationMarkers = ["——", "—", "－－", "--", "―"];

    /// <summary>搭配行里「搭配 — 说明」的分隔符，按优先级从长到短匹配。</summary>
    /// <remarks>
    /// 全都带前后空格是故意的：<c>state-of-the-art</c> 这种词内连字符不能被当成分隔符。
    /// </remarks>
    private static readonly string[] CollocationSeparators = [" —— ", " — ", " – ", " － ", " - "];

    /// <summary>模型表示"这一节没内容"时会写的词。</summary>
    private static readonly string[] EmptySectionWords = ["无", "None", "N/A", "暂无", "没有"];

    public static DeepTranslationResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DeepTranslationResult.Empty;
        }

        var translation = new StringBuilder();
        List<string> exampleLines = [];
        List<string> collocationLines = [];

        // 起始节是「译文」：模型没写标题时，整段输出至少还能当译文显示出来。
        List<string>? currentList = null;
        StringBuilder? currentText = translation;

        foreach (string line in raw.ReplaceLineEndings("\n").Split('\n'))
        {
            if (TryReadHeader(line, out string header, out string inlineContent))
            {
                switch (header)
                {
                    case TranslationHeader:
                        currentText = translation;
                        currentList = null;
                        break;
                    case ExamplesHeader:
                        currentText = null;
                        currentList = exampleLines;
                        break;
                    case CollocationsHeader:
                        currentText = null;
                        currentList = collocationLines;
                        break;
                    default:
                        // 不认识的小节标题：当普通内容处理，别把它后面的文字丢掉。
                        AppendLine(currentText, currentList, line);
                        continue;
                }

                if (inlineContent.Length > 0)
                {
                    AppendLine(currentText, currentList, inlineContent);
                }

                continue;
            }

            AppendLine(currentText, currentList, line);
        }

        return new DeepTranslationResult
        {
            Translation = translation.ToString().Trim(),
            Examples = BuildExamples(exampleLines),
            Collocations = BuildCollocations(collocationLines)
        };
    }

    private static void AppendLine(StringBuilder? text, List<string>? list, string line)
    {
        if (text is not null)
        {
            text.Append(line).Append('\n');
        }
        else
        {
            list?.Add(line);
        }
    }

    /// <summary>
    /// 认出【译文】这样的小节标题行。容忍模型加的 markdown 修饰、半角方括号和标题后跟着的正文。
    /// </summary>
    private static bool TryReadHeader(string line, out string header, out string inlineContent)
    {
        header = string.Empty;
        inlineContent = string.Empty;

        string trimmed = line.Trim().TrimStart('#', '*', ' ', '\t');
        if (trimmed.Length == 0)
        {
            return false;
        }

        char open = trimmed[0];
        char close = open switch
        {
            '【' => '】',
            '[' => ']',
            '［' => '］',
            _ => '\0'
        };
        if (close == '\0')
        {
            return false;
        }

        int closeIndex = trimmed.IndexOf(close, 1);
        if (closeIndex <= 1)
        {
            return false;
        }

        header = trimmed[1..closeIndex].Trim().Trim('*', ' ');
        inlineContent = trimmed[(closeIndex + 1)..].Trim().TrimStart('：', ':').Trim();
        return header.Length > 0;
    }

    private static List<DeepExample> BuildExamples(List<string> lines)
    {
        if (IsDeclaredEmpty(lines))
        {
            return [];
        }

        List<DeepExample> examples = [];
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryStripTranslationMarker(line, out string translationText))
            {
                // 译文行必须挂在前一条例句上。没有前一条时整行丢弃 ——
                // 孤零零一句中文摆在例句区里，用户根本不知道它在解释什么。
                if (examples.Count > 0 && translationText.Length > 0)
                {
                    DeepExample last = examples[^1];
                    examples[^1] = last with
                    {
                        Translation = last.Translation.Length == 0
                            ? translationText
                            : $"{last.Translation} {translationText}"
                    };
                }

                continue;
            }

            string text = StripBullet(line);
            if (text.Length > 0)
            {
                examples.Add(new DeepExample(text, string.Empty));
            }
        }

        return examples;
    }

    private static List<DeepCollocation> BuildCollocations(List<string> lines)
    {
        if (IsDeclaredEmpty(lines))
        {
            return [];
        }

        List<DeepCollocation> collocations = [];
        foreach (string rawLine in lines)
        {
            string line = StripBullet(rawLine.Trim());
            if (line.Length == 0)
            {
                continue;
            }

            collocations.Add(SplitCollocation(line));
        }

        return collocations;
    }

    private static DeepCollocation SplitCollocation(string line)
    {
        foreach (string separator in CollocationSeparators)
        {
            int index = line.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                return new DeepCollocation(
                    line[..index].Trim(),
                    line[(index + separator.Length)..].Trim());
            }
        }

        return new DeepCollocation(line, string.Empty);
    }

    private static bool TryStripTranslationMarker(string line, out string translationText)
    {
        foreach (string marker in TranslationMarkers)
        {
            if (line.StartsWith(marker, StringComparison.Ordinal))
            {
                translationText = line[marker.Length..].Trim();
                return true;
            }
        }

        translationText = string.Empty;
        return false;
    }

    /// <summary>整节只有一个"无"字时，当作这一节没有内容。</summary>
    private static bool IsDeclaredEmpty(List<string> lines)
    {
        string joined = string.Join(
            string.Empty,
            lines.Select(line => StripBullet(line.Trim()))).Trim().TrimEnd('。', '.');
        return joined.Length == 0
            || EmptySectionWords.Contains(joined, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>去掉行首的项目符号和编号，让「1. xxx」「- xxx」「• xxx」都归一成 xxx。</summary>
    private static string StripBullet(string line)
    {
        string text = line.TrimStart('-', '•', '*', '·', '+', '>', ' ', '\t');

        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        if (digits > 0
            && digits < text.Length
            && text[digits] is '.' or ')' or '）' or '、' or '．' or '：' or ':')
        {
            text = text[(digits + 1)..];
        }

        return text.Trim().Trim('*').Trim();
    }
}
