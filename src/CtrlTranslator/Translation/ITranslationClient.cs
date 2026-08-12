using System.Net;

namespace CtrlTranslator.Translation;

internal interface ITranslationClient : IDisposable
{
    Task<TranslationClientResult> TranslateAsync(
        TranslationRequest request,
        IProgress<TranslationStreamUpdate>? progress,
        CancellationToken cancellationToken);
}

internal sealed record TranslationRequest(
    string BaseUrl,
    string ApiKey,
    string Model,
    string SystemPrompt,
    string UserContent,
    double Temperature,
    int MaxTokens,
    int RequestTimeoutSeconds,
    int FirstTokenTimeoutSeconds,
    string ExtraRequestBody);

internal sealed record TranslationStreamUpdate(
    string Delta,
    long ElapsedMilliseconds,
    bool SawReasoning);

/// <param name="PromptTokens">
/// 尽力而为：流式响应里默认<b>不一定</b>带 usage，也不是所有服务商都支持
/// <c>stream_options.include_usage</c>（Ollama、部分网关会因未知键直接 400）。
/// 这里只解析对方主动送来的 usage，拿不到就是 null —— 不为了统计去冒险改请求体。
/// </param>
internal sealed record TranslationClientResult(
    string Text,
    long FirstTokenLatencyMilliseconds,
    long TotalLatencyMilliseconds,
    bool SawReasoning,
    long? PromptTokens = null,
    long? CompletionTokens = null);

internal enum TranslationConfigurationError
{
    MissingApiKey,
    InvalidBaseUrl
}

internal sealed class TranslationConfigurationException(
    TranslationConfigurationError error) : Exception
{
    public TranslationConfigurationError Error { get; } = error;
}

internal sealed class ApiHttpException(
    HttpStatusCode statusCode,
    string responseBody) : Exception
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string ResponseBody { get; } = responseBody;
}

internal sealed class FirstTokenTimeoutException(
    int seconds,
    long? responseHeadersLatencyMilliseconds) : TimeoutException
{
    public int Seconds { get; } = seconds;

    public long? ResponseHeadersLatencyMilliseconds { get; } = responseHeadersLatencyMilliseconds;
}

internal sealed class OverallTimeoutException(int seconds) : TimeoutException
{
    public int Seconds { get; } = seconds;
}

internal sealed class TranslationJsonException(Exception? innerException = null)
    : Exception("无法解析翻译服务响应。", innerException);
