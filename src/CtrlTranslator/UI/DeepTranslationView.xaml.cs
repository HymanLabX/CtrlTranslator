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
using CtrlTranslator.Speech;
using CtrlTranslator.Translation;
// 项目引用了 WinForms（托盘图标），下面几个名字两边都有，必须指名道姓。
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace CtrlTranslator.UI;

/// <summary>
/// 目标语言下拉的一项。<see cref="Value"/> 为空串代表「自动判断方向」。
/// </summary>
/// <remarks>
/// 声明成 public 是给 WPF 数据绑定用的：<c>DisplayMemberPath</c> 走反射取属性，
/// 类型本身不可见时会静默绑定失败，下拉里只剩类型名。
/// </remarks>
public sealed record LanguageChoice(string Label, string Value);

/// <summary>
/// 词条里的一个说法，供结果区按词条排版。查外文词时只有一个（就是那个词本身），
/// 用中文查英文时有 1–3 个候选。public 的理由同 <see cref="LanguageChoice"/>。
/// </summary>
/// <param name="IsHeadline">只有一个说法时为 true：它就是整块结果的标题，字号再大一档。</param>
/// <param name="CanCopy">候选说法可以单独复制；查外文词时复制的是释义，走标题栏那个复制按钮。</param>
public sealed record DeepWordCandidate(
    string Word,
    string Phonetic,
    IReadOnlyList<DictionarySense> Senses,
    bool IsHeadline,
    bool CanCopy,
    bool CanSpeak)
{
    public Visibility SpeechVisibility => CanSpeak ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CopyVisibility => CanCopy ? Visibility.Visible : Visibility.Collapsed;

    public string SpeakToolTip => $"朗读「{Word}」";

    public string CopyToolTip => $"复制「{Word}」";
}

/// <summary>
/// 主窗口的「深度翻译」页：坐下来精读一段文字，一次拿到译文、例句和常见搭配。
/// </summary>
/// <remarks>
/// <para>
/// 和划词浮窗是两条完全独立的路，<b>互不影响</b>：浮窗回答"这是什么意思"，要快；
/// 这一页回答"我该怎么写"，可以慢。两者用的提示词、超时、取消令牌都是分开的。
/// </para>
/// <para>
/// 结果只活在页面里 —— 不写缓存也不进翻译历史。
/// </para>
/// </remarks>
internal sealed partial class DeepTranslationView : UserControl
{
    /// <summary>流式输出重新分段的间隔。太密会让译文框每秒重排十几次，看着发抖。</summary>
    private static readonly TimeSpan StreamRefreshInterval = TimeSpan.FromMilliseconds(150);

    private readonly AppSettings _settings;
    private readonly TranslationService _translationService;
    private readonly SpeechService? _speechService;
    private readonly ObservableCollection<DeepExample> _examples = [];
    private readonly ObservableCollection<DeepCollocation> _collocations = [];
    private readonly ObservableCollection<DeepWordCandidate> _wordCandidates = [];
    private readonly StringBuilder _streamBuffer = new();
    private readonly DispatcherTimer _refreshTimer;
    private CancellationTokenSource? _cancellation;
    private Stopwatch? _stopwatch;
    private bool _streamDirty;
    private bool _switchedToResultView;

    /// <summary>这一次往哪边翻、是不是查词。开跑前算好，显示和提示词用的是同一份。</summary>
    private DeepPlan? _plan;

    /// <summary>这一次的原文。查外文词时，词条标题就是它。</summary>
    private string _sourceText = string.Empty;

    /// <summary>
    /// 标题栏「复制」按钮这一次该复制什么：普通译文是整段译文；
    /// 查外文词是释义；用中文查英文是排第一的那个说法。
    /// </summary>
    private string _primaryCopyText = string.Empty;

    /// <summary>复制成功后在底部那行显示的反馈，例如「译文已复制」「已复制「elephant」」。</summary>
    private string _primaryCopyFeedback = "译文已复制";

    /// <summary>上一次出错时修复按钮要做的事（去设置、打开日志……）。</summary>
    private ErrorAction _errorAction;

