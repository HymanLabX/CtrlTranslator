using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using CtrlTranslator.Diagnostics;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace CtrlTranslator.UI;

public partial class DiagnosticWindow : Window
{
    private readonly DiagnosticRunner _runner;
    private readonly ObservableCollection<DiagnosticResult> _results = [];
    private CancellationTokenSource? _cancellation;

    public DiagnosticWindow(DiagnosticRunner runner)
    {
        InitializeComponent();
        _runner = runner;
        ResultsList.ItemsSource = _results;
        Loaded += DiagnosticWindow_OnLoaded;
        Closed += DiagnosticWindow_OnClosed;
    }

    private async void DiagnosticWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunDiagnosticsAsync();
        }
        catch (Exception exception)
        {
            HandleUnexpectedError(exception);
        }
    }

    private void DiagnosticWindow_OnClosed(object? sender, EventArgs e)
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }

    private async void RerunButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunDiagnosticsAsync();
        }
        catch (Exception exception)
        {
            HandleUnexpectedError(exception);
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _results.Clear();
        SummaryText.Text = "正在逐项检查，请稍候…";
        RerunButton.IsEnabled = false;
        CopyButton.IsEnabled = false;

        try
        {
            Progress<DiagnosticResult> progress = new(result =>
            {
                _results.Add(result);
                SummaryText.Text = $"已完成 {result.Number} / 12 项检查…";
            });
            await _runner.RunAsync(progress, _cancellation.Token);
            int failed = _results.Count(item => item.Status == DiagnosticStatus.Failed);
            int warnings = _results.Count(item => item.Status == DiagnosticStatus.Warning);
            SummaryText.Text = failed == 0
                ? $"诊断完成：没有失败项，{warnings} 项提醒。"
                : $"诊断完成：{failed} 项未通过，{warnings} 项提醒。";
            CopyButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            Logger.Info("诊断窗口", "诊断已取消。 ");
        }
        finally
        {
            RerunButton.IsEnabled = true;
        }
    }

    private async void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await SetClipboardTextWithRetryAsync(_runner.BuildReport(_results));
            SummaryText.Text = "诊断报告已复制，API Key 已脱敏。";
        }
        catch (Exception exception)
        {
            Logger.Error("诊断窗口", "复制诊断报告失败。", exception);
            MessageBox.Show(this, "暂时无法使用剪贴板，请稍后再试。", "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void HandleUnexpectedError(Exception exception)
    {
        Logger.Error("诊断窗口", "运行诊断时发生意外错误。", exception);
        SummaryText.Text = "诊断没有完整运行，详细原因已写入日志。";
        RerunButton.IsEnabled = true;
        CopyButton.IsEnabled = _results.Count > 0;
    }

    private static async Task SetClipboardTextWithRetryAsync(string text)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Logger.Warning("诊断窗口", $"剪贴板正忙，第 {attempt} 次复制失败，将在 20ms 后重试。");
                await Task.Delay(20);
            }
        }

        Clipboard.SetText(text);
    }
}
