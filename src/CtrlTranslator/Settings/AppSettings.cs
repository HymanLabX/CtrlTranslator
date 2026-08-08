namespace CtrlTranslator.Settings;

/// <summary>
/// 应用配置的数据模型。M0 只读写这些值，相关功能将在对应里程碑接通。
/// </summary>
public sealed class AppSettings
{
    public const int DefaultRequestTimeoutSeconds = 15;
    public const int DefaultFirstTokenTimeoutSeconds = 10;
    public const string DefaultExtraRequestBody = "{\"thinking\": {\"type\": \"disabled\"}}";

    /// <summary>
    /// 内置模式提示词的版本号。<b>每次改动 <see cref="BuiltInModes"/> 里的提示词文字，必须把这个数字加一。</b>
    /// 提示词会被序列化进 settings.json，光改代码对老用户不生效 —— 靠这个版本号触发覆盖。
    /// </summary>
    public const int CurrentPromptVersion = 2;

    public string BaseUrl { get; set; } = string.Empty;

    public string ProviderPreset { get; set; } = "自定义";

    public string ApiKeyProtected { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.2;

    public int MaxTokens { get; set; } = 2048;

    public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;

    public int FirstTokenTimeoutSeconds { get; set; } = DefaultFirstTokenTimeoutSeconds;

    public string ExtraRequestBody { get; set; } = DefaultExtraRequestBody;

    public string CurrentModeId { get; set; } = "auto";

    /// <summary>
    /// 本配置里存的内置提示词属于哪一版。
    /// 故意不给默认值：老配置里没有这个字段，反序列化后就是 0，从而被 <see cref="Normalize"/> 判定为过期并刷新。
    /// 若在这里写了初始值，缺字段的老配置会保留初始值，迁移就永远不会触发。
    /// </summary>
    public int PromptVersion { get; set; }

    public List<TranslationMode> Modes { get; set; } = BuiltInModes.CreateDefaults();

    public string TargetLanguage { get; set; } = "中文";

    public string FallbackLanguage { get; set; } = "英语";

    public int WordThreshold { get; set; } = 3;

    public string TriggerKey { get; set; } = "Ctrl";

    public int DoubleTapIntervalMs { get; set; } = 350;

    public int MaxKeyHoldMs { get; set; } = 400;

    public int ClipboardWaitMs { get; set; } = 300;

    public bool Enabled { get; set; } = true;

    public bool PrivacyMode { get; set; }

    public bool DetailedLogging { get; set; }

    public List<string> AppBlacklist { get; set; } = [];

    public int LongTextThreshold { get; set; } = 2000;

    public bool StartWithWindows { get; set; }

    public string Theme { get; set; } = "System";

    public double PopupFontSize { get; set; } = 14;

    public double PopupMaxWidth { get; set; } = 480;

    public double PopupMaxHeight { get; set; } = 400;

    public int StreamFallbackDelayMs { get; set; } = 1500;

    public bool SuppressReasoningWarning { get; set; }

    /// <summary>
    /// 修复旧版或手工编辑配置中可能缺失的集合及模式选择。
    /// </summary>
    public void Normalize()
    {
        Modes ??= [];
        AppBlacklist ??= [];
        ProviderPreset ??= "自定义";
        ExtraRequestBody ??= DefaultExtraRequestBody;

        List<TranslationMode> builtIns = BuiltInModes.CreateDefaults();

        foreach (TranslationMode builtIn in builtIns)
        {
            if (!Modes.Any(mode => string.Equals(mode.Id, builtIn.Id, StringComparison.Ordinal)))
            {
                Modes.Add(builtIn);
            }
        }

        // 内置模式的提示词以代码为准：老配置里存的是旧版提示词，这里整体覆盖。
        // 自定义模式（Id 不在内置清单里）不动。
        if (PromptVersion < CurrentPromptVersion)
        {
            foreach (TranslationMode builtIn in builtIns)
            {
                TranslationMode? stored = Modes.FirstOrDefault(
                    mode => string.Equals(mode.Id, builtIn.Id, StringComparison.Ordinal));
                if (stored is not null)
                {
                    stored.SystemPromptTemplate = builtIn.SystemPromptTemplate;
                    stored.IsBuiltIn = true;
                }
            }

            PromptVersion = CurrentPromptVersion;
        }

        if (!Modes.Any(mode => string.Equals(mode.Id, CurrentModeId, StringComparison.Ordinal)))
        {
            CurrentModeId = "auto";
        }
    }
}
