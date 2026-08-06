using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Interop;
using CtrlTranslator.Settings;
using CtrlTranslator.Translation;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using WpfClipboard = System.Windows.Clipboard;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace CtrlTranslator.UI;

internal sealed class PopupActionEventArgs(ErrorAction action) : EventArgs
{
    public ErrorAction Action { get; } = action;
}

internal sealed class StarredChangedEventArgs(long recordId, bool starred) : EventArgs
{
    public long RecordId { get; } = recordId;

    public bool Starred { get; } = starred;
}

/// <summary>
/// 不抢焦点的翻译浮窗，负责加载态、流式回退、结果渲染和关闭行为。
/// </summary>
public partial class PopupWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LowLevelMouseHook _mouseHook;
    private readonly DispatcherTimer _loadingTimer;
    private readonly Stopwatch _stopwatch = new();
    private readonly string _sourceText;
    private readonly string _appName;
    private readonly System.Text.StringBuilder _streamBuffer = new();
    private TranslationResult? _result;
    private ErrorAction _noticeAction;
    private bool _streamingVisible;
    private bool _isPinned;
    private bool _isClosed;

    public PopupWindow(string sourceText, string? appName, AppSettings settings)
    {
        InitializeComponent();
        _sourceText = sourceText;
        _appName = appName ?? "未知";
        _settings = settings;
        Width = Math.Clamp(settings.PopupMaxWidth, 260, 900);
        MaxHeight = Math.Clamp(settings.PopupMaxHeight, 180, 900);
        ContentViewer.FontSize = Math.Clamp(settings.PopupFontSize, 10, 24);
        ApplyTheme();

        _mouseHook = new LowLevelMouseHook(Dispatcher);
        _mouseHook.ClickedOutside += MouseHook_OnClickedOutside;
        _loadingTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnLoadingTick, Dispatcher);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnLocationChanged;
        Closed += OnClosed;
        SetLoadingState(isLoading: true);
    }

    internal event EventHandler? ClosingRequested;

    internal event EventHandler<PopupActionEventArgs>? ActionRequested;

    internal event EventHandler<StarredChangedEventArgs>? StarredChanged;

    internal event EventHandler? SuppressReasoningWarningRequested;

    internal event EventHandler? DisableThinkingRequested;

    internal string SourceText => _sourceText;

    internal string AppName => _appName;

    internal void RestoreDisableThinkingButton()
    {
        DisableThinkingButton.Content = "关闭思考模式";
        DisableThinkingButton.IsEnabled = true;
    }

    internal void StartLoading()
    {
        _stopwatch.Restart();
        _loadingTimer.Start();
        SetLoadingState(isLoading: true);
    }

    internal void AppendStream(TranslationStreamUpdate update)
    {
        if (_isClosed)
        {
            return;
        }

        _streamBuffer.Append(update.Delta);
        if (!_streamingVisible && update.ElapsedMilliseconds >= _settings.StreamFallbackDelayMs)
        {
            _streamingVisible = true;
            SetLoadingState(isLoading: false);
            RenderPlainText(_streamBuffer.ToString());
            LockCurrentHeight();
            return;
        }

        if (_streamingVisible)
        {
            AppendPlainText(update.Delta);
            LockCurrentHeight();
        }
    }

    internal void ShowResult(TranslationResult result)
    {
        if (_isClosed)
        {
            return;
        }

        _result = result;
        _loadingTimer.Stop();
        _stopwatch.Stop();
        SetLoadingState(isLoading: false);
        RenderResult(result.ResultText, result.IsWord);
        CopyButton.IsEnabled = true;
        StarButton.IsEnabled = result.RecordId.HasValue;
        StarButton.Content = result.Starred ? "★" : "☆";

        if (result.ShowReasoningWarning)
        {
            ShowReasoningWarning(result.Model);
        }

        RepositionAndRefreshBounds();
    }

    internal void ShowTranslationError(UserFacingError error)
    {
        if (_isClosed)
        {
            return;
        }

        _loadingTimer.Stop();
        _stopwatch.Stop();
        SetLoadingState(isLoading: false);
        RenderErrorText(error.Message);
        if (error.Action == ErrorAction.None)
        {
            NoticeBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShowNotice("可以尝试下面的修复操作。", error.Action, error.ActionText, isWarning: false);
        }
        CopyButton.IsEnabled = false;
        StarButton.IsEnabled = false;
        RepositionAndRefreshBounds();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ClosingRequested?.Invoke(this, EventArgs.Empty);
        base.OnClosing(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            _mouseHook.Install();
        }
        catch (Exception exception)
        {
            Logger.Error("浮窗", "安装浮窗外部点击监听失败。", exception);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RepositionAndRefreshBounds();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshPhysicalBounds();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        RefreshPhysicalBounds();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _loadingTimer.Stop();
        _mouseHook.ClickedOutside -= MouseHook_OnClickedOutside;
        _mouseHook.Dispose();
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
        LocationChanged -= OnLocationChanged;
        Closed -= OnClosed;
    }

    private void OnLoadingTick(object? sender, EventArgs e)
    {
        LoadingText.Text = $"翻译中…（已等待 {_stopwatch.Elapsed.TotalSeconds:F1} 秒）";
    }

    private void MouseHook_OnClickedOutside(object? sender, EventArgs e)
    {
        if (!_isPinned && !_isClosed)
        {
            Close();
        }
    }

    private void Toolbar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException exception)
        {
            Logger.Error("浮窗", "拖动浮窗失败。", exception);
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        _mouseHook.SetPinned(_isPinned);
        PinButton.Content = _isPinned ? "📍" : "📌";
        PinButton.ToolTip = _isPinned ? "取消钉住" : "钉住";
    }

    private async void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_result is null)
            {
                return;
            }

            await SetClipboardTextWithRetryAsync(_result.ResultText);
            CopyButton.Content = "已复制";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopyButton.Content = "📋";
            };
            timer.Start();
        }
        catch (Exception exception)
        {
            Logger.Error("浮窗", "复制译文失败。", exception);
            ShowNotice(
                "剪贴板正被其他程序占用，稍等一下再试。",
                ErrorAction.None,
                null,
                isWarning: false);
        }
    }

    private void StarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_result?.RecordId is not long recordId)
        {
            return;
        }

        bool starred = StarButton.Content?.ToString() != "★";
        StarButton.Content = starred ? "★" : "☆";
        StarredChanged?.Invoke(this, new StarredChangedEventArgs(recordId, starred));
    }

    private void NoticeActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        ActionRequested?.Invoke(this, new PopupActionEventArgs(_noticeAction));
    }

    private void DisableThinkingButton_OnClick(object sender, RoutedEventArgs e)
    {
        DisableThinkingButton.IsEnabled = false;
        DisableThinkingButton.Content = "正在重译…";
        DisableThinkingRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NoticeDismissButton_OnClick(object sender, RoutedEventArgs e)
    {
        NoticeBorder.Visibility = Visibility.Collapsed;
        SuppressReasoningWarningRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetLoadingState(bool isLoading)
    {
        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        ContentViewer.Visibility = isLoading ? Visibility.Collapsed : Visibility.Visible;
        SpeechButton.IsEnabled = false;
        StarButton.IsEnabled = !isLoading && _result?.RecordId is not null;
        CopyButton.IsEnabled = !isLoading && _result is not null;
    }

    private void RenderResult(string text, bool isWord)
    {
        if (isWord)
        {
            RenderDictionary(text);
        }
        else
        {
            RenderPlainText(text);
        }
    }

    private void RenderPlainText(string text)
    {
        var document = CreateDocument();
        document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
        ContentViewer.Document = document;
    }

    private void AppendPlainText(string delta)
    {
        if (ContentViewer.Document?.Blocks.LastBlock is Paragraph paragraph)
        {
            paragraph.Inlines.Add(new Run(delta));
        }
        else
        {
            RenderPlainText(delta);
        }
    }

    private void RenderDictionary(string text)
    {
        var document = CreateDocument();
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            var paragraph = new Paragraph { Margin = new Thickness(0, index == 0 ? 0 : 4, 0, 0) };
            if (index == 0 && line.StartsWith('/') && line.EndsWith('/'))
            {
                paragraph.FontFamily = new FontFamily("Consolas");
                paragraph.Foreground = SecondaryBrush();
                paragraph.Inlines.Add(new Run(line));
            }
            else if (line.StartsWith("例", StringComparison.Ordinal))
            {
                paragraph.Margin = new Thickness(12, 6, 0, 0);
                paragraph.FontStyle = FontStyles.Italic;
                paragraph.Foreground = SecondaryBrush();
                paragraph.Inlines.Add(new Run(line));
            }
            else
            {
                int separator = line.IndexOfAny(['.', '．', '。']);
                if (separator is > 0 and < 8)
                {
                    paragraph.Inlines.Add(new Run(line[..(separator + 1)]) { FontWeight = FontWeights.Bold });
                    paragraph.Inlines.Add(new Run(line[(separator + 1)..]));
                }
                else
                {
                    paragraph.Inlines.Add(new Run(line));
                }
            }

            document.Blocks.Add(paragraph);
        }

        ContentViewer.Document = document;
    }

    private void RenderErrorText(string message)
    {
        var document = CreateDocument();
        document.Blocks.Add(new Paragraph(new Run($"❌ {message}"))
        {
            Foreground = new SolidColorBrush(Color.FromRgb(190, 45, 45)),
            Margin = new Thickness(0)
        });
        ContentViewer.Document = document;
    }

    private FlowDocument CreateDocument()
    {
        return new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = Math.Clamp(_settings.PopupFontSize, 10, 24),
            Foreground = ContentViewer.Foreground
        };
    }

    private void ShowReasoningWarning(string model)
    {
        ShowNotice(
            $"⚠️ 你配置的模型「{model}」正在“思考”，这会让翻译慢很多、也更费钱。",
            ErrorAction.Settings,
            "去设置",
            isWarning: true);
        DisableThinkingButton.Content = "关闭思考模式";
        DisableThinkingButton.IsEnabled = true;
        DisableThinkingButton.Visibility = Visibility.Visible;
        NoticeDismissButton.Visibility = Visibility.Visible;
        NoticeDismissButton.Content = "不再提示";
    }

    private void ShowNotice(string message, ErrorAction action, string? actionText, bool isWarning)
    {
        _noticeAction = action;
        NoticeText.Text = message;
        NoticeBorder.Background = isWarning
            ? new SolidColorBrush(Color.FromRgb(255, 244, 190))
            : new SolidColorBrush(Color.FromRgb(255, 224, 224));
        NoticeText.Foreground = new SolidColorBrush(Color.FromRgb(65, 45, 20));
        NoticeActionButton.Content = actionText;
        NoticeActionButton.Visibility = action == ErrorAction.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (!isWarning)
        {
            DisableThinkingButton.Visibility = Visibility.Collapsed;
            NoticeDismissButton.Visibility = Visibility.Collapsed;
        }

        NoticeBorder.Visibility = Visibility.Visible;
    }

    private void ApplyTheme()
    {
        bool dark = IsDarkTheme();
        ShellBorder.Background = new SolidColorBrush(dark
            ? Color.FromRgb(35, 37, 41)
            : Color.FromRgb(255, 255, 255));
        ShellBorder.BorderBrush = new SolidColorBrush(dark
            ? Color.FromRgb(75, 78, 84)
            : Color.FromRgb(210, 213, 218));
        Foreground = new SolidColorBrush(dark
            ? Color.FromRgb(239, 239, 239)
            : Color.FromRgb(32, 34, 38));
        ContentViewer.Foreground = Foreground;
        LoadingText.Foreground = Foreground;
    }

    private bool IsDarkTheme()
    {
        if (string.Equals(_settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_settings.Theme, "深色", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_settings.Theme, "浅色", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception)
        {
            Logger.Error("浮窗", "读取 Windows 主题失败，使用浅色主题。", exception);
            return false;
        }
    }

    private System.Windows.Media.Brush SecondaryBrush()
    {
        return new SolidColorBrush(IsDarkTheme()
            ? Color.FromRgb(180, 184, 190)
            : Color.FromRgb(100, 105, 112));
    }

    private void RepositionAndRefreshBounds()
    {
        try
        {
            DpiHelper.PositionPopup(this);
            RefreshPhysicalBounds();
        }
        catch (Exception exception)
        {
            Logger.Error("浮窗", "定位翻译浮窗失败。", exception);
        }
    }

    private void RefreshPhysicalBounds()
    {
        if (!IsLoaded)
        {
            return;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero && NativeMethods.GetWindowRect(handle, out NativeMethods.Rect rectangle))
        {
            _mouseHook.UpdateBounds(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        }
    }

    private void LockCurrentHeight()
    {
        if (ActualHeight > 0)
        {
            MinHeight = Math.Min(ActualHeight, MaxHeight);
        }
    }

    private static async Task SetClipboardTextWithRetryAsync(string text)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                WpfClipboard.SetText(text, WpfTextDataFormat.UnicodeText);
                return;
            }
            catch (Exception exception) when (exception is COMException or ExternalException)
            {
                lastException = exception;
                Logger.Warning("浮窗", $"写入剪贴板第 {attempt + 1} 次失败，将在 20ms 后重试。");
                await Task.Delay(20);
            }
        }

        throw new IOException("剪贴板在 5 次重试后仍不可用。", lastException);
    }
}
