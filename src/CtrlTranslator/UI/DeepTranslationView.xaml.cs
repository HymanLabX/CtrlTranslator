using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CtrlTranslator.Core;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;
using CtrlTranslator.Translation;
// 项目引用了 WinForms（托盘图标），下面几个名字两边都有，必须指名道姓。
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace CtrlTranslator.UI;

/// <summary>
/// 目标语言下拉的一项。<see cref="Value"/> 为空串代表「自动检测」。
/// </summary>
/// <remarks>
/// 声明成 public 是给 WPF 数据绑定用的：<c>DisplayMemberPath</c> 走反射取属性，
/// 类型本身不可见时会静默绑定失败，下拉里只剩类型名。
/// </remarks>
public sealed record LanguageChoice(string Label, string Value);

/// <summary>
/// 主窗口的「深度翻译」页：坐下来精读一段文字，一次拿到译文、例句和常见搭配。
/// </summary>
/// <remarks>
/// <para>
/// 和划词浮窗是两条完全独立的路，<b>互不影响</b>：浮窗回答"这是什么意思"，要快；
/// 这一页回答"我该怎么写"，可以慢。两者用的提示词、超时、取消令牌都是分开的。
/// </para>
/// <para>
/// 结果只活在页面里 —— 不写缓存也不进翻译历史，切走再回来就没了。
/// </para>
/// </remarks>
internal sealed partial class DeepTranslationView : UserControl
{
    /// <summary>流式输出重新分段的间隔。太密会让译文框每秒重排十几次，看着发抖。</summary>
    private static readonly TimeSpan StreamRefreshInterval = TimeSpan.FromMilliseconds(150);

    private readonly AppSettings _settings;
    private readonly TranslationService _translationService;
    private readonly ObservableCollection<DeepExample> _examples = [];
    private readonly ObservableCollection<DeepCollocation> _collocations = [];
    private readonly StringBuilder _streamBuffer = new();
    private readonly DispatcherTimer _refreshTimer;
    private CancellationTokenSource? _cancellation;
    private Stopwatch? _stopwatch;
    private bool _streamDirty;
    private bool _switchedToResultView;

    public DeepTranslationView(AppSettings settings, TranslationService translationService)
    {
        InitializeComponent();
        _settings = settings;
        _translationService = translationService;
        ExamplesList.ItemsSource = _examples;
        CollocationsList.ItemsSource = _collocations;

        _refreshTimer = new DispatcherTimer { Interval = StreamRefreshInterval };
        _refreshTimer.Tick += RefreshTimer_OnTick;

        SyncOptions();
        UpdateSourceCount();
        IsVisibleChanged += DeepTranslationView_OnIsVisibleChanged;
    }

    /// <summary>是否正在跑一次深度翻译。</summary>
    private bool IsRunning => _cancellation is not null;

    /// <summary>
    /// 主窗口关闭时调用。不取消的话，一次跑了一半的深度翻译会拿着已经销毁的控件继续回调。
    /// </summary>
    public void CancelPending()
    {
        _cancellation?.Cancel();
        _refreshTimer.Stop();
    }

    /// <summary>
    /// 把模式和目标语言下拉重新对齐设置，<b>尽量保住用户当前的选择</b>。
    /// 每次切到本页都会调一次：用户可能刚在设置页改过模式或语言。
    /// </summary>
    public void SyncOptions()
    {
        string? selectedModeId = (ModeCombo.SelectedItem as TranslationMode)?.Id
            ?? _settings.CurrentModeId;
        ModeCombo.ItemsSource = null;
        ModeCombo.ItemsSource = _settings.Modes;
        ModeCombo.SelectedItem = _settings.Modes.FirstOrDefault(
                mode => string.Equals(mode.Id, selectedModeId, StringComparison.Ordinal))
            ?? _settings.Modes.FirstOrDefault();

        string? selectedLanguage = (LanguageCombo.SelectedItem as LanguageChoice)?.Value;
        List<LanguageChoice> choices = [new LanguageChoice("自动检测", DeepPromptBuilder.AutoLanguage)];
        AddLanguageChoice(choices, _settings.TargetLanguage);
        AddLanguageChoice(choices, _settings.FallbackLanguage);
        LanguageCombo.ItemsSource = choices;
        LanguageCombo.SelectedItem = choices.FirstOrDefault(
                choice => string.Equals(choice.Value, selectedLanguage, StringComparison.Ordinal))
            ?? choices[0];
    }

