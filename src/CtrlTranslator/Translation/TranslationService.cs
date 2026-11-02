using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using CtrlTranslator.Data;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;

namespace CtrlTranslator.Translation;

/// <param name="FinishReason">
/// 非空且不是 <c>stop</c> 时说明译文不完整：<c>length</c> 是写到了输出上限，
/// <c>content_filter</c> 是被服务商的内容审核截停。浮窗据此提示用户。
/// </param>
/// <param name="MaxTokens">本次请求实际用的输出上限，截断提示里要报出这个数。</param>
/// <param name="DirectConnectionProxy">非空表示系统代理没开、本次是自动直连完成的。</param>
/// <param name="ProfileId">实际翻出这条译文的档案。浮窗上的修复按钮改的是它。</param>
internal sealed record TranslationResult(
    string SourceText,
    string ResultText,
    string TargetLanguage,
    string ModeId,
    string Model,
    bool IsWord,
    bool FromCache,
    bool Starred,
    long? RecordId,
    long FirstTokenLatencyMilliseconds,
    long TotalLatencyMilliseconds,
    bool ShowReasoningWarning,
    string ProfileName,
    string? FellBackFromProfileName,
    string? FinishReason = null,
    int MaxTokens = 0,
    string? DirectConnectionProxy = null,
    string? ProfileId = null)
{
    public bool IsTruncated =>
        TranslationCompletion.IsIncomplete(FinishReason);
}

/// <summary>
/// 一次深度翻译的产出。<see cref="RawText"/> 是模型原样吐出的三节文本，
/// 交给 <see cref="DeepResultParser"/> 才变成结构化结果。
/// </summary>
internal sealed record DeepTranslationOutcome(
    string RawText,
    string TargetLanguage,
    string ModeId,
    string Model,
    string ProfileName,
    string? FellBackFromProfileName,
    long TotalLatencyMilliseconds,
    string? FinishReason = null,
    string? DirectConnectionProxy = null)
{
    public bool IsTruncated =>
        TranslationCompletion.IsIncomplete(FinishReason);
}

/// <summary>一次划词请求的时间与产出预算。</summary>
/// <param name="TimeoutSeconds">从发请求到输出完毕的硬上限。</param>
/// <param name="MaxTokens">本次请求的输出上限。</param>
/// <param name="Raised">是否因为长文本而放宽过（放宽过的输出上限被服务商拒绝时，要退回档案值重试）。</param>
internal readonly record struct TranslationBudget(int TimeoutSeconds, int MaxTokens, bool Raised);

/// <summary>
/// 串联提示词、缓存和网络请求；新请求开始时会先取消旧请求。
/// 当前档案秒级失败时，按 <see cref="AppSettings.BuildFailoverChain"/> 给出的顺序自动改用备用档案。
/// </summary>
internal sealed class TranslationService : IDisposable
{
    private readonly object _requestSync = new();
    private readonly Func<AppSettings> _settingsProvider;
    private readonly HistoryRepository _historyRepository;
    private readonly ITranslationClient _client;
    private readonly Action<ApiProfile, string>? _profileMarkedFailed;
    private CancellationTokenSource? _currentCancellation;
    private bool _disposed;

    /// <param name="profileMarkedFailed">
    /// 某个档案因持久故障被标记失效时回调。持久化配置与提醒用户由上层负责，
    /// 翻译服务不碰 <see cref="SettingsStore"/>，也不碰界面。
    /// </param>
    public TranslationService(
        Func<AppSettings> settingsProvider,
        HistoryRepository historyRepository,
        Action<ApiProfile, string>? profileMarkedFailed = null)
    {
        _settingsProvider = settingsProvider;
        _historyRepository = historyRepository;
        _profileMarkedFailed = profileMarkedFailed;
        _client = new OpenAiCompatibleClient(() => settingsProvider().CurrentProfile);
    }

