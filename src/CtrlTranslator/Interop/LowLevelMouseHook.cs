using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Interop;

/// <summary>
/// 浮窗显示期间监听全局鼠标按下；回调只比较数字并异步投递关闭事件。
/// </summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.HookProcedure _procedure;
    private nint _hookHandle;
    private int _left;
    private int _top;
    private int _right;
    private int _bottom;
    private int _pinned;
    private int _closeQueued;
    private bool _disposed;

    public LowLevelMouseHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _procedure = HookCallback;
    }

    public event EventHandler? ClickedOutside;

    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hookHandle != nint.Zero)
        {
            return;
        }

        nint moduleHandle = NativeMethods.GetModuleHandle(null);
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _procedure,
            moduleHandle,
            0);
        if (_hookHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安装浮窗鼠标监听。");
        }
    }

    public void UpdateBounds(int left, int top, int right, int bottom)
    {
        Volatile.Write(ref _left, left);
        Volatile.Write(ref _top, top);
        Volatile.Write(ref _right, right);
        Volatile.Write(ref _bottom, bottom);
    }

    public void SetPinned(bool pinned)
    {
        Volatile.Write(ref _pinned, pinned ? 1 : 0);
        if (pinned)
        {
            Volatile.Write(ref _closeQueued, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hookHandle != nint.Zero)
        {
            if (!NativeMethods.UnhookWindowsHookEx(_hookHandle))
            {
                Logger.Warning("浮窗", "卸载浮窗鼠标监听失败。");
            }

            _hookHandle = nint.Zero;
        }
    }

    private nint HookCallback(int code, nint message, nint dataPointer)
    {
        try
        {
            if (code >= 0
                && Volatile.Read(ref _pinned) == 0
                && IsMouseButtonDown(unchecked((int)message)))
            {
                NativeMethods.MouseLowLevelHookData data =
                    Marshal.PtrToStructure<NativeMethods.MouseLowLevelHookData>(dataPointer);
                int x = data.Point.X;
                int y = data.Point.Y;
                bool outside = x < Volatile.Read(ref _left)
                    || x >= Volatile.Read(ref _right)
                    || y < Volatile.Read(ref _top)
                    || y >= Volatile.Read(ref _bottom);
                if (outside && Interlocked.Exchange(ref _closeQueued, 1) == 0)
                {
                    _dispatcher.BeginInvoke(new Action(RaiseClickedOutside), DispatcherPriority.Input);
                }
            }

            return NativeMethods.CallNextHookEx(nint.Zero, code, message, dataPointer);
        }
        catch (Exception exception)
        {
            Logger.EnqueueError("浮窗", "浮窗鼠标监听回调发生异常。", exception);
            return NativeMethods.CallNextHookEx(nint.Zero, code, message, dataPointer);
        }
    }

    private void RaiseClickedOutside()
    {
        ClickedOutside?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsMouseButtonDown(int message) => message is
        NativeMethods.WmLButtonDown or
        NativeMethods.WmRButtonDown or
        NativeMethods.WmMButtonDown or
        NativeMethods.WmXButtonDown;
}
