namespace CtrlTranslator.Settings;

/// <summary>
/// 一种可选择的翻译模式。内置模式的标识固定，名称和提示词以后可由设置页编辑。
/// </summary>
public sealed class TranslationMode
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string SystemPromptTemplate { get; set; } = string.Empty;

    public bool IsBuiltIn { get; set; }
}
