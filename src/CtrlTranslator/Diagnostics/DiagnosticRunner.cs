using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Principal;
using System.Speech.Synthesis;
using CtrlTranslator.Data;
using CtrlTranslator.Settings;
using CtrlTranslator.Translation;

namespace CtrlTranslator.Diagnostics;

public enum DiagnosticStatus
{
    Passed,
    Warning,
    Failed
}

public sealed record DiagnosticResult(
    int Number,
    string Name,
    DiagnosticStatus Status,
    string Detail,
    long DurationMilliseconds)
{
    public string StatusSymbol => Status switch
    {
        DiagnosticStatus.Passed => "✓",
        DiagnosticStatus.Warning => "⚠",
        _ => "✗"
    };
}

/// <summary>
/// 按规格顺序对<b>某一个服务商档案</b>执行十二项诊断，每完成一项就报告一次结果。
/// </summary>
/// <remarks>
/// M5 把它从独立诊断窗口下放到了设置页里每个档案旁的「测试」按钮：入口从四步缩到一步，
/// 而且多档案下"到底哪个挂了"变成一目了然。
/// </remarks>
public sealed class DiagnosticRunner
{
    private readonly ApiProfile _profile;
    private readonly AppSettings _settings;
    private readonly HistoryRepository _repository;
    private readonly Func<bool> _hookInstalledProvider;

    public DiagnosticRunner(
        ApiProfile profile,
        AppSettings settings,
        HistoryRepository repository,
        Func<bool> hookInstalledProvider)
    {
        // 用快照跑：测试期间用户可能还在设置页里继续敲字段，读到改了一半的值会得出莫名其妙的结论。
        _profile = profile.Clone();
        _settings = settings;
        _repository = repository;
        _hookInstalledProvider = hookInstalledProvider;
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(
        IProgress<DiagnosticResult> progress,
        CancellationToken cancellationToken)
    {
        List<DiagnosticResult> results = [];
        Uri? serviceUri = null;
        TranslationClientResult? helloResult = null;

        await RunCheckAsync(1, "配置完整性", async token =>
        {
            await Task.CompletedTask;
            if (string.IsNullOrWhiteSpace(_profile.BaseUrl) || string.IsNullOrWhiteSpace(_profile.Model))
            {
                return Failed("服务地址或模型名尚未填写。");
            }

            if (string.IsNullOrWhiteSpace(_profile.ApiKey))
            {
                return ApiProfile.IsLocalAddress(_profile.BaseUrl)
                    ? Passed("服务地址和模型名已填写；这是本机服务，不需要 API Key。")
                    : Failed("还没有填写 API Key。");
            }

            return Passed("服务地址、API Key 和模型名均已填写。");
        });

        await RunCheckAsync(2, "服务地址格式", async token =>
        {
            await Task.CompletedTask;
            if (!Uri.TryCreate(_profile.BaseUrl, UriKind.Absolute, out serviceUri)
                || serviceUri.Scheme is not ("http" or "https"))
            {
                return Failed("服务地址必须以 http:// 或 https:// 开头。");
            }

            bool hasVersionPath = serviceUri.AbsolutePath.Contains("/v1", StringComparison.OrdinalIgnoreCase)
                || serviceUri.AbsolutePath.Contains("/v4", StringComparison.OrdinalIgnoreCase);
            return hasVersionPath
                ? Passed("服务地址格式正确，并包含接口版本路径。")
                : Warning("地址格式正确，但没有看到常见的 /v1 或 /v4 版本路径，请向服务商核对。");
        });

        // 翻译请求会不会经过系统代理。DNS 和连接两项要据此判断：
        // 走代理时域名由代理去解析、连接对象是代理本身，直接拿目标地址去测会得出错误结论 ——
        // 实际日志里出现过：代理软件没开，这两项照样打勾，到第 5 项才莫名其妙地失败。
        Uri? systemProxy = null;

        await RunCheckAsync(3, "DNS 解析", async token =>
        {
            if (serviceUri is null)
            {
                return Failed("服务地址无效，无法解析域名。");
            }

            systemProxy = OpenAiCompatibleClient.FindSystemProxy(serviceUri);
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(serviceUri.Host, token);
                if (addresses.Length > 0)
                {
                    return Passed($"已解析到 {addresses.Length} 个网络地址。");
                }
            }
            catch (SocketException) when (systemProxy is not null)
            {
                // 走代理时本机解析不了不要紧，代理会替我们解析。
            }

            return systemProxy is not null
                ? Warning($"本机没能解析这个域名，但翻译请求经过系统代理（{systemProxy.Authority}），由代理负责解析，通常不影响使用。")
                : Failed("没有解析到服务器地址，请检查网络或域名。");
        });

        await RunCheckAsync(4, "网络连接", async token =>
        {
            if (serviceUri is null)
            {
                return Failed("服务地址无效，无法建立连接。");
            }

            if (systemProxy is not null)
            {
                return await CheckThroughProxyAsync(serviceUri, systemProxy, token);
            }

            (bool connected, string detail) = await TryDirectConnectAsync(serviceUri, token);
            if (!connected)
            {
                return Failed(detail);
            }

            return serviceUri.Scheme == "http"
                ? Warning(detail + "该地址使用未加密的 HTTP，没有 TLS 保护（本机服务可以忽略）。")
                : Passed(detail);
        });

        await RunCheckAsync(5, "身份认证", async token =>
        {
            EnsureApiConfiguration();
            HttpStatusCode status = await OpenAiCompatibleClient.ProbeModelsAsync(
                _profile.BaseUrl, _profile.ApiKey, token);
            if (status == HttpStatusCode.NotFound)
            {
                return Warning("服务商不提供 /models 接口，已跳过本项；下一项会直接验证模型。");
            }

            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Failed("API Key 无效或已过期，请检查是否填错。");
            }

            return (int)status is >= 200 and < 300
                ? Passed($"鉴权成功（HTTP {(int)status}）。")
                : Failed($"服务商返回 HTTP {(int)status}，请核对接口地址和 API Key。");
        });

