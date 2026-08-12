namespace CtrlTranslator.Data;

/// <summary>
/// 一条翻译历史及缓存记录。
/// </summary>
public sealed class TranslationRecord
{
    public long Id { get; set; }

    public string CacheKey { get; set; } = string.Empty;

    public string SourceText { get; set; } = string.Empty;

    public string ResultText { get; set; } = string.Empty;

    public string TargetLanguage { get; set; } = string.Empty;

    public string ModeId { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public bool IsWord { get; set; }

    public bool Starred { get; set; }

    public string? AppName { get; set; }

    public long? LatencyMilliseconds { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>翻出这条译文的服务商档案名。老记录没有这一列，读出来是 null。</summary>
    public string? ProfileName { get; set; }

    /// <summary>服务商主动回报的 token 用量；拿不到就是 null（详见 TranslationClientResult 的说明）。</summary>
    public long? PromptTokens { get; set; }

    public long? CompletionTokens { get; set; }
}
