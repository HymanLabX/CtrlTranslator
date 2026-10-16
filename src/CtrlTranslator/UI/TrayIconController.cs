using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Resources;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Interop;
using CtrlTranslator.Settings;
using Forms = System.Windows.Forms;

namespace CtrlTranslator.UI;

/// <summary>
/// 管理通知区域图标和应用的常用入口。
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _enabledIcon;
    private readonly Icon _pausedIcon;
    private readonly Icon _warningIcon;
    private readonly nint _warningIconHandle;
    private readonly Forms.ToolStripMenuItem _enabledItem;
    private readonly Forms.ToolStripMenuItem _privacyItem;
    private readonly Forms.ToolStripMenuItem _modesItem;
    private readonly Forms.ToolStripMenuItem _profilesItem;
    private readonly List<Forms.ToolStripMenuItem> _modeItems = [];
    private readonly List<Forms.ToolStripMenuItem> _profileItems = [];
    private bool _disposed;

    /// <summary>
    /// 本次运行出现过未处理异常（SPEC §9.3 第 3 条）。以前只改一次悬停文字，
    /// 下次右键打开菜单时 <see cref="RefreshVisualState"/> 一刷新就冲掉了 —— 警告态只活了几秒。
    /// </summary>
    private bool _hasUnhandledError;

    public TrayIconController(AppSettings settings, SettingsStore settingsStore)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _enabledIcon = LoadIcon("Assets/ctrltranslator.ico");
        _pausedIcon = LoadIcon("Assets/ctrltranslator-paused.ico");
        _warningIcon = CreateWarningBadgeIcon(_enabledIcon, out _warningIconHandle);

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(CreateActionItem("打开主窗口", () => RaiseRequested(MainWindowRequested, "主窗口")));
        _menu.Items.Add(CreateActionItem("设置…", RaiseSettingsRequested));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        // 右键菜单只留最常用的几项：切模型、切模式、开关划词和隐私模式。
        // 测试服务、日志文件夹、检查更新、关于这些都在设置窗口里，不必在这里再摆一遍。
        _profilesItem = new Forms.ToolStripMenuItem("翻译模型");
        RebuildProfileItems();
        _menu.Items.Add(_profilesItem);

        _modesItem = new Forms.ToolStripMenuItem("翻译模式");
        RebuildModeItems();
        _menu.Items.Add(_modesItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        _enabledItem = new Forms.ToolStripMenuItem("启用划词")
        {
            CheckOnClick = true,
            Checked = _settings.Enabled
        };
        _enabledItem.Click += (_, _) => ExecuteSafely("切换启用状态", ToggleEnabled);
        _menu.Items.Add(_enabledItem);

        _privacyItem = new Forms.ToolStripMenuItem("隐私模式（不记录历史）")
        {
            CheckOnClick = true,
            Checked = _settings.PrivacyMode
        };
        _privacyItem.Click += (_, _) => ExecuteSafely("切换隐私模式", TogglePrivacyMode);
        _menu.Items.Add(_privacyItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        _menu.Items.Add(CreateActionItem("退出", () => System.Windows.Application.Current.Shutdown()));

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.MouseClick += NotifyIconOnMouseClick;
        _menu.Opening += Menu_OnOpening;
        RefreshVisualState();
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? MainWindowRequested;

    public void ShowInformation(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(4000);
    }

    public void ShowError(string message)
    {
        _notifyIcon.BalloonTipTitle = "CtrlTranslator";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Error;
        _notifyIcon.ShowBalloonTip(5000);
    }

    public void RefreshFromSettings()
    {
        _enabledItem.Checked = _settings.Enabled;
        _privacyItem.Checked = _settings.PrivacyMode;
        RebuildProfileItems();
        RebuildModeItems();
        RefreshVisualState();
    }

    public void OpenLogDirectory()
    {
        Directory.CreateDirectory(Logger.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = Logger.LogDirectory,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// 切到"出现过错误"状态：图标带红点、悬停文字提示查看日志、左键单击打开日志文件夹（SPEC §9.3）。
    /// 用户点开过一次日志就算看到了，状态随之解除，左键恢复成打开主窗口。
    /// </summary>
    public void SetWarningState()
    {
        _hasUnhandledError = true;
        RefreshVisualState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.MouseClick -= NotifyIconOnMouseClick;
        _menu.Opening -= Menu_OnOpening;
        _notifyIcon.Dispose();
        _menu.Dispose();

        // 这四项和动态生成的模式/档案菜单项都是 IDisposable，
        // ContextMenuStrip.Dispose 只释放它当前挂着的项，重建过的旧项不在其中。
        _enabledItem.Dispose();
        _privacyItem.Dispose();
        _modesItem.Dispose();
        _profilesItem.Dispose();
        foreach (Forms.ToolStripMenuItem item in _modeItems)
        {
            item.Dispose();
        }

        foreach (Forms.ToolStripMenuItem item in _profileItems)
        {
            item.Dispose();
        }
        _enabledIcon.Dispose();
        _pausedIcon.Dispose();
        _warningIcon.Dispose();

        // Icon.FromHandle 不接管 HICON 的所有权，漏掉这一步就是一个图标句柄泄漏。
        if (_warningIconHandle != nint.Zero)
        {
            NativeMethods.DestroyIcon(_warningIconHandle);
        }
    }

    /// <summary>
    /// 在正常图标右下角画一个警示圆点，用于"有档案已失效"的状态。
    /// 不复用暂停图标：那个的语义是"划词已关"，会让人误以为功能被关掉了。
    /// </summary>
    private static Icon CreateWarningBadgeIcon(Icon source, out nint handle)
    {
        handle = nint.Zero;
        try
        {
            using Bitmap bitmap = source.ToBitmap();
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float diameter = Math.Max(6f, bitmap.Width * 0.46f);
                var badge = new RectangleF(
                    bitmap.Width - diameter,
                    bitmap.Height - diameter,
                    diameter,
                    diameter);

                using SolidBrush ring = new(Color.White);
                using SolidBrush fill = new(Color.FromArgb(0xD9, 0x48, 0x2F));
                graphics.FillEllipse(ring, badge);
                badge.Inflate(-diameter * 0.12f, -diameter * 0.12f);
                graphics.FillEllipse(fill, badge);
            }

            handle = bitmap.GetHicon();
            using Icon composed = Icon.FromHandle(handle);
            return (Icon)composed.Clone();
        }
        catch (Exception exception)
        {
            Logger.Error("托盘", "生成失效角标图标失败，将沿用普通图标。", exception);
            return (Icon)source.Clone();
        }
    }

    private static Icon LoadIcon(string resourcePath)
    {
        StreamResourceInfo resource = System.Windows.Application.GetResourceStream(new Uri(resourcePath, UriKind.Relative))
            ?? throw new InvalidOperationException($"找不到图标资源：{resourcePath}");
        using (resource.Stream)
        using (Icon source = new(resource.Stream))
        {
            return (Icon)source.Clone();
        }
    }

    private Forms.ToolStripMenuItem CreateActionItem(string text, Action action)
    {
        Forms.ToolStripMenuItem item = new(text);
        item.Click += (_, _) => ExecuteSafely(text, action);
        return item;
    }

    private void NotifyIconOnMouseClick(object? sender, Forms.MouseEventArgs eventArgs)
    {
        ExecuteSafely("托盘图标单击", () =>
        {
            if (eventArgs.Button != Forms.MouseButtons.Left)
            {
                return;
            }

            if (_hasUnhandledError)
            {
                _hasUnhandledError = false;
                RefreshVisualState();
                OpenLogDirectory();
                return;
            }

            RaiseRequested(MainWindowRequested, "主窗口");
        });
    }

    private void ToggleEnabled()
    {
        _settings.Enabled = _enabledItem.Checked;
        RefreshVisualState();
        SaveSettings();
        Logger.Info("托盘", _settings.Enabled ? "划词已启用。" : "划词已暂停。");
    }

    private void TogglePrivacyMode()
    {
        _settings.PrivacyMode = _privacyItem.Checked;
        SaveSettings();
        Logger.Info("托盘", _settings.PrivacyMode ? "隐私模式已启用。" : "隐私模式已关闭。");
    }

    private void SelectProfile(string profileId)
    {
        ApiProfile? profile = _settings.FindProfile(profileId);
        if (profile is null)
        {
            return;
        }

        _settings.CurrentProfileId = profileId;
        RebuildProfileItems();
        SaveSettings();
        Logger.Info("托盘", $"当前翻译模型已切换为「{profile.Name}」。");
    }

    private void RebuildProfileItems()
    {
        _profilesItem.DropDownItems.Clear();
        _profileItems.Clear();
        foreach (ApiProfile profile in _settings.Profiles)
        {
            string profileId = profile.Id;
            Forms.ToolStripMenuItem item = new(profile.IsFailed ? $"异常 · {profile.Name}" : profile.Name)
            {
                Tag = profileId,
                Checked = string.Equals(profileId, _settings.CurrentProfileId, StringComparison.Ordinal),
                ToolTipText = profile.IsFailed
                    ? $"已标记失效：{profile.FailureReason}。到设置里改一下就会自动解除。"
                    : profile.Model
            };
            item.Click += (_, _) => ExecuteSafely("切换翻译模型", () => SelectProfile(profileId));
            _profileItems.Add(item);
            _profilesItem.DropDownItems.Add(item);
        }

        _profilesItem.DropDownItems.Add(new Forms.ToolStripSeparator());
        _profilesItem.DropDownItems.Add(CreateActionItem("管理翻译模型…", RaiseSettingsRequested));
    }

    private void SelectMode(string modeId)
    {
        _settings.CurrentModeId = modeId;
        foreach (Forms.ToolStripMenuItem item in _modeItems)
        {
            item.Checked = string.Equals(item.Tag as string, modeId, StringComparison.Ordinal);
        }

        SaveSettings();
        Logger.Info("托盘", $"翻译模式已切换为 {modeId}。");
    }

    private void RebuildModeItems()
    {
        _modesItem?.DropDownItems.Clear();
        _modeItems.Clear();
        foreach (TranslationMode mode in _settings.Modes)
        {
            string modeId = mode.Id;
            Forms.ToolStripMenuItem item = new(mode.Name)
            {
                Tag = modeId,
                Checked = string.Equals(modeId, _settings.CurrentModeId, StringComparison.Ordinal)
            };
            item.Click += (_, _) => ExecuteSafely("切换翻译模式", () => SelectMode(modeId));
            _modeItems.Add(item);
            _modesItem?.DropDownItems.Add(item);
        }
    }

    private void RefreshVisualState()
    {
        if (_hasUnhandledError)
        {
            _notifyIcon.Icon = _warningIcon;
            _notifyIcon.Text = "CtrlTranslator — 出现过错误，请查看日志（单击打开）";
            return;
        }

        int failedCount = _settings.Profiles.Count(profile => profile.IsFailed);
        if (!_settings.Enabled)
        {
            _notifyIcon.Icon = _pausedIcon;
            _notifyIcon.Text = "CtrlTranslator — 已暂停";
            return;
        }

        // 托盘提示文字有 63 字符上限，超出会被 Windows 直接截断，所以这里只报数不报名字。
        _notifyIcon.Icon = failedCount > 0 ? _warningIcon : _enabledIcon;
        _notifyIcon.Text = failedCount > 0
            ? $"CtrlTranslator — {failedCount} 个翻译模型已失效，点开设置修一下"
            : "CtrlTranslator — 划词已启用";
    }

    private void Menu_OnOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        RefreshFromSettings();
    }

    private void SaveSettings()
    {
        if (!_settingsStore.TrySave(_settings, out string? errorMessage))
        {
            ShowError(errorMessage ?? "设置保存失败，请稍后重试。");
        }
    }

    private void RaiseSettingsRequested()
    {
        EventHandler? handler = SettingsRequested;
        if (handler is null)
        {
            Logger.Error("托盘", "用户点击设置，但设置窗口没有订阅者。");
            ShowError("设置窗口还没有准备好，请退出程序后重新打开再试。");
            return;
        }

        handler.Invoke(this, EventArgs.Empty);
    }

    private void RaiseRequested(EventHandler? handler, string featureName)
    {
        if (handler is null)
        {
            Logger.Error("托盘", $"用户打开{featureName}，但没有可用的处理程序。");
            ShowError($"{featureName}还没有准备好，请退出程序后重新打开再试。");
            return;
        }

        handler.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteSafely(string actionName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Logger.Error("托盘", $"{actionName}失败。", exception);
            ShowError("操作没有完成，详情已记录到日志。请稍后重试。");
        }
    }
}
