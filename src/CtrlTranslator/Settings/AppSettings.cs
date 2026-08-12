using System.Text.Json.Serialization;

namespace CtrlTranslator.Settings;

/// <summary>
/// 应用配置的数据模型。翻译连接参数不在这里，而在 <see cref="Profiles"/> 的各个档案里。
/// </summary>
public sealed class AppSettings
{
    public const int DefaultRequestTimeoutSeconds = 15;
    public const int DefaultFirstTokenTimeoutSeconds = 10;
    public const string DefaultExtraRequestBody = "{\"thinking\": {\"type\": \"disabled\"}}";

    /// <summary>
    /// 一次划词最多尝试几个档案（1 个当前 + 2 个备用）。
    /// 不设上限的话，5 个档案全挂时用户要盯着转圈等好几秒才看到报错。
    /// </summary>
    public const int MaxFailoverAttempts = 3;

    /// <summary>
    /// 内置模式提示词的版本号。<b>每次改动 <see cref="BuiltInModes"/> 里的提示词文字，必须把这个数字加一。</b>
    /// 提示词会被序列化进 settings.json，光改代码对老用户不生效 —— 靠这个版本号触发覆盖。
    /// </summary>
    public const int CurrentPromptVersion = 2;

    /// <summary>
    /// 服务商档案列表。<b>顺序即降级顺序</b>：当前档案失败后按此顺序往下找备用。
    /// </summary>
    public List<ApiProfile> Profiles { get; set; } = [];

    public string CurrentProfileId { get; set; } = string.Empty;

    /// <summary>
    /// 当前生效的档案。<see cref="Normalize"/> 保证列表非空，因此这里永远不返回 null。
    /// </summary>
    [JsonIgnore]
    public ApiProfile CurrentProfile
    {
        get
        {
            EnsureProfilesNotEmpty();
            return Profiles.FirstOrDefault(profile =>
                       string.Equals(profile.Id, CurrentProfileId, StringComparison.Ordinal))
                   ?? Profiles[0];
        }
    }

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

    public double PopupFontSize { get; set; } = 14;

    public double PopupMaxWidth { get; set; } = 480;

    public double PopupMaxHeight { get; set; } = 400;

    public int StreamFallbackDelayMs { get; set; } = 1500;

    public bool SuppressReasoningWarning { get; set; }

    /// <summary>
    /// 是否在启动后和之后每 24 小时查一次 GitHub 有没有新版本。
    /// 关掉之后程序完全不联网查更新（翻译请求不受影响）。
    /// </summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// 用户点过「稍后」的那个版本号。同一个版本不再自动弹窗，避免每天骚扰一次；
    /// 设置页里仍然会显示"有新版本"，手动点「检查更新」也照样能弹出来。
    /// </summary>
    public string DismissedUpdateVersion { get; set; } = string.Empty;

    /// <summary>
    /// 构造本次划词的降级链：从当前档案开始，按列表顺序绕一圈，<b>跳过已标记失效的档案</b>，
    /// 最多取 <see cref="MaxFailoverAttempts"/> 个。
    /// </summary>
    /// <remarks>
    /// 如果所有档案都被标记失效，仍然返回当前档案跑一次 —— 让用户看到真正的错误原因，
    /// 而不是一句无从下手的"没有可用的服务商"。
    /// </remarks>
    public IReadOnlyList<ApiProfile> BuildFailoverChain()
    {
        EnsureProfilesNotEmpty();

        int start = Profiles.FindIndex(profile =>
            string.Equals(profile.Id, CurrentProfileId, StringComparison.Ordinal));
        if (start < 0)
        {
            start = 0;
        }

        List<ApiProfile> chain = [];
        for (int offset = 0; offset < Profiles.Count && chain.Count < MaxFailoverAttempts; offset++)
        {
            ApiProfile candidate = Profiles[(start + offset) % Profiles.Count];
            if (!candidate.IsFailed)
            {
                chain.Add(candidate);
            }
        }

        if (chain.Count == 0)
        {
            chain.Add(Profiles[start]);
        }

        return chain;
    }

    public ApiProfile? FindProfile(string? profileId)
    {
        return string.IsNullOrWhiteSpace(profileId)
            ? null
            : Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, profileId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 生成一个不与现有档案重名的名称。
    /// </summary>
    public string GenerateUniqueProfileName(string baseName)
    {
        if (Profiles.All(profile => profile.Name != baseName))
        {
            return baseName;
        }

        int suffix = 2;
        while (Profiles.Any(profile => profile.Name == $"{baseName} {suffix}"))
        {
            suffix++;
        }

        return $"{baseName} {suffix}";
    }

    /// <summary>
    /// 修复旧版或手工编辑配置中可能缺失的集合及选择。
    /// </summary>
    public void Normalize()
    {
        Modes ??= [];
        AppBlacklist ??= [];
        Profiles ??= [];

        EnsureProfilesNotEmpty();
        NormalizeProfiles();

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

    private void EnsureProfilesNotEmpty()
    {
        if (Profiles.Count == 0)
        {
            Profiles.Add(new ApiProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "默认服务商"
            });
        }

        if (!Profiles.Any(profile =>
                string.Equals(profile.Id, CurrentProfileId, StringComparison.Ordinal)))
        {
            CurrentProfileId = Profiles[0].Id;
        }
    }

    private void NormalizeProfiles()
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Profiles.Count; index++)
        {
            ApiProfile profile = Profiles[index];
            if (string.IsNullOrWhiteSpace(profile.Id) || !seenIds.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
                seenIds.Add(profile.Id);
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = $"服务商 {index + 1}";
            }

            profile.ProviderPreset ??= ProviderPresets.CustomName;
            profile.ExtraRequestBody ??= string.Empty;

            // 手工编辑配置也不能突破超时上限，且首字超时必须先于整体超时触发。
            profile.RequestTimeoutSeconds = Math.Clamp(
                profile.RequestTimeoutSeconds, 2, DefaultRequestTimeoutSeconds);
            profile.FirstTokenTimeoutSeconds = Math.Clamp(
                profile.FirstTokenTimeoutSeconds, 1, DefaultFirstTokenTimeoutSeconds);
            if (profile.FirstTokenTimeoutSeconds >= profile.RequestTimeoutSeconds)
            {
                profile.FirstTokenTimeoutSeconds = Math.Max(1, profile.RequestTimeoutSeconds - 1);
            }
        }
    }
}
