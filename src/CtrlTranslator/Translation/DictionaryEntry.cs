namespace CtrlTranslator.Translation;

/// <summary>词典条目里的一个义项。<see cref="PartOfSpeech"/> 可能为空（模型没给词性）。</summary>
public sealed record DictionarySense(string PartOfSpeech, string Definition);

/// <summary>词典条目里的一条例句。<see cref="Translation"/> 可能为空（模型漏给了译文）。</summary>
public sealed record DictionaryExample(string Text, string Translation);

/// <summary>
/// 反查（用中文词查英文怎么说）时的一个候选说法：<c>elephant /ˈelɪfənt/ n. 大象，最常用的说法</c>。
/// <see cref="Phonetic"/>、<see cref="PartOfSpeech"/>、<see cref="Note"/> 都可能为空（模型没给）。
/// </summary>
public sealed record DictionaryEquivalent(string Word, string Phonetic, string PartOfSpeech, string Note);

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

    /// <summary>
    /// 拆反查词条：每行一个说法，格式「说法 /音标/ 词性. 一句说明」（深度翻译 <c>DeepWordStyle.Equivalents</c>）。
    /// </summary>
    /// <remarks>
    /// 同样是尽力而为：音标、词性缺了都照收；整行没有中文、又不长，就当成只有说法本身；
    /// 夹在中间的纯说明行（模型把说明另起了一行）挂到上一个说法后面。
    /// 一个说法都认不出来时返回空列表，由调用方整段按普通译文显示。
    /// <para>
    /// 还要兜住旧的乱序写法（负责人 2026-10 截图）：先一行音标、再一行中文释义、最后才是英文词。
    /// 出现在第一个说法之前的音标行和释义行先攒着，挂到后面第一个说法上 ——
    /// 否则音标会被当成一个"说法"，英文词反倒成了第二个。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DictionaryEquivalent> ParseEquivalents(string text)
    {
        List<DictionaryEquivalent> items = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return items;
        }

        string pendingPhonetic = string.Empty;
        DictionarySense? pendingSense = null;
        foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = StripListMarker(raw);
            if (line.Length == 0)
            {
                continue;
            }

            // 单独一行音标：给上一个还没有音标的说法；前面还没有说法就先攒着。
            if (IsPhonetic(line))
            {
                if (items.Count > 0 && items[^1].Phonetic.Length == 0)
                {
                    items[^1] = items[^1] with { Phonetic = line };
                }
                else if (items.Count == 0)
                {
                    pendingPhonetic = line;
                }

                continue;
            }

            if (TrySplitEquivalent(line, out DictionaryEquivalent? item))
            {
                if (pendingPhonetic.Length > 0 && item.Phonetic.Length == 0)
                {
                    item = item with { Phonetic = pendingPhonetic };
                }

                if (pendingSense is not null && item.PartOfSpeech.Length == 0 && item.Note.Length == 0)
                {
                    item = item with { PartOfSpeech = pendingSense.PartOfSpeech, Note = pendingSense.Definition };
                }

                pendingPhonetic = string.Empty;
                pendingSense = null;
                items.Add(item);
            }
            else if (line.Any(TextLanguage.IsHan))
            {
                if (items.Count > 0)
                {
                    DictionaryEquivalent last = items[^1];
                    items[^1] = last with { Note = last.Note.Length == 0 ? line : $"{last.Note} {line}" };
                }
                else
                {
                    pendingSense = SplitSense(line);
                }
            }
        }

        return items;
    }

    /// <summary>音标：一对斜杠或方括号裹着的一段（<c>/ˈelɪfənt/</c>、<c>[ˈelɪfənt]</c>）。</summary>
    private static readonly System.Text.RegularExpressions.Regex EquivalentWithPhonetic = new(
        @"^(?<word>[^/\[\]]+?)\s*(?<phonetic>/[^/]+/|\[[^\]]+\])\s*(?<rest>.*)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>没有音标、但有词性：<c>pachyderm n. 厚皮动物</c>。</summary>
    private static readonly System.Text.RegularExpressions.Regex EquivalentWithPartOfSpeech = new(
        @"^(?<word>.+?)\s+(?<pos>[A-Za-z]{1,5}\.(?:\s*[/,&]\s*[A-Za-z]{1,5}\.)*)\s*(?<rest>.*)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>说法和说明之间用破折号或冒号隔开的写法：<c>elephant —— 大象</c>。</summary>
    private static readonly string[] EquivalentSeparators = [" —— ", " — ", " - ", "：", ": "];

    private static bool TrySplitEquivalent(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DictionaryEquivalent? item)
    {
        item = null;

        System.Text.RegularExpressions.Match withPhonetic = EquivalentWithPhonetic.Match(line);
        if (withPhonetic.Success && !withPhonetic.Groups["word"].Value.Any(TextLanguage.IsHan))
        {
            (string pos, string note) = SplitPartOfSpeech(withPhonetic.Groups["rest"].Value);
            item = new DictionaryEquivalent(
                withPhonetic.Groups["word"].Value.Trim(),
                withPhonetic.Groups["phonetic"].Value.Trim(),
                pos,
                note);
            return item.Word.Length > 0;
        }

        System.Text.RegularExpressions.Match withPos = EquivalentWithPartOfSpeech.Match(line);
        if (withPos.Success && !withPos.Groups["word"].Value.Any(TextLanguage.IsHan))
        {
            item = new DictionaryEquivalent(
                withPos.Groups["word"].Value.Trim(),
                string.Empty,
                withPos.Groups["pos"].Value.Trim(),
                CleanNote(withPos.Groups["rest"].Value));
            return item.Word.Length > 0;
        }

        foreach (string separator in EquivalentSeparators)
        {
            int index = line.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0 && !line[..index].Any(TextLanguage.IsHan))
            {
                item = new DictionaryEquivalent(line[..index].Trim(), string.Empty, string.Empty, CleanNote(line[(index + separator.Length)..]));
                return true;
            }
        }

        // 整行没有中文、也不长：就是一个说法本身（模型只给了词，没给音标和说明）。
        if (!line.Any(TextLanguage.IsHan) && line.Length <= 40)
        {
            item = new DictionaryEquivalent(line, string.Empty, string.Empty, string.Empty);
            return true;
        }

        return false;
    }

    private static (string PartOfSpeech, string Note) SplitPartOfSpeech(string rest)
    {
        string trimmed = rest.Trim();
        System.Text.RegularExpressions.Match english = EnglishPartOfSpeech.Match(trimmed);
        if (english.Success && english.Groups[1].Value.Length > 0)
        {
            return (english.Groups[1].Value.Trim(), CleanNote(english.Groups[2].Value));
        }

        return (string.Empty, CleanNote(trimmed));
    }

    private static string CleanNote(string note) => note.Trim().TrimStart('—', '-', '：', ':', '，', ',', ' ').Trim();

    /// <summary>去掉行首的列表符号、编号和 markdown 加粗：「1. **elephant** …」→「elephant …」。</summary>
    private static string StripListMarker(string raw)
    {
        string line = raw.Replace("**", string.Empty, StringComparison.Ordinal).Trim();
        line = line.TrimStart('-', '•', '*', '·', '+', ' ', '\t');
        int digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits < line.Length && line[digits] is '.' or ')' or '）' or '、' or '．')
        {
            line = line[(digits + 1)..];
        }

        return line.Trim();
    }

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
