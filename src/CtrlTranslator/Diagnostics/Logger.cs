using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CtrlTranslator.Diagnostics;

/// <summary>
/// 线程安全的按日日志。所有消息在落盘前都会统一脱敏。
/// </summary>
public static partial class Logger
{
    private static readonly object SyncRoot = new();
    private static readonly ConcurrentQueue<PendingLog> PendingLogs = new();
    private static string _logDirectory = string.Empty;
    private static string[] _apiKeys = [];
    private static bool _initialized;
    private static System.Threading.Timer? _queueTimer;
    private static int _isDrainingQueue;

    /// <summary>
    /// 上一次清理过期日志是哪一天。托盘程序一开就是几个星期，只在启动时清理的话，
    /// 「自动删除 14 天前的日志」这条规矩实际上永远等不到执行（实测 17 天的日志都还在）。
    /// 所以改成：每跨过一天，写下一行日志之前顺手清一次。
    /// </summary>
    private static DateTime _lastCleanupDate = DateTime.MinValue;

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CtrlTranslator");

    public static string LogDirectory => _logDirectory;

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(DataDirectory);
            _logDirectory = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(_logDirectory);
            _initialized = true;
            _lastCleanupDate = DateTime.Now.Date;
            DeleteExpiredLogs();
            _queueTimer ??= new System.Threading.Timer(
                static _ => DrainQueuedLogs(),
                state: null,
                dueTime: 250,
                period: 250);
        }
    }

    /// <summary>
    /// 登记所有需要在日志里脱敏的 Key。多档案下每个档案各有一个 Key，漏登记一个就等于明文落盘。
    /// </summary>
    public static void SetApiKeys(IEnumerable<string?> apiKeys)
    {
        string[] values = apiKeys
            .Select(key => key?.Trim())
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        lock (SyncRoot)
        {
            _apiKeys = values;
        }
    }

    public static void Info(string module, string message) => Write("信息", module, message);

    public static void Warning(string module, string message) => Write("警告", module, message);

    public static void Error(string module, string message, Exception? exception = null)
    {
        string fullMessage = exception is null
            ? message
            : $"{message} | {Describe(exception)}{Environment.NewLine}{exception.StackTrace}";
        Write("错误", module, fullMessage);
    }

    /// <summary>
    /// 把异常连同它的内层原因写成一行：<c>外层类型: 消息 ← 内层类型: 消息 ← …</c>。
    /// </summary>
    /// <remarks>
    /// 只写外层那一句是不够的。实际日志里反复出现
    /// 「The SSL connection could not be established, see inner exception.」——
    /// 真正的原因（证书、协议、代理）全在内层，以前一个字都没记下来，
    /// 「详情见日志」打开一看什么也没有。调用栈仍然只记外层的，内层只要类型和消息就够定位。
    /// </remarks>
    public static string Describe(Exception exception)
    {
        StringBuilder builder = new();
        AppendException(builder, exception, depth: 0);
        return builder.ToString();
    }

    private static void AppendException(StringBuilder builder, Exception exception, int depth)
    {
        if (depth > 0)
        {
            builder.Append(" ← ");
        }

        builder.Append(exception.GetType().Name).Append(": ").Append(exception.Message);

        // 层数设个上限：异常链理论上可以无限长，日志一行没必要写成一本书。
        if (depth >= 5)
        {
            return;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions.Take(3))
            {
                AppendException(builder, inner, depth + 1);
            }

            return;
        }

        if (exception.InnerException is { } innerException)
        {
            AppendException(builder, innerException, depth + 1);
        }
    }

    /// <summary>
    /// 供系统钩子回调使用：只把异常对象放入无锁队列，不做格式化或文件 IO。
    /// </summary>
    public static void EnqueueError(string module, string message, Exception exception)
    {
        PendingLogs.Enqueue(new PendingLog("错误", module, message, exception));
    }

    public static void Shutdown()
    {
        _queueTimer?.Dispose();
        _queueTimer = null;
        DrainQueuedLogs();
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string result = ApiKeyPattern().Replace(value, "***");
        string[] apiKeys;
        lock (SyncRoot)
        {
            apiKeys = _apiKeys;
        }

        foreach (string apiKey in apiKeys)
        {
            result = result.Replace(apiKey, "***", StringComparison.Ordinal);
        }

        return result;
    }

    private static void Write(string level, string module, string message)
    {
        lock (SyncRoot)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("日志系统尚未初始化。");
            }

            DateTime now = DateTime.Now;
            if (now.Date != _lastCleanupDate)
            {
                // 先改日期再清理：清理失败时会回头调 Error() 写日志，
                // 日期不先落位的话就会在这里无限递归。
                _lastCleanupDate = now.Date;
                DeleteExpiredLogs();
            }

            string safeModule = Redact(module).ReplaceLineEndings(" ");
            string safeMessage = Redact(message).ReplaceLineEndings(" ⏎ ");
            string line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{safeModule}] {safeMessage}{Environment.NewLine}";
            string path = Path.Combine(_logDirectory, $"ctrltranslator-{now:yyyyMMdd}.log");
            File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static void DeleteExpiredLogs()
    {
        DateTime cutoff = DateTime.Now.Date.AddDays(-14);
        foreach (string path in Directory.EnumerateFiles(_logDirectory, "ctrltranslator-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
            {
                Error("日志", $"清理过期日志失败：{Path.GetFileName(path)}", exception);
            }
        }
    }

    private static void DrainQueuedLogs()
    {
        if (Interlocked.Exchange(ref _isDrainingQueue, 1) != 0)
        {
            return;
        }

        try
        {
            while (PendingLogs.TryDequeue(out PendingLog? pending))
            {
                string fullMessage = pending.Exception is null
                    ? pending.Message
                    : $"{pending.Message} | {Describe(pending.Exception)}"
                        + $"{Environment.NewLine}{pending.Exception.StackTrace}";
                Write(pending.Level, pending.Module, fullMessage);
            }
        }
        catch (Exception exception)
        {
            // 主日志不可写时仍把队列异常落入临时目录，绝不静默吞掉。
            string emergencyPath = Path.Combine(Path.GetTempPath(), "CtrlTranslator-emergency.log");
            string safeMessage = Redact($"刷新钩子日志队列失败：{exception}");
            File.AppendAllText(
                emergencyPath,
                $"[{DateTime.Now:O}] [错误] [日志] {safeMessage}{Environment.NewLine}");
        }
        finally
        {
            Volatile.Write(ref _isDrainingQueue, 0);
        }
    }

    private sealed record PendingLog(string Level, string Module, string Message, Exception? Exception);

    [GeneratedRegex(@"(?i)sk-[a-z0-9_\-]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();
}