    public async Task<TranslationResult> TranslateAsync(
        string sourceText,
        string? appName,
        IProgress<TranslationStreamUpdate>? progress,
        CancellationToken cancellationToken,
        bool bypassCache = false)
    {
        CancellationTokenSource ownCancellation = ReplaceCurrentCancellation(cancellationToken);
        try
        {
            AppSettings settings = _settingsProvider();
            BuiltPrompt prompt = PromptBuilder.Build(sourceText, settings);
            return await RunWithFailoverAsync(
                settings,
                (profile, fellBackFromProfileName, token) => TranslateWithProfileAsync(
                    profile,
                    fellBackFromProfileName,
                    sourceText,
                    appName,
                    prompt,
                    settings,
                    progress,
                    bypassCache,
                    token),
                ownCancellation.Token);
        }
        finally
        {
            ReleaseCurrentCancellation(ownCancellation);
        }
    }

    /// <summary>
    /// 主窗口「深度翻译」页专用：一次拿到译文 + 例句 + 搭配三节的原始文本，交给
    /// <see cref="DeepResultParser"/> 分段。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 和 <see cref="TranslateAsync"/> 有三处刻意的不同，每一处都是有原因的：
    /// </para>
    /// <list type="number">
    /// <item><b>不读也不写缓存和历史</b>。深度翻译的结果只活在页面里，
    /// 所以既不查 <see cref="HistoryRepository"/>，也不插记录。顺带解决了一个隐患：
    /// 缓存 key 不含"哪条路"这一维，若深度翻译也写缓存，划同一段文字时浮窗会命中
    /// 这一大坨三节文本并原样显示出来。</item>
    /// <item><b>不进 <see cref="_currentCancellation"/></b>。那个字段的语义是"最新一次划词
    /// 取消上一次划词"，深度翻译若挂进去，用户在等结果的半路随手划一下词就会把跑了
    /// 二十秒的深度翻译掐掉。这里直接用调用方给的 token，由页面自己持有。</item>
    /// <item><b>超时和 max_tokens 走 <c>AppSettings.Deep*</c></b>，不用档案里那套浮窗参数。</item>
    /// </list>
    /// </remarks>
    public async Task<DeepTranslationOutcome> TranslateDeepAsync(
        string sourceText,
        string modeId,
        string? targetLanguageOverride,
        IProgress<TranslationStreamUpdate>? progress,
        CancellationToken cancellationToken)
    {
        AppSettings settings = _settingsProvider();
        BuiltDeepPrompt prompt = DeepPromptBuilder.Build(
            sourceText,
            settings,
            modeId,
            targetLanguageOverride);

        return await RunWithFailoverAsync(
            settings,
            (profile, fellBackFromProfileName, token) => DeepTranslateWithProfileAsync(
                profile,
                fellBackFromProfileName,
                prompt,
                progress,
                token),
            cancellationToken);
    }

    /// <summary>
    /// 按 <see cref="AppSettings.BuildFailoverChain"/> 依次尝试各档案，直到某个成功。
    /// 划词和深度翻译共用这一份降级策略 —— 拆成两份复制粘贴的话，
    /// 以后改判定条件一定会漏掉其中一条路。
    /// </summary>
    private async Task<TResult> RunWithFailoverAsync<TResult>(
        AppSettings settings,
        Func<ApiProfile, string?, CancellationToken, Task<TResult>> attempt,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ApiProfile> chain = settings.BuildFailoverChain();

        // 只有一个档案时标记失效毫无意义（没有备用可切），只会让用户对着一个红色感叹号发懵。
        bool canMarkFailed = settings.Profiles.Count > 1;
        ExceptionDispatchInfo? lastFailure = null;

        for (int index = 0; index < chain.Count; index++)
        {
            ApiProfile profile = chain[index];
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await attempt(
                    profile,
                    index > 0 ? chain[0].Name : null,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 报错文案要说的是"这一次真正失败的那个档案"，不一定是当前档案。
                exception.Data[FailureContext.ProfileIdKey] = profile.Id;
                exception.Data[FailureContext.ModelKey] = profile.Model.Trim();
                exception.Data[FailureContext.ThinkingDisabledKey] =
                    ExtraRequestBodyHelper.IsThinkingDisabled(profile.ExtraRequestBody);
                lastFailure = ExceptionDispatchInfo.Capture(exception);

                FailoverDecision decision = FailoverPolicy.Classify(exception, profile.Model);
                if (decision.Verdict == FailoverVerdict.Fatal)
                {
                    Logger.Warning(
                        "翻译",
                        $"档案「{profile.Name}」失败且不适合降级（{exception.GetType().Name}），直接报错。");
                    throw;
                }

                if (decision.Verdict == FailoverVerdict.Persistent
                    && canMarkFailed
                    && decision.PersistentReason is { } reason)
                {
                    profile.MarkFailed(reason);
                    Logger.Warning("翻译", $"档案「{profile.Name}」已标记失效：{reason}。");
                    _profileMarkedFailed?.Invoke(profile, reason);
                }

                bool hasNext = index + 1 < chain.Count;
                Logger.Warning(
                    "翻译",
                    $"档案「{profile.Name}」本次失败（{decision.Verdict}）。"
                    + (hasNext ? "正在改用下一个备用档案。" : "没有更多备用档案可用。"));
            }
        }

        // 原样重抛最后一次失败，保留它的调用栈（直接 throw 变量会把调用栈截断在这里）。
        lastFailure?.Throw();
        throw new InvalidOperationException("没有任何可用的服务商档案。");
    }

