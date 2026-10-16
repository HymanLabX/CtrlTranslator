using System.Net;
using System.Text.Json;

namespace CtrlTranslator.Translation;

/// <summary>服务商返回的 HTTP 错误归到哪一类。浮窗报错和自动降级都按这个分类走。</summary>
internal enum ApiErrorKind
{
    /// <summary>Key 无效、过期或没权限。</summary>
    Authentication,

    /// <summary>余额不足、欠费、额度用完。</summary>
    Balance,

    /// <summary>模型名在这家服务商不存在。</summary>
    ModelNotFound,

    /// <summary>接口地址本身找不到（404 且报文里没提模型）：多半是 Base URL 填错了路径。</summary>
    EndpointNotFound,

    /// <summary>服务商不对当前地区开放。</summary>
    RegionBlocked,

    /// <summary>服务商的内容审核拒绝了这段文字。</summary>
    ContentFiltered,

    /// <summary>限流。</summary>
    RateLimited,

    /// <summary>服务商自己的 5xx 故障。</summary>
    ServerError,

    /// <summary>其余 4xx：参数不被接受等。最常见的原因是额外请求参数里有对方不认的键。</summary>
    BadRequest
}

/// <summary>
/// 把服务商的 HTTP 错误归类。<see cref="FailoverPolicy"/>（要不要降级、要不要标记失效）
/// 和 <see cref="CtrlTranslator.Diagnostics.ErrorMessages"/>（给用户看什么话）共用这一份判断。
/// </summary>
/// <remarks>
/// <para>
/// 以前两边各写一套，判断条件早就分了叉：降级那边已经吸取教训、不再拿裸子串
/// "insufficient / balance / quota" 判余额，报错文案这边却还在用 ——
/// 结果 "insufficient permissions"、"max_tokens exceeds the per-request quota" 这类参数错误
/// 会被说成"余额不足，去充值"。合并成一份之后，两边不可能再说两种话。
/// </para>
/// <para>
/// 下面的报文特征都来自各家的真实返回，<b>只认足够具体的说法</b>，宁可漏判成"其他错误"，
/// 也不要把用户指向错误的方向：
/// DeepSeek 余额不足是 402 "Insufficient Balance"、模型写错是 400 "Model Not Exist"；
/// 智谱欠费是 429 + "您的账户已欠费"、模型写错是 "模型不存在"；
/// 通义欠费是 400 "Arrearage"；硅基流动余额不足是 403 "account balance is insufficient"；
/// Kimi 额度用完是 429 "exceeded_current_quota"；OpenRouter 余额不足是 402 "Insufficient credits"。
/// </para>
/// </remarks>
internal static class ApiErrorClassifier
{
    private static readonly string[] BalanceSignals =
    [
        "insufficient_quota",
        "exceeded your current quota",
        "exceeded_current_quota",
        "insufficient balance",
        "insufficient_balance",
        "balance is insufficient",
        "insufficient account balance",
        "insufficient credits",
        "credit balance is too low",
        "arrearage",
        "in good standing",
        "check your plan and billing",
        "billing details",
        "欠费",
        "余额不足",
        "额度已用完",
        "额度不足"
    ];

    private static readonly string[] RegionSignals =
    [
        "unsupported_country_region_territory",
        "unsupported_country",
        "not available in your country",
        "not available in your region",
        "region is not supported",
        "country, region, or territory not supported"
    ];

    private static readonly string[] ContentFilterSignals =
    [
        "content_filter",
        "datainspectionfailed",
        "data_inspection_failed",
        "inappropriate content",
        "sensitive content",
        "敏感内容",
        "不安全或敏感"
    ];

    /// <summary>和 "model" 同时出现才算"模型不存在"的说法。单独出现可能是在说接口或参数。</summary>
    private static readonly string[] ModelMissingPhrases =
    [
        "not found",
        "does not exist",
        "not exist",
        "not_found",
        "not a valid model",
        "invalid model",
        "unknown model",
        "no such model"
    ];

    public static ApiErrorKind Classify(HttpStatusCode statusCode, string? responseBody)
    {
        string body = responseBody ?? string.Empty;
        int code = (int)statusCode;

        // 余额要排在最前：硅基流动的余额不足是 403，智谱的欠费是 429，
        // 按状态码先判就会被说成"Key 无效"或"请求太频繁"。
        if (statusCode == HttpStatusCode.PaymentRequired || ContainsAny(body, BalanceSignals))
        {
            return ApiErrorKind.Balance;
        }

        if (ContainsAny(body, RegionSignals))
        {
            return ApiErrorKind.RegionBlocked;
        }

        if (ContainsAny(body, ContentFilterSignals))
        {
            return ApiErrorKind.ContentFiltered;
        }

        if ((code is 400 or 403 or 404 or 422) && IsModelMissing(body))
        {
            return ApiErrorKind.ModelNotFound;
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ApiErrorKind.Authentication;
        }

        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return ApiErrorKind.EndpointNotFound;
        }

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return ApiErrorKind.RateLimited;
        }

        return code >= 500 ? ApiErrorKind.ServerError : ApiErrorKind.BadRequest;
    }

    /// <summary>
    /// 从错误响应里取出服务商自己说的那句话（<c>error.message</c> 之类），压成一行、截短。
    /// 取不到结构化字段就截一段原文。用来写日志，以及在"其他错误"时给用户一个能拿去求助的原话。
    /// </summary>
    public static string? ExtractMessage(string? responseBody, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        string? message = TryReadJsonMessage(responseBody) ?? responseBody;
        message = string.Join(' ', message.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        if (message.Length == 0)
        {
            return null;
        }

        return message.Length <= maxLength ? message : message[..maxLength] + "…";
    }

    private static string? TryReadJsonMessage(string body)
    {
        string trimmed = body.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object
                    && ReadFirstString(error, "message", "msg", "detail") is { } nested)
                {
                    return nested;
                }
            }

            return ReadFirstString(root, "message", "msg", "detail", "error_msg");
        }
        catch (JsonException)
        {
            // 不是合法 JSON（比如网关返回的 HTML 错误页），调用方会退回截一段原文。
            return null;
        }
    }

    private static string? ReadFirstString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool IsModelMissing(string body)
    {
        if (body.Contains("模型不存在", StringComparison.Ordinal)
            || body.Contains("model_not_found", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return body.Contains("model", StringComparison.OrdinalIgnoreCase)
            && ContainsAny(body, ModelMissingPhrases);
    }

    private static bool ContainsAny(string value, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (value.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
