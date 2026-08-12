using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Principal;
using CtrlTranslator.Data;
using CtrlTranslator.Settings;
using CtrlTranslator.Translation;
using Microsoft.Win32;

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
            if (string.IsNullOrWhiteSpace(_profile.BaseUrl)
                || string.IsNullOrWhiteSpace(_profile.ApiKey)
                || string.IsNullOrWhiteSpace(_profile.Model))
            {
                return Failed("服务地址、API Key 或模型名尚未填写完整。");
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

        await RunCheckAsync(3, "DNS 解析", async token =>
        {
            if (serviceUri is null)
            {
                return Failed("服务地址无效，无法解析域名。");
            }

            IPAddress[] addresses = await Dns.GetHostAddressesAsync(serviceUri.Host, token);
            return addresses.Length > 0
                ? Passed($"已解析到 {addresses.Length} 个网络地址。")
                : Failed("没有解析到服务器地址，请检查网络或域名。");
        });

        await RunCheckAsync(4, "TCP / TLS 连接", async token =>
        {
            if (serviceUri is null)
            {
                return Failed("服务地址无效，无法建立连接。");
            }

            int port = serviceUri.IsDefaultPort
                ? serviceUri.Scheme == "https" ? 443 : 80
                : serviceUri.Port;
            using TcpClient tcp = new();
            await tcp.ConnectAsync(serviceUri.Host, port, token);
            if (serviceUri.Scheme == "http")
            {
                return Warning("TCP 连接正常；该地址使用未加密的 HTTP，没有 TLS 保护。");
            }

            await using SslStream ssl = new(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = serviceUri.Host,
                EnabledSslProtocols = SslProtocols.None
            }, token);
            return Passed($"TCP 和 TLS 连接正常，使用 {ssl.SslProtocol}。");
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
            TranslationClientResult result = await client.TranslateAsync(
                CreateRequest("只回复一个字：好", maxTokens: 1), null, token);
            return Passed($"模型「{_profile.Model}」可用，返回“{Shorten(result.Text, 12)}”。");
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
                return Failed("没有检测到 Windows 语音，请在系统设置中安装语音包。");
            }

            return english > 0
                ? Passed($"检测到 {total} 个语音，其中 {english} 个英语语音。"): Warning($"检测到 {total} 个语音，但没有英语语音；英文朗读可能不可用。");
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
        if (string.IsNullOrWhiteSpace(_profile.ApiKey)
            || string.IsNullOrWhiteSpace(_profile.Model)
            || !Uri.TryCreate(_profile.BaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("翻译服务配置尚未填写完整。");
        }
    }

    private static (DiagnosticStatus, string) Passed(string detail) => (DiagnosticStatus.Passed, detail);
    private static (DiagnosticStatus, string) Warning(string detail) => (DiagnosticStatus.Warning, detail);
    private static (DiagnosticStatus, string) Failed(string detail) => (DiagnosticStatus.Failed, detail);

    private static string RedactApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "未填写";
        }

        string suffix = apiKey.Length <= 4 ? apiKey : apiKey[^4..];
        return $"sk-****{suffix}";
    }

    private static string Shorten(string value, int length) =>
        value.Length <= length ? value : $"{value[..length]}…";

    private static string ToHumanDiagnosticMessage(Exception exception)
    {
        if (exception is ApiHttpException api)
        {
            return ErrorMessages.FromException(api, string.Empty, thinkingDisabled: false).Message;
        }

        if (exception is SocketException or HttpRequestException or AuthenticationException)
        {
            return "连接服务器失败，请检查网络、代理和服务地址。";
        }

        if (exception is InvalidOperationException invalid)
        {
            return invalid.Message;
        }

        return "检查没有通过，详细原因已写入日志。";
    }

    private static (int Total, int English) CountInstalledVoices()
    {
        List<string> names = [];
        string[] paths =
        [
            @"SOFTWARE\Microsoft\Speech\Voices\Tokens",
            @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens"
        ];
        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string path in paths)
            {
                using RegistryKey? key = root.OpenSubKey(path);
                if (key is null)
                {
                    continue;
                }

                foreach (string subKeyName in key.GetSubKeyNames())
                {
                    using RegistryKey? voiceKey = key.OpenSubKey(subKeyName);
                    names.Add($"{subKeyName} {voiceKey?.GetValue(string.Empty)}");
                }
            }
        }

        int english = names.Count(name => name.Contains("English", StringComparison.OrdinalIgnoreCase)
            || name.Contains("en-", StringComparison.OrdinalIgnoreCase)
            || name.Contains("409", StringComparison.OrdinalIgnoreCase));
        return (names.Count, english);
    }
}
