using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Interop;

internal sealed class OutsideClickEventArgs(int x, int y) : EventArgs
{
    public int X { get; } = x;

    public int Y { get; } = y;
}

/// <summary>
/// 浮窗显示期间监听全局鼠标按下；回调只比较数字并异步投递关闭事件。
/// </summary>
/// <remarks>
/// 和键盘钩子一样装在专用的钩子线程（<see cref="HookThread"/>）上：界面线程一卡，
/// 装在界面线程上的鼠标钩子会被系统悄悄撤掉，表现是"点浮窗外面不关了"。
/// 回调里读写的几个数字都走 <see cref="Volatile"/> / <see cref="Interlocked"/>，跨线程是安全的。
/// </remarks>
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
    private int _outsideClickX;
    private int _outsideClickY;
    private bool _disposed;

    public LowLevelMouseHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _procedure = HookCallback;
    }

    public event EventHandler<OutsideClickEventArgs>? ClickedOutside;

    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hookHandle != nint.Zero)
        {
            return;
        }

        HookThread.Instance.Invoke(() =>
        {
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
        });
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
        Volatile.Write(ref _closeQueued, 0);
    }

    public void ResetOutsideClickCheck()
    {
        Volatile.Write(ref _closeQueued, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hookHandle == nint.Zero)
        {
            return;
        }

        HookThread.Instance.Invoke(() =>
        {
            if (!NativeMethods.UnhookWindowsHookEx(_hookHandle))
            {
                Logger.Warning("浮窗", "卸载浮窗鼠标监听失败。");
            }

            _hookHandle = nint.Zero;
        });
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
                    Volatile.Write(ref _outsideClickX, x);
                    Volatile.Write(ref _outsideClickY, y);
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
        ClickedOutside?.Invoke(
            this,
            new OutsideClickEventArgs(
                Volatile.Read(ref _outsideClickX),
                Volatile.Read(ref _outsideClickY)));
    }

    private static bool IsMouseButtonDown(int message) => message is
        NativeMethods.WmLButtonDown or
        NativeMethods.WmRButtonDown or
        NativeMethods.WmMButtonDown or
        NativeMethods.WmXButtonDown;
}
