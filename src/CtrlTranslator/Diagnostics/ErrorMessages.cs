using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using CtrlTranslator.Core;
using CtrlTranslator.Translation;
using Microsoft.Data.Sqlite;

namespace CtrlTranslator.Diagnostics;

internal enum ErrorAction
{
    None,
    Settings,
    Retry,
    Diagnostics,
    OpenLog
}

internal sealed record UserFacingError(string Message, ErrorAction Action, string? ActionText);

internal static class ErrorMessages
{
    public static UserFacingError FromException(Exception exception, string model)
    {
        if (exception is TranslationConfigurationException configurationException)
        {
            return configurationException.Error == TranslationConfigurationError.InvalidBaseUrl
                ? new UserFacingError(
                    "服务地址格式不对。正确的样子像这样：https://api.deepseek.com/v1",
                    ErrorAction.Settings,
                    "去设置")
                : new UserFacingError(
                    "还没配置翻译服务，点下面的按钮花一分钟设置好就能用了。",
                    ErrorAction.Settings,
                    "去设置");
        }

        if (exception is ApiHttpException apiException)
        {
            return FromApiException(apiException, model);
        }

        if (exception is FirstTokenTimeoutException firstTokenTimeout)
        {
            return new UserFacingError(
                $"等了 {firstTokenTimeout.Seconds} 秒还没有任何回应。可能网络很慢，也可能你填的是「推理模型」——那种模型会先思考很久，不适合用来划词翻译。",
                ErrorAction.Diagnostics,
                "一键诊断");
        }

        if (exception is OverallTimeoutException overallTimeout)
        {
            return new UserFacingError(
                $"翻译超时（{overallTimeout.Seconds} 秒）。文本可能太长，或者网络不稳定。",
                ErrorAction.Retry,
                "重试");
        }

        if (exception is TranslationJsonException)
        {
            return new UserFacingError(
                "服务器返回了看不懂的内容，这个地址可能不是 OpenAI 兼容接口。",
                ErrorAction.Diagnostics,
                "一键诊断");
        }

        if (exception is HttpRequestException or SocketException)
        {
            return new UserFacingError(
                "连不上服务器。检查一下网络；如果这个服务需要代理，请确认代理已开启。",
                ErrorAction.Diagnostics,
                "一键诊断");
        }

        if (exception is ClipboardUnavailableException)
        {
            return new UserFacingError(
                "剪贴板正被其他程序占用，稍等一下再试。",
                ErrorAction.None,
                null);
        }

        if (exception is SqliteException)
        {
            return new UserFacingError(
                "历史记录保存失败，但翻译结果不受影响。详情见日志。",
                ErrorAction.OpenLog,
                "打开日志");
        }

        return new UserFacingError(
            "翻译没有完成。详情已经写入日志，请稍后再试。",
            ErrorAction.Retry,
            "重试");
    }

    private static UserFacingError FromApiException(ApiHttpException exception, string model)
    {
        int code = (int)exception.StatusCode;
        string body = exception.ResponseBody;
        if (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new UserFacingError(
                "API Key 无效或已过期。请检查是不是填错了、或者前后多了空格。",
                ErrorAction.Settings,
                "去设置");
        }

        if (exception.StatusCode == HttpStatusCode.PaymentRequired
            || ContainsAny(body, "insufficient", "balance", "quota"))
        {
            return new UserFacingError(
                "账户余额不足或额度已用完，需要到服务商网站充值。",
                ErrorAction.Settings,
                "去设置");
        }

        if (exception.StatusCode == HttpStatusCode.NotFound
            || (body.Contains("model", StringComparison.OrdinalIgnoreCase)
                && ContainsAny(body, "not found", "does not exist")))
        {
            return new UserFacingError(
                $"模型名「{model}」在这个服务商不存在。点「去设置」里的「拉取可用模型」看看有哪些能用。",
                ErrorAction.Settings,
                "去设置");
        }

        if (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new UserFacingError(
                "请求太频繁被限流了，等几秒钟再试一次。",
                ErrorAction.Retry,
                "重试");
        }

        if (code >= 500)
        {
            return new UserFacingError(
                $"服务商那边出问题了（错误码 {code}），不是你的配置问题，过一会儿再试。",
                ErrorAction.Retry,
                "重试");
        }

        return new UserFacingError(
            $"翻译服务返回了错误（错误码 {code}）。详情已经写入日志。",
            ErrorAction.Retry,
            "重试");
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
