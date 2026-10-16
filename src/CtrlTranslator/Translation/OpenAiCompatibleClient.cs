using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
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
    /// <summary>走系统代理设置的客户端（默认路径）。</summary>
    private static readonly HttpClient SharedHttpClient = CreateHttpClient(useProxy: true);

    /// <summary>
    /// 不走代理的客户端，只在"系统代理端口根本连不上"时兜底用。
    /// </summary>
    /// <remarks>
    /// 实际日志里出现过：代理软件（127.0.0.1:7897）没开，但 Windows 的系统代理设置还留着，
    /// 于是所有翻译都报"连不上服务器"——而 DeepSeek 这类国内服务本来就不需要代理。
    /// 只在代理端口<b>拒绝连接</b>时直连（这是瞬间就能判定的），代理慢、代理报错都不绕开它。
    /// </remarks>
    private static readonly HttpClient DirectHttpClient = CreateHttpClient(useProxy: false);

    private readonly Func<ApiProfile?>? _warmupProfileProvider;
    private readonly CancellationTokenSource _warmupCancellation = new();
    private readonly System.Threading.Timer? _warmupTimer;
    private int _isWarmingUp;
    private bool _disposed;

    /// <summary>上一次预热的结果（0 = 还没预热过，1 = 成功，2 = 失败）。只在状态变化时写日志。</summary>
    private int _lastWarmupState;
    private string? _lastWarmupError;

    /// <param name="warmupProfileProvider">
    /// 传 null 表示这是一次性用途（诊断、连接测试），不启动后台预热定时器。
    /// </param>
    public OpenAiCompatibleClient(Func<ApiProfile?>? warmupProfileProvider = null)
    {
        _warmupProfileProvider = warmupProfileProvider;
        if (warmupProfileProvider is not null)
        {
            _warmupTimer = new System.Threading.Timer(
                static state => ((OpenAiCompatibleClient)state!).StartWarmup(),
                this,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromMinutes(4));
        }
    }

    public async Task<TranslationClientResult> TranslateAsync(
        TranslationRequest request,
        IProgress<TranslationStreamUpdate>? progress,
        CancellationToken cancellationToken)
    {
        // 上限由调用方随请求给出，不再写死成浮窗那一套（见 TranslationRequest 的说明）。
        int overallTimeoutSeconds = Math.Clamp(
            request.RequestTimeoutSeconds,
            1,
            Math.Max(1, request.MaxRequestTimeoutSeconds));
        int firstTokenTimeoutSeconds = Math.Clamp(
            request.FirstTokenTimeoutSeconds,
            1,
            Math.Max(1, request.MaxFirstTokenTimeoutSeconds));
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
        // 用 -1 而不是 0 当"还没收到首字"的哨兵：本地模型（llama.cpp / LM Studio）
        // 的第一个 chunk 完全可能落在 stopwatch.ElapsedMilliseconds == 0 上，
        // 用 0 当哨兵会把首字延迟记成后面某个 chunk 的时间，进而误判成"推理模型"。
        long firstTokenLatency = -1;
        long? responseHeadersLatency = null;
        var sawReasoning = false;
        long? promptTokens = null;
        long? completionTokens = null;
        string? finishReason = null;
        var resultBuilder = new StringBuilder();

        try
        {
            (HttpResponseMessage sentResponse, string? refusedProxy) = await SendAsync(
                () => CreateRequest(request, HttpMethod.Post, "chat/completions"),
                HttpCompletionOption.ResponseHeadersRead,
                firstTokenCancellation.Token);
            using HttpResponseMessage response = sentResponse;
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
                CancellationToken readToken = firstTokenLatency < 0
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

                StreamChunk chunk = ParseChunk(payload);
                sawReasoning |= chunk.SawReasoning;
                promptTokens = chunk.PromptTokens ?? promptTokens;
                completionTokens = chunk.CompletionTokens ?? completionTokens;
                finishReason = chunk.FinishReason ?? finishReason;
                if (chunk.Delta.Length == 0)
                {
                    continue;
                }

                if (firstTokenLatency < 0)
                {
                    firstTokenLatency = stopwatch.ElapsedMilliseconds;
                }

                resultBuilder.Append(chunk.Delta);
                progress?.Report(new TranslationStreamUpdate(
                    chunk.Delta,
                    stopwatch.ElapsedMilliseconds,
                    sawReasoning));
            }

            if (resultBuilder.Length == 0)
            {
                // 收到过合法 chunk 却一个字都没有 = 模型真的返回了空译文（纯标点、纯数字时常见）；
                // 一个 chunk 都没收到才说明对方压根不是 OpenAI 兼容接口。两者给的提示必须不同。
                throw firstTokenLatency < 0 && !sawReasoning && finishReason is null
                    ? new TranslationJsonException()
                    : new EmptyTranslationException();
            }

            if (finishReason is not null
                && !string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning("翻译请求", $"本次输出的结束原因是「{finishReason}」，译文可能不完整。");
            }

            return new TranslationClientResult(
                resultBuilder.ToString(),
                Math.Max(0, firstTokenLatency),
                stopwatch.ElapsedMilliseconds,
                sawReasoning,
                promptTokens,
                completionTokens,
                finishReason,
                refusedProxy);
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
        (HttpResponseMessage sentResponse, _) = await SendAsync(
            () => CreateModelsRequest(baseUrl, apiKey),
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        using HttpResponseMessage response = sentResponse;
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
        (HttpResponseMessage sentResponse, _) = await SendAsync(
            () => CreateModelsRequest(baseUrl, apiKey),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        using HttpResponseMessage response = sentResponse;
        return response.StatusCode;
    }

    /// <summary>
    /// 这个地址此刻会走哪个系统代理；不走代理时返回 null。诊断用它告诉用户"翻译是经过代理出去的"。
    /// </summary>
    internal static Uri? FindSystemProxy(Uri target)
    {
        try
        {
            IWebProxy proxy = HttpClient.DefaultProxy;
            if (proxy.IsBypassed(target))
            {
                return null;
            }

            Uri? proxyUri = proxy.GetProxy(target);
            return proxyUri is null || proxyUri == target ? null : proxyUri;
        }
        catch (Exception exception)
        {
            // 读代理设置失败不影响翻译本身（HttpClient 会自己再读一遍），这里只是拿不到说明。
            Logger.Warning("网络", $"读取系统代理设置失败：{exception.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _warmupTimer?.Dispose();
        _warmupCancellation.Cancel();
        _warmupCancellation.Dispose();
    }

    private static HttpClient CreateHttpClient(bool useProxy)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            // 不设上限的话，DNS 解析或代理探测卡住时连接会挂很久，
            // 拖慢的不只是这一次请求，还会让人觉得整个界面在卡顿。
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = useProxy
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>
    /// 先按系统代理设置发；代理端口拒绝连接时，换成直连再发一次。
    /// </summary>
    /// <returns>响应，以及"连不上的那个代理"（没有发生直连兜底时为 null）。</returns>
    /// <remarks>
    /// 请求用工厂现造：一个 <see cref="HttpRequestMessage"/> 只能发一次，重试必须是新对象。
    /// </remarks>
    private static async Task<(HttpResponseMessage Response, string? RefusedProxy)> SendAsync(
        Func<HttpRequestMessage> createRequest,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = createRequest();
        string? refusedProxy = null;
        try
        {
            return (await SharedHttpClient.SendAsync(request, completionOption, cancellationToken), null);
        }
        catch (HttpRequestException exception)
            when (TryGetRefusedProxy(request.RequestUri, exception, out refusedProxy))
        {
            Logger.Warning("网络", $"系统代理 {refusedProxy} 连不上（端口拒绝连接），本次自动改为直连。");
        }

        using HttpRequestMessage directRequest = createRequest();
        try
        {
            return (await DirectHttpClient.SendAsync(directRequest, completionOption, cancellationToken), refusedProxy);
        }
        catch (HttpRequestException exception)
        {
            // 让报错文案知道"代理没开、直连也不通"，而不是笼统一句"连不上服务器"。
            exception.Data[FailureContext.ProxyRefusedKey] = refusedProxy;
            throw;
        }
    }

    /// <summary>
    /// 判断这次失败是不是"系统代理端口拒绝连接"。
    /// </summary>
    /// <remarks>
    /// 配了代理时，TCP 连接的对象就是代理本身，所以"连接被拒绝"只可能是代理没在监听 ——
    /// 典型场景就是代理软件退出了，但 Windows 里的系统代理设置还留着。
    /// </remarks>
    private static bool TryGetRefusedProxy(Uri? target, HttpRequestException exception, out string? proxy)
    {
        proxy = null;
        if (target is null || exception.HttpRequestError != HttpRequestError.ConnectionError)
        {
            return false;
        }

        bool refused = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                refused = true;
                break;
            }
        }

        if (!refused || FindSystemProxy(target) is not { } proxyUri)
        {
            return false;
        }

        proxy = proxyUri.Authority;
        return true;
    }

    private static HttpRequestMessage CreateModelsRequest(string baseUrl, string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
        AddAuthorization(message, apiKey);
        return message;
    }

    /// <summary>
    /// Key 为空时不发鉴权头。本地 Ollama 这类服务本来就不要 Key，
    /// 发一个空的 <c>Bearer </c> 头反而可能被某些网关当成格式错误拒掉。
    /// </summary>
    private static void AddAuthorization(HttpRequestMessage message, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    private static HttpRequestMessage CreateRequest(
        TranslationRequest request,
        HttpMethod method,
        string relativePath)
    {
        var message = new HttpRequestMessage(
            method,
            $"{request.BaseUrl.TrimEnd('/')}/{relativePath}");
        AddAuthorization(message, request.ApiKey);

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

            // 合并之后再写回，保证这三个键不会被用户填的额外参数覆盖掉。
            // 尤其是 stream：本客户端只会按 SSE 解析响应，一旦被改成 false，
            // 服务端返回的整块 JSON 里一行 data: 都没有，循环空转到超时，
            // 最后报一句"返回内容不是 OpenAI 兼容格式"——完全指不到真因。
            if (body.TryGetValue("stream", out object? streamValue)
                && streamValue is JsonElement { ValueKind: JsonValueKind.False })
            {
                Logger.Warning(
                    "翻译请求",
                    "额外请求参数里的 \"stream\": false 已被忽略：浮窗依赖流式输出才能边收边显示译文。");
            }

            body["stream"] = true;
            body["model"] = request.Model;
            body["messages"] = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserContent }
            };

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

    private readonly record struct StreamChunk(
        string Delta,
        bool SawReasoning,
        long? PromptTokens,
        long? CompletionTokens,
        string? FinishReason);

    private static StreamChunk ParseChunk(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;

        // 有的服务商（OpenRouter 等聚合网关最常见）HTTP 200 已经回了，流到一半才说出错：
        // data: {"error": {"code": 502, "message": "..."}}。以前这种块被当成"空 chunk"跳过，
        // 最后报一句"返回内容看不懂"，把用户引向去查接口地址。现在按 API 错误处理。
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("error", out JsonElement error)
            && (error.ValueKind == JsonValueKind.Object
                || (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))))
        {
            throw new ApiHttpException(ReadStreamErrorStatus(error), payload);
        }

        (long? promptTokens, long? completionTokens) = ReadUsage(root);

        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return new StreamChunk(string.Empty, false, promptTokens, completionTokens, null);
        }

        JsonElement choice = choices[0];
        string? finishReason = choice.TryGetProperty("finish_reason", out JsonElement reason)
            && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;

        if (!choice.TryGetProperty("delta", out JsonElement deltaObject)
            || deltaObject.ValueKind != JsonValueKind.Object)
        {
            return new StreamChunk(string.Empty, false, promptTokens, completionTokens, finishReason);
        }

        bool sawReasoning = HasNonEmptyProperty(deltaObject, "reasoning_content")
            || HasNonEmptyProperty(deltaObject, "reasoning");
        string delta = deltaObject.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : string.Empty;
        return new StreamChunk(delta, sawReasoning, promptTokens, completionTokens, finishReason);
    }

    /// <summary>
    /// 流内错误块里带了 HTTP 风格的数字错误码就用它（OpenRouter 会给 402、429、502 这类），
    /// 否则按 502 处理：流已经开始了才出错，本质上是服务端那头的问题。
    /// 具体是余额、模型还是内容审核，<see cref="ApiErrorClassifier"/> 会再看报文本身。
    /// </summary>
    private static HttpStatusCode ReadStreamErrorStatus(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out JsonElement code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out int status)
            && status is >= 400 and <= 599)
        {
            return (HttpStatusCode)status;
        }

        return HttpStatusCode.BadGateway;
    }

    /// <summary>
    /// 只读对方主动送来的 usage。多数服务商会在最后一个 chunk 里带上，没带就当没有 ——
    /// 为了拿这个数字去加 <c>stream_options</c> 参数不值得，未知键可能让整次翻译 400。
    /// </summary>
    private static (long? PromptTokens, long? CompletionTokens) ReadUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("usage", out JsonElement usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (ReadTokenCount(usage, "prompt_tokens"), ReadTokenCount(usage, "completion_tokens"));
    }

    private static long? ReadTokenCount(JsonElement usage, string propertyName)
    {
        return usage.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long count)
                ? count
                : null;
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

    /// <summary>
    /// 预热：定时 GET 一次 /models，让连接池里一直有一条握好手的 TLS 连接。
    /// </summary>
    /// <remarks>
    /// 日志只在<b>状态变化</b>时写。以前每 4 分钟写一行"预热完成"，
    /// 实际日志里六千多行有九成以上是它，真正的问题全被淹没了。
    /// </remarks>
    private async Task WarmupAsync()
    {
        try
        {
            // 只预热当前档案。备用档案平时不该被打扰 —— 那会白白产生请求，也可能触发对方的限流。
            ApiProfile? profile = _warmupProfileProvider?.Invoke();
            if (profile is null
                || string.IsNullOrWhiteSpace(profile.BaseUrl)
                || !Uri.TryCreate(profile.BaseUrl.Trim(), UriKind.Absolute, out _))
            {
                return;
            }

            string baseUrl = profile.BaseUrl.Trim();
            string apiKey = profile.ApiKey.Trim();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_warmupCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            (HttpResponseMessage sentResponse, string? refusedProxy) = await SendAsync(
                () => CreateModelsRequest(baseUrl, apiKey),
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            using HttpResponseMessage response = sentResponse;
            if (Interlocked.Exchange(ref _lastWarmupState, 1) != 1)
            {
                Logger.Info(
                    "翻译预热",
                    $"连接预热成功（HTTP {(int)response.StatusCode}"
                    + (refusedProxy is null ? "）" : $"，代理 {refusedProxy} 没开，走的直连）")
                    + "。之后只在状态变化时记录。");
            }

            _lastWarmupError = null;
        }
        catch (OperationCanceledException) when (_warmupCancellation.IsCancellationRequested)
        {
            // 程序退出时取消，不值得记录。
        }
        catch (Exception exception)
        {
            // 预热失败是常态（电脑刚从睡眠醒来、网络抖一下），同一种失败只记一次。
            string description = Logger.Describe(exception);
            int previousState = Interlocked.Exchange(ref _lastWarmupState, 2);
            if (previousState != 2 || !string.Equals(description, _lastWarmupError, StringComparison.Ordinal))
            {
                Logger.Warning("翻译预热", $"连接预热失败，不影响正常翻译：{description}");
            }

            _lastWarmupError = description;
        }
        finally
        {
            Volatile.Write(ref _isWarmingUp, 0);
        }
    }
}
