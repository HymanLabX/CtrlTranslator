using System.Runtime.InteropServices;

namespace CtrlTranslator.Interop;

/// <summary>
/// M1 使用的全部 Win32 声明。互操作代码只允许集中在此文件。
/// </summary>
internal static class NativeMethods
{
    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;

    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmRButtonDown = 0x0204;
    internal const int WmMButtonDown = 0x0207;
    internal const int WmXButtonDown = 0x020B;

    internal const uint LlkhfInjected = 0x00000010;
    internal const uint LlmhfInjected = 0x00000001;

    internal const ushort VkShift = 0x10;
    internal const ushort VkControl = 0x11;
    internal const ushort VkMenu = 0x12;
    internal const ushort VkC = 0x43;
    internal const ushort VkLShift = 0xA0;
    internal const ushort VkRShift = 0xA1;
    internal const ushort VkLControl = 0xA2;
    internal const ushort VkRControl = 0xA3;
    internal const ushort VkLMenu = 0xA4;
    internal const ushort VkRMenu = 0xA5;
    internal const ushort VkLWin = 0x5B;
    internal const ushort VkRWin = 0x5C;
    internal const ushort VkEscape = 0x1B;

    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;

    internal const uint InputKeyboard = 1;
    internal const uint KeyEventFKeyUp = 0x0002;

    internal delegate nint HookProcedure(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(
        int idHook,
        HookProcedure callback,
        nint moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hookHandle, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint windowHandle, out Rect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct KeyboardLowLevelHookData
    {
        internal readonly uint VirtualKeyCode;
        internal readonly uint ScanCode;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MouseLowLevelHookData
    {
        internal readonly Point Point;
        internal readonly uint MouseData;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Point(int x, int y)
    {
        internal readonly int X = x;
        internal readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;
    }

    internal enum MonitorDpiType
    {
        EffectiveDpi = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;

        [FieldOffset(0)]
        internal HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        internal uint Message;
        internal ushort ParameterLow;
        internal ushort ParameterHigh;
    }
}
