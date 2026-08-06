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
}
