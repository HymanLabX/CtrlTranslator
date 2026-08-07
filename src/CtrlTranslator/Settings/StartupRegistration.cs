using Microsoft.Win32;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Settings;

/// <summary>
/// 管理当前用户的开机自启注册表项，不需要管理员权限。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CtrlTranslator";

    public static bool TrySet(bool enabled, out string? errorMessage)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                string executablePath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法取得程序文件路径。");
                key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            errorMessage = null;
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "修改开机自动启动失败。", exception);
            errorMessage = "开机自动启动设置没有生效，请检查安全软件是否阻止了程序修改启动项。";
            return false;
        }
    }
}
