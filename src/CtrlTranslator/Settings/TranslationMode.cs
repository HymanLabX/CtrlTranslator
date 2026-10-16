namespace CtrlTranslator.Settings;

/// <summary>
/// 一种可选择的翻译模式。内置模式的标识固定，名称和提示词以后可由设置页编辑。
/// </summary>
public sealed class TranslationMode
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>划词浮窗用的提示词：只出一句译文，为"快"服务。</summary>
    public string SystemPromptTemplate { get; set; } = string.Empty;

    /// <summary>
    /// 主窗口「深度翻译」页用的提示词：出译文 + 例句 + 搭配三节，为"写得对"服务。
    /// </summary>
    /// <remarks>
    /// 刻意和 <see cref="SystemPromptTemplate"/> 分开而不是共用一套加参数：
    /// 浮窗那套提示词是逐条调出来的（片段规则、词典标记阈值都踩过坑），
    /// 掺进"再给我几条例句"只会把它搅乱。两条路各走各的，互不影响。
    /// 老配置里没有这个字段，反序列化后为空，由 <see cref="AppSettings.Normalize"/> 补上。
    /// </remarks>
    public string DeepPromptTemplate { get; set; } = string.Empty;

    public bool IsBuiltIn { get; set; }
}