        await RunCheckAsync(6, "模型可用性", async token =>
        {
            EnsureApiConfiguration();
            using OpenAiCompatibleClient client = new();
            try
            {
                // 给 16 个 token 而不是 1 个：会"先想一下"的模型，1 个 token 刚好用在思考上，
                // 一个字都吐不出来，以前这会被误判成模型不可用。
                TranslationClientResult result = await client.TranslateAsync(
                    CreateRequest("只回复一个字：好", maxTokens: 16), null, token);
                return Passed($"模型「{_profile.Model}」可用，返回“{Shorten(result.Text, 12)}”。");
            }
            catch (EmptyTranslationException)
            {
                // 服务商正常应答了，只是这次没吐文字 —— 模型本身是存在、可用的。
                return Passed($"模型「{_profile.Model}」可用（这次测试没有返回文字，不影响翻译）。");
            }
        });

        await RunCheckAsync(7, "翻译链路", async token =>
        {
            EnsureApiConfiguration();
            using OpenAiCompatibleClient client = new();
            helloResult = await client.TranslateAsync(
                CreateRequest("Hello, world!", Math.Min(_profile.MaxTokens, 128)), null, token);
            return Passed(
                $"翻译成功：“{Shorten(helloResult.Text, 60)}”；首字 {helloResult.FirstTokenLatencyMilliseconds}ms，总耗时 {helloResult.TotalLatencyMilliseconds}ms。");
        });

        await RunCheckAsync(8, "推理模型适配", async token =>
        {
            await Task.CompletedTask;
            if (helloResult is null)
            {
                return Failed("翻译链路未通过，暂时无法判断推理模型适配情况。");
            }

            bool warning = ModelHeuristics.ShouldWarnAboutReasoningModel(
                _profile.Model,
                helloResult.SawReasoning,
                helloResult.FirstTokenLatencyMilliseconds);
            return warning
                ? Warning("模型表现出推理模型特征，首字可能较慢；建议关闭思考模式或换用快速模型。"): Passed("模型名称、首字耗时和响应字段均未发现推理模型风险。");
        });

        await RunCheckAsync(9, "语音引擎", async token =>
        {
            await Task.CompletedTask;
            (int total, int english) = CountInstalledVoices();
            if (total == 0)
            {
                return Failed("没有检测到可用的 Windows 语音，请在系统设置中安装语音包。");
            }

            return english > 0
                ? Passed($"检测到 {total} 个可用语音，其中 {english} 个英语语音。")
                : Warning($"检测到 {total} 个可用语音，但没有英语语音；英文朗读可能不可用。");
        });

