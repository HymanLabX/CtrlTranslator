using System.Diagnostics;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Interop;

namespace CtrlTranslator.Core;

/// <summary>
/// 当前前台窗口所属进程的基本信息。
/// </summary>
public sealed record ForegroundApp(int ProcessId, string ProcessName)
{
    public static ForegroundApp? GetCurrent()
    {
        try
        {
            nint windowHandle = NativeMethods.GetForegroundWindow();
            if (windowHandle == nint.Zero)
            {
                Logger.Warning("前台应用", "当前没有可识别的前台窗口。");
                return null;
            }

            _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out uint processId);
            if (processId == 0)
            {
                Logger.Warning("前台应用", "无法取得前台窗口的进程编号。");
                return null;
            }

            using Process process = Process.GetProcessById(checked((int)processId));
            return new ForegroundApp(process.Id, process.ProcessName);
        }
        catch (Exception exception)
        {
            Logger.Error("前台应用", "读取前台窗口所属进程失败。", exception);
            return null;
        }
    }
}