    private static void AddLanguageChoice(List<LanguageChoice> choices, string language)
    {
        string trimmed = language?.Trim() ?? string.Empty;
        if (trimmed.Length == 0
            || choices.Any(choice => string.Equals(choice.Value, trimmed, StringComparison.Ordinal)))
        {
            return;
        }

        choices.Add(new LanguageChoice(trimmed, trimmed));
    }

    private void DeepTranslationView_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        SyncOptions();
        if (!IsRunning)
        {
            // 切过来就能直接打字，少一次点击。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => SourceBox.Focus()));
        }
    }

    private void SourceBox_OnTextChanged(object sender, TextChangedEventArgs e) => UpdateSourceCount();

    private void SourceBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = StartAsync();
        }
    }

    private void UpdateSourceCount()
    {
        int length = SourceBox.Text.Length;
        SourceCountText.Text = $"{length} / {AppSettings.DeepInputMaxChars}";
        SourceCountText.SetResourceReference(
            TextBlock.ForegroundProperty,
            length > AppSettings.DeepInputMaxChars ? "Brush.Danger" : "Brush.TextTertiary");
    }

    private void TranslateButton_OnClick(object sender, RoutedEventArgs e) => _ = StartAsync();

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        Logger.Info("深度翻译", "用户中止了本次深度翻译。");
    }

    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        string text = SourceBox.Text.Trim();
        if (text.Length == 0)
        {
            SourceBox.Focus();
            return;
        }

        if (text.Length > AppSettings.DeepInputMaxChars)
        {
            ShowError(
                $"这段有 {text.Length} 个字符，超过了深度翻译的上限 {AppSettings.DeepInputMaxChars}。"
                + "深度翻译要为每个关键表达举例、给搭配，喂太长只会得到一堆泛泛而谈——请分段来。");
            return;
        }

        string modeId = (ModeCombo.SelectedItem as TranslationMode)?.Id ?? _settings.CurrentModeId;
        string? targetLanguage = (LanguageCombo.SelectedItem as LanguageChoice)?.Value;

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _streamBuffer.Clear();
        _streamDirty = false;
        _switchedToResultView = false;
        _stopwatch = Stopwatch.StartNew();
        EnterRunningState();
        _refreshTimer.Start();

        var progress = new Progress<TranslationStreamUpdate>(update =>
        {
            if (!ReferenceEquals(_cancellation, cancellation))
            {
                return;
            }

            _streamBuffer.Append(update.Delta);
            _streamDirty = true;
        });

        try
        {
            DeepTranslationOutcome outcome = await _translationService.TranslateDeepAsync(
                text,
                modeId,
                targetLanguage,
                progress,
                cancellation.Token);
            ShowResult(outcome);
        }
        catch (OperationCanceledException)
        {
            // 已经生成出来的部分留在屏幕上：用户按停止往往是"够了"，不是"作废重来"。
            ShowStopped();
        }
        catch (Exception exception)
        {
            Logger.Error("深度翻译", "深度翻译失败。", exception);
            ShowError(ErrorMessages.FromException(
                exception,
                _settings.CurrentProfile.Model,
                ExtraRequestBodyHelper.IsThinkingDisabled(_settings.CurrentProfile.ExtraRequestBody)).Message);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            _refreshTimer.Stop();
            _stopwatch?.Stop();
            cancellation.Dispose();
            ExitRunningState();
        }
    }

    private void RefreshTimer_OnTick(object? sender, EventArgs e)
    {
        double seconds = (_stopwatch?.ElapsedMilliseconds ?? 0) / 1000d;
        string elapsed = seconds.ToString("F1", CultureInfo.CurrentCulture);
        LoadingText.Text = $"正在深度翻译… {elapsed} 秒";

        if (!_streamDirty)
        {
            if (_switchedToResultView)
            {
                ResultMetaText.Text = $"正在生成… {elapsed} 秒";
            }

            return;
        }

        _streamDirty = false;
        DeepTranslationResult snapshot = DeepResultParser.Parse(_streamBuffer.ToString());
        if (!snapshot.HasTranslation)
        {
            return;
        }

        // 译文是三节里的第一节，会最先流出来。一拿到就切到结果视图，让用户看着它长出来，
        // 而不是对着转圈等三十秒。例句和搭配等收完再一次性渲染 ——
        // 边收边重建列表会让它们不停闪。
        if (!_switchedToResultView)
        {
            _switchedToResultView = true;
            EmptyState.Visibility = Visibility.Collapsed;
            LoadingState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Collapsed;
            ResultScroller.Visibility = Visibility.Visible;
        }

        TranslationBox.Text = snapshot.Translation;
        ResultMetaText.Text = $"正在生成… {elapsed} 秒";
    }

    private void EnterRunningState()
    {
        TranslateButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
        ResultScroller.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Visible;
        LoadingText.Text = "正在深度翻译… 0.0 秒";
        ExamplesSection.Visibility = Visibility.Collapsed;
        CollocationsSection.Visibility = Visibility.Collapsed;
        _examples.Clear();
        _collocations.Clear();
        TranslationBox.Clear();
        ResultMetaText.Text = string.Empty;
    }

    private void ExitRunningState()
    {
        TranslateButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    private void ShowResult(DeepTranslationOutcome outcome)
    {
        DeepTranslationResult parsed = DeepResultParser.Parse(outcome.RawText);
        Render(parsed);

        if (!parsed.HasTranslation)
        {
            // 三节标题一个都没认出来，且正文也是空的。与其显示一片空白，
            // 不如把模型原样吐出来的东西摆上去让用户自己看。
            TranslationBox.Text = outcome.RawText.Trim();
        }

        ResultMetaText.Text = BuildMeta(outcome, parsed);
    }

    private void ShowStopped()
    {
        DeepTranslationResult parsed = DeepResultParser.Parse(_streamBuffer.ToString());
        if (!parsed.HasTranslation)
        {
            ResetToEmptyState();
            return;
        }

        Render(parsed);
        ResultMetaText.Text = "已停止生成，上面是已经收到的部分。";
    }

    private void Render(DeepTranslationResult parsed)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
        ResultScroller.Visibility = Visibility.Visible;

        TranslationBox.Text = parsed.Translation;

        _examples.Clear();
        foreach (DeepExample example in parsed.Examples)
        {
            _examples.Add(example);
        }

        _collocations.Clear();
        foreach (DeepCollocation collocation in parsed.Collocations)
        {
            _collocations.Add(collocation);
        }

        ExamplesSection.Visibility = parsed.HasExamples ? Visibility.Visible : Visibility.Collapsed;
        CollocationsSection.Visibility = parsed.HasCollocations ? Visibility.Visible : Visibility.Collapsed;
        ResultScroller.ScrollToTop();
    }

    private static string BuildMeta(DeepTranslationOutcome outcome, DeepTranslationResult parsed)
    {
        var parts = new List<string>
        {
            $"{outcome.Model}",
            $"{outcome.TotalLatencyMilliseconds / 1000d:F1} 秒"
        };

        if (outcome.FellBackFromProfileName is { Length: > 0 } failed)
        {
            parts.Add($"「{failed}」没响应，已改用「{outcome.ProfileName}」");
        }

        if (outcome.DirectConnectionProxy is { Length: > 0 } proxy)
        {
            parts.Add($"代理（{proxy}）没开，已自动直连");
        }

        // 写到输出上限被截断时，最后一节（通常是搭配）会缺一截，必须说出来。
        if (outcome.IsTruncated)
        {
            parts.Add(string.Equals(outcome.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase)
                ? "服务商的内容审核中途截停了输出，结果不完整"
                : "写到输出上限被截断了，结果不完整，可以把原文分短一点再翻");
        }

        // 模型没守格式时，用户至少该知道"例句那一节没出来"不是软件吞了。
        if (!parsed.HasExamples && !parsed.HasCollocations)
        {
            parts.Add("这次模型只给了译文，没按格式给例句和搭配，可以再翻一次");
        }

        return string.Join(" · ", parts);
    }


    private void ResetToEmptyState()
    {
        ResultScroller.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Collapsed;
        ResultScroller.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Visible;
        ErrorText.Text = message;
    }

    private const string CopiedFeedback = "译文已复制";
    private const string CopyFailedFeedback = "剪贴板正被其他程序占用，没复制上，稍等一下再点一次。";

    private async void CopyTranslation_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = TranslationBox.Text;
            if (text.Length == 0)
            {
                return;
            }

            string previousMeta = ResultMetaText.Text;
            try
            {
                await ClipboardService.SetTextAsync(text);
                // 复制成功要看得见。以前点了没有任何反馈，用户分不清复制上了没有。
                ResultMetaText.Text = CopiedFeedback;
            }
            catch (ClipboardUnavailableException exception)
            {
                // 剪贴板被别的程序占着是常事，不该为此弹窗打断用户；但必须让他知道没复制上。
                Logger.Error("深度翻译", "复制译文到剪贴板失败。", exception);
                ResultMetaText.Text = CopyFailedFeedback;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            if (ResultMetaText.Text is CopiedFeedback or CopyFailedFeedback)
            {
                ResultMetaText.Text = previousMeta;
            }
        }
        catch (Exception exception)
        {
            Logger.Error("深度翻译", "复制译文时发生意外错误。", exception);
        }
    }
}
