using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
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
        int overallTimeoutSeconds = Math.Clamp(
            request.RequestTimeoutSeconds,
            1,
            AppSettings.DefaultRequestTimeoutSeconds);
        int firstTokenTimeoutSeconds = Math.Clamp(
            request.FirstTokenTimeoutSeconds,
            1,
            AppSettings.DefaultFirstTokenTimeoutSeconds);
        if (firstTokenTimeoutSeconds >= overallTimeoutSeconds)
        {
            // 首字超时必须先于整体超时，手工编辑配置也不能破坏这个判定顺序。
            firstTokenTimeoutSeconds = Math.Max(1, overallTimeoutSeconds - 1);
        }

        using var overallCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCancellation.CancelAfter(TimeSpan.FromSeconds(overallTimeoutSeconds));
        using var firstTokenCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            overallCancellation.Token);
        firstTokenCancellation.CancelAfter(TimeSpan.FromSeconds(firstTokenTimeoutSeconds));

        var stopwatch = Stopwatch.StartNew();
        long firstTokenLatency = 0;
        long? responseHeadersLatency = null;
        var sawReasoning = false;
        var resultBuilder = new StringBuilder();

        try
        {
            using var httpRequest = CreateRequest(request, HttpMethod.Post, "chat/completions");
            using HttpResponseMessage response = await SharedHttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                firstTokenCancellation.Token);
            responseHeadersLatency = stopwatch.ElapsedMilliseconds;

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
            Logger.Warning("翻译请求", $"翻译请求超过整体超时 {overallTimeoutSeconds} 秒。");
            throw new OverallTimeoutException(overallTimeoutSeconds);
        }
        catch (OperationCanceledException) when (firstTokenCancellation.IsCancellationRequested)
        {
            string headersDescription = responseHeadersLatency.HasValue
                ? $"，响应头耗时 {responseHeadersLatency.Value}ms"
                : "，超时前未收到响应头";
            Logger.Warning(
                "翻译请求",
                $"翻译请求超过首字超时 {firstTokenTimeoutSeconds} 秒{headersDescription}。");
            throw new FirstTokenTimeoutException(
                firstTokenTimeoutSeconds,
                responseHeadersLatency);
        }
        catch (JsonException exception)
        {
            Logger.Error("翻译请求", "解析流式响应失败。", exception);
            throw new TranslationJsonException(exception);
        }
    }

    internal static async Task<IReadOnlyList<string>> FetchModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl.TrimEnd('/')}/models");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using HttpResponseMessage response = await SharedHttpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiHttpException(response.StatusCode, responseBody);
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new TranslationJsonException();
        }

        return data.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static async Task<HttpStatusCode> ProbeModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl.TrimEnd('/')}/models");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using HttpResponseMessage response = await SharedHttpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return response.StatusCode;
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
