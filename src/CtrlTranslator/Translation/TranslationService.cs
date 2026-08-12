using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CtrlTranslator.Data;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;

namespace CtrlTranslator.Translation;

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
    string? FellBackFromProfileName);

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
            IReadOnlyList<ApiProfile> chain = settings.BuildFailoverChain();

            // 只有一个档案时标记失效毫无意义（没有备用可切），只会让用户对着一个红色感叹号发懵。
            bool canMarkFailed = settings.Profiles.Count > 1;
            Exception? lastException = null;

            for (int index = 0; index < chain.Count; index++)
            {
                ApiProfile profile = chain[index];
                ownCancellation.Token.ThrowIfCancellationRequested();

                try
                {
                    return await TranslateWithProfileAsync(
                        profile,
                        index > 0 ? chain[0].Name : null,
                        sourceText,
                        appName,
                        prompt,
                        settings,
                        progress,
                        bypassCache,
                        ownCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastException = exception;
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

            throw lastException
                ?? new InvalidOperationException("没有任何可用的服务商档案。");
        }
        finally
        {
            ReleaseCurrentCancellation(ownCancellation);
        }
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

        // 缓存 key 里带的是本次实际要用的模型名。降级到别的模型时，写入和读取用的是同一把
        // key，绝不会出现"主档案恢复后却命中了备用模型译文"这种串台。
        string cacheKey = CreateCacheKey(
            sourceText,
            prompt.TargetLanguage,
            prompt.Mode.Id,
            profile.Model);

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
                    FellBackFromProfileName: null);
            }
        }

        if (bypassCache)
        {
            Logger.Info("翻译", "本次重译已绕过缓存，将使用最新请求参数。");
        }

        var request = new TranslationRequest(
            profile.BaseUrl.Trim(),
            profile.ApiKey.Trim(),
            profile.Model.Trim(),
            prompt.SystemPrompt,
            prompt.UserContent,
            profile.Temperature,
            profile.MaxTokens,
            profile.RequestTimeoutSeconds,
            profile.FirstTokenTimeoutSeconds,
            profile.ExtraRequestBody);

        TranslationClientResult clientResult = await _client.TranslateAsync(
            request,
            progress,
            cancellationToken);
        Logger.Info(
            "翻译",
            $"翻译完成（档案「{profile.Name}」）：首字 {clientResult.FirstTokenLatencyMilliseconds}ms，"
            + $"总耗时 {clientResult.TotalLatencyMilliseconds}ms。");

        long? recordId = null;
        if (!settings.PrivacyMode)
        {
            var record = new TranslationRecord
            {
                CacheKey = cacheKey,
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
            fellBackFromProfileName);
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
        if (string.IsNullOrWhiteSpace(profile.BaseUrl)
            || string.IsNullOrWhiteSpace(profile.ApiKey)
            || string.IsNullOrWhiteSpace(profile.Model))
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

    // 提示词版本必须参与缓存 key：提示词一改，同一段原文该出的译文就变了。
    // 不带版本的话，改完提示词再划同一句话仍会命中旧缓存，看上去像"改动没生效"（已踩坑）。
    // 老缓存不会被删，只是不再命中，历史记录和收藏都完整保留。
    private static string CreateCacheKey(
        string sourceText,
        string targetLanguage,
        string modeId,
        string model)
    {
        string promptVersion = AppSettings.CurrentPromptVersion.ToString(CultureInfo.InvariantCulture);
        string material = string.Join('\u0001', sourceText, targetLanguage, modeId, model, promptVersion);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash);
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
