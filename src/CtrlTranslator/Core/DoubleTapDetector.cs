using System.Diagnostics;
using CtrlTranslator.Interop;

namespace CtrlTranslator.Core;

/// <summary>
/// 无锁的双击触发键状态机。实例只由低级钩子所在的 WPF 线程访问。
/// </summary>
public sealed class DoubleTapDetector
{
    private readonly TriggerKeyKind _triggerKey;
    private readonly long _doubleTapIntervalTicks;
    private readonly long _maxKeyHoldTicks;

    // 这三个字段与 SPEC §2.1 的状态机定义逐一对应。
    private long _lastTapReleaseTicks;
    private long _currentPressTicks;
    private bool _pollutedByOtherKey;

    public DoubleTapDetector(string triggerKey, int doubleTapIntervalMs, int maxKeyHoldMs)
    {
        _triggerKey = ParseTriggerKey(triggerKey);
        _doubleTapIntervalTicks = MillisecondsToStopwatchTicks(doubleTapIntervalMs);
        _maxKeyHoldTicks = MillisecondsToStopwatchTicks(maxKeyHoldMs);
    }

    /// <summary>
    /// 处理一个键盘事件，返回本次抬起是否构成双击。
    /// </summary>
    public bool ProcessKeyEvent(uint virtualKeyCode, bool isKeyDown, bool isKeyUp, long nowTicks)
    {
        bool isTriggerKey = IsTriggerKey(virtualKeyCode);

        if (isKeyDown)
        {
            if (isTriggerKey)
            {
                // 自动重复的 KEYDOWN 不得重新计时。
                if (_currentPressTicks == 0)
                {
                    _currentPressTicks = nowTicks;
                    _pollutedByOtherKey = false;
                }
            }
            else
            {
                _pollutedByOtherKey = true;
            }

            return false;
        }

        if (!isKeyUp || !isTriggerKey || _currentPressTicks == 0)
        {
            return false;
        }

        long heldTicks = nowTicks - _currentPressTicks;
        bool isValidTap = heldTicks >= 0
            && heldTicks < _maxKeyHoldTicks
            && !_pollutedByOtherKey;

        _currentPressTicks = 0;
        _pollutedByOtherKey = false;

        if (!isValidTap)
        {
            _lastTapReleaseTicks = 0;
            return false;
        }

        long intervalTicks = nowTicks - _lastTapReleaseTicks;
        if (_lastTapReleaseTicks != 0
            && intervalTicks >= 0
            && intervalTicks < _doubleTapIntervalTicks)
        {
            // 触发后立即重置，使连续三次敲击只触发一次。
            _lastTapReleaseTicks = 0;
            return true;
        }

        _lastTapReleaseTicks = nowTicks;
        return false;
    }

    /// <summary>
    /// 鼠标按下会污染当前敲击，但不会影响两次敲击之间的空闲阶段。
    /// </summary>
    public void MarkMouseClick()
    {
        if (_currentPressTicks != 0)
        {
            _pollutedByOtherKey = true;
        }
    }

    private bool IsTriggerKey(uint virtualKeyCode) => _triggerKey switch
    {
        TriggerKeyKind.Control => virtualKeyCode is NativeMethods.VkControl
            or NativeMethods.VkLControl
            or NativeMethods.VkRControl,
        TriggerKeyKind.Shift => virtualKeyCode is NativeMethods.VkShift
            or NativeMethods.VkLShift
            or NativeMethods.VkRShift,
        TriggerKeyKind.Alt => virtualKeyCode is NativeMethods.VkMenu
            or NativeMethods.VkLMenu
            or NativeMethods.VkRMenu,
        _ => false
    };

    private static TriggerKeyKind ParseTriggerKey(string triggerKey)
    {
        if (string.Equals(triggerKey, "Shift", StringComparison.OrdinalIgnoreCase))
        {
            return TriggerKeyKind.Shift;
        }

        if (string.Equals(triggerKey, "Alt", StringComparison.OrdinalIgnoreCase))
        {
            return TriggerKeyKind.Alt;
        }

        return TriggerKeyKind.Control;
    }

    private static long MillisecondsToStopwatchTicks(int milliseconds)
    {
        int safeMilliseconds = Math.Max(1, milliseconds);
        return safeMilliseconds * Stopwatch.Frequency / 1000;
    }

    private enum TriggerKeyKind
    {
        Control,
        Shift,
        Alt
    }
}