    private async Task<DeepTranslationOutcome> DeepTranslateWithProfileAsync(
        ApiProfile profile,
        string? fellBackFromProfileName,
        BuiltDeepPrompt prompt,
        IProgress<TranslationStreamUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ValidateProfile(profile);

        var request = new TranslationRequest(
            profile.BaseUrl.Trim(),
            profile.ApiKey.Trim(),
            profile.Model.Trim(),
            prompt.SystemPrompt,
            prompt.UserContent,
            profile.Temperature,
            // 档案里的 max_tokens 是按"一句译文"配的，够不着三节输出；
            // 取两者较大值，这样把它调大过的用户不会被这里反而压回来。
            Math.Max(profile.MaxTokens, AppSettings.DeepMaxTokens),
            AppSettings.DeepRequestTimeoutSeconds,
            AppSettings.DeepFirstTokenTimeoutSeconds,
            profile.ExtraRequestBody,
            AppSettings.DeepRequestTimeoutSeconds,
            AppSettings.DeepFirstTokenTimeoutSeconds);

        TranslationClientResult clientResult = await _client.TranslateAsync(
            request,
            progress,
            cancellationToken);
        Logger.Info(
            "深度翻译",
            $"完成（档案「{profile.Name}」，模式 {prompt.Mode.Id}，译成 {prompt.TargetLanguage}"
            + prompt.WordStyle switch
            {
                DeepWordStyle.Explain => "，查外文词",
                DeepWordStyle.Equivalents => "，查对应说法",
                _ => string.Empty
            }
            + "）："
            + $"首字 {clientResult.FirstTokenLatencyMilliseconds}ms，"
            + $"总耗时 {clientResult.TotalLatencyMilliseconds}ms。");

        return new DeepTranslationOutcome(
            clientResult.Text,
            prompt.TargetLanguage,
            prompt.Mode.Id,
            profile.Model.Trim(),
            profile.Name,
            fellBackFromProfileName,
            clientResult.TotalLatencyMilliseconds,
            clientResult.FinishReason,
            clientResult.DirectConnectionProxy);
    }

