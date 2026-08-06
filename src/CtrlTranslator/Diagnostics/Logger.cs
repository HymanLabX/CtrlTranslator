using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CtrlTranslator.Diagnostics;

/// <summary>
/// 线程安全的按日日志。所有消息在落盘前都会统一脱敏。
/// </summary>
public static partial class Logger
{
    private static readonly object SyncRoot = new();
    private static string _logDirectory = string.Empty;
    private static string _apiKey = string.Empty;
    private static bool _initialized;

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
            DeleteExpiredLogs();
        }
    }

    public static void SetApiKey(string? apiKey)
    {
        lock (SyncRoot)
        {
            _apiKey = apiKey?.Trim() ?? string.Empty;
        }
    }

    public static void Info(string module, string message) => Write("信息", module, message);

    public static void Warning(string module, string message) => Write("警告", module, message);

    public static void Error(string module, string message, Exception? exception = null)
    {
        string fullMessage = exception is null
            ? message
            : $"{message} | {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";
        Write("错误", module, fullMessage);
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string result = ApiKeyPattern().Replace(value, "***");
        string apiKey;
        lock (SyncRoot)
        {
            apiKey = _apiKey;
        }

        if (!string.IsNullOrEmpty(apiKey))
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

            string safeModule = Redact(module).ReplaceLineEndings(" ");
            string safeMessage = Redact(message).ReplaceLineEndings(" ⏎ ");
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{safeModule}] {safeMessage}{Environment.NewLine}";
            string path = Path.Combine(_logDirectory, $"ctrltranslator-{DateTime.Now:yyyyMMdd}.log");
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

    [GeneratedRegex(@"(?i)sk-[a-z0-9_\-]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();
}
