using System.Diagnostics;
using CtrlTranslator.Settings;

namespace CtrlTranslator.Core;

/// <summary>
/// 取词链路的全局开关和 500ms 防抖状态。
/// </summary>
public sealed class AppState
{
    private static readonly long DebounceTicks = Stopwatch.Frequency / 2;

    private readonly AppSettings _settings;
    private long _lastTriggerTicks;

    public AppState(AppSettings settings)
    {
        _settings = settings;
    }

    public bool Enabled => _settings.Enabled;

    public bool DetailedLogging => _settings.DetailedLogging;

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
}
