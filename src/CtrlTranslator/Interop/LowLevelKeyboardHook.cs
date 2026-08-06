using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CtrlTranslator.Core;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Interop;

/// <summary>
/// 在 WPF 主线程安装低级键盘钩子，并把双击结果异步投递到普通事件。
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DoubleTapDetector _detector;
    private readonly NativeMethods.HookProcedure _keyboardProcedure;
    private readonly NativeMethods.HookProcedure _mousePollutionProcedure;
    private nint _keyboardHookHandle;
    private nint _mousePollutionHookHandle;
    private bool _disposed;

    public LowLevelKeyboardHook(Dispatcher dispatcher, DoubleTapDetector detector)
    {
        _dispatcher = dispatcher;
        _detector = detector;
        _keyboardProcedure = KeyboardHookCallback;
        _mousePollutionProcedure = MousePollutionHookCallback;
    }

    public event EventHandler? DoubleTapped;

    public bool IsInstalled => _keyboardHookHandle != nint.Zero;

    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("键盘钩子必须在 WPF 主线程安装。");
        }

        if (IsInstalled)
        {
            return;
        }

        nint moduleHandle = NativeMethods.GetModuleHandle(moduleName: null);
        if (moduleHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法取得当前程序模块句柄。");
        }

        _keyboardHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _keyboardProcedure,
            moduleHandle,
            threadId: 0);
        if (_keyboardHookHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安装全局键盘钩子。");
        }

        // SPEC 把“按键期间没有鼠标点击”定义为有效敲击条件。
        // 此处仅监听鼠标按下并污染状态，不实现 M2 的浮窗外部点击功能。
        _mousePollutionHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _mousePollutionProcedure,
            moduleHandle,
            threadId: 0);
        if (_mousePollutionHookHandle == nint.Zero)
        {
            int errorCode = Marshal.GetLastWin32Error();
            if (!NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle))
            {
                Logger.Warning("键盘钩子", "鼠标监听安装失败后，回收键盘钩子也失败。");
            }

            _keyboardHookHandle = nint.Zero;
            throw new Win32Exception(errorCode, "无法安装鼠标点击监听。");
        }

        Logger.Info("键盘钩子", "全局键盘钩子已安装。");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mousePollutionHookHandle != nint.Zero)
        {
            if (!NativeMethods.UnhookWindowsHookEx(_mousePollutionHookHandle))
            {
                Logger.Warning("键盘钩子", "卸载鼠标点击监听失败。");
            }

            _mousePollutionHookHandle = nint.Zero;
        }

        if (_keyboardHookHandle != nint.Zero)
        {
            if (!NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle))
            {
                Logger.Warning("键盘钩子", "卸载全局键盘钩子失败。");
            }

            _keyboardHookHandle = nint.Zero;
        }

        Logger.Info("键盘钩子", "全局键盘钩子已卸载。");
    }

    private nint KeyboardHookCallback(int code, nint message, nint dataPointer)
    {
        try
        {
            if (code >= 0)
            {
                NativeMethods.KeyboardLowLevelHookData data =
                    Marshal.PtrToStructure<NativeMethods.KeyboardLowLevelHookData>(dataPointer);

                // 忽略 SendInput 产生的 Ctrl+C，避免程序把自己的模拟按键当成下一次敲击。
                if ((data.Flags & NativeMethods.LlkhfInjected) == 0)
                {
                    int messageCode = unchecked((int)message);
                    bool isKeyDown = messageCode is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
                    bool isKeyUp = messageCode is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
                    long nowTicks = Stopwatch.GetTimestamp();

                    if (_detector.ProcessKeyEvent(data.VirtualKeyCode, isKeyDown, isKeyUp, nowTicks))
                    {
                        _dispatcher.BeginInvoke(new Action(RaiseDoubleTapped), DispatcherPriority.Input);
                    }
                }
            }

            return NativeMethods.CallNextHookEx(nint.Zero, code, message, dataPointer);
        }
        catch (Exception exception)
        {
            // 系统关键路径内只允许入队，绝不直接写文件或弹窗。
            Logger.EnqueueError("键盘钩子", "低级键盘钩子回调发生异常。", exception);
            return NativeMethods.CallNextHookEx(nint.Zero, code, message, dataPointer);
        }
    }

    private nint MousePollutionHookCallback(int code, nint message, nint dataPointer)
    {
        try
        {
            if (code >= 0 && IsMouseButtonDown(unchecked((int)message)))
            {
                NativeMethods.MouseLowLevelHookData data =
                    Marshal.PtrToStructure<NativeMethods.MouseLowLevelHookData>(dataPointer);
                if ((data.Flags & NativeMethods.LlmhfInjected) == 0)
                {
                    _detector.MarkMouseClick();
                }
            }

            return NativeMethods.CallNextHookEx(nint.Zero, code, message, dataPointer);
        }
        catch (Exception exception)
        {
            Logger.EnqueueError("键盘钩子", "鼠标点击监听回调发生异常。", exception);
            return NativeMethods.CallNextHookEx(nint.Zero, code, message, dataPointer);
        }
    }

    private void RaiseDoubleTapped()
    {
        DoubleTapped?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsMouseButtonDown(int message) => message is
        NativeMethods.WmLButtonDown or
        NativeMethods.WmRButtonDown or
        NativeMethods.WmMButtonDown or
        NativeMethods.WmXButtonDown;
}
