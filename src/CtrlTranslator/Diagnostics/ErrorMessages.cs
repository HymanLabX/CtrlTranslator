using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
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
    OpenLog,
    DisableThinking
}

/// <param name="ProfileId">
/// 出错的那个档案。「立即关闭思考模式」这类修复按钮要改的是它，不一定是当前档案。
/// </param>
internal sealed record UserFacingError(
    string Message,
    ErrorAction Action,
    string? ActionText,
    string? ProfileId = null);

internal static class ErrorMessages
{
    /// <summary>
    /// 把异常翻译成给用户看的话（SPEC §9.1）。
    /// </summary>
    /// <param name="model">调用方眼里的模型名；异常上挂着实际失败档案的信息时以后者为准。</param>
    /// <param name="thinkingDisabled">调用方眼里"思考模式关没关"；同上，以异常上挂的为准。</param>
    public static UserFacingError FromException(
        Exception exception,
        string model,
        bool thinkingDisabled)
    {
        // 自动降级之后，最后报错的未必是"当前档案"：主档案 Key 失效、备用档案又超时，
        // 这时提示里该说的是备用那个的模型名和它自己的思考开关。
        if (exception.Data[FailureContext.ModelKey] is string failedModel)
        {
            model = failedModel;
        }

        if (exception.Data[FailureContext.ThinkingDisabledKey] is bool failedThinkingDisabled)
        {
            thinkingDisabled = failedThinkingDisabled;
        }

        UserFacingError error = Build(exception, model, thinkingDisabled);
        return exception.Data[FailureContext.ProfileIdKey] is string profileId
            ? error with { ProfileId = profileId }
            : error;
    }

    private static UserFacingError Build(
        Exception exception,
        string model,
        bool thinkingDisabled)
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
            if (!thinkingDisabled)
            {
                return new UserFacingError(
                    // 秒数一律用实际生效的那个值：划词和深度翻译的上限不一样（15 秒 / 30 秒），
                    // 写死任何一个数字，从另一条路报出来就是假消息。
                    $"翻译超时了（等了 {firstTokenTimeout.Seconds} 秒）。原因是你的「关闭思考模式」开关关着，模型会先思考很久再回答，而本软件的等待上限是 {firstTokenTimeout.Seconds} 秒。想用思考模式的话，它基本不适合拿来做翻译。",
                    ErrorAction.DisableThinking,
                    "立即关闭思考模式");
            }

            if (firstTokenTimeout.ResponseHeadersLatencyMilliseconds is long headersLatency
                && headersLatency < 2_000)
            {
                return new UserFacingError(
                    $"已经连上服务商了（响应只用了 {headersLatency} 毫秒），但它迟迟不返回内容。这基本是服务商自己出了状况，不是你的配置问题。可以去服务商的状态页看看，或者过一会儿再试。",
                    ErrorAction.Retry,
                    "重试");
            }