    public DeepTranslationView(AppSettings settings, TranslationService translationService, SpeechService? speechService)
    {
        InitializeComponent();
        _settings = settings;
        _translationService = translationService;
        _speechService = speechService;
        ExamplesList.ItemsSource = _examples;
        CollocationsList.ItemsSource = _collocations;
        WordCandidatesList.ItemsSource = _wordCandidates;
        // 例句模板里的朗读按钮看这个 Tag 决定显不显示：没有可用语音时整排都不出现，
        // 不留一个点了没反应的按钮。
        ExamplesList.Tag = CanSpeak ? Visibility.Visible : Visibility.Collapsed;

        _refreshTimer = new DispatcherTimer { Interval = StreamRefreshInterval };
        _refreshTimer.Tick += RefreshTimer_OnTick;

        SyncOptions();
        UpdateSourceState();
        IsVisibleChanged += DeepTranslationView_OnIsVisibleChanged;
    }

    /// <summary>
    /// 出错时用户点了「去设置」「打开日志」这类修复按钮。由 App 接手，
    /// 和浮窗上的修复按钮走同一套处理（「重试」在本页自己处理，不往外抛）。
    /// </summary>
    internal event EventHandler<PopupActionEventArgs>? ActionRequested;

    /// <summary>是否正在跑一次深度翻译。</summary>
    private bool IsRunning => _cancellation is not null;

    private bool CanSpeak => _speechService?.IsAvailable == true;

    /// <summary>
    /// 主窗口关闭时调用。不取消的话，一次跑了一半的深度翻译会拿着已经销毁的控件继续回调。
    /// </summary>
    public void CancelPending()
    {
        _cancellation?.Cancel();
        _refreshTimer.Stop();
        _speechService?.Stop();
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

        // 以前这一项叫「自动检测」。它放在「译成」后面，读起来像"检测原文是什么语言"，
        // 实际意思是"按原文自动决定往哪边翻"。规则写在悬停提示里，翻完后结果上也会标出方向。
        string? selectedLanguage = (LanguageCombo.SelectedItem as LanguageChoice)?.Value;
        List<LanguageChoice> choices = [new LanguageChoice("自动判断方向", DeepPromptBuilder.AutoLanguage)];
        AddLanguageChoice(choices, _settings.TargetLanguage);
        AddLanguageChoice(choices, _settings.FallbackLanguage);
        LanguageCombo.ItemsSource = choices;
        LanguageCombo.SelectedItem = choices.FirstOrDefault(
                choice => string.Equals(choice.Value, selectedLanguage, StringComparison.Ordinal))
            ?? choices[0];
        LanguageCombo.ToolTip =
            $"自动判断方向：外文译成{_settings.TargetLanguage}，{_settings.TargetLanguage}译成{_settings.FallbackLanguage}";
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
            // 切走了就别再念了：人已经不在这一页，声音还在响会让人找不到是哪儿发出来的。
            _speechService?.Stop();
            return;
        }

