using CtrlTranslator.Settings;

namespace CtrlTranslator.Translation;

internal sealed record BuiltDeepPrompt(
    string SystemPrompt,
    string UserContent,
    string TargetLanguage,
    TranslationMode Mode,
    bool IsWord);

/// <summary>
/// 组装「深度翻译」的提示词。和 <see cref="PromptBuilder"/> 分开，两者除了语言方向判定
/// 没有任何共用逻辑：浮窗要的是一句译文，这里要的是三节结构化输出。
/// </summary>
internal static class DeepPromptBuilder
{
    /// <summary>目标语言下拉里代表「自动检测」的取值。</summary>
    public const string AutoLanguage = "";

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

        string targetLanguage = string.IsNullOrWhiteSpace(targetLanguageOverride)
            ? PromptBuilder.DetectTargetLanguage(text, settings)
            : targetLanguageOverride.Trim();

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

        // 语言指令拼在最后，理由见 PromptBuilder.BuildLanguageInstruction 的注释。
        // 老版本的自定义深度提示词里可能还留着占位符，四个 Replace 保留着做兼容。
        string systemPrompt = ResolveTemplate(mode)
                .Replace("{target_lang}", targetLanguage, StringComparison.Ordinal)
                .Replace("{fallback_lang}", ResolveFallback(targetLanguage, settings), StringComparison.Ordinal)
                .Replace("{study_lang}", settings.FallbackLanguage, StringComparison.Ordinal)
                .Replace("{native_lang}", settings.TargetLanguage, StringComparison.Ordinal)
            + "\n\n"
            + languageInstruction;

        // 查单词时必须告诉模型"这是个词条"，否则它会给一个孤零零的词自行脑补上下文，
        // 把【译文】写成一整段含该词释义的文章（已踩坑：查 harness 得到一段医疗 AI 的话）。
        bool isWord = PromptBuilder.IsWord(text, settings.WordThreshold);
        string userContent = isWord
            ? BuiltInModes.DictionaryMarkerTag + "\n" + text
            : text;

        return new BuiltDeepPrompt(systemPrompt, userContent, targetLanguage, mode, isWord);
    }

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
