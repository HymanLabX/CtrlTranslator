using System.Text.RegularExpressions;

namespace CtrlTranslator.Translation;

internal static partial class ModelHeuristics
{
    public static bool ShouldWarnAboutReasoningModel(
        string model,
        bool sawReasoningField,
        long firstTokenLatencyMilliseconds)
    {
        return ReasoningModelNameRegex().IsMatch(model)
            || sawReasoningField
            || firstTokenLatencyMilliseconds > 5_000;
    }

    [GeneratedRegex(@"(^|[-_/])(r1|o1|o3|o4|qwq|reasoner|thinking|reasoning)([-_/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningModelNameRegex();
}
