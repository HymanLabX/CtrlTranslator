namespace CtrlTranslator.Settings;

/// <summary>
/// 应用配置的数据模型。M0 只读写这些值，相关功能将在对应里程碑接通。
/// </summary>
public sealed class AppSettings
{
    public const string DefaultExtraRequestBody = "{\"thinking\": {\"type\": \"disabled\"}}";

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKeyProtected { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.2;

    public int MaxTokens { get; set; } = 2048;

    public int RequestTimeoutSeconds { get; set; } = 60;

    public int FirstTokenTimeoutSeconds { get; set; } = 15;

    public string ExtraRequestBody { get; set; } = DefaultExtraRequestBody;

    public string CurrentModeId { get; set; } = "auto";

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
        ExtraRequestBody ??= DefaultExtraRequestBody;

        foreach (TranslationMode builtIn in BuiltInModes.CreateDefaults())
        {
            if (!Modes.Any(mode => string.Equals(mode.Id, builtIn.Id, StringComparison.Ordinal)))
            {
                Modes.Add(builtIn);
            }
        }

        if (!Modes.Any(mode => string.Equals(mode.Id, CurrentModeId, StringComparison.Ordinal)))
        {
            CurrentModeId = "auto";
        }
    }
}
