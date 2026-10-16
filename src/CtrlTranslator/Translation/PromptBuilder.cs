using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;

namespace CtrlTranslator.Translation;

internal sealed record BuiltPrompt(
    string SystemPrompt,
    string UserContent,
    string TargetLanguage,
    TranslationMode Mode,
    bool IsWord);

internal static class PromptBuilder
{
    // 标记文字定义在 BuiltInModes，提示词里引用的是同一个常量，改一处即可。
    private const string DictionaryMarker = BuiltInModes.DictionaryMarkerTag + "\n";

    public static BuiltPrompt Build(string text, AppSettings settings)
    {
        var targetLanguage = DetectTargetLanguage(text, settings);
        var mode = settings.Modes.FirstOrDefault(item => item.Id == settings.CurrentModeId)
            ?? BuiltInModes.CreateDefaults()[0];
        var isWord = IsWord(text, settings.WordThreshold);
        // 语言指令由程序拼在最后，模式提示词只管风格。
        // 老版本的自定义提示词里可能还留着占位符，两个 Replace 保留着做兼容。
        var systemPrompt = mode.SystemPromptTemplate
                .Replace("{target_lang}", targetLanguage, StringComparison.Ordinal)
                .Replace("{fallback_lang}", settings.FallbackLanguage, StringComparison.Ordinal)
            + "\n\n"
            + BuildLanguageInstruction(targetLanguage, settings);
        var userContent = isWord ? DictionaryMarker + text : text;

        // 方向判定一直没进日志，出「怎么没翻成英文」这类问题时只能靠猜。
        // 深度翻译那条路本来就记了，这里补齐，两条路的可观测性拉平。
        Logger.Info(
            "翻译",
            $"方向判定：译成 {targetLanguage}（模式 {mode.Name}{(isWord ? "，词条" : string.Empty)}）。");

        return new BuiltPrompt(systemPrompt, userContent, targetLanguage, mode, isWord);
    }

    /// <summary>
    /// 翻译方向指令。以前这段话是抄进每套模式提示词里的，靠 <c>{target_lang}</c> 之类的
    /// 占位符替换 —— 用户想写自己的模式，就得先学会一套占位符语法，写漏了还不报错。
    /// 现在统一由程序生成。
    /// </summary>
    /// <remarks>
    /// 几个踩过的坑，别改回去：
    /// <list type="number">
    /// <item>拼在<b>最后</b>而不是最前面。放最前面时它被后面一整套风格规则盖过去，
    /// 中译英会翻出中文（已实测）。方向是本次任务的结论，放在最靠近用户内容的位置。</item>
    /// <item>直接说死<b>源语言 → 目标语言</b>，不要写「如果原文已经是 X 就反向翻译」。
    /// 方向已经由 <see cref="DetectTargetLanguage"/> 定死了，那种条件句只会邀请模型
    /// 自己再判断一次原文语种 —— 而那正是它会想歪的地方。</item>
    /// <item>原文是外文时只写「外文」，不写具体语种：方向判定只认得出"是不是中文"，
    /// 以前这里一律写成「英语 → 中文」，划一段法文、日文进来，等于在提示词里给了模型一句假话。</item>
    /// </list>
    /// </remarks>
    internal static string BuildLanguageInstruction(string targetLanguage, AppSettings settings)
    {
        string sourceLanguage = string.Equals(targetLanguage, settings.FallbackLanguage, StringComparison.Ordinal)
            ? settings.TargetLanguage
            : "外文";
        return $"本次翻译方向：{sourceLanguage} → {targetLanguage}。整段译文必须用{targetLanguage}书写。";
    }

    /// <summary>
    /// 判断该往哪个方向翻：原文是中文就译成反向语言，否则译成目标语言。
    /// 深度翻译页选「自动」时复用的也是这套判断，两条路必须一致，
    /// 否则同一段文字在浮窗和深度翻译页会翻出不同语言。
    /// </summary>
    /// <remarks>
    /// 日文、韩文按外文处理（见 <see cref="TextLanguage"/>）：以前日文因为汉字多被当成中文，翻成了英语。
    /// </remarks>
    internal static string DetectTargetLanguage(string text, AppSettings settings)
    {
        return TextLanguage.Detect(text) == TextScript.Chinese
            ? settings.FallbackLanguage
            : settings.TargetLanguage;
    }

