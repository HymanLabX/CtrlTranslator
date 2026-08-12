using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Update;

namespace CtrlTranslator.UI;

/// <summary>
/// 「发现新版本」弹窗：展示更新说明 → 用户点「立即更新」→ 下载 → 校验 → 静默安装 → 自动重启。
/// </summary>
/// <remarks>
/// 免安装版走不了这条路（没有安装记录可以覆盖），只给它一个"去下载安装包"的出口。
/// </remarks>
public partial class UpdateWindow : Window
{
    private readonly ReleaseInfo _release;
    private readonly InstallationInfo _installation;
    private readonly Action _updateStarting;
    private readonly Action _dismissed;
    private CancellationTokenSource? _downloadCancellation;
    private bool _installerLaunched;

    public UpdateWindow(
        ReleaseInfo release,
        InstallationInfo installation,
        Action updateStarting,
        Action dismissed)
    {
        InitializeComponent();
        _release = release;
        _installation = installation;
        _updateStarting = updateStarting;
        _dismissed = dismissed;

        HeadlineText.Text = $"发现新版本 {release.Version.ToString(3)}";
        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "这个版本没有附带更新说明。"
            : release.Notes.Trim();

        if (_installation.Kind == InstallationKind.Portable)
        {
            SubtitleText.Text =
                $"你当前用的是免安装版（{UpdateChecker.CurrentVersion.ToString(3)}），没法就地更新。"
                + "请到发布页面下载安装包版装一次，之后就能一键更新了。";
            UpdateButton.Content = "去下载";
            return;
        }

        string elevationNote = _installation.RequiresElevation
            ? "（这份装在 Program Files，更新时 Windows 会弹一次权限确认）"
            : string.Empty;
        SubtitleText.Text =
            $"当前版本 {UpdateChecker.CurrentVersion.ToString(3)}，安装包约 {release.SetupSizeMb} MB。"
            + $"点「立即更新」后会自动下载、校验、安装并重启程序{elevationNote}。";
    }

    private async void UpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_installation.Kind == InstallationKind.Portable)
        {
            OpenReleasePage();
            Close();
            return;
        }

        await RunUpdateAsync();
    }

    private async Task RunUpdateAsync()
    {
        _downloadCancellation = new CancellationTokenSource();
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "下载中…";
        LaterButton.Content = "取消";
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "正在获取官方校验值…";

        try
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                DownloadProgressBar.Value = report.Fraction;
                ProgressText.Text = $"正在下载：{report.Describe()}";
            });

            string setupPath = await UpdateDownloader.DownloadAndVerifyAsync(
                _release,
                progress,
                _downloadCancellation.Token);

            ProgressText.Text = "校验通过，正在启动安装程序…";
            UpdateButton.Content = "安装中…";
            LaterButton.IsEnabled = false;

            // 先让上层放开单实例互斥体：安装脚本会等它消失才开始覆盖文件。
            _updateStarting();
            _installerLaunched = true;
            UpdateDownloader.LaunchInstaller(setupPath, _installation.RequiresElevation);

            Logger.Info("自动更新", $"已交给安装程序处理 {_release.Tag}，本进程退出。");
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            Logger.Info("自动更新", "用户取消了下载。");
            ResetToIdle();
        }
        catch (Exception exception)
        {
            Logger.Error("自动更新", "更新失败。", exception);
            ShowError(exception is UpdateDownloadException
                ? exception.Message
                : "更新没有完成，详情已记录到日志。你也可以点「打开发布页面」手动下载。");
            ResetToIdle();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void ResetToIdle()
    {
        _downloadCancellation?.Dispose();
        _downloadCancellation = null;
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = "重试更新";
        LaterButton.IsEnabled = true;
        LaterButton.Content = "稍后";
    }

    private void LaterButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null)
        {
            _downloadCancellation.Cancel();
            return;
        }

        Close();
    }

    private void ReleasePageButton_OnClick(object sender, RoutedEventArgs e) => OpenReleasePage();

    private void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_release.HtmlUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Logger.Error("自动更新", "打开发布页面失败。", exception);
            ShowError($"打不开浏览器。手动访问：{_release.HtmlUrl}");
        }
    }

    private void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        _downloadCancellation?.Cancel();

        // 已经把活交给安装程序了就别再记"用户跳过了这个版本"，
        // 否则装完万一失败，下次就再也不会提醒了。
        if (!_installerLaunched)
        {
            _dismissed();
        }
    }
}
