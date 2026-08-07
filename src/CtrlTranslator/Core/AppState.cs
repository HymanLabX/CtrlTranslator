using System.Diagnostics;
using System.IO;
using CtrlTranslator.Settings;

namespace CtrlTranslator.Core;

/// <summary>
/// 取词链路的全局开关、应用黑名单和 500ms 防抖状态。
/// </summary>
public sealed class AppState
{
    private static readonly long DebounceTicks = Stopwatch.Frequency / 2;

    private readonly AppSettings _settings;
    private HashSet<string> _blacklist;
    private long _lastTriggerTicks;

    public AppState(AppSettings settings)
    {
        _settings = settings;
        _blacklist = CreateBlacklist(settings.AppBlacklist);
    }

    public void RefreshBlacklist()
    {
        _blacklist = CreateBlacklist(_settings.AppBlacklist);
    }

    private static HashSet<string> CreateBlacklist(IEnumerable<string> names)
    {
        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool Enabled => _settings.Enabled;

    public bool DetailedLogging => _settings.DetailedLogging;

    public bool IsBlacklisted(string? processName)
    {
        return !string.IsNullOrWhiteSpace(processName)
            && _blacklist.Contains(NormalizeProcessName(processName));
    }

    public bool TryEnterDebounce(long nowTicks)
    {
        long elapsed = nowTicks - _lastTriggerTicks;
        if (_lastTriggerTicks != 0 && elapsed >= 0 && elapsed < DebounceTicks)
        {
            return false;
        }

        _lastTriggerTicks = nowTicks;
        return true;
    }

    private static string NormalizeProcessName(string processName)
    {
        string trimmed = processName.Trim();
        return Path.GetFileNameWithoutExtension(trimmed);
    }
}
