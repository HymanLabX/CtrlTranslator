using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace CtrlTranslator.Translation;

/// <summary>
/// 一次失败该不该切备用、以及要不要把这个档案标记为失效。
/// </summary>
public enum FailoverVerdict
{
    /// <summary>不降级，直接把错误抛给用户。超时属于这一类 —— 已经等了十秒再换一家只会更慢。</summary>
    Fatal,

    /// <summary>瞬时故障（429 / 5xx / 网络不通）：本次切备用，但<b>不</b>标记失效，下次照常先试它。</summary>
    Transient,

    /// <summary>持久故障（Key 失效 / 余额不足 / 模型不存在 / 配置不全）：切备用并标记失效，等用户动手修。</summary>
    Persistent
}

public readonly record struct FailoverDecision(FailoverVerdict Verdict, string? PersistentReason);

/// <summary>
/// 降级判据。核心原则：<b>只在"秒级失败"时降级</b> —— 那些 1 秒内就能判定的错误，
/// 切完备用总耗时跟正常翻译差不多；而超时意味着用户已经盯着转圈等了十秒，再换一家是雪上加霜。
/// </summary>
public static class FailoverPolicy
{
    public static FailoverDecision Classify(Exception exception, string model)
    {
        // 配置不完整属于"你不动手它永远好不了"，归持久故障；只有一个档案时上层不会真的打标记。
        if (exception is TranslationConfigurationException configurationException)
        {
            return Persistent(configurationException.Error == TranslationConfigurationError.InvalidBaseUrl
                ? "服务地址格式不对"
                : "配置不完整（地址 / Key / 模型没填全）");
        }

        if (exception is ApiHttpException apiException)
        {
            return ClassifyApiException(apiException, model);
        }

        // 连不上服务器：可能是对方挂了，也可能是自己断网。两者都会自行恢复，不打持久标记。
        if (exception is HttpRequestException or SocketException)
        {
            return new FailoverDecision(FailoverVerdict.Transient, null);
        }

        // 首字超时 / 整体超时 / 返回内容不是 OpenAI 兼容格式：都不是秒级失败，按决策不降级。
        return new FailoverDecision(FailoverVerdict.Fatal, null);
    }

    private static FailoverDecision ClassifyApiException(ApiHttpException exception, string model)
    {
        int code = (int)exception.StatusCode;
        string body = exception.ResponseBody;

        if (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Persistent("API Key 无效或已过期");
        }

        if (exception.StatusCode == HttpStatusCode.PaymentRequired
            || ContainsAny(body, "insufficient", "balance", "quota"))
        {
            return Persistent("账户余额不足或额度已用完");
        }

        if (exception.StatusCode == HttpStatusCode.NotFound
            || (body.Contains("model", StringComparison.OrdinalIgnoreCase)
                && ContainsAny(body, "not found", "does not exist")))
        {
            return Persistent($"模型「{model}」在这个服务商不存在");
        }

        if (exception.StatusCode == HttpStatusCode.TooManyRequests || code >= 500)
        {
            return new FailoverDecision(FailoverVerdict.Transient, null);
        }

        // 其余 4xx 含义不明。宁可只切一次备用，也不要凭一个看不懂的状态码把档案永久判死。
        return new FailoverDecision(FailoverVerdict.Transient, null);
    }

    private static FailoverDecision Persistent(string reason) =>
        new(FailoverVerdict.Persistent, reason);

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