        SyncOptions();
        if (!IsRunning)
        {
            // 切过来就能直接打字，少一次点击。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => SourceBox.Focus()));
        }
    }

    /// <summary>Esc 停止生成。只在跑着的时候接管，别的时候 Esc 留给下拉框收起之类的默认行为。</summary>
    private void DeepTranslationView_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsRunning)
        {
            e.Handled = true;
            _cancellation?.Cancel();
            Logger.Info("深度翻译", "用户按 Esc 中止了本次深度翻译。");
        }
    }

    private void SourceBox_OnTextChanged(object sender, TextChangedEventArgs e) => UpdateSourceState();

    private void SourceBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = StartAsync();
        }
    }

    /// <summary>字数、占位提示、「清空」按钮跟着输入框内容走。</summary>
    private void UpdateSourceState()
    {
        int length = SourceBox.Text.Length;
        SourceCountText.Text = $"{length} / {AppSettings.DeepInputMaxChars}";
        SourceCountText.SetResourceReference(
            TextBlock.ForegroundProperty,
            length > AppSettings.DeepInputMaxChars ? "Brush.Danger" : "Brush.TextTertiary");
        SourcePlaceholder.Visibility = length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSourceButton.Visibility = length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearSourceButton_OnClick(object sender, RoutedEventArgs e)
    {
        SourceBox.Clear();
        SourceBox.Focus();
    }

    private void TranslateButton_OnClick(object sender, RoutedEventArgs e) => _ = StartAsync();

    private void RetryButton_OnClick(object sender, RoutedEventArgs e) => _ = StartAsync();

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        Logger.Info("深度翻译", "用户中止了本次深度翻译。");
    }

    private void ErrorActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        Logger.Info("深度翻译", $"用户点击了出错提示上的按钮：{ErrorActionButton.Content}。");
        EventHandler<PopupActionEventArgs>? handler = ActionRequested;
        if (handler is null)
        {
            Logger.Error("深度翻译", "出错提示上的修复操作没有订阅者，无法执行用户请求。");
            return;
        }

        handler.Invoke(this, new PopupActionEventArgs(_errorAction));
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
                + "深度翻译要为每个关键表达举例、给搭配，喂太长只会得到一堆泛泛而谈——请分段来。",
                ErrorAction.None,
                null,
                canRetry: false);
            return;
        }

        string modeId = (ModeCombo.SelectedItem as TranslationMode)?.Id ?? _settings.CurrentModeId;
        string? targetLanguage = (LanguageCombo.SelectedItem as LanguageChoice)?.Value;
        _sourceText = text;
        _plan = DeepPromptBuilder.Plan(text, _settings, targetLanguage);

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _streamBuffer.Clear();
        _streamDirty = false;
        _switchedToResultView = false;
        _stopwatch = Stopwatch.StartNew();
        _speechService?.Stop();
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
            UserFacingError error = ErrorMessages.FromException(
                exception,
                _settings.CurrentProfile.Model,
                ExtraRequestBodyHelper.IsThinkingDisabled(_settings.CurrentProfile.ExtraRequestBody));
            ShowError(error.Message, error.Action, error.ActionText, canRetry: true);
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

        // 查词时等「译文」那一节写完（后面的节开始了）再切过去：词条只有一两行，很快就完，
        // 半截的词条按词条排会一闪一闪地变形，不如多转一小会儿圈。
        bool isWord = _plan?.WordStyle is DeepWordStyle.Explain or DeepWordStyle.Equivalents;
        if (isWord && snapshot.LastSection == DeepSection.Translation)
        {
            return;
        }

        // 译文是三节里的第一节，会最先流出来。一拿到就切到结果视图，让用户看着它长出来，
        // 而不是对着转圈等三十秒。
        if (!_switchedToResultView)
        {
            _switchedToResultView = true;
            ShowResultView();
            RenderTranslation(snapshot.Translation);
        }
        else if (!isWord)
        {
            TranslationBox.Text = snapshot.Translation;
        }

        // 例句和搭配边生成边出现：只把<b>已经写完</b>的条目追加上去。
        // 当前正在写的那一节，最后一条可能还没写完（例句的译文行还没来），先不显示；
        // 下一条开始了，它就一定写完了。只追加不重排，不会闪。
        SyncList(_examples, CompletedItems(snapshot.Examples, snapshot.LastSection, DeepSection.Examples));
        SyncList(_collocations, CompletedItems(snapshot.Collocations, snapshot.LastSection, DeepSection.Collocations));
        UpdateSectionVisibility();
        ResultMetaText.Text = $"正在生成… {elapsed} 秒";
    }

    /// <summary>流式显示时，某一节里已经写完、可以放心显示的条目。</summary>
    private static IReadOnlyList<T> CompletedItems<T>(IReadOnlyList<T> items, DeepSection lastSection, DeepSection section)
    {
        if (lastSection < section)
        {
            return [];
        }

        return lastSection == section && items.Count > 0
            ? items.Take(items.Count - 1).ToList()
            : items;
    }

    /// <summary>
    /// 让列表和新内容一致。新内容只是在后面多了几条时就只追加 ——
    /// 整表清空重建会让已经显示的例句闪一下，读到一半的人会被打断。
    /// </summary>
    private static void SyncList<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        bool isPrefix = target.Count <= items.Count
            && target.Select((item, index) => EqualityComparer<T>.Default.Equals(item, items[index])).All(same => same);
        if (!isPrefix)
        {
            target.Clear();
        }

        for (int index = target.Count; index < items.Count; index++)
        {
            target.Add(items[index]);
        }
    }

    private void UpdateSectionVisibility()
    {
        ExamplesSection.Visibility = _examples.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CollocationsSection.Visibility = _collocations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _wordCandidates.Clear();
        TranslationBox.Clear();
        ResultMetaText.Text = string.Empty;
        DirectionRun.Text = string.Empty;
    }

    private void ExitRunningState()
    {
        TranslateButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    private void ShowResultView()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
        ResultScroller.Visibility = Visibility.Visible;
        // 前面两个空格是和「译文」两个字拉开的距离。
        DirectionRun.Text = _plan is null ? string.Empty : "  " + _plan.DirectionText;
    }

    private void ShowResult(DeepTranslationOutcome outcome)
    {
        DeepTranslationResult parsed = DeepResultParser.Parse(outcome.RawText);
        bool firstShow = !_switchedToResultView;
        Render(parsed);

        if (!parsed.HasTranslation)
        {
            // 三节标题一个都没认出来，且正文也是空的。与其显示一片空白，
            // 不如把模型原样吐出来的东西摆上去让用户自己看。
            ShowPlainTranslation(outcome.RawText.Trim());
        }

        ResultMetaText.Text = BuildMeta(outcome, parsed);
        if (firstShow)
        {
            ResultScroller.ScrollToTop();
        }
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
        _switchedToResultView = true;
        ShowResultView();
        RenderTranslation(parsed.Translation);

        // 和流式显示时走同一个同步：已经显示的条目原样留着，只补后面没出来的，结束那一下不闪。
        SyncList(_examples, parsed.Examples);
        SyncList(_collocations, parsed.Collocations);
        UpdateSectionVisibility();
    }

    /// <summary>
    /// 「译文」那一节：普通译文是一段能选中的文字；查词时按词条排版。
    /// 词条认不出结构（模型没守格式）就退回普通译文，不留空白。
    /// </summary>
    private void RenderTranslation(string translation)
    {
        switch (_plan?.WordStyle)
        {
            case DeepWordStyle.Explain when TryRenderExplain(translation):
            case DeepWordStyle.Equivalents when TryRenderEquivalents(translation):
                return;
            default:
                ShowPlainTranslation(translation);
                return;
        }
    }

    private void ShowPlainTranslation(string translation)
    {
        _wordCandidates.Clear();
        WordCandidatesList.Visibility = Visibility.Collapsed;
        TranslationBox.Visibility = Visibility.Visible;
        TranslationBox.Text = translation;
        SetPrimaryCopy(translation, "复制译文", "译文已复制");
    }

    /// <summary>
    /// 查外文词：标题是这个词本身（来自原文），下面是音标和词性释义。
    /// 复制按钮复制的是释义 —— 原文那个词用户自己手里就有。
    /// </summary>
    private bool TryRenderExplain(string translation)
    {
        DictionaryEntry entry = DictionaryEntryParser.Parse(translation);
        if (entry.Senses.Count == 0)
        {
            return false;
        }

        string headword = PromptBuilder.Headword(_sourceText);
        ShowWordCandidates([new DeepWordCandidate(headword, entry.Phonetic, entry.Senses, IsHeadline: true, CanCopy: false, CanSpeak)]);
        string definitions = string.Join(
            Environment.NewLine,
            entry.Senses.Select(sense => sense.PartOfSpeech.Length > 0 ? $"{sense.PartOfSpeech} {sense.Definition}" : sense.Definition));
        SetPrimaryCopy(definitions, "复制释义", "释义已复制");
        return true;
    }

    /// <summary>
    /// 用中文查英文：每个候选说法一块，最常用的排第一。
    /// 复制按钮复制排第一的那个说法 —— 查「大象」的人要的是 elephant 这个词，不是音标和释义。
    /// </summary>
    private bool TryRenderEquivalents(string translation)
    {
        IReadOnlyList<DictionaryEquivalent> equivalents = DictionaryEntryParser.ParseEquivalents(translation);
        if (equivalents.Count == 0)
        {
            return false;
        }

        bool single = equivalents.Count == 1;
        ShowWordCandidates(equivalents
            .Select(item => new DeepWordCandidate(
                item.Word,
                item.Phonetic,
                item.PartOfSpeech.Length > 0 || item.Note.Length > 0
                    ? [new DictionarySense(item.PartOfSpeech, item.Note)]
                    : [],
                IsHeadline: single,
                CanCopy: true,
                CanSpeak))
            .ToList());
        string first = equivalents[0].Word;
        SetPrimaryCopy(first, $"复制「{first}」", $"已复制「{first}」");
        return true;
    }

    private void ShowWordCandidates(IReadOnlyList<DeepWordCandidate> candidates)
    {
        TranslationBox.Clear();
        TranslationBox.Visibility = Visibility.Collapsed;
        WordCandidatesList.Visibility = Visibility.Visible;
        _wordCandidates.Clear();
        foreach (DeepWordCandidate candidate in candidates)
        {
            _wordCandidates.Add(candidate);
        }
    }

    private void SetPrimaryCopy(string text, string label, string feedback)
    {
        _primaryCopyText = text;
        _primaryCopyFeedback = feedback;
        CopyTranslationButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(CopyTranslationButton, label);
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

    /// <summary>
    /// 出错：原因 +「重试」+ 修复按钮（去设置 / 打开日志……，有才显示）。
    /// 修复动作本身就是「重试」时不重复放两个一样的按钮；原文超长这种重试也没用的，不放「重试」。
    /// </summary>
    private void ShowError(string message, ErrorAction action, string? actionText, bool canRetry)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Collapsed;
        ResultScroller.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Visible;
        ErrorText.Text = message;

        _errorAction = action;
        bool hasFix = action is not (ErrorAction.None or ErrorAction.Retry) && !string.IsNullOrWhiteSpace(actionText);
        ErrorActionButton.Content = actionText;
        ErrorActionButton.Visibility = hasFix ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        ErrorActionButton.Margin = canRetry ? new Thickness(8, 0, 0, 0) : new Thickness(0);
    }

    private async void CopyTranslation_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await CopyWithFeedbackAsync(_primaryCopyText, _primaryCopyFeedback);
        }
        catch (Exception exception)
        {
            Logger.Error("深度翻译", "复制译文时发生意外错误。", exception);
        }
    }

    private async void CopyWord_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is DeepWordCandidate candidate)
            {
                await CopyWithFeedbackAsync(candidate.Word, $"已复制「{candidate.Word}」");
            }
        }
        catch (Exception exception)
        {
            Logger.Error("深度翻译", "复制词条说法时发生意外错误。", exception);
        }
    }

    private async void CopyExample_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is DeepExample example)
            {
                await CopyWithFeedbackAsync(example.Text, "例句已复制");
            }
        }
        catch (Exception exception)
        {
            Logger.Error("深度翻译", "复制例句时发生意外错误。", exception);
        }
    }

    private async void CopyCollocation_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is DeepCollocation collocation)
            {
                await CopyWithFeedbackAsync(collocation.Text, $"已复制「{collocation.Text}」");
            }
        }
        catch (Exception exception)
        {
            Logger.Error("深度翻译", "复制搭配时发生意外错误。", exception);
        }
    }

    private const string CopyFailedFeedback = "剪贴板正被其他程序占用，没复制上，稍等一下再点一次。";

    /// <summary>
    /// 复制并在底部那行给 2 秒反馈。复制失败是常事（剪贴板被别的程序占着），
    /// 不弹窗打断，但必须让用户知道没复制上。
    /// </summary>
    private async Task CopyWithFeedbackAsync(string text, string feedback)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string previousMeta = ResultMetaText.Text;
        string shown;
        try
        {
            await ClipboardService.SetTextAsync(text);
            shown = feedback;
        }
        catch (ClipboardUnavailableException exception)
        {
            Logger.Error("深度翻译", "复制到剪贴板失败。", exception);
            shown = CopyFailedFeedback;
        }

        ResultMetaText.Text = shown;
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (string.Equals(ResultMetaText.Text, shown, StringComparison.Ordinal))
        {
            ResultMetaText.Text = previousMeta;
        }
    }

    private void SpeakWord_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeepWordCandidate candidate)
        {
            Speak(candidate.Word);
        }
    }

    private void SpeakExample_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeepExample example)
        {
            Speak(example.Text);
        }
    }

    /// <summary>
    /// 朗读。正在念的就是这一句时，再点一下是停；点别的句子就改念那一句。
    /// 语音按文字自动选（英文句子用英文语音），见 SpeechService。
    /// </summary>
    private void Speak(string text)
    {
        if (_speechService is not { IsAvailable: true } speech)
        {
            return;
        }

        try
        {
            if (speech.IsSpeaking && string.Equals(_lastSpoken, text, StringComparison.Ordinal))
            {
                speech.Stop();
                _lastSpoken = null;
                return;
            }

            _lastSpoken = speech.Speak(text) ? text : null;
        }
        catch (Exception exception)
        {
            Logger.Error("深度翻译", "朗读失败。", exception);
        }
    }

    private string? _lastSpoken;
}
