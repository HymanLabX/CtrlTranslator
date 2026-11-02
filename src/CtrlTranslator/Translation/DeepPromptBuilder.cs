using CtrlTranslator.Settings;
using CtrlTranslator.Core;

namespace CtrlTranslator.Translation;

internal sealed record BuiltDeepPrompt(
    string SystemPrompt,
    string UserContent,
    string TargetLanguage,
    TranslationMode Mode,
    bool IsWord,
    DeepWordStyle WordStyle);

/// <summary>查词时【译文】一节写成哪一种词条。</summary>
internal enum DeepWordStyle
{
    /// <summary>不是查词：给完整译文。</summary>
    None,

    /// <summary>
    /// 查一个外文词（往「目标语言」翻）：音标 + 词性释义，用目标语言解释这个词。
    /// 例：heterogeneity → /ˌhetərədʒəˈniːəti/、n. 异质性；不均匀性
    /// </summary>
    Explain,

    /// <summary>
    /// 查一个母语词（往「反向语言」翻）：列出反向语言里 1–3 个说法，各带音标、词性和一句区别说明。
    /// 例：大象 → elephant /ˈelɪfənt/ n. 大象，最常用的说法
    /// </summary>
    Equivalents
}

/// <summary>
/// 一次深度翻译「往哪边翻、是不是查词、查词用哪种格式」。
/// 页面开跑前先算一遍（决定怎么显示、在结果上标方向），组装提示词时用的也是同一份，两边不会对不上。
/// </summary>
internal sealed record DeepPlan(string TargetLanguage, string SourceLabel, bool IsWord, DeepWordStyle WordStyle)
{
    /// <summary>结果上标的方向，例如「中文 → 英语」「外文 → 中文」。</summary>
    public string DirectionText => $"{SourceLabel} → {TargetLanguage}";
}

/// <summary>
/// 组装「深度翻译」的提示词。和 <see cref="PromptBuilder"/> 分开，两者除了语言方向判定
/// 没有任何共用逻辑：浮窗要的是一句译文，这里要的是三节结构化输出。
/// </summary>
internal static class DeepPromptBuilder
{
    /// <summary>目标语言下拉里代表「自动判断方向」的取值。</summary>
    public const string AutoLanguage = "";

    /// <summary>
    /// 定下这次往哪边翻、是不是查词、查词用哪种格式。
    /// </summary>
    /// <remarks>
    /// 来源语言的叫法和 <see cref="PromptBuilder.BuildLanguageInstruction"/> 同一个口径：
    /// 译成反向语言时，原文就是目标语言（默认中文）；否则统称「外文」——
    /// 程序只认得出"是不是中文"，写成「英语」遇到法文、德文就是假话。
    /// </remarks>
    public static DeepPlan Plan(string text, AppSettings settings, string? targetLanguageOverride)
    {
        text = TranslationTextCleaner.Clean(text);
        string targetLanguage = string.IsNullOrWhiteSpace(targetLanguageOverride)
            ? PromptBuilder.DetectTargetLanguage(text, settings)
            : targetLanguageOverride.Trim();
        bool towardStudyLanguage = string.Equals(targetLanguage, settings.FallbackLanguage, StringComparison.Ordinal);
        string sourceLabel = towardStudyLanguage ? settings.TargetLanguage : "外文";
        bool isWord = PromptBuilder.IsWord(text, settings.WordThreshold);
        DeepWordStyle style = !isWord
            ? DeepWordStyle.None
            : towardStudyLanguage ? DeepWordStyle.Equivalents : DeepWordStyle.Explain;
        return new DeepPlan(targetLanguage, sourceLabel, isWord, style);
    }

