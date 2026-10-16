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
    /// 深度翻译单独的超时与产出上限。<b>刻意不复用档案里那几个值</b>：
    /// 那些是为划词浮窗调的（"必须秒回"，超过 15 秒就该报错让用户重试），
    /// 而深度翻译要一次吐出译文 + 例句 + 搭配三节，15 秒必然生成到一半被掐断。
    /// </summary>
    public const int DeepRequestTimeoutSeconds = 120;

    public const int DeepFirstTokenTimeoutSeconds = 30;

    public const int DeepMaxTokens = 4096;

    /// <summary>
    /// 深度翻译单次输入的字符上限。比划词那条 20000 严得多 ——
    /// 深度翻译对每个关键表达都要举例、给搭配，喂一整章进去只会得到一堆泛泛而谈。
    /// </summary>
    public const int DeepInputMaxChars = 5000;

    /// <summary>
    /// 一次划词最多尝试几个档案（1 个当前 + 2 个备用）。
    /// 不设上限的话，5 个档案全挂时用户要盯着转圈等好几秒才看到报错。
    /// </summary>
    public const int MaxFailoverAttempts = 3;

    /// <summary>
    /// 单次划词允许翻译的字符上限。超过直接拒绝，不发请求。
    /// </summary>
    /// <remarks>
    /// 这个值同时是"长文本二次确认阈值"设置项的取值上界 —— 阈值再大也没意义，
    /// 因为超过上限根本不会走到确认那一步。以前两处各写了一个 20000 字面量，
    /// 改一处漏一处就会让两者打架。
    /// </remarks>
    public const int MaxTranslatableChars = 20_000;

    /// <summary>
    /// 超过这个字数的划词按"长文本"给时间和输出上限（见 TranslationService.ComputeBudget）。
    /// 与"长文本二次确认阈值"的默认值一致：过了这个长度，用户已经在确认框里明确点过「翻译」。
    /// </summary>
    public const int LongTextBudgetStartChars = 2_000;

    /// <summary>长文本一次翻译最多等多久。再长就该分段了。</summary>
    public const int LongTextMaxTimeoutSeconds = 120;

    /// <summary>长文本放宽后的输出上限封顶值。多数服务商单次最多输出这么多，再高会被直接拒绝。</summary>
    public const int LongTextMaxOutputTokens = 8_192;

    /// <summary>
    /// 内置模式提示词的版本号。<b>每次改动 <see cref="BuiltInModes"/> 里的提示词文字，必须把这个数字加一。</b>
    /// 提示词会被序列化进 settings.json，光改代码对老用户不生效 —— 靠这个版本号触发覆盖。
    /// </summary>
    public const int CurrentPromptVersion = 8;

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

    public int LongTextThreshold { get; set; } = 2000;

    public bool StartWithWindows { get; set; }

    public double PopupFontSize { get; set; } = 14;

    public double PopupMaxWidth { get; set; } = 480;

    public double PopupMaxHeight { get; set; } = 400;

    /// <summary>浮窗配色的名字，见 <see cref="PopupThemes"/>。只影响浮窗，不影响主窗口。</summary>
    public string PopupTheme { get; set; } = PopupThemes.DefaultName;

    /// <summary>配色选「自定义」时用户自己填的浮窗底色，<c>#RRGGBB</c>。</summary>
    public string PopupCustomSurface { get; set; } = "#FFFFFF";

    /// <summary>配色选「自定义」时用户自己填的浮窗文字色，<c>#RRGGBB</c>。</summary>
    public string PopupCustomText { get; set; } = "#171717";

    /// <summary>当前该用哪套配色：选了「自定义」就现拼一套，否则取内置的。</summary>
    public PopupTheme ResolvePopupTheme() =>
        string.Equals(PopupTheme, PopupThemes.CustomName, StringComparison.Ordinal)
            ? PopupThemes.CreateCustom(PopupCustomSurface, PopupCustomText)
            : PopupThemes.Find(PopupTheme);

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
    /// 构造本次划词的降级链：从当前档案开始，按列表顺序绕一圈，
    /// <b>跳过已标记失效的档案，以及三要素没填全的档案</b>，
    /// 最多取 <see cref="MaxFailoverAttempts"/> 个。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <see cref="ApiProfile.IsConfigured"/> 这一条不能省。没配完的档案进了链，
    /// <c>ValidateProfile</c> 会抛 <c>TranslationConfigurationException</c>，
    /// 而 <c>FailoverPolicy</c> 把配置异常判为 <b>Persistent</b> —— 于是这个档案会被
    /// <c>MarkFailed</c> 永久标记失效，用户得进设置页手动清掉。
    /// 一个还没填完的档案不该因为"恰好排在降级链里"就被判死刑。
    /// </para>
    /// <para>
    /// 如果一个可用的都挑不出来，仍然返回当前档案跑一次 —— 让用户看到真正的错误原因，
    /// 而不是一句无从下手的"没有可用的服务商"。
    /// </para>
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
            if (!candidate.IsFailed && candidate.IsConfigured)
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
                    stored.DeepPromptTemplate = builtIn.DeepPromptTemplate;
                    stored.IsBuiltIn = true;
                }
            }

            PromptVersion = CurrentPromptVersion;
        }

        if (!Modes.Any(mode => string.Equals(mode.Id, CurrentModeId, StringComparison.Ordinal)))
        {
            CurrentModeId = "auto";
        }

        // 配色名是手改配置文件时最容易写错的地方，认不出来就退回浅色，别让浮窗变成一团黑。
        if (!PopupThemes.IsKnown(PopupTheme))
        {
            PopupTheme = PopupThemes.DefaultName;
        }

        ClampNumbers();
    }

    /// <summary>
    /// 数值类设置夹回设置页允许的范围。设置页本身会校验，但手改过的配置文件、
    /// 或者老版本写进去的值不一定守规矩 —— 双击间隔写成 0、浮窗宽度写成 5，软件就没法用了。
    /// </summary>
    private void ClampNumbers()
    {
        WordThreshold = Math.Clamp(WordThreshold, 1, 8);
        DoubleTapIntervalMs = Math.Clamp(DoubleTapIntervalMs, 150, 800);
        MaxKeyHoldMs = Math.Clamp(MaxKeyHoldMs, 1, 5_000);
        ClipboardWaitMs = Math.Clamp(ClipboardWaitMs, 100, 1_000);
        LongTextThreshold = Math.Clamp(LongTextThreshold, 0, MaxTranslatableChars);
        StreamFallbackDelayMs = Math.Clamp(StreamFallbackDelayMs, 0, 10_000);
        PopupFontSize = ClampFinite(PopupFontSize, 10, 24, 14);
        PopupMaxWidth = ClampFinite(PopupMaxWidth, 260, 900, 480);
        PopupMaxHeight = ClampFinite(PopupMaxHeight, 180, 900, 400);

        if (string.IsNullOrWhiteSpace(TargetLanguage))
        {
            TargetLanguage = "中文";
        }

        if (string.IsNullOrWhiteSpace(FallbackLanguage))
        {
            FallbackLanguage = "英语";
        }

        foreach (ApiProfile profile in Profiles)
        {
            profile.Temperature = ClampFinite(profile.Temperature, 0, 2, 0.2);
            profile.MaxTokens = Math.Clamp(profile.MaxTokens, 1, 128_000);
        }
    }

    /// <summary>NaN、无穷大这种值 Math.Clamp 夹不住，单独换成默认值。</summary>
    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

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