    /// <summary>
    /// 判断这段文字该不该按"词条"处理。深度翻译页复用同一套判定，
    /// 两条路对"什么算一个词"的口径必须一致。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在 PDF、网页里双击一个英文词，选区经常带上旁边的引号、括号、逗号，或者句尾的句号，
    /// 以前一个 <c>heterogeneity.</c> 就因为结尾的句点被当成"一句话"，拿不到音标和释义。
    /// 现在先剥掉首尾的引号、括号、逗号、分号、冒号；结尾的英文句点只在<b>整段只有一个词</b>时才剥 ——
    /// 好几个词再加句号就是一句完整的话（<c>Hello, world.</c>、<c>I love you.</c>），
    /// 按 SPEC §3.4 照样给译文，不查词典（实测踩过：剥得太宽会把短句当成词条）。
    /// 中文的「。」不剥，按 SPEC 原规则处理。
    /// </para>
    /// <para>
    /// 中间出现的句末标点照样说明这是句子；英文句点只有后面跟着空白才算断句，
    /// 这样 <c>e.g.</c>、<c>U.S.</c> 不会被误判。
    /// </para>
    /// </remarks>
    internal static bool IsWord(string text, int wordThreshold)
    {
        string core = TrimSurroundingPunctuation(text);

        // 一个字母、一个汉字都没有（纯数字、纯符号）就不是"词"，查词典没有意义。
        if (core.Length == 0 || !core.Any(char.IsLetter))
        {
            return false;
        }

        if (core[^1] is '.' or '…')
        {
            string withoutPeriod = core.TrimEnd('.', '…');
            if (withoutPeriod.Any(char.IsWhiteSpace) || withoutPeriod.Any(TextLanguage.IsHan))
            {
                return false;
            }

            core = TrimSurroundingPunctuation(withoutPeriod);
            if (core.Length == 0)
            {
                return false;
            }
        }

        if (core.IndexOfAny(['。', '！', '？', '!', '?', ';', '；', '\n', '\r']) >= 0
            || ContainsSentencePeriod(core))
        {
            return false;
        }

        string withoutPunctuation = new(
            core.Where(character => !char.IsPunctuation(character)).ToArray());
        string trimmed = withoutPunctuation.Trim();
        var cjkCount = trimmed.Count(TextLanguage.IsHan);
        if (cjkCount > 0)
        {
            return cjkCount <= 4;
        }

        var words = trimmed.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length is > 0 && words.Length <= Math.Max(1, wordThreshold);
    }

    /// <summary>
    /// 词条模式下浮窗标题里显示的那个词：剥掉选区顺带进来的首尾标点，
    /// 单个词末尾的句点也剥（<c>simulation.</c> → <c>simulation</c>）。
    /// </summary>
    /// <remarks>
    /// 词里本来就有句点的（<c>e.g.</c>、<c>U.S.</c>）不剥结尾那个，否则会显示成 <c>e.g</c>。
    /// </remarks>
    internal static string Headword(string text)
    {
        string core = TrimSurroundingPunctuation(text.ReplaceLineEndings(" "));
        if (core.Length > 1
            && core[^1] is '.' or '…'
            && core.IndexOf('.') == core.Length - 1
            && !core.Any(char.IsWhiteSpace))
        {
            string withoutPeriod = TrimSurroundingPunctuation(core.TrimEnd('.', '…'));
            return withoutPeriod.Length > 0 ? withoutPeriod : core;
        }

        return core;
    }

    /// <summary>
    /// 剥掉首尾的空白、引号、括号和句读（句号、逗号、分号、冒号）—— 这些是选区边界顺带进来的。
    /// </summary>
    /// <remarks>
    /// 问号、叹号<b>不剥</b>：结尾带「？」「！」说明这是一句问话或感叹（<c>Really?</c>），
    /// 按 SPEC §3.4 应该给译文，不该查词典。
    /// </remarks>
    private static string TrimSurroundingPunctuation(string text)
    {
        int start = 0;
        int end = text.Length - 1;
        while (start <= end && IsSurrounding(text[start]))
        {
            start++;
        }

        while (end >= start && IsSurrounding(text[end]))
        {
            end--;
        }

        return start > end ? string.Empty : text[start..(end + 1)];

        // 句点不在这里剥：要先看是不是"一个词 + 句点"，见 IsWord。
        static bool IsSurrounding(char character)
        {
            if (char.IsWhiteSpace(character)
                || character is ',' or ';' or ':' or '"' or '\'' or '，' or '；' or '：' or '、')
            {
                return true;
            }

            return char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.OpenPunctuation
                or System.Globalization.UnicodeCategory.ClosePunctuation
                or System.Globalization.UnicodeCategory.InitialQuotePunctuation
                or System.Globalization.UnicodeCategory.FinalQuotePunctuation;
        }
    }

    /// <summary>中间有没有"句点 + 空白"这种断句。</summary>
    private static bool ContainsSentencePeriod(string text)
    {
        for (int index = 0; index < text.Length - 1; index++)
        {
            if (text[index] == '.' && char.IsWhiteSpace(text[index + 1]))
            {
                return true;
            }
        }

        return false;
    }
}