    public static BuiltDeepPrompt Build(
        string text,
        AppSettings settings,
        string modeId,
        string? targetLanguageOverride)
    {
        TranslationMode mode = settings.Modes.FirstOrDefault(
                item => string.Equals(item.Id, modeId, StringComparison.Ordinal))
            ?? settings.Modes.FirstOrDefault()
            ?? BuiltInModes.CreateDefaults()[0];

        text = TranslationTextCleaner.Clean(text);

        DeepPlan plan = Plan(text, settings, targetLanguageOverride);
        string targetLanguage = plan.TargetLanguage;

        // 深度翻译要交代三件事：往哪边翻、例句用哪门语言写、对照说明用哪门语言写。
        // 后两件跟"用户在学哪门语言"走，不跟本次翻译方向走 —— 详见 BuiltInModes.DeepOutputFormat。
        string languageInstruction =
            PromptBuilder.BuildLanguageInstruction(targetLanguage, settings)
            + "\n"
            + $"""
              例句和搭配一律用{settings.FallbackLanguage}书写（下称「例句语言」），
              它们的对照译文和说明用{settings.TargetLanguage}（下称「对照语言」）。
              这两项固定不变，不随本次翻译方向改变。
              """;

        // 语言指令和词条格式都拼在最后，理由见 PromptBuilder.BuildLanguageInstruction 的注释：
        // 放在前面会被后面的风格规则盖掉。自建模式、老版本抄出去的深度提示词里
        // 可能还写着旧的两行词条格式，最后这段同样能把它们纠正过来。
        // 老版本的自定义深度提示词里可能还留着占位符，四个 Replace 保留着做兼容。
        string systemPrompt = ResolveTemplate(mode)
                .Replace("{target_lang}", targetLanguage, StringComparison.Ordinal)
                .Replace("{fallback_lang}", ResolveFallback(targetLanguage, settings), StringComparison.Ordinal)
                .Replace("{study_lang}", settings.FallbackLanguage, StringComparison.Ordinal)
                .Replace("{native_lang}", settings.TargetLanguage, StringComparison.Ordinal)
            + "\n\n"
            + languageInstruction
            + BuildWordFormat(plan, settings);

        // 查单词时必须告诉模型"这是个词条"，否则它会给一个孤零零的词自行脑补上下文，
        // 把【译文】写成一整段含该词释义的文章（已踩坑：查 harness 得到一段医疗 AI 的话）。
        string userContent = plan.IsWord
            ? BuiltInModes.DictionaryMarkerTag + "\n" + text
            : text;

        return new BuiltDeepPrompt(systemPrompt, userContent, targetLanguage, mode, plan.IsWord, plan.WordStyle);
    }

    /// <summary>
    /// 查词时【译文】一节的格式，按方向分两种。
    /// </summary>
    /// <remarks>
    /// 以前只有一种「/音标/ + 词性. 释义」，是按"查英文词、看中文释义"设计的。
    /// 反过来用中文查英文（查「大象」）时，这个格式里根本没有放英文词的位置，
    /// 模型只好先写英文词的音标和一串中文释义，再把 <c>elephant</c> 补在最后 ——
    /// 用户最想看到的那个词被挤到了最下面（负责人 2026-10 截图反馈）。
    /// <para>
    /// 两种格式都要求「只写真实常用的义项、拿不准宁可不写」：模型给一个词编义项的倾向很重，
    /// 截图里 elephant 就被安上了一条「国际象棋中的棋子」。
    /// </para>
    /// </remarks>
    private static string BuildWordFormat(DeepPlan plan, AppSettings settings) => plan.WordStyle switch
    {
        DeepWordStyle.Explain => $"""


            本次的词条格式（查的是一个外文词，用{plan.TargetLanguage}解释它）：
            【译文】一节只写下面这几行，不要把这个词本身再写一遍，也不要写别的：
            /音标/
            词性. 释义1；释义2；释义3
            - 第一行是音标：英文用国际音标；这门语言没有通行的音标写法就省掉这一行。
            - 有几个词性就写几行，每行一个词性，释义用{plan.TargetLanguage}写。
            - 只写这个词真实常用的义项，按常用程度排，每个词性最多 4 个；生僻、牵强、拿不准的义项宁可不写。
            """,
        DeepWordStyle.Equivalents => $"""


            本次的词条格式（查的是一个{settings.TargetLanguage}词，看它在{plan.TargetLanguage}里怎么说）：
            【译文】一节写 1 到 3 行，每行一个{plan.TargetLanguage}说法，最常用的排第一，格式严格如下：
            {plan.TargetLanguage}说法 /音标/ 词性. 用{settings.TargetLanguage}写的一句说明：什么场合用、和其他说法有什么区别
            例如：elephant /ˈelɪfənt/ n. 大象，最常用的说法
            - {plan.TargetLanguage}说法一定写在每行最前面。不要另起一行写{settings.TargetLanguage}释义，也不要把{plan.TargetLanguage}说法放到最后。
            - 音标：英文用国际音标；这门语言没有通行的音标写法就省掉。
            - 只列{plan.TargetLanguage}里真实常用的说法，拿不准的宁可不写；只有一个合适的说法就只写一行。
            """,
        _ => string.Empty
    };

    /// <summary>
    /// 用户自建的模式没有深度提示词（那个字段只有内置模式填了），这时退回「通用」那套。
    /// 不退而是报错的话，用户会以为是自己的模式坏了。
    /// </summary>
    private static string ResolveTemplate(TranslationMode mode)
    {
        if (!string.IsNullOrWhiteSpace(mode.DeepPromptTemplate))
        {
            return mode.DeepPromptTemplate;
        }

        return BuiltInModes.CreateDefaults()[0].DeepPromptTemplate;
    }

    /// <summary>
    /// 「原文已经是目标语言了」时该往哪儿翻。手动指定了目标语言时，
    /// 兜底语言取用户配置里的另一头，避免出现「译成中文」和「原文已是中文时也译成中文」这种死循环。
    /// </summary>
    private static string ResolveFallback(string targetLanguage, AppSettings settings)
    {
        return string.Equals(targetLanguage, settings.TargetLanguage, StringComparison.Ordinal)
            ? settings.FallbackLanguage
            : settings.TargetLanguage;
    }
}
