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
    private const string DictionaryMarker = "[请按词典条目格式输出]\n";

    public static BuiltPrompt Build(string text, AppSettings settings)
    {
        var targetLanguage = DetectTargetLanguage(text, settings);
        var mode = settings.Modes.FirstOrDefault(item => item.Id == settings.CurrentModeId)
            ?? BuiltInModes.CreateDefaults()[0];
        var isWord = IsWord(text, settings.WordThreshold);
        var systemPrompt = mode.SystemPromptTemplate
            .Replace("{target_lang}", targetLanguage, StringComparison.Ordinal)
            .Replace("{fallback_lang}", settings.FallbackLanguage, StringComparison.Ordinal);
        var userContent = isWord ? DictionaryMarker + text : text;

        return new BuiltPrompt(systemPrompt, userContent, targetLanguage, mode, isWord);
    }

    private static string DetectTargetLanguage(string text, AppSettings settings)
    {
        var meaningfulCount = 0;
        var cjkCount = 0;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            meaningfulCount++;
            if (character is >= '\u3400' and <= '\u9fff')
            {
                cjkCount++;
            }
        }

        return meaningfulCount > 0 && (double)cjkCount / meaningfulCount > 0.2
            ? settings.FallbackLanguage
            : settings.TargetLanguage;
    }

    private static bool IsWord(string text, int wordThreshold)
    {
        if (text.IndexOfAny(['。', '！', '？', '.', '!', '?', ';', '；', '\n', '\r']) >= 0)
        {
            return false;
        }

        string withoutPunctuation = new(
            text.Where(character => !char.IsPunctuation(character)).ToArray());
        string trimmed = withoutPunctuation.Trim();
        var cjkCount = trimmed.Count(character => character is >= '\u3400' and <= '\u9fff');
        if (cjkCount > 0)
        {
            return cjkCount <= 4;
        }

        var words = trimmed.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length is > 0 && words.Length <= Math.Max(1, wordThreshold);
    }
}
