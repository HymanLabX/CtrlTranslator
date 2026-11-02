using System.Text.RegularExpressions;

namespace CtrlTranslator.Core;

/// <summary>
/// 只清理送给模型的文字，界面、历史与复制原文仍保留用户实际选中的内容。
/// 对普通连字符只在拼接后是明确的常用学术词时去掉，无法确定时保留连字符。
/// </summary>
internal static partial class TranslationTextCleaner
{
    private static readonly HashSet<string> JoinedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "translation", "international", "national", "heterogeneity", "heterogeneous",
        "homogeneous", "homogeneity", "stabilization", "stabilisation", "simulation",
        "analysis", "analytical", "experimental", "experiment", "experiments",
        "distribution", "concentration", "adsorption", "absorption", "desorption",
        "temperature", "characterization", "characterisation", "measurement",
        "measurements", "parameter", "parameters", "performance", "information",
        "interaction", "interactions", "molecular", "mechanism", "mechanisms",
        "environment", "environmental", "methodology", "significant", "significantly",
        "different", "difference", "differences", "diffusion", "investigation",
        "investigations", "application", "applications", "development", "structure",
        "structures", "structural", "equilibrium", "thermodynamic", "thermodynamics"
    };

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        bool[] protectedLines = new bool[lines.Length];
        bool inCode = false;
        for (int index = 0; index < lines.Length; index++)
        {
            string trimmed = lines[index].TrimStart();
            bool fence = trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal);
            protectedLines[index] = inCode || fence || IsStructuredLine(lines[index]);
            if (fence)
            {
                inCode = !inCode;
            }

            if (!protectedLines[index])
            {
                lines[index] = SoftHyphenInsideWord().Replace(lines[index], string.Empty);
            }
        }

        List<string> cleaned = [];
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            bool isProtected = protectedLines[index];
            while (index + 1 < lines.Length && !isProtected && !protectedLines[index + 1])
            {
                string next = lines[index + 1];
                Match end = HyphenatedEnd().Match(line);
                Match start = WordAtStart().Match(next);
                if (end.Success && start.Success)
                {
                    string joined = end.Groups["word"].Value + start.Groups["word"].Value;
                    bool removeHyphen = end.Groups["hyphen"].Value == "\u00ad" || JoinedWords.Contains(joined);
                    line = line[..end.Index] + end.Groups["word"].Value
                        + (removeHyphen ? string.Empty : "-") + next.TrimStart();
                }
                else if (IsWrappedProse(line, next))
                {
                    line = line.TrimEnd() + " " + next.TrimStart();
                }
                else
                {
                    break;
                }

                index++;
            }

            cleaned.Add(line);
        }

        // 保留代码行首的缩进，只清掉选区外围的空行。
        return string.Join('\n', cleaned).Trim('\r', '\n');
    }

    private static bool IsStructuredLine(string line)
    {
        string trimmed = line.TrimStart();
        // 空行保留段落；列表、标题、表格、代码缩进和公式行都不参加拼接。
        return trimmed.Length == 0 || line.StartsWith("    ", StringComparison.Ordinal)
            || line.StartsWith('\t') || StructuredPrefix().IsMatch(trimmed)
            || line.IndexOfAny(['=', '+', '*', '/', '<', '>', '≤', '≥', '∑', '∫', '$', '|', '\\']) >= 0;
    }

    private static bool IsWrappedProse(string line, string next)
    {
        string left = line.TrimEnd();
        string right = next.TrimStart();
        // 只合并长正文行后以小写字母起头的延续行；短标题、句尾和新段落保持原样。
        return left.Length >= 60 && right.Length >= 20
            && right[0] is >= 'a' and <= 'z'
            && (char.IsLetter(left[^1]) || left[^1] is ',' or ';')
            && left.Count(char.IsLetter) >= left.Length * 0.65
            && right.Count(char.IsLetter) >= right.Length * 0.65
            && right.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    }

    [GeneratedRegex("(?<=[A-Za-z])\\u00ad(?=[A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex SoftHyphenInsideWord();

    [GeneratedRegex("(?<word>[A-Za-z]{2,})(?<hyphen>[-\\u00ad])[ \\t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenatedEnd();

    [GeneratedRegex("^[ \\t]*(?<word>[a-z]{2,})\\b", RegexOptions.CultureInvariant)]
    private static partial Regex WordAtStart();

    [GeneratedRegex("^(?:[-*•#>]\\s|[0-9]+[.)]\\s|\\[[0-9]+\\]|(?:Figure|Table|Fig\\.)\\s)", RegexOptions.CultureInvariant)]
    private static partial Regex StructuredPrefix();
}
