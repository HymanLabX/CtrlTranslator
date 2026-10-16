using System.Net;
using CtrlTranslator.Settings;

namespace CtrlTranslator.Translation;

internal interface ITranslationClient : IDisposable
{
    Task<TranslationClientResult> TranslateAsync(
        TranslationRequest request,
        IProgress<TranslationStreamUpdate>? progress,
        CancellationToken cancellationToken);
}

/// <param name="MaxRequestTimeoutSeconds">
/// 本次请求允许的超时上限。客户端会把 <paramref name="RequestTimeoutSeconds"/> 夹到这个值以内 ——
/// 上限存在的意义是拦住手工编辑配置填进来的离谱数值，但它<b>不能是全局常量</b>：
/// 划词浮窗的上限（15 秒）套到深度翻译上，会让后者每次都在生成到一半时被掐断（已踩坑）。
/// 默认值就是浮窗那套，所以所有既有调用点行为不变。
/// </param>
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
    string ExtraRequestBody,
    int MaxRequestTimeoutSeconds = AppSettings.DefaultRequestTimeoutSeconds,
    int MaxFirstTokenTimeoutSeconds = AppSettings.DefaultFirstTokenTimeoutSeconds);

internal sealed record TranslationStreamUpdate(
    string Delta,
    long ElapsedMilliseconds,
    bool SawReasoning);

/// <param name="PromptTokens">
/// 尽力而为：流式响应里默认<b>不一定</b>带 usage，也不是所有服务商都支持
/// <c>stream_options.include_usage</c>（Ollama、部分网关会因未知键直接 400）。
/// 这里只解析对方主动送来的 usage，拿不到就是 null —— 不为了统计去冒险改请求体。
/// </param>
/// <param name="FinishReason">
/// 流里最后一次出现的 <c>finish_reason</c>。<c>length</c> 表示写到 max_tokens 上限被截断了，
/// <c>content_filter</c> 表示被服务商的内容审核截断了 —— 这两种译文都是不完整的。
/// </param>
/// <param name="DirectConnectionProxy">
/// 非空表示系统代理（这里记的就是它的地址）连不上，本次是自动改成直连完成的。
/// </param>
internal sealed record TranslationClientResult(
    string Text,
    long FirstTokenLatencyMilliseconds,
    long TotalLatencyMilliseconds,
    bool SawReasoning,
    long? PromptTokens = null,
    long? CompletionTokens = null,
    string? FinishReason = null,
    string? DirectConnectionProxy = null)
{
    /// <summary>译文是否被截断（达到输出上限或被内容审核截停）。</summary>
    public bool IsTruncated =>
        string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase)
        || string.Equals(FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase);
}

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

/// <remarks>
/// 消息里带上状态码和服务商自己说的那句话。以前 Message 是默认的
/// "Exception of type ... was thrown"，日志里永远看不到服务商到底说了什么，
/// 浮窗上那句「详情已经写入日志」打开一看却没有详情。
/// </remarks>
internal sealed class ApiHttpException(
    HttpStatusCode statusCode,
    string responseBody) : Exception(
        $"服务商返回 HTTP {(int)statusCode}：{ApiErrorClassifier.ExtractMessage(responseBody, 300) ?? "（没有正文）"}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string ResponseBody { get; } = responseBody;

    public ApiErrorKind Kind { get; } = ApiErrorClassifier.Classify(statusCode, responseBody);

    /// <summary>服务商原话，已压成一行并截短；取不到时为 null。</summary>
    public string? ProviderMessage { get; } = ApiErrorClassifier.ExtractMessage(responseBody);
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

/// <summary>
/// 流收完了、格式也没问题，但模型一个字都没吐。
/// </summary>
/// <remarks>
/// 必须和 <see cref="TranslationJsonException"/> 分开：选中纯标点、纯数字、纯表情时
/// 模型合法地返回空串，如果沿用"响应格式看不懂"那句提示，用户会跑去查服务地址和接口兼容性，
/// 而真实原因只是这段内容没什么可翻的。
/// </remarks>
internal sealed class EmptyTranslationException()
    : Exception("翻译服务没有返回任何内容。");