        await RunCheckAsync(10, "历史数据库", async token =>
        {
            await _repository.RunReadWriteDiagnosticAsync(token);
            return Passed("写入、读回和删除测试均成功。");
        });

        await RunCheckAsync(11, "全局快捷键", async token =>
        {
            await Task.CompletedTask;
            return _hookInstalledProvider() && _settings.Enabled
                ? Passed("全局键盘钩子已安装，翻译开关已开启。")
                : Failed(_settings.Enabled
                    ? "全局键盘钩子未安装，请重启应用后再试。"
                    : "翻译总开关当前已暂停，请从托盘菜单开启。");
        });

        await RunCheckAsync(12, "管理员权限", async token =>
        {
            await Task.CompletedTask;
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator)
                ? Passed("当前以管理员权限运行，可在管理员程序中取词。")
                : Warning("当前不是管理员权限；普通程序可正常取词，但管理员程序中可能无法触发。");
        });

        return results;

        async Task RunCheckAsync(
            int number,
            string name,
            Func<CancellationToken, Task<(DiagnosticStatus Status, string Detail)>> check)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stopwatch stopwatch = Stopwatch.StartNew();
            DiagnosticStatus status;
            string detail;
            try
            {
                (status, detail) = await check(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.Info("一键诊断", $"第 {number} 项“{name}”检查已取消。");
                throw;
            }
            catch (Exception exception)
            {
                Logger.Error("一键诊断", $"第 {number} 项“{name}”检查失败。", exception);
                status = DiagnosticStatus.Failed;
                detail = ToHumanDiagnosticMessage(exception);
            }

            DiagnosticResult result = new(number, name, status, detail, stopwatch.ElapsedMilliseconds);
            results.Add(result);
            progress.Report(result);
        }
    }

    public string BuildReport(IEnumerable<DiagnosticResult> results)
    {
        string safeKey = RedactApiKey(_profile.ApiKey);
        string header = $"""
            CtrlTranslator 诊断报告
            生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}
            服务商档案：{_profile.Name}
            服务地址：{_profile.BaseUrl}
            API Key：{safeKey}
            模型：{_profile.Model}

            """;
        return header + string.Join(Environment.NewLine, results.Select(result =>
            $"{result.Number:00}. {result.StatusSymbol} {result.Name}（{result.DurationMilliseconds}ms）— {result.Detail}"));
    }

    private TranslationRequest CreateRequest(string text, int maxTokens) => new(
        _profile.BaseUrl,
        _profile.ApiKey,
        _profile.Model,
        "你是翻译诊断助手，只输出简短答案。",
        text,
        _profile.Temperature,
        maxTokens,
        _profile.RequestTimeoutSeconds,
        _profile.FirstTokenTimeoutSeconds,
        _profile.ExtraRequestBody);

    private void EnsureApiConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_profile.Model)
            || !Uri.TryCreate(_profile.BaseUrl, UriKind.Absolute, out _)
            || (string.IsNullOrWhiteSpace(_profile.ApiKey) && !ApiProfile.IsLocalAddress(_profile.BaseUrl)))
        {
            throw new InvalidOperationException("翻译服务配置尚未填写完整。");
        }
    }

    /// <summary>
    /// 走系统代理时的连接检查：先看代理本身连不连得上；连不上再试直连，
    /// 因为翻译遇到"代理端口拒绝连接"时会自动改走直连（负责人 2026-10 决定）。
    /// </summary>
    private static async Task<(DiagnosticStatus, string)> CheckThroughProxyAsync(
        Uri serviceUri,
        Uri proxy,
        CancellationToken cancellationToken)
    {
        string proxyName = proxy.Authority;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            using TcpClient tcp = new();
            await tcp.ConnectAsync(proxy.Host, proxy.Port, timeout.Token);
            return Passed($"翻译请求经过系统代理（{proxyName}），代理软件在运行。");
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            Logger.Info("一键诊断", $"系统代理 {proxyName} 连不上：{exception.Message}");
        }

        (bool connected, string directDetail) = await TryDirectConnectAsync(serviceUri, cancellationToken);
        return connected
            ? Warning($"你设置了系统代理（{proxyName}），但它连不上，代理软件可能没开。直连可以连上服务器，翻译会自动改为直连。")
            : Failed($"你设置了系统代理（{proxyName}），但它连不上，代理软件可能没开；直连也连不上（{directDetail}）。把代理软件打开再试。");
    }

    /// <summary>不经过代理，直接和服务器建立 TCP（以及 HTTPS 的 TLS）连接。</summary>
    private static async Task<(bool Connected, string Detail)> TryDirectConnectAsync(
        Uri serviceUri,
        CancellationToken cancellationToken)
    {
        int port = serviceUri.IsDefaultPort
            ? serviceUri.Scheme == "https" ? 443 : 80
            : serviceUri.Port;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            using TcpClient tcp = new();
            await tcp.ConnectAsync(serviceUri.Host, port, timeout.Token);
            if (serviceUri.Scheme == "http")
            {
                return (true, "TCP 连接正常。");
            }

            await using SslStream ssl = new(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = serviceUri.Host,
                EnabledSslProtocols = SslProtocols.None
            }, timeout.Token);
            return (true, $"TCP 和 TLS 连接正常，使用 {ssl.SslProtocol}。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"{ConnectTimeout.TotalSeconds:0} 秒内没有连上服务器，网络可能不通");
        }
        catch (AuthenticationException exception)
        {
            Logger.Info("一键诊断", $"TLS 握手失败：{Logger.Describe(exception)}");
            return (false, "加密连接（TLS）没有建立成功，可能是网络拦截了 HTTPS 或系统时间不对");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            Logger.Info("一键诊断", $"直连失败：{Logger.Describe(exception)}");
            return (false, "连不上服务器，请检查网络和服务地址");
        }
    }

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static (DiagnosticStatus, string) Passed(string detail) => (DiagnosticStatus.Passed, detail);
    private static (DiagnosticStatus, string) Warning(string detail) => (DiagnosticStatus.Warning, detail);
    private static (DiagnosticStatus, string) Failed(string detail) => (DiagnosticStatus.Failed, detail);

    /// <summary>
    /// 报告里的 Key 只露最后 4 位。Key 太短（不到 12 位）时一位都不露 ——
    /// 以前 4 位以内的 Key 会被原样整串印出来。
    /// </summary>
    private static string RedactApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "未填写";
        }

        return apiKey.Length < 12 ? "****（已隐藏）" : $"sk-****{apiKey[^4..]}";
    }

    private static string Shorten(string value, int length) =>
        value.Length <= length ? value : $"{value[..length]}…";

    private string ToHumanDiagnosticMessage(Exception exception)
    {
        if (exception is InvalidOperationException invalid)
        {
            return invalid.Message;
        }

        if (exception is ApiHttpException or SocketException or HttpRequestException or AuthenticationException
            or FirstTokenTimeoutException or OverallTimeoutException or TranslationJsonException)
        {
            // 和浮窗报错用同一套说法：代理没开、TLS 被拦、域名解析失败、思考模式没关……各给各的话。
            return ErrorMessages.FromException(
                exception,
                _profile.Model,
                ExtraRequestBodyHelper.IsThinkingDisabled(_profile.ExtraRequestBody)).Message;
        }

        return "检查没有通过，详细原因已写入日志。";
    }

    /// <summary>
    /// 数一数朗读功能实际能用的语音。和 <see cref="CtrlTranslator.Speech.SpeechService"/> 同一个来源：
    /// 以前这里还把注册表里的 OneCore 语音也算进来，可朗读用的接口（SAPI 5）根本用不了它们，
    /// 结果诊断说"有语音"，浮窗上的朗读按钮却是灰的。
    /// </summary>
    private static (int Total, int English) CountInstalledVoices()
    {
        using SpeechSynthesizer synthesizer = new();
        List<InstalledVoice> voices = synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled)
            .ToList();
        int english = voices.Count(voice => string.Equals(
            voice.VoiceInfo.Culture.TwoLetterISOLanguageName,
            "en",
            StringComparison.OrdinalIgnoreCase));
        return (voices.Count, english);
    }
}