    /// <summary>
    /// 算这次划词能等多久、能写多长（负责人 2026-10 决定的长文本规则）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 短文本（≤ <see cref="AppSettings.LongTextBudgetStartChars"/> 字）原样用档案里的设置，
    /// 也就是 SPEC 的 15 秒硬上限 —— 划一句话等超过 15 秒，即使出来了也已经打断阅读。
    /// </para>
    /// <para>
    /// 长文本是用户在确认框里明确点了「翻译」的，他知道要等。以前这里也套 15 秒，
    /// 按实测速度（约 3 秒 / 千字）超过五千字必然超时，确认框却还在说"大约需要 30 秒"。
    /// 现在按每千字 6 秒（实测的两倍余量）往上加，封顶 <see cref="AppSettings.LongTextMaxTimeoutSeconds"/> 秒；
    /// 输出上限按原文长度的 1.2 倍估，封顶 <see cref="AppSettings.LongTextMaxOutputTokens"/>
    /// —— 大多数服务商单次输出就到这么多，再高会被直接拒绝。
    /// </para>
    /// </remarks>
    internal static TranslationBudget ComputeBudget(int textLength, ApiProfile profile)
    {
        if (textLength <= AppSettings.LongTextBudgetStartChars)
        {
            return new TranslationBudget(profile.RequestTimeoutSeconds, profile.MaxTokens, Raised: false);
        }

        int thousands = (int)Math.Ceiling(textLength / 1000d);
        int timeout = Math.Clamp(
            AppSettings.DefaultRequestTimeoutSeconds + thousands * 6,
            profile.RequestTimeoutSeconds,
            AppSettings.LongTextMaxTimeoutSeconds);
        int estimatedTokens = (int)Math.Ceiling(textLength * 1.2) + 256;
        int maxTokens = Math.Max(
            profile.MaxTokens,
            Math.Min(estimatedTokens, AppSettings.LongTextMaxOutputTokens));
        return new TranslationBudget(timeout, maxTokens, Raised: maxTokens > profile.MaxTokens);
    }

