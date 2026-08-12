using System.Text.Json.Serialization;

namespace CtrlTranslator.Settings;

/// <summary>
/// 一个服务商档案：连接一个 OpenAI 兼容接口所需的<b>全部</b>参数。
/// </summary>
/// <remarks>
/// <para>
/// 为什么 <see cref="ExtraRequestBody"/>、<see cref="Temperature"/>、超时都归档案而不是全局：
/// 关闭"思考模式"的参数是服务商特定的（DeepSeek 用 <c>thinking.type</c>，通义用
/// <c>enable_thinking</c>），若做成全局，切到别家会带着上一家的参数发过去，轻则不生效、重则 400。
/// </para>
/// <para>
/// <see cref="ApiKey"/> 标了 <see cref="JsonIgnoreAttribute"/>，明文永远不落盘；
/// 写文件时由 <see cref="SettingsStore"/> 用 DPAPI 加密进 <see cref="ApiKeyProtected"/>。
/// </para>
/// </remarks>
public sealed class ApiProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ProviderPreset { get; set; } = ProviderPresets.CustomName;

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKeyProtected { get; set; } = string.Empty;

    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.2;

    public int MaxTokens { get; set; } = 2048;

    public int RequestTimeoutSeconds { get; set; } = AppSettings.DefaultRequestTimeoutSeconds;

    public int FirstTokenTimeoutSeconds { get; set; } = AppSettings.DefaultFirstTokenTimeoutSeconds;

    public string ExtraRequestBody { get; set; } = AppSettings.DefaultExtraRequestBody;

    /// <summary>
    /// 持久故障的原因（Key 失效 / 余额不足 / 模型不存在）。非空即代表本档案已被标记失效，
    /// 降级链会跳过它。null 表示正常。<b>瞬时故障（429/5xx/网络）绝不写这里</b>，
    /// 否则服务商恢复后还要等用户手动解除。
    /// </summary>
    public string? FailureReason { get; set; }

    public DateTime? FailedAt { get; set; }

    [JsonIgnore]
    public bool IsFailed => !string.IsNullOrWhiteSpace(FailureReason);

    /// <summary>
    /// 三要素齐全才算配置完整。本地 Ollama 允许不填 Key，故 Key 不参与判断。
    /// </summary>
    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    public static ApiProfile CreateFromPreset(string presetName, string profileName)
    {
        ProviderPreset preset = ProviderPresets.Find(presetName);
        return new ApiProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = profileName,
            ProviderPreset = preset.Name,
            BaseUrl = preset.BaseUrl,
            Model = preset.Model,
            ExtraRequestBody = preset.ExtraRequestBody
        };
    }

    /// <summary>
    /// 供诊断与连接测试使用的快照，避免测试过程中用户正在编辑的字段被读到一半。
    /// </summary>
    public ApiProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        ProviderPreset = ProviderPreset,
        BaseUrl = BaseUrl,
        ApiKeyProtected = ApiKeyProtected,
        ApiKey = ApiKey,
        Model = Model,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        FirstTokenTimeoutSeconds = FirstTokenTimeoutSeconds,
        ExtraRequestBody = ExtraRequestBody,
        FailureReason = FailureReason,
        FailedAt = FailedAt
    };

    public void MarkFailed(string reason)
    {
        FailureReason = reason;
        FailedAt = DateTime.Now;
    }

    public void ClearFailure()
    {
        FailureReason = null;
        FailedAt = null;
    }

    public override string ToString() => Name;
}
