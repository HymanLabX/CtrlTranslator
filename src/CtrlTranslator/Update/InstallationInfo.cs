using System.IO;
using CtrlTranslator.Diagnostics;
using Microsoft.Win32;

namespace CtrlTranslator.Update;

public enum InstallationKind
{
    /// <summary>用安装包装的，能一键更新。</summary>
    Installer,

    /// <summary>直接拷过来的裸 exe，没有安装记录，只能提示用户去下载安装包。</summary>
    Portable
}

/// <summary>
/// 本次运行是怎么装上来的 —— 决定了更新能不能一键完成、要不要提权。
/// </summary>
public sealed record InstallationInfo(
    InstallationKind Kind,
    bool PerMachine,
    string? InstallLocation,
    string? DisplayVersion)
{
    /// <summary>Inno Setup 会在卸载表里用 <c>AppId_is1</c> 作为键名，必须与 .iss 里的 AppId 一致。</summary>
    private const string UninstallSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8331A63C-0924-44FD-BE82-9F712630854D}_is1";

    /// <summary>装在 Program Files 那种（HKLM）更新时必然要过一次 UAC，没法真正静默。</summary>
    public bool RequiresElevation => Kind == InstallationKind.Installer && PerMachine;

    public static InstallationInfo Detect()
    {
        string? processPath = Environment.ProcessPath;
        string? processDirectory = string.IsNullOrEmpty(processPath)
            ? null
            : Path.GetDirectoryName(processPath);

        // 先查当前用户（安装包默认就是这种，不需要管理员权限），再查全机器安装。
        InstallationInfo? found = Read(RegistryHive.CurrentUser, RegistryView.Default, perMachine: false, processDirectory);
        found ??= Read(RegistryHive.LocalMachine, RegistryView.Registry64, perMachine: true, processDirectory);
        found ??= Read(RegistryHive.LocalMachine, RegistryView.Registry32, perMachine: true, processDirectory);

        return found
            ?? new InstallationInfo(InstallationKind.Portable, PerMachine: false, processDirectory, null);
    }

    private static InstallationInfo? Read(
        RegistryHive hive,
        RegistryView view,
        bool perMachine,
        string? processDirectory)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(UninstallSubKey);
            if (key is null)
            {
                return null;
            }

            string? location = key.GetValue("InstallLocation") as string;
            string? version = key.GetValue("DisplayVersion") as string;

            // 关键一步：注册表里有记录 ≠ 现在跑的这份就是它装的。
            // 用户完全可能装过一次、又从 publish\ 拷了个裸 exe 到桌面运行。
            // 那种情况下按"安装版"去更新，会把桌面这份晾在原地，用户以为没更新成功。
            if (!IsSameDirectory(location, processDirectory))
            {
                Logger.Info(
                    "自动更新",
                    "找到了安装记录，但当前运行的 exe 不在安装目录里，按免安装版处理。");
                return null;
            }

            return new InstallationInfo(InstallationKind.Installer, perMachine, location, version);
        }
        catch (Exception exception)
        {
            Logger.Warning("自动更新", $"读取安装记录失败，按免安装版处理：{exception.Message}");
            return null;
        }
    }

    private static bool IsSameDirectory(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }
}
