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
    bool ShowReasoningWarning);

/// <summary>
/// 串联提示词、缓存和网络请求；新请求开始时会先取消旧请求。
/// </summary>
internal sealed class TranslationService : IDisposable
{
    private readonly object _requestSync = new();
    private readonly Func<AppSettings> _settingsProvider;
    private readonly HistoryRepository _historyRepository;
    private readonly ITranslationClient _client;
    private CancellationTokenSource? _currentCancellation;
    private bool _disposed;

    public TranslationService(
        Func<AppSettings> settingsProvider,
        HistoryRepository historyRepository)
    {
        _settingsProvider = settingsProvider;
        _historyRepository = historyRepository;
        _client = new OpenAiCompatibleClient(settingsProvider);
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
            ValidateConfiguration(settings);
            BuiltPrompt prompt = PromptBuilder.Build(sourceText, settings);
            string cacheKey = CreateCacheKey(
                sourceText,
                prompt.TargetLanguage,
                prompt.Mode.Id,
                settings.Model);

            if (!settings.PrivacyMode && !bypassCache)
            {
                TranslationRecord? cached = await TryReadCacheAsync(cacheKey, ownCancellation.Token);
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
                        ShowReasoningWarning: false);
                }
            }

            if (bypassCache)
            {
                Logger.Info("翻译", "本次重译已绕过缓存，将使用最新请求参数。");
            }

            var request = new TranslationRequest(
                settings.BaseUrl.Trim(),
                settings.ApiKey.Trim(),
                settings.Model.Trim(),
                prompt.SystemPrompt,
                prompt.UserContent,
                settings.Temperature,
                settings.MaxTokens,
                settings.RequestTimeoutSeconds,
                settings.FirstTokenTimeoutSeconds,
                settings.ExtraRequestBody);

            TranslationClientResult clientResult = await _client.TranslateAsync(
                request,
                progress,
                ownCancellation.Token);
            Logger.Info(
                "翻译",
                $"翻译完成：首字 {clientResult.FirstTokenLatencyMilliseconds}ms，总耗时 {clientResult.TotalLatencyMilliseconds}ms。");

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
                    Model = settings.Model.Trim(),
                    IsWord = prompt.IsWord,
                    Starred = false,
                    AppName = appName,
                    LatencyMilliseconds = clientResult.TotalLatencyMilliseconds,
                    CreatedAt = DateTime.Now
                };
                recordId = await TryInsertHistoryAsync(record, ownCancellation.Token);
            }

            bool showReasoningWarning = !settings.SuppressReasoningWarning
                && ModelHeuristics.ShouldWarnAboutReasoningModel(
                    settings.Model,
                    clientResult.SawReasoning,
                    clientResult.FirstTokenLatencyMilliseconds);
            return new TranslationResult(
                sourceText,
                clientResult.Text,
                prompt.TargetLanguage,
                prompt.Mode.Id,
                settings.Model.Trim(),
                prompt.IsWord,
                FromCache: false,
                Starred: false,
                recordId,
                clientResult.FirstTokenLatencyMilliseconds,
                clientResult.TotalLatencyMilliseconds,
                showReasoningWarning);
        }
        finally
        {
            ReleaseCurrentCancellation(ownCancellation);
        }
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

    private static void ValidateConfiguration(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl)
            || string.IsNullOrWhiteSpace(settings.ApiKey)
            || string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new TranslationConfigurationException(
                TranslationConfigurationError.MissingApiKey);
        }

        if (!Uri.TryCreate(settings.BaseUrl.Trim(), UriKind.Absolute, out Uri? uri)
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
