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
using CtrlTranslator.Core;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Interop;
using CtrlTranslator.Settings;
using CtrlTranslator.Speech;
using CtrlTranslator.Translation;

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

    /// <summary>顶行当前是词条标题（true）还是原文回显（false）。</summary>
    private bool _headwordMode;

    /// <summary>
    /// 收藏状态的唯一真相。以前是拿 <c>StarButton.Content</c> 的字符串反推的，
    /// 改个图标名就会静默改变逻辑，也没法在存库失败时回滚。
    /// </summary>
    private bool _starred;

    private readonly SpeechService? _speechService;

    public PopupWindow(string sourceText, string? appName, AppSettings settings, SpeechService? speechService = null)
    {
        InitializeComponent();
        _sourceText = sourceText;
        _appName = appName ?? "未知";
        _settings = settings;
        _speechService = speechService;
        // 配色必须在任何内容生成之前落位：后面 BuildDocument 里的 SetResourceReference
        // 也走同一套查找顺序，晚了就会先按浅色画一遍再跳色。
        ApplyTheme(settings.ResolvePopupTheme());
        Width = Math.Clamp(settings.PopupMaxWidth, 260, 900);
        MaxHeight = Math.Clamp(settings.PopupMaxHeight, 180, 900);
        ContentViewer.FontSize = Math.Clamp(settings.PopupFontSize, 10, 24);
        // 划的是一个词：顶行从一开始就是这个词的标题，加载中、出结果都不再跳样式。
        // 划的是句子：顶行是一行灰色小字的原文回显。
        if (PromptBuilder.IsWord(sourceText, settings.WordThreshold))
        {
            ShowHeadword();
        }
        else
        {
            ShowSourceEcho();
        }

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

    /// <summary>
    /// 把配色塞进<b>本窗口</b>的资源字典。DynamicResource 找不到就往上找，
    /// 于是这些同名键盖住了 App 里的浅色令牌，而其它窗口一点不受影响。
    /// </summary>
    private void ApplyTheme(PopupTheme theme)
    {
        foreach ((string key, System.Windows.Media.SolidColorBrush brush) in PopupThemePalette.Build(theme))
        {
            Resources[key] = brush;
        }
    }

    internal event EventHandler? ClosingRequested;

    internal event EventHandler<PopupActionEventArgs>? ActionRequested;

    internal event EventHandler<StarredChangedEventArgs>? StarredChanged;

    internal event EventHandler? SuppressReasoningWarningRequested;

    internal event EventHandler? DisableThinkingRequested;

    internal string SourceText => _sourceText;

    internal string AppName => _appName;

    /// <summary>
    /// 这次结果（或报错）来自哪个档案。「关闭思考模式」要改的是它 ——
    /// 自动降级之后它未必是当前档案。
    /// </summary>
    internal string? ProfileId { get; private set; }

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
        ProfileId = result.ProfileId;
        _loadingTimer.Stop();
        _stopwatch.Stop();
        SetLoadingState(isLoading: false);
        // 构造时按原文猜过一次是不是词条；最终以翻译服务的判定为准，两边不一致才换样式。
        if (result.IsWord != _headwordMode)
        {
            if (result.IsWord)
            {
                ShowHeadword();
            }
            else
            {
                ShowSourceEcho();
            }
        }

        RenderResult(result.ResultText, result.IsWord);
        CopyButton.IsEnabled = true;
        StarButton.IsEnabled = result.RecordId.HasValue;
        SetStarVisual(result.Starred);
        UpdateMetaText(result);
        ShowFallbackNotice(result);

        // 译文不完整比"模型在思考"更要紧：前者是结果本身有缺，后者只是慢和贵。
        if (result.IsTruncated)
        {
            ShowTruncationNotice(result);
        }
        else if (result.ShowReasoningWarning)
        {
            ShowReasoningWarning(result.Model);
        }

        RepositionAndRefreshBounds();
    }

    /// <summary>
    /// 译文被截断时必须说出来。以前这种情况毫无提示 —— 用户读到半截戛然而止的译文，
    /// 只会以为模型翻得不好，不知道调大一个数字就能解决。
    /// </summary>
    private void ShowTruncationNotice(TranslationResult result)
    {
        if (!string.Equals(result.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(result.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice("译文可能不完整：服务商没有正常完成输出。可以重新翻译一次。",
                ErrorAction.Retry, "重新翻译", isWarning: true);
            return;
        }

        string message = string.Equals(result.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase)
            ? "译文不完整：服务商的内容审核中途截停了输出。"
            : result.MaxTokens > 0
                ? $"译文不完整：写到了「最大输出长度」上限（{result.MaxTokens}）被截断了。可以在设置里调大。"
                : "译文不完整：写到了「最大输出长度」上限被截断了。可以在设置里调大。";
        ShowNotice(message, ErrorAction.Settings, "去设置", isWarning: true);
    }

    /// <summary>
    /// 顶行显示为词条标题：划的是一个词时，这个词本身就是浮窗的标题。
    /// </summary>
    /// <remarks>
    /// 以前词条模式下这个词只出现在顶行那行 12px 灰字里，正文从音标开始 ——
    /// 查词的人第一眼找的就是这个词，它却是整扇窗里最不起眼的东西。
    /// 字号跟着「字体大小」预设走（比译文大 3），选了大字的人标题也跟着大。
    /// </remarks>
    private void ShowHeadword()
    {
        _headwordMode = true;
        SourceEchoText.Inlines.Clear();
        SourceEchoText.Text = PromptBuilder.Headword(_sourceText);
        SourceEchoText.ToolTip = null;
        SourceEchoText.FontSize = Math.Clamp(_settings.PopupFontSize, 10, 24) + 3;
        SourceEchoText.FontWeight = FontWeights.SemiBold;
        SourceEchoText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.Text");
    }

    /// <summary>
    /// 顶行显示为原文回显：一行灰色小字，过长时<b>掐头留尾</b>，中间放一个省略标记。
    /// </summary>
    /// <remarks>
    /// 不用 <c>TextTrimming</c> 那种"截到放不下为止"：那样只能确认这段话从哪儿开始，
    /// 确认不了在哪儿结束。而划词最常见的失手正是<b>多划了半句或少划了结尾</b> ——
    /// 头尾都露出来才看得出选区边界对不对。
    /// <para>
    /// 宽度按<b>实际排版量出来</b>，不再按"字数 ≈ 宽度 ÷ 字号"估：
    /// 那个估法对西文偏保守一倍多，480 宽的浮窗只给 30 个字符，
    /// 一句英文被切成 <c>The simula (··· ···) hroughout.</c>，头尾都认不出来。
    /// </para>
    /// <para>
    /// 省略标记是一颗灰底小胶囊，不是文字：原文里本来就可能有「…」或括号，
    /// 写成文字就分不清哪个是原文、哪个是程序截掉的。胶囊一眼就不是内容。
    /// </para>
    /// </remarks>
    private void ShowSourceEcho()
    {
        _headwordMode = false;
        SourceEchoText.Inlines.Clear();
        SourceEchoText.SetResourceReference(System.Windows.Controls.TextBlock.FontSizeProperty, "Size.BodySmall");
        SourceEchoText.FontWeight = FontWeights.Normal;
        SourceEchoText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.TextTertiary");
        string echo = CollapseWhitespace(_sourceText);
        SourceEchoText.ToolTip = echo.Length > 0 ? echo : null;
        if (echo.Length == 0)
        {
            return;
        }

        double available = Width - EchoReservedWidth;
        var typeface = new Typeface(
            SourceEchoText.FontFamily,
            SourceEchoText.FontStyle,
            SourceEchoText.FontWeight,
            SourceEchoText.FontStretch);
        double fontSize = SourceEchoText.FontSize;
        double Measure(string text) => new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            fontSize,
            System.Windows.Media.Brushes.Black,
            1.0).WidthIncludingTrailingWhitespace;

        double room = available - EchoGapWidth;
        if (Measure(echo) <= available || room < 80)
        {
            // 放得下，或者窄到连"头 + 标记 + 尾"都排不开：整句交给 TextTrimming 兜底。
            SourceEchoText.Inlines.Add(new Run(echo));
            return;
        }

        // 开头多给一点：开头比结尾更能认出"这是哪一段"。
        int headLength = FitPrefix(echo, room * 0.55, Measure);
        string head = TrimHeadToWord(echo, headLength);
        int tailLength = FitSuffix(echo, room - Measure(head), Measure);
        string tail = TrimTailToWord(echo, tailLength);

        SourceEchoText.Inlines.Add(new Run(head));
        SourceEchoText.Inlines.Add(CreateEchoGap());
        SourceEchoText.Inlines.Add(new Run(tail));
    }

    /// <summary>顶行右侧钉住、关闭两个按钮，外壳内边距和边框占掉的宽度。</summary>
    private const double EchoReservedWidth = 96;

    /// <summary>省略胶囊连同两侧间距的宽度。</summary>
    private const double EchoGapWidth = 34;

    private static string CollapseWhitespace(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.ReplaceLineEndings(" "), @"\s+", " ").Trim();

    private static InlineUIContainer CreateEchoGap()
    {
        var label = new System.Windows.Controls.TextBlock
        {
            Text = "···",
            FontSize = 10,
            LineHeight = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.TextSecondary");
        var chip = new System.Windows.Controls.Border
        {
            Child = label,
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(5, 0, 5, 0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        chip.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Pressed");
        return new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center };
    }

    /// <summary>从头开始，量得下的最长前缀有几个字符（二分，不把代理对劈开）。</summary>
    private static int FitPrefix(string text, double budget, Func<string, double> measure)
    {
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (measure(text[..middle]) <= budget)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low > 0 && low < text.Length && char.IsHighSurrogate(text[low - 1]))
        {
            low--;
        }

        return low;
    }

    /// <summary>从尾开始，量得下的最长后缀有几个字符。</summary>
    private static int FitSuffix(string text, double budget, Func<string, double> measure)
    {
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (measure(text[^middle..]) <= budget)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low > 0 && low < text.Length && char.IsLowSurrogate(text[^low]))
        {
            low--;
        }

        return low;
    }

    /// <summary>
    /// 开头那截尽量断在词边界上。
    /// </summary>
    /// <remarks>
    /// 硬按宽度切会把单词拦腰截断（<c>simulation</c> 变成 <c>simula</c>），
    /// 那反而看不出选中的是什么。往回退到最近的空格 —— 但退让超过三成就不退了：
    /// 中文没有空格，硬退会把一大截内容砍掉。
    /// </remarks>
    private static string TrimHeadToWord(string text, int length)
    {
        if (length >= text.Length)
        {
            return text;
        }

        string slice = text[..length];
        bool cutsWord = length > 0 && !char.IsWhiteSpace(text[length]) && !char.IsWhiteSpace(slice[^1]);
        int space = slice.LastIndexOf(' ');
        return (cutsWord && space >= length * 0.7 ? slice[..space] : slice).TrimEnd();
    }

    /// <summary>结尾那截同样尽量断在词边界上。</summary>
    private static string TrimTailToWord(string text, int length)
    {
        if (length >= text.Length)
        {
            return text;
        }

        string slice = text[^length..];
        bool cutsWord = length > 0
            && !char.IsWhiteSpace(text[text.Length - length - 1])
            && !char.IsWhiteSpace(slice[0]);
        int space = slice.IndexOf(' ');
        return (cutsWord && space >= 0 && space <= length * 0.3 ? slice[(space + 1)..] : slice).TrimStart();
    }

    /// <summary>
    /// 底部那行「谁翻的、花了多久」。配了多个模型又开了失败回退之后，
    /// 用户根本不知道这次是哪个模型出的结果 —— 这信息以前只在主窗口的历史详情里有，
    /// 等你想去查的时候浮窗早关了。
    /// </summary>
    private void UpdateMetaText(TranslationResult result)
    {
        string model = string.IsNullOrWhiteSpace(result.Model) ? result.ProfileName : result.Model;
        MetaText.Text = result.FromCache
            ? $"{model} · 缓存"
            : $"{model} · {result.TotalLatencyMilliseconds / 1000d:0.0}s";
    }

    internal void ShowTranslationError(UserFacingError error)
    {
        if (_isClosed)
        {
            return;
        }

        ProfileId = error.ProfileId;
        _loadingTimer.Stop();
        _stopwatch.Stop();
        SetLoadingState(isLoading: false);

        // 报错只说一次：原因和修复按钮放在同一块红色提示里。
        // 以前红条里写「可以尝试下面的修复操作」、真正的原因在正文里再红一遍 ——
        // 同一件事说两次，而真正的原因离修复按钮最远。
        ContentViewer.Visibility = Visibility.Collapsed;
        ShowNotice(error.Message, error.Action, error.ActionText, isWarning: false);
        NoticeBorder.Margin = new Thickness(0, 8, 0, 0);

        // 出错时底部那行整个收起：收藏、复制没有对象，只剩一个朗读按钮孤零零挂在红条下面，
        // 看着像少画了东西。要听原文的人等翻译成功了再听。
        ActionRow.Visibility = Visibility.Collapsed;
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
        // 用户拖宽 / 拖窄浮窗后，回显的头尾要按新宽度重新截，否则窄了尾巴被切、宽了留一截空。
        if (e.WidthChanged && !_headwordMode && e.PreviousSize.Width > 0)
        {
            ShowSourceEcho();
        }

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

    /// <summary>
    /// 整扇浮窗都能拖，只排除两处：内容区（要能选中译文）和按钮（要能点）。
    /// </summary>
    /// <remarks>
    /// 以前只有顶部那条工具栏可拖。工具栏从 36px 收到 26px、中间又被原文占住之后，
    /// 可拖的带子太细，稍微瞄低一点就落到内容区，表现就是「拖了没反应」——
    /// 而且不会报错，日志里一条记录都没有，光看日志根本查不出来。
    /// </remarks>
    private void Shell_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ContentViewer) || node is System.Windows.Controls.Primitives.ButtonBase)
            {
                return;
            }
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

            await ClipboardService.SetTextAsync(_result.ResultText);
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

        // 先点亮再存库，存库是异步的；失败时由 RevertStar 改回来，
        // 否则用户会对着一颗亮着的星以为已经收藏了。
        bool starred = !_starred;
        SetStarVisual(starred);
        StarredChanged?.Invoke(this, new StarredChangedEventArgs(recordId, starred));
    }

    /// <summary>
    /// 收藏没写进数据库时把图标改回原样并给一句人话。
    /// 只改图标不吭声的话，用户不会知道这一条其实没存下来。
    /// </summary>
    internal void RevertStar(bool attemptedStarred)
    {
        if (_isClosed)
        {
            return;
        }

        SetStarVisual(!attemptedStarred);
        ShowNotice(
            attemptedStarred ? "收藏没有保存成功，可以再点一次试试。" : "取消收藏没有保存成功，可以再点一次试试。",
            ErrorAction.OpenLog,
            "打开日志",
            isWarning: true);
    }

    private void SetStarVisual(bool starred)
    {
        _starred = starred;
        StarButton.Content = starred ? "star-filled" : "star";
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
        SpeechButton.IsEnabled = !isLoading
            && _speechService?.IsAvailable == true
            && !string.IsNullOrWhiteSpace(_sourceText);
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

    /// <summary>
    /// 词典条目的排版。结构由 <see cref="DictionaryEntryParser"/> 拆好，这里只管画。
    /// </summary>
    /// <remarks>
    /// 认不出结构就整段退回普通译文：模型不守格式时，宁可给一段没排版的文字，
    /// 也不能给一个空白浮窗。
    /// </remarks>
    private void RenderDictionary(string text)
    {
        DictionaryEntry entry = DictionaryEntryParser.Parse(text);
        if (!entry.HasStructure)
        {
            RenderPlainText(text);
            return;
        }

        var document = CreateDocument();

        if (entry.Phonetic.Length > 0)
        {
            var phonetic = new Paragraph { Margin = new Thickness(0) };
            phonetic.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
            phonetic.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
            phonetic.SetResourceReference(TextElement.FontSizeProperty, "Size.BodySmall");
            phonetic.Inlines.Add(new Run(entry.Phonetic));
            document.Blocks.Add(phonetic);
        }

        for (int index = 0; index < entry.Senses.Count; index++)
        {
            DictionarySense sense = entry.Senses[index];
            bool first = index == 0 && entry.Phonetic.Length == 0;
            var paragraph = new Paragraph { Margin = new Thickness(0, first ? 0 : 5, 0, 0) };
            if (sense.PartOfSpeech.Length > 0)
            {
                var partOfSpeech = new Run(sense.PartOfSpeech + " ") { FontWeight = FontWeights.SemiBold };
                partOfSpeech.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
                paragraph.Inlines.Add(partOfSpeech);
            }

            paragraph.Inlines.Add(new Run(sense.Definition));
            document.Blocks.Add(paragraph);
        }

        foreach (DictionaryExample example in entry.Examples)
        {
            // 例句自成一块：左侧一道竖线圈起来，和上面的释义拉开距离。
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(9, 0, 0, 0),
                BorderThickness = new Thickness(2, 0, 0, 0)
            };
            paragraph.SetResourceReference(Block.BorderBrushProperty, "Brush.Accent");
            paragraph.Inlines.Add(new Run(example.Text));

            if (example.Translation.Length > 0)
            {
                // 例句和它的译文分两行：挤在一行里用「——」隔开时，
                // 长例句一换行，破折号两边的东西就看不出是一对了。
                paragraph.Inlines.Add(new LineBreak());
                var translation = new Run(example.Translation);
                translation.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
                translation.SetResourceReference(TextElement.FontSizeProperty, "Size.BodySmall");
                paragraph.Inlines.Add(translation);
            }

            document.Blocks.Add(paragraph);
        }

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
        List<string> notes = [];
        if (result.FellBackFromProfileName is { Length: > 0 } failedProfile)
        {
            notes.Add($"「{failedProfile}」暂时不可用，已自动改用「{result.ProfileName}」翻译。");
        }

        // 代理软件没开时软件自动改成了直连（负责人 2026-10 决定）。说一句，
        // 免得用户以为代理开着，或者不明白为什么这家需要代理的服务突然慢了。
        if (result.DirectConnectionProxy is { Length: > 0 } proxy)
        {
            notes.Add($"你设置的代理（{proxy}）没开，这次已自动直连。");
        }

        if (notes.Count == 0)
        {
            FallbackNoticeText.Visibility = Visibility.Collapsed;
            return;
        }

        FallbackNoticeText.Text = string.Join(Environment.NewLine, notes);
        FallbackNoticeText.Visibility = Visibility.Visible;
    }

    private void ShowReasoningWarning(string model)
    {
        ShowNotice(
            $"模型「{model}」开着“思考”，翻译会慢很多、也更费钱。",
            ErrorAction.Settings,
            "去设置",
            isWarning: true);
        DisableThinkingButton.Content = "关闭思考模式";
        DisableThinkingButton.IsEnabled = true;
        DisableThinkingButton.Visibility = Visibility.Visible;
        NoticeDismissButton.Visibility = Visibility.Visible;
        NoticeDismissButton.Content = "不再提示";
        UpdateNoticeActionsVisibility();
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
        foreach (System.Windows.Controls.Button button in new[] { DisableThinkingButton, NoticeActionButton, NoticeDismissButton })
        {
            button.SetResourceReference(ForegroundProperty, isWarning ? "Brush.Warning" : "Brush.Danger");
        }

        NoticeActionButton.Content = actionText;
        NoticeActionButton.Visibility = action == ErrorAction.None || string.IsNullOrWhiteSpace(actionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        // 每条提示自带自己的按钮组合：先收起只属于「思考模式提醒」的两个，
        // 需要的话由 ShowReasoningWarning 在这之后再打开。
        DisableThinkingButton.Visibility = Visibility.Collapsed;
        NoticeDismissButton.Visibility = Visibility.Collapsed;
        UpdateNoticeActionsVisibility();

        NoticeBorder.Visibility = Visibility.Visible;
    }

    /// <summary>一个按钮都没有时整行收起，否则提示条下面会空出一截。</summary>
    private void UpdateNoticeActionsVisibility()
    {
        bool any = DisableThinkingButton.Visibility == Visibility.Visible
            || NoticeActionButton.Visibility == Visibility.Visible
            || NoticeDismissButton.Visibility == Visibility.Visible;
        NoticeActions.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
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
}
