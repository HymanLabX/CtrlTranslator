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

internal sealed record TranslationClientResult(
    string Text,
    long FirstTokenLatencyMilliseconds,
    long TotalLatencyMilliseconds,
    bool SawReasoning);

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

internal sealed class FirstTokenTimeoutException(int seconds) : TimeoutException
{
    public int Seconds { get; } = seconds;
}

internal sealed class OverallTimeoutException(int seconds) : TimeoutException
{
    public int Seconds { get; } = seconds;
}

internal sealed class TranslationJsonException(Exception? innerException = null)
    : Exception("无法解析翻译服务响应。", innerException);
