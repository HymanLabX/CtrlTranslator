using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CtrlTranslator.Interop;

/// <summary>
/// 按光标所在显示器的物理像素工作区定位浮窗，避免混合缩放时跨屏错位。
/// </summary>
internal static class DpiHelper
{
    public static void PositionPopup(Window window)
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.Point cursor))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取鼠标位置。");
        }

        nint monitor = NativeMethods.MonitorFromPoint(
            cursor,
            NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = checked((uint)Marshal.SizeOf<NativeMethods.MonitorInfo>())
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取显示器工作区。");
        }

        int dpiResult = NativeMethods.GetDpiForMonitor(
            monitor,
            NativeMethods.MonitorDpiType.EffectiveDpi,
            out uint dpiX,
            out uint dpiY);
        if (dpiResult != 0)
        {
            throw new Win32Exception(dpiResult, "无法读取显示器缩放比例。");
        }

        double scaleX = dpiX / 96d;
        double scaleY = dpiY / 96d;
        int windowWidth = Math.Max(1, checked((int)Math.Ceiling(window.ActualWidth * scaleX)));
        int windowHeight = Math.Max(1, checked((int)Math.Ceiling(window.ActualHeight * scaleY)));
        int horizontalGap = checked((int)Math.Round(16 * scaleX));
        int verticalGap = checked((int)Math.Round(20 * scaleY));

        int x = cursor.X + horizontalGap;
        int y = cursor.Y + verticalGap;
        NativeMethods.Rect workArea = monitorInfo.WorkArea;
        if (x + windowWidth > workArea.Right)
        {
            x = cursor.X - windowWidth - horizontalGap;
        }

        if (y + windowHeight > workArea.Bottom)
        {
            y = cursor.Y - windowHeight - verticalGap;
        }

        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - windowWidth));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - windowHeight));

        nint windowHandle = new WindowInteropHelper(window).Handle;
        if (!NativeMethods.SetWindowPos(
                windowHandle,
                nint.Zero,
                x,
                y,
                0,
                0,
                NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位翻译浮窗。");
        }
    }
}