    /// <summary>
    /// 服务商是不是因为输出上限太大而拒绝了请求（有的家单次输出只给 4096）。
    /// </summary>
    private static bool IsMaxTokensRejected(Exception exception)
    {
        return exception is ApiHttpException { Kind: ApiErrorKind.BadRequest } api
            && (api.ResponseBody.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
                || api.ResponseBody.Contains("max tokens", StringComparison.OrdinalIgnoreCase)
                || api.ResponseBody.Contains("maximum", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<TranslationResult> TranslateWithProfileAsync(
        ApiProfile profile,
        string? fellBackFromProfileName,
        string sourceText,
        string? appName,
        BuiltPrompt prompt,
        AppSettings settings,
        IProgress<TranslationStreamUpdate>? progress,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        ValidateProfile(profile);

        // 缓存 key 里带的是本次实际要用的档案参数。降级到别的模型时，写入和读取用的是同一把
        // key，绝不会出现"主档案恢复后却命中了备用模型译文"这种串台。
        string cacheKey = CreateCacheKey(sourceText, prompt, profile);

        if (!settings.PrivacyMode && !bypassCache)
        {
            TranslationRecord? cached = await TryReadCacheAsync(cacheKey, cancellationToken);
            if (cached is not null)
            {
                Logger.Info("翻译", "cache hit");
                return new TranslationResult(
                    cached.SourceText,
                    cached.ResultText,
                    cached.TargetLanguage,
                    cached.ModeId,
                    cached.Model,
                    cached.IsWord,
                    FromCache: true,
                    cached.Starred,
                    cached.Id,
                    FirstTokenLatencyMilliseconds: 0,
                    TotalLatencyMilliseconds: 0,
                    ShowReasoningWarning: false,
                    profile.Name,
                    FellBackFromProfileName: null,
                    ProfileId: profile.Id);
            }
        }

        if (bypassCache)
        {
            Logger.Info("翻译", "本次重译已绕过缓存，将使用最新请求参数。");
        }

        TranslationBudget budget = ComputeBudget(sourceText.Length, profile);
        if (budget.Raised || budget.TimeoutSeconds > profile.RequestTimeoutSeconds)
        {
            Logger.Info(
                "翻译",
                $"长文本（{sourceText.Length} 字）：等待上限放宽到 {budget.TimeoutSeconds} 秒，输出上限 {budget.MaxTokens}。");
        }

        TranslationRequest CreateRequest(int maxTokens) => new(
            profile.BaseUrl.Trim(),
            profile.ApiKey.Trim(),
            profile.Model.Trim(),
            prompt.SystemPrompt,
            prompt.UserContent,
            profile.Temperature,
            maxTokens,
            budget.TimeoutSeconds,
            profile.FirstTokenTimeoutSeconds,
            profile.ExtraRequestBody,
            MaxRequestTimeoutSeconds: Math.Max(budget.TimeoutSeconds, AppSettings.DefaultRequestTimeoutSeconds));

        int maxTokensUsed = budget.MaxTokens;
        TranslationClientResult clientResult;
        try
        {
            clientResult = await _client.TranslateAsync(CreateRequest(maxTokensUsed), progress, cancellationToken);
        }
        catch (Exception exception) when (budget.Raised && IsMaxTokensRejected(exception))
        {
            // 放宽过的输出上限被这家服务商拒了：退回档案里的值再试一次，不算这个档案的失败。
            Logger.Warning(
                "翻译",
                $"服务商不接受输出上限 {budget.MaxTokens}，退回档案设置的 {profile.MaxTokens} 重试一次。");
            maxTokensUsed = profile.MaxTokens;
            clientResult = await _client.TranslateAsync(CreateRequest(maxTokensUsed), progress, cancellationToken);
        }

        Logger.Info(
            "翻译",
            $"翻译完成（档案「{profile.Name}」）：首字 {clientResult.FirstTokenLatencyMilliseconds}ms，"
            + $"总耗时 {clientResult.TotalLatencyMilliseconds}ms。");

        long? recordId = null;
        if (!settings.PrivacyMode)
        {
            var record = new TranslationRecord
            {
                // 不完整的译文照样记进历史（用户可能还想收藏它），但缓存 key 加个后缀让它永远不被命中：
                // 否则调大输出上限之后再划同一段，拿到的还是这份被截断的旧结果。
                CacheKey = clientResult.IsTruncated ? $"{cacheKey}#incomplete" : cacheKey,
                SourceText = sourceText,
                ResultText = clientResult.Text,
                TargetLanguage = prompt.TargetLanguage,
                ModeId = prompt.Mode.Id,
                Model = profile.Model.Trim(),
                IsWord = prompt.IsWord,
                Starred = false,
                AppName = appName,
                LatencyMilliseconds = clientResult.TotalLatencyMilliseconds,
                CreatedAt = DateTime.Now,
                ProfileName = profile.Name,
                PromptTokens = clientResult.PromptTokens,
                CompletionTokens = clientResult.CompletionTokens
            };
            recordId = await TryInsertHistoryAsync(record, cancellationToken);
        }

        bool showReasoningWarning = !settings.SuppressReasoningWarning
            && ModelHeuristics.ShouldWarnAboutReasoningModel(
                profile.Model,
                clientResult.SawReasoning,
                clientResult.FirstTokenLatencyMilliseconds);
        return new TranslationResult(
            sourceText,
            clientResult.Text,
            prompt.TargetLanguage,
            prompt.Mode.Id,
            profile.Model.Trim(),
            prompt.IsWord,
            FromCache: false,
            Starred: false,
            recordId,
            clientResult.FirstTokenLatencyMilliseconds,
            clientResult.TotalLatencyMilliseconds,
            showReasoningWarning,
            profile.Name,
            fellBackFromProfileName,
            clientResult.FinishReason,
            maxTokensUsed,
            clientResult.DirectConnectionProxy,
            profile.Id);
    }

    public async Task SetStarredAsync(long recordId, bool starred, CancellationToken cancellationToken)
    {
        try
        {
            await _historyRepository.SetStarredAsync(recordId, starred, cancellationToken);
        }
        catch (Exception exception)
        {
            Logger.Error("数据库", "更新收藏状态失败。", exception);
            throw;
        }
    }

    public void CancelCurrent()
    {
        lock (_requestSync)
        {
            _currentCancellation?.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelCurrent();
        lock (_requestSync)
        {
            _currentCancellation?.Dispose();
            _currentCancellation = null;
        }

        _client.Dispose();
    }

    private static void ValidateProfile(ApiProfile profile)
    {
        // 本机服务（本地 Ollama 等）不需要 Key。以前这里一律要求 Key，
        // 结果向导里测试通过、保存成功，真划词时却永远报"还没配置翻译服务"。
        if (string.IsNullOrWhiteSpace(profile.BaseUrl)
            || string.IsNullOrWhiteSpace(profile.Model)
            || (string.IsNullOrWhiteSpace(profile.ApiKey) && !ApiProfile.IsLocalAddress(profile.BaseUrl)))
        {
            throw new TranslationConfigurationException(
                TranslationConfigurationError.MissingApiKey);
        }

        if (!Uri.TryCreate(profile.BaseUrl.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new TranslationConfigurationException(
                TranslationConfigurationError.InvalidBaseUrl);
        }
    }

    /// <summary>
    /// 缓存 key 必须覆盖<b>所有会改变模型输出的输入</b>。漏掉任何一项，表现都是同一种：
    /// 用户改完设置再划同一句话，拿到的还是改之前的译文，而界面上完全看不出这是缓存。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这里直接哈希 <see cref="BuiltPrompt.SystemPrompt"/> 与 <see cref="BuiltPrompt.UserContent"/>，
    /// 而不是"模式 Id + 目标语言 + 一个手工维护的提示词版本号"。后者漏了两类改动：
    /// </para>
    /// <list type="number">
    /// <item>用户在设置页直接编辑模式的提示词正文 —— 模式 Id 不变，
    /// <see cref="AppSettings.CurrentPromptVersion"/> 是编译期常量也不变，于是命中旧缓存。</item>
    /// <item>开发者改了内置提示词却忘了把版本号加一 —— 这个契约以前只靠 BuiltInModes 顶上一行注释维持。</item>
    /// </list>
    /// <para>
    /// 提示词正文进了 key 之后，这两种情况都自动生效。temperature 和额外请求参数同样纳入：
    /// 它们也改变输出，「关闭思考模式」那条路以前靠显式 <c>bypassCache</c> 单独绕开，现在不必再特事特办。
    /// </para>
    /// <para>老缓存不会被删，只是不再命中；历史记录和收藏完整保留。</para>
    /// </remarks>
    private static string CreateCacheKey(
        string sourceText,
        BuiltPrompt prompt,
        ApiProfile profile)
    {
        string material = string.Join('\u0001',
            NormalizeServiceAddress(profile.BaseUrl),
            sourceText,
            prompt.SystemPrompt,
            prompt.UserContent,
            prompt.TargetLanguage,
            prompt.Mode.Id,
            profile.Model.Trim(),
            profile.Temperature.ToString("R", CultureInfo.InvariantCulture),
            profile.ExtraRequestBody?.Trim() ?? string.Empty);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash);
    }

    private static string NormalizeServiceAddress(string baseUrl)
    {
        string address = baseUrl.Trim().TrimEnd('/');
        // 主机名不区分大小写，路径仍保留大小写；同名模型在不同接口上的结果必须隔离。
        return Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant()
                + uri.AbsolutePath.TrimEnd('/') + uri.Query
            : address;
    }

    private async Task<TranslationRecord?> TryReadCacheAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _historyRepository.FindCacheAndTouchAsync(cacheKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Logger.Info("数据库", "读取翻译缓存已取消。");
            throw;
        }
        catch (Exception exception)
        {
            Logger.Error("数据库", "读取翻译缓存失败，继续请求翻译。", exception);
            return null;
        }
    }

    private async Task<long?> TryInsertHistoryAsync(
        TranslationRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _historyRepository.InsertAsync(record, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Logger.Info("数据库", "保存翻译历史已取消。");
            throw;
        }
        catch (Exception exception)
        {
            Logger.Error("数据库", "保存翻译历史失败，翻译结果仍然可用。", exception);
            return null;
        }
    }

    private CancellationTokenSource ReplaceCurrentCancellation(CancellationToken cancellationToken)
    {
        lock (_requestSync)
        {
            _currentCancellation?.Cancel();
            _currentCancellation?.Dispose();
            _currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return _currentCancellation;
        }
    }

    private void ReleaseCurrentCancellation(CancellationTokenSource cancellation)
    {
        lock (_requestSync)
        {
            if (ReferenceEquals(_currentCancellation, cancellation))
            {
                _currentCancellation = null;
                cancellation.Dispose();
            }
        }
    }
}
