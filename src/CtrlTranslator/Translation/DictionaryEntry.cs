namespace CtrlTranslator.Translation;

/// <summary>词典条目里的一个义项。<see cref="PartOfSpeech"/> 可能为空（模型没给词性）。</summary>
public sealed record DictionarySense(string PartOfSpeech, string Definition);

/// <summary>词典条目里的一条例句。<see cref="Translation"/> 可能为空（模型漏给了译文）。</summary>
public sealed record DictionaryExample(string Text, string Translation);

/// <summary>
/// 划词浮窗的词典条目：音标 + 若干义项 + 若干例句。
/// </summary>
public sealed class DictionaryEntry
{
    public string Phonetic { get; init; } = string.Empty;

    public IReadOnlyList<DictionarySense> Senses { get; init; } = [];

    public IReadOnlyList<DictionaryExample> Examples { get; init; } = [];

    /// <summary>解析出东西了没有。全是空的说明这段文字不是词典格式，调用方该整段当普通译文渲染。</summary>
    public bool HasStructure =>
        Phonetic.Length > 0 || Senses.Count > 0 || Examples.Count > 0;
}

/// <summary>
/// 把模型按 <see cref="Settings.BuiltInModes.DictionaryMarkerTag"/> 约定吐出的词典文本拆开。
/// </summary>
/// <remarks>
/// <para>
/// 格式由提示词里的 <c>DictionaryRule</c> 规定，这里只负责按那份约定拆：
/// <code>
/// /音标/
/// 词性. 释义1；释义2；释义3
/// 例：一个英文例句 —— 该例句的中文翻译
/// </code>
/// </para>
/// <para>
/// 设计前提和 <see cref="DeepTranslationResult"/> 一样：<b>模型迟早会不守格式</b>。
/// 所以每一步都是"尽力而为、失败也不炸"，认不出结构时返回一个
/// <see cref="DictionaryEntry.HasStructure"/> 为 false 的空条目，
/// 由调用方退回整段纯文本 —— 用户至少还看得到翻译结果，而不是对着空白发懵。
/// </para>
/// <para>
/// ⚠️ 这套判断以前是直接写在 PopupWindow 的渲染方法里的（首行以 / 开头就当音标、
/// 以「例」开头就当例句、前 8 个字符内的句点前面就当词性）。挪到这里不是为了好看：
/// 渲染层同时管"怎么认"和"怎么画"，改任何一边都要重读另一边，而且没法单独验证。
/// </para>
/// </remarks>
public static class DictionaryEntryParser
{
    /// <summary>例句和它的译文之间的分隔符，长短破折号都收。</summary>
    private static readonly string[] ExampleSeparators = ["——", "—", " -- "];

    /// <summary>中文写法的词性（查中文词时模型有时会这样标）。</summary>
    private static readonly string[] ChinesePartsOfSpeech =
        ["名词", "动词", "形容词", "副词", "介词", "连词", "代词", "数词", "量词", "助词", "叹词", "感叹词"];

    /// <summary>
    /// 英文词性缩写：一到五个字母加句点，可以用斜杠或逗号连着好几个（<c>vt./vi.</c>、<c>n., v.</c>）。
    /// </summary>
    /// <remarks>
    /// 以前的规则是"前 8 个字符里出现句号就把它之前当词性"，
    /// 于是「异质性。不均匀性」这种不带词性的释义行，会把「异质性。」加粗当成词性。
    /// 现在只认长得像词性缩写的东西。
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex EnglishPartOfSpeech = new(
        @"^((?:[A-Za-z]{1,5}\.)(?:\s*[/,&]\s*[A-Za-z]{1,5}\.)*)\s*(.*)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static DictionaryEntry Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new DictionaryEntry();
        }

        string phonetic = string.Empty;
        List<DictionarySense> senses = [];
        List<DictionaryExample> examples = [];

        foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (phonetic.Length == 0 && IsPhonetic(line))
            {
                phonetic = line;
                continue;
            }

            if (IsExample(line, out string body))
            {
                examples.Add(SplitExample(body));
                continue;
            }

            senses.Add(SplitSense(line));
        }

        return new DictionaryEntry
        {
            Phonetic = phonetic,
            Senses = senses,
            Examples = examples
        };
    }

    /// <summary>
    /// 音标行。约定的写法是整行被一对斜杠裹着；也认模型常见的几种变体：
    /// 方括号 <c>[ˌhetərədʒəˈniːəti]</c>、带英美标注的 <c>英 /…/ 美 /…/</c>。
    /// </summary>
    private static bool IsPhonetic(string line)
    {
        if (line.Length < 2)
        {
            return false;
        }

        if ((line[0] == '/' && line[^1] == '/') || (line[0] == '[' && line[^1] == ']' && line.Length <= 60))
        {
            return true;
        }

        // 「英 /…/ 美 /…/」「UK /…/ US /…/」这类：以英美标注开头、后面跟着斜杠或方括号。
        foreach (string label in (string[])["英", "美", "UK", "US", "BrE", "AmE"])
        {
            if (line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                string rest = line[label.Length..].TrimStart(' ', ':', '：');
                return rest.Length > 1 && rest[0] is '/' or '[';
            }
        }

        return false;
    }

    /// <summary>
    /// 例句行的识别。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不能只看「以『例』开头」—— 释义里「例外；异常」这种词就会被整行当成例句吃掉。
    /// 必须跟着冒号（提示词里约定的写法就是「例：」），或者退一步：跟着空格<b>且</b>
    /// 整行带例句译文的破折号，才认。宁可漏认一条例句当成普通释义显示，
    /// 也不能把一条释义抹掉。
    /// </remarks>
    private static bool IsExample(string line, out string body)
    {
        foreach (string prefix in (string[])["例句：", "例句:", "例：", "例:"])
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                body = line[prefix.Length..].TrimStart('：', ':', ' ');
                return true;
            }
        }

        if (line.StartsWith("例 ", StringComparison.Ordinal)
            && ExampleSeparators.Any(separator => line.Contains(separator, StringComparison.Ordinal)))
        {
            body = line[2..].Trim();
            return true;
        }

        body = string.Empty;
        return false;
    }

    private static DictionaryExample SplitExample(string body)
    {
        foreach (string separator in ExampleSeparators)
        {
            int index = body.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                return new DictionaryExample(
                    body[..index].Trim(),
                    body[(index + separator.Length)..].Trim());
            }
        }

        return new DictionaryExample(body.Trim(), string.Empty);
    }

    private static DictionarySense SplitSense(string line)
    {
        System.Text.RegularExpressions.Match english = EnglishPartOfSpeech.Match(line);
        if (english.Success && english.Groups[2].Value.Trim().Length > 0)
        {
            return new DictionarySense(english.Groups[1].Value.Trim(), english.Groups[2].Value.Trim());
        }

        foreach (string partOfSpeech in ChinesePartsOfSpeech)
        {
            if (line.StartsWith(partOfSpeech, StringComparison.Ordinal))
            {
                string rest = line[partOfSpeech.Length..].TrimStart('.', '．', '。', ':', '：', '、', ' ');
                if (rest.Length > 0)
                {
                    return new DictionarySense(partOfSpeech, rest);
                }
            }
        }

        return new DictionarySense(string.Empty, line);
    }
}