            return new UserFacingError(
                $"等了 {firstTokenTimeout.Seconds} 秒还没有任何回应。可能网络很慢或服务商在限流，也可能你填的模型不适合拿来做翻译。稍等一下再试一次通常就好了。",
                ErrorAction.Diagnostics,
                "一键诊断");
        }

        if (exception is OverallTimeoutException overallTimeout)
        {
            return new UserFacingError(
                $"翻译超时（{overallTimeout.Seconds} 秒）。文本可能太长，或者服务商这会儿很慢。",
                ErrorAction.Retry,
                "重试");
        }

        if (exception is EmptyTranslationException)
        {
            return new UserFacingError(
                "模型没有返回译文。选中的如果是纯标点、纯数字或表情，本来就没什么可翻的；"
                + "换一段文字再试试。",
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
            return FromConnectionFailure(exception);
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

    /// <summary>
    /// 连不上服务器的几种情况分开说。「检查一下网络」对用户最没用 ——
    /// 网络好好的、浏览器也能上网时，他根本不知道该查什么。
    /// </summary>
    private static UserFacingError FromConnectionFailure(Exception exception)
    {
        // 代理端口连不上时客户端已经自动改成直连试过一次了；走到这里说明直连也不通。
        if (exception.Data[FailureContext.ProxyRefusedKey] is string proxy)
        {
            return new UserFacingError(
                $"你电脑上设置的代理（{proxy}）没有打开，已经自动改为直连，但直连也连不上服务器。"
                + "如果这个服务商需要代理才能访问，把代理软件打开再试；否则检查一下网络。",
                ErrorAction.Diagnostics,
                "一键诊断");
        }

        if (exception is HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError })
        {
            return new UserFacingError(
                "代理软件开着，但它连不上翻译服务。检查一下代理软件里的节点是否正常；"
                + "这个服务商如果不需要代理（比如 DeepSeek、智谱、通义），也可以在代理软件里把它设为直连。",
                ErrorAction.Diagnostics,
                "一键诊断");
        }

        if (HasInner<AuthenticationException>(exception))
        {
            return new UserFacingError(
                "和服务器的加密连接没有建立成功。常见原因是代理软件或公司网络拦截了 HTTPS，"
                + "或者电脑的系统时间不对。稍后再试；反复出现的话点「一键诊断」看看卡在哪。",
                ErrorAction.Diagnostics,
                "一键诊断");
        }

        if (exception is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
        {
            return new UserFacingError(
                "找不到服务器（域名解析失败）。检查一下网络是否连上，以及服务地址有没有拼错。",
                ErrorAction.Diagnostics,
                "一键诊断");
        }

        return new UserFacingError(
            "连不上服务器。检查一下网络；如果这个服务需要代理，请确认代理已开启。",
            ErrorAction.Diagnostics,
            "一键诊断");
    }

    private static UserFacingError FromApiException(ApiHttpException exception, string model)
    {
        int code = (int)exception.StatusCode;
        return exception.Kind switch
        {
            ApiErrorKind.Authentication => new UserFacingError(
                "API Key 无效或已过期。请检查是不是填错了、或者前后多了空格。",
                ErrorAction.Settings,
                "去设置"),
            ApiErrorKind.Balance => new UserFacingError(
                "账户余额不足或额度已用完，需要到服务商网站充值。",
                ErrorAction.Settings,
                "去设置"),
            ApiErrorKind.ModelNotFound => new UserFacingError(
                $"模型名「{model}」在这个服务商不存在。点「去设置」里的「拉取可用模型」看看有哪些能用。",
                ErrorAction.Settings,
                "去设置"),
            ApiErrorKind.EndpointNotFound => new UserFacingError(
                $"服务地址可能填错了：服务商说找不到这个接口（错误码 {code}）。"
                + "对照服务商文档检查一下地址，通常以 /v1 之类的版本号结尾。",
                ErrorAction.Settings,
                "去设置"),
            ApiErrorKind.RegionBlocked => new UserFacingError(
                "这个服务商不对你所在的地区开放。换一家国内服务商，或者开代理后再试。",
                ErrorAction.Settings,
                "去设置"),
            ApiErrorKind.ContentFiltered => new UserFacingError(
                "服务商的内容审核拒绝翻译这段文字。换一段文字，或者在设置里换一家服务商试试。",
                ErrorAction.None,
                null),
            ApiErrorKind.RateLimited => new UserFacingError(
                "请求太频繁被限流了，等几秒钟再试一次。",
                ErrorAction.Retry,
                "重试"),
            ApiErrorKind.ServerError => new UserFacingError(
                $"服务商那边出问题了（错误码 {code}），不是你的配置问题，过一会儿再试。",
                ErrorAction.Retry,
                "重试"),
            _ => new UserFacingError(
                $"服务商拒绝了这次请求（错误码 {code}）。最常见的原因是「额外请求参数」里有它不认识的参数，"
                + "换了服务商之后记得点一下对应的预设按钮。"
                + (exception.ProviderMessage is { } said ? $"服务商的原话：{said}" : string.Empty),
                ErrorAction.Settings,
                "去设置")
        };
    }

    private static bool HasInner<T>(Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 翻译失败时挂在异常 <see cref="Exception.Data"/> 上的补充信息的键名。
/// 只放字符串和布尔值：这两种类型放进 Data 永远安全。
/// </summary>
internal static class FailureContext
{
    /// <summary>实际失败的那个档案的 Id。</summary>
    public const string ProfileIdKey = "CtrlTranslator.FailedProfileId";

    /// <summary>实际失败的那个档案的模型名。</summary>
    public const string ModelKey = "CtrlTranslator.FailedModel";

    /// <summary>实际失败的那个档案有没有关闭思考模式。</summary>
    public const string ThinkingDisabledKey = "CtrlTranslator.FailedThinkingDisabled";

    /// <summary>连不上的系统代理地址（如 127.0.0.1:7897）。有这个键说明已经自动直连重试过。</summary>
    public const string ProxyRefusedKey = "CtrlTranslator.ProxyRefused";
}
