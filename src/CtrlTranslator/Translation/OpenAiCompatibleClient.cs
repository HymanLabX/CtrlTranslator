using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;

namespace CtrlTranslator.Translation;

/// <summary>
/// OpenAI 兼容接口客户端。翻译请求始终使用流式响应，预热请求独立运行且不影响翻译。
/// </summary>
internal sealed class OpenAiCompatibleClient : ITranslationClient
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly Func<AppSettings> _settingsProvider;
    private readonly CancellationTokenSource _warmupCancellation = new();
    private readonly System.Threading.Timer _warmupTimer;
    private int _isWarmingUp;
    private bool _disposed;

    public OpenAiCompatibleClient(Func<AppSettings> settingsProvider)
    {
        _settingsProvider = settingsProvider;
        _warmupTimer = new System.Threading.Timer(
            static state => ((OpenAiCompatibleClient)state!).StartWarmup(),
            this,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMinutes(4));
    }

    public async Task<TranslationClientResult> TranslateAsync(
        TranslationRequest request,
        IProgress<TranslationStreamUpdate>? progress,
        CancellationToken cancellationToken)
    {
        using var overallCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCancellation.CancelAfter(TimeSpan.FromSeconds(request.RequestTimeoutSeconds));
        using var firstTokenCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            overallCancellation.Token);
        firstTokenCancellation.CancelAfter(TimeSpan.FromSeconds(request.FirstTokenTimeoutSeconds));

        var stopwatch = Stopwatch.StartNew();
        long firstTokenLatency = 0;
        var sawReasoning = false;
        var resultBuilder = new StringBuilder();

        try
        {
            using var httpRequest = CreateRequest(request, HttpMethod.Post, "chat/completions");
            using HttpResponseMessage response = await SharedHttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                firstTokenCancellation.Token);

            if (!response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync(overallCancellation.Token);
                throw new ApiHttpException(response.StatusCode, responseBody);
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(overallCancellation.Token);
            using var reader = new StreamReader(responseStream);
            while (true)
            {
                CancellationToken readToken = firstTokenLatency == 0
                    ? firstTokenCancellation.Token
                    : overallCancellation.Token;
                string? line = await reader.ReadLineAsync(readToken);
                if (line is null)
                {
                    break;
                }

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string payload = line[5..].Trim();
                if (payload.Length == 0 || payload == "[DONE]")
                {
                    if (payload == "[DONE]")
                    {
                        break;
                    }

                    continue;
                }

                ParseChunk(payload, out string delta, out bool chunkSawReasoning);
                sawReasoning |= chunkSawReasoning;
                if (delta.Length == 0)
                {
                    continue;
                }

                if (firstTokenLatency == 0)
                {
                    firstTokenLatency = stopwatch.ElapsedMilliseconds;
                }

                resultBuilder.Append(delta);
                progress?.Report(new TranslationStreamUpdate(
                    delta,
                    stopwatch.ElapsedMilliseconds,
                    sawReasoning));
            }

            if (resultBuilder.Length == 0)
            {
                throw new TranslationJsonException();
            }

            return new TranslationClientResult(
                resultBuilder.ToString(),
                firstTokenLatency,
                stopwatch.ElapsedMilliseconds,
                sawReasoning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.Info("翻译请求", "翻译请求已由用户操作取消。");
            throw;
        }
        catch (OperationCanceledException) when (overallCancellation.IsCancellationRequested)
        {
            Logger.Warning("翻译请求", $"翻译请求超过整体超时 {request.RequestTimeoutSeconds} 秒。");
            throw new OverallTimeoutException(request.RequestTimeoutSeconds);
        }
        catch (OperationCanceledException) when (firstTokenCancellation.IsCancellationRequested)
        {
            Logger.Warning("翻译请求", $"翻译请求超过首字超时 {request.FirstTokenTimeoutSeconds} 秒。");
            throw new FirstTokenTimeoutException(request.FirstTokenTimeoutSeconds);
        }
        catch (JsonException exception)
        {
            Logger.Error("翻译请求", "解析流式响应失败。", exception);
            throw new TranslationJsonException(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _warmupTimer.Dispose();
        _warmupCancellation.Cancel();
        _warmupCancellation.Dispose();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static HttpRequestMessage CreateRequest(
        TranslationRequest request,
        HttpMethod method,
        string relativePath)
    {
        var message = new HttpRequestMessage(
            method,
            $"{request.BaseUrl.TrimEnd('/')}/{relativePath}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        if (method == HttpMethod.Post)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = request.SystemPrompt },
                    new { role = "user", content = request.UserContent }
                },
                ["temperature"] = request.Temperature,
                ["max_tokens"] = request.MaxTokens,
                ["stream"] = true
            };
            MergeExtraRequestBody(body, request.ExtraRequestBody);
            message.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
        }

        return message;
    }

    private static void MergeExtraRequestBody(
        Dictionary<string, object?> body,
        string? extraRequestBody)
    {
        if (string.IsNullOrWhiteSpace(extraRequestBody))
        {
            Logger.Info("翻译请求", "额外请求参数为空，已跳过合并。");
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(extraRequestBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Logger.Warning("翻译请求", "额外请求参数不是 JSON 对象，已忽略，不影响本次翻译。");
                return;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                // 浅合并：只覆盖请求体顶层同名键，嵌套对象不递归合并。
                body[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException exception)
        {
            Logger.Warning(
                "翻译请求",
                $"额外请求参数不是合法 JSON，已忽略，不影响本次翻译：{exception.Message}");
        }
    }

    private static void ParseChunk(string payload, out string delta, out bool sawReasoning)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("delta", out JsonElement deltaObject))
        {
            delta = string.Empty;
            sawReasoning = false;
            return;
        }

        sawReasoning = HasNonEmptyProperty(deltaObject, "reasoning_content")
            || HasNonEmptyProperty(deltaObject, "reasoning");
        delta = deltaObject.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : string.Empty;
    }

    private static bool HasNonEmptyProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString());
    }

    private void StartWarmup()
    {
        if (Interlocked.Exchange(ref _isWarmingUp, 1) != 0)
        {
            return;
        }

        _ = WarmupAsync();
    }

    private async Task WarmupAsync()
    {
        try
        {
            AppSettings settings = _settingsProvider();
            if (string.IsNullOrWhiteSpace(settings.BaseUrl)
                || string.IsNullOrWhiteSpace(settings.ApiKey)
                || !Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _))
            {
                return;
            }

            var request = new TranslationRequest(
                settings.BaseUrl,
                settings.ApiKey,
                settings.Model,
                string.Empty,
                string.Empty,
                settings.Temperature,
                settings.MaxTokens,
                settings.RequestTimeoutSeconds,
                settings.FirstTokenTimeoutSeconds,
                settings.ExtraRequestBody);
            using HttpRequestMessage message = CreateRequest(request, HttpMethod.Get, "models");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_warmupCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using HttpResponseMessage response = await SharedHttpClient.SendAsync(message, timeout.Token);
            Logger.Info("翻译预热", $"连接预热完成，状态码 {(int)response.StatusCode}。");
        }
        catch (OperationCanceledException exception)
        {
            Logger.Info("翻译预热", $"连接预热已取消：{exception.Message}");
        }
        catch (Exception exception)
        {
            Logger.Error("翻译预热", "连接预热失败，不影响正常翻译。", exception);
        }
        finally
        {
            Volatile.Write(ref _isWarmingUp, 0);
        }
    }
}
