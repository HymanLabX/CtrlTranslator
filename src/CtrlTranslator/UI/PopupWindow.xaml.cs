using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Interop;
using CtrlTranslator.Settings;
using CtrlTranslator.Speech;
using CtrlTranslator.Translation;
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
    private int _boundsRefreshQueued;

    private readonly SpeechService? _speechService;

    public PopupWindow(string sourceText, string? appName, AppSettings settings, SpeechService? speechService = null)
    {
        InitializeComponent();
        _sourceText = sourceText;
        _appName = appName ?? "未知";
        _settings = settings;
        _speechService = speechService;
        Width = Math.Clamp(settings.PopupMaxWidth, 260, 900);
        MaxHeight = Math.Clamp(settings.PopupMaxHeight, 180, 900);
        ContentViewer.FontSize = Math.Clamp(settings.PopupFontSize, 10, 24);
        InitializeSpeechButton();
        if (_speechService is not null)
        {
            _speechService.SpeakCompleted += SpeechService_OnSpeakCompleted;
        }

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
        StarButton.Content = result.Starred ? "star-filled" : "star";
        ShowFallbackNotice(result);

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
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        RepositionAndRefreshBounds();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SchedulePhysicalBoundsRefresh();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        SchedulePhysicalBoundsRefresh();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _loadingTimer.Stop();

        // SPEC §8：浮窗关闭时必须停止朗读，否则关了窗还在念。
        if (_speechService is not null)
        {
            _speechService.SpeakCompleted -= SpeechService_OnSpeakCompleted;
            _speechService.Stop();
        }

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
        LoadingText.Text = $"翻译中… {_stopwatch.Elapsed.TotalSeconds:F1} 秒";
    }

    private void MouseHook_OnClickedOutside(object? sender, OutsideClickEventArgs e)
    {
        if (_isPinned || _isClosed)
        {
            _mouseHook.ResetOutsideClickCheck();
            return;
        }

        // 低级钩子使用的是缓存边界；真正关闭前必须读取原生窗口的最新矩形再次确认。
        bool confirmed = TryConfirmOutsideCurrentNativeBounds(e.X, e.Y, out bool outside);
        if (confirmed && outside)
        {
            Close();
            return;
        }

        if (confirmed && !outside)
        {
            Logger.Info("浮窗", "外部点击关闭复核发现点击仍在浮窗内，已保留窗口。");
        }

        _mouseHook.ResetOutsideClickCheck();
        SchedulePhysicalBoundsRefresh();
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
        PinButton.Content = _isPinned ? "pinned" : "pin";
        PinButton.ToolTip = _isPinned ? "取消钉住" : "钉住";
    }

    private async void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_result is null)
            {
                Logger.Warning("浮窗", "用户点击复制，但当前浮窗还没有可复制的译文。");
                return;
            }

            await SetClipboardTextWithRetryAsync(_result.ResultText);
            CopyButton.Content = "copied";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopyButton.Content = "copy";
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
            Logger.Warning("浮窗", "用户点击收藏，但当前译文没有可保存的历史记录。");
            return;
        }

        bool starred = StarButton.Content?.ToString() != "star-filled";
        StarButton.Content = starred ? "star-filled" : "star";
        StarredChanged?.Invoke(this, new StarredChangedEventArgs(recordId, starred));
    }

    private void NoticeActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        Logger.Info("浮窗", $"用户点击了底部提示条按钮：{NoticeActionButton.Content}。");
        EventHandler<PopupActionEventArgs>? handler = ActionRequested;
        if (handler is null)
        {
            Logger.Error("浮窗", "底部提示条操作没有订阅者，无法执行用户请求。");
            return;
        }

        handler.Invoke(this, new PopupActionEventArgs(_noticeAction));
    }

    private void DisableThinkingButton_OnClick(object sender, RoutedEventArgs e)
    {
        Logger.Info("浮窗", "用户点击了底部提示条按钮：关闭思考模式。");
        DisableThinkingButton.IsEnabled = false;
        DisableThinkingButton.Content = "正在重译…";
        EventHandler? handler = DisableThinkingRequested;
        if (handler is null)
        {
            Logger.Error("浮窗", "关闭思考模式操作没有订阅者，无法执行用户请求。");
            RestoreDisableThinkingButton();
            return;
        }

        handler.Invoke(this, EventArgs.Empty);
    }

    private void NoticeDismissButton_OnClick(object sender, RoutedEventArgs e)
    {
        Logger.Info("浮窗", "用户点击了底部提示条按钮：不再提示。");
        EventHandler? handler = SuppressReasoningWarningRequested;
        if (handler is null)
        {
            Logger.Error("浮窗", "不再提示操作没有订阅者，无法执行用户请求。");
            return;
        }

        NoticeBorder.Visibility = Visibility.Collapsed;
        handler.Invoke(this, EventArgs.Empty);
    }

    private void SetLoadingState(bool isLoading)
    {
        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        ContentViewer.Visibility = isLoading ? Visibility.Collapsed : Visibility.Visible;
        // 朗读的是原文，翻译还没回来时原文也已经拿到了；但按 §4.4 的规定，
        // 加载期间工具条统一置灰，加载结束后再按语音是否可用决定。
        SpeechButton.IsEnabled = !isLoading && _speechService?.IsAvailable == true;
        StarButton.IsEnabled = !isLoading && _result?.RecordId is not null;
        CopyButton.IsEnabled = !isLoading && _result is not null;
    }

    /// <summary>
    /// 初始化朗读按钮。无可用语音时置灰，并把原因写进悬停提示（SPEC §8）。
    /// </summary>
    private void InitializeSpeechButton()
    {
        if (_speechService?.IsAvailable == true)
        {
            SpeechButton.ToolTip = "朗读原文";
            return;
        }

        SpeechButton.IsEnabled = false;
        SpeechButton.ToolTip = _speechService?.UnavailableReason
            ?? "系统里没有安装可用的语音包。可到 Windows 设置 → 时间和语言 → 语音 → 添加语音 安装。";
    }

    private void SpeechButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_speechService?.IsAvailable != true)
        {
            return;
        }

        try
        {
            if (_speechService.IsSpeaking)
            {
                _speechService.Stop();
                UpdateSpeechButtonState(speaking: false);
                return;
            }

            if (_speechService.Speak(_sourceText))
            {
                UpdateSpeechButtonState(speaking: true);
            }
        }
        catch (Exception exception)
        {
            Logger.Error("浮窗", "朗读操作失败。", exception);
            UpdateSpeechButtonState(speaking: false);
        }
    }

    private void UpdateSpeechButtonState(bool speaking)
    {
        SpeechButton.Content = speaking ? "stop" : "speech";
        SpeechButton.ToolTip = speaking ? "停止朗读" : "朗读原文";
    }

    private void SpeechService_OnSpeakCompleted(object? sender, EventArgs e)
    {
        // 朗读线程回调，必须切回 UI 线程改按钮。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosed)
            {
                UpdateSpeechButtonState(speaking: false);
            }
        }));
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
                paragraph.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
                paragraph.SetResourceReference(TextElement.FontSizeProperty, "Size.BodySmall");
                paragraph.Inlines.Add(new Run(line));
            }
            else if (line.StartsWith("例", StringComparison.Ordinal))
            {
                paragraph.Margin = new Thickness(0, 8, 0, 0);
                paragraph.Padding = new Thickness(8, 0, 0, 0);
                paragraph.BorderThickness = new Thickness(2, 0, 0, 0);
                paragraph.SetResourceReference(Block.BorderBrushProperty, "Brush.BorderStrong");
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
                paragraph.SetResourceReference(TextElement.FontSizeProperty, "Size.BodySmall");
                paragraph.Inlines.Add(new Run(line));
            }
            else
            {
                int separator = line.IndexOfAny(['.', '．', '。']);
                if (separator is > 0 and < 8)
                {
                    var partOfSpeech = new Run(line[..(separator + 1)]) { FontWeight = FontWeights.SemiBold };
                    partOfSpeech.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
                    paragraph.Inlines.Add(partOfSpeech);
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
        var icon = new System.Windows.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(0, 0, 6, -2)
        };
        icon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "Icon.Error");
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.Danger");

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "Brush.Danger");
        paragraph.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
        paragraph.Inlines.Add(new Run(message));
        document.Blocks.Add(paragraph);
        ContentViewer.Document = document;
    }

    private FlowDocument CreateDocument()
    {
        double fontSize = Math.Clamp(_settings.PopupFontSize, 10, 24);
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = fontSize,
            LineHeight = fontSize * 1.65
        };
        document.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        document.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        return document;
    }

    /// <summary>
    /// 发生了自动降级时，用一行小字说明本次是谁翻的。刻意不用醒目的警告条 ——
    /// 降级是软件替用户兜住了故障，不该做成打断阅读的红色报错。
    /// </summary>
    private void ShowFallbackNotice(TranslationResult result)
    {
        if (result.FellBackFromProfileName is not { Length: > 0 } failedProfile)
        {
            FallbackNoticeText.Visibility = Visibility.Collapsed;
            return;
        }

        FallbackNoticeText.Text = $"「{failedProfile}」暂时不可用，已自动改用「{result.ProfileName}」翻译。";
        FallbackNoticeText.Visibility = Visibility.Visible;
    }

    private void ShowReasoningWarning(string model)
    {
        ShowNotice(
            $"你配置的模型「{model}」正在“思考”，这会让翻译慢很多、也更费钱。",
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
        NoticeBorder.SetResourceReference(
            System.Windows.Controls.Border.BackgroundProperty,
            isWarning ? "Brush.WarningBg" : "Brush.DangerBg");
        NoticeText.SetResourceReference(
            TextElement.ForegroundProperty,
            isWarning ? "Brush.Warning" : "Brush.Danger");
        NoticeIcon.SetResourceReference(
            System.Windows.Shapes.Path.DataProperty,
            isWarning ? "Icon.Warning" : "Icon.Error");
        NoticeIcon.SetResourceReference(
            System.Windows.Shapes.Shape.StrokeProperty,
            isWarning ? "Brush.Warning" : "Brush.Danger");
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

    private void RepositionAndRefreshBounds()
    {
        try
        {
            DpiHelper.PositionPopup(this);
            SchedulePhysicalBoundsRefresh();
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
        if (handle == nint.Zero)
        {
            Logger.Warning("浮窗", "刷新浮窗边界时无法取得原生窗口句柄。");
            return;
        }

        if (!NativeMethods.GetWindowRect(handle, out NativeMethods.Rect rectangle))
        {
            Logger.Warning(
                "浮窗",
                $"刷新浮窗边界失败。Windows 错误码：{Marshal.GetLastWin32Error()}。");
            return;
        }

        _mouseHook.UpdateBounds(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
    }

    private void SchedulePhysicalBoundsRefresh()
    {
        if (_isClosed || Interlocked.Exchange(ref _boundsRefreshQueued, 1) != 0)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    Volatile.Write(ref _boundsRefreshQueued, 0);
                    if (!_isClosed)
                    {
                        RefreshPhysicalBounds();
                    }
                }),
                DispatcherPriority.Loaded);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _boundsRefreshQueued, 0);
            Logger.Error("浮窗", "安排浮窗边界刷新失败。", exception);
        }
    }

    private bool TryConfirmOutsideCurrentNativeBounds(int x, int y, out bool outside)
    {
        outside = false;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            Logger.Warning("浮窗", "关闭前无法取得浮窗句柄，已取消本次外部点击关闭。");
            return false;
        }

        if (!NativeMethods.GetWindowRect(handle, out NativeMethods.Rect rectangle))
        {
            Logger.Warning(
                "浮窗",
                $"关闭前无法复核浮窗边界，已取消本次关闭。Windows 错误码：{Marshal.GetLastWin32Error()}。");
            return false;
        }

        _mouseHook.UpdateBounds(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        outside = x < rectangle.Left
            || x >= rectangle.Right
            || y < rectangle.Top
            || y >= rectangle.Bottom;
        return true;
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
