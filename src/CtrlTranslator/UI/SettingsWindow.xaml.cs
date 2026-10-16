using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CtrlTranslator.Core;
using CtrlTranslator.Data;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;
using CtrlTranslator.Translation;
using CtrlTranslator.Update;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using TextBox = System.Windows.Controls.TextBox;

namespace CtrlTranslator.UI;

public enum SettingsChangeKind
{
    General,
    KeyboardHook
}

/// <summary>
/// 嵌入主窗口的设置视图。字段离开编辑状态后立即保存，开关和下拉选择则点击后立即保存。
/// 连接信息与模型参数写入<b>列表里选中的那个档案</b>，不一定是当前正在用的档案。
/// </summary>
public partial class SettingsWindow : System.Windows.Controls.UserControl
{
    private const int GeneralSectionIndex = 0;
    private const int ProfilesSectionIndex = 1;

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryRepository _historyRepository;
    private readonly Action<SettingsChangeKind> _settingsChanged;
    private readonly Func<ApiProfile, DiagnosticRunner> _diagnosticRunnerFactory;
    private readonly UpdateService? _updateService;
    private readonly Action<ReleaseInfo> _showUpdateWindow;
    private readonly DispatcherTimer _triggerSaveTimer;
    private readonly DispatcherTimer _saveFeedbackTimer;
    private readonly ObservableCollection<DiagnosticResult> _testResults = [];
    private readonly ScrollViewer[] _sections;
    private readonly System.Windows.Controls.RadioButton[] _sectionTabs;
    private CancellationTokenSource? _modelFetchCancellation;
    private CancellationTokenSource? _testCancellation;
    private DiagnosticRunner? _lastTestRunner;
    private bool _loading = true;
    // 浮窗尺寸 / 字号不再有输入框，预设按钮直接改这三个字段，再由 SaveAll 统一落盘 ——
    // 和「浮窗配色」走的是同一条路。
    private double _popupMaxWidth = 480;
    private double _popupMaxHeight = 400;
    private double _popupFontSize = 14;
    private string _popupThemeName = PopupThemes.DefaultName;
    private string _popupCustomSurface = "#FFFFFF";
    private string _popupCustomText = "#171717";
    private bool _startWithWindows;
    private bool _apiKeyVisible;
    private bool _feedbackReady;

    /// <summary>
    /// 「新建模型」正在填、但还没成型的那一个。<b>刻意不放进 <c>_settings.Profiles</c></b>：
    /// 一旦放进去，它立刻就是一张卡片、会被写盘、还会参与划词失败时的回退，
    /// 而此时它连接口地址都没有。填齐三项（接口地址 / API Key / 模型名称）才真正加入。
    /// </summary>
    private ApiProfile? _draftProfile;

    /// <summary>
    /// 草稿是否已经通过一次连接检测。<b>这是"能不能添加"的唯一判据</b> ——
    /// 光看三个字段非空只能证明"填了东西"，证明不了这个模型真能翻译；
    /// 地址打错一个字母、Key 过期、模型名不存在，全都要等到真正划词时才暴露。
    /// 任何一处连接信息被改动都会把它重置回 false（见 SaveAll）。
    /// </summary>
    private bool _draftTestPassed;

    /// <summary>拖动排序用：按下时的坐标和被按住的那张卡片。</summary>
    private System.Windows.Point _cardDragStart;
    private ProfileListItem? _cardDragCandidate;
    private bool _cardDragStarted;

    /// <summary>模式卡片：这次按下是不是落在某张卡片上（抬起时才决定跳不跳详情）。</summary>
    private bool _modeCardPressed;

    public SettingsWindow(
        AppSettings settings,
        SettingsStore settingsStore,
        HistoryRepository historyRepository,
        Action<SettingsChangeKind> settingsChanged,
        Func<ApiProfile, DiagnosticRunner> diagnosticRunnerFactory,
        UpdateService? updateService,
        Action<ReleaseInfo> showUpdateWindow)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _historyRepository = historyRepository;
        _settingsChanged = settingsChanged;
        _diagnosticRunnerFactory = diagnosticRunnerFactory;
        _updateService = updateService;
        _showUpdateWindow = showUpdateWindow;
        _triggerSaveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            TriggerSaveTimer_OnTick,
            Dispatcher);
        _saveFeedbackTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2.5),
            DispatcherPriority.Background,
            SaveFeedbackTimer_OnTick,
            Dispatcher);

        _sections =
        [
            GeneralSection,
            ProfilesSection,
            BehaviorSection,
            AboutSection
        ];
        _sectionTabs =
        [
            GeneralTabButton,
            ServiceTabButton,
            PreferenceTabButton,
            AboutTabButton
        ];
        TestResultsList.ItemsSource = _testResults;

        LoadControlsFromSettings();
        SelectSection(GeneralSectionIndex);
        _loading = false;
        UpdateExtraRequestBodyValidation();
        UpdateModeButtons();
    }

    /// <summary>打开「服务商档案」页；需要时把焦点直接落到额外请求参数上。</summary>
    internal void ShowTranslationService(bool focusExtraRequestBody)
    {
        // 这条路是浮窗上"打开设置"点进来的，用户要改的是当前那个模型的参数，
        // 所以直接落到详情页，不要停在卡片网格上让人再点一次。
        SelectSection(ProfilesSectionIndex);
        ShowProfileDetail();

        if (focusExtraRequestBody)
        {
            AdvancedModelSettingsExpander.IsExpanded = true;
            Dispatcher.BeginInvoke(() =>
            {
                ExtraRequestBodyBox.Focus();
                ExtraRequestBodyBox.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    /// <summary>打开当前模型的详情并立刻跑一遍检测。浮窗上那些"查不出原因"的错误走这条路。</summary>
    internal void ShowProfileTest()
    {
        SelectSection(ProfilesSectionIndex);
        ShowProfileDetail();
        _ = RunProfileTestAsync();
    }

    /// <summary>
    /// 草稿优先。这样所有既有的字段处理器（改名、改地址、改 Key、SaveAll…）
    /// 不用知道草稿的存在，照常读写"当前这个档案"即可；
    /// 草稿不在 <c>_settings.Profiles</c> 里，SaveAll 自然也就落不了盘。
    /// </summary>
    private ApiProfile? SelectedProfile =>
        _draftProfile ?? (ProfilesList.SelectedItem as ProfileListItem)?.Profile;

    /// <summary>模式列表现在装的是卡片包装项，取值统一走这里。</summary>
    private TranslationMode? SelectedMode => (ModesList.SelectedItem as ModeListItem)?.Mode;

    /// <summary>
    /// 地址、模型齐了，再加上 Key（本机服务不需要），才算"配置好了"：缺一样，这个档案划词时必然失败。
    /// </summary>
    private static bool IsProfileUsable(ApiProfile profile) => profile.IsConfigured;

    internal void SelectSection(int index)
    {
        if (index < 0 || index >= _sections.Length)
        {
            return;
        }

        for (int position = 0; position < _sections.Length; position++)
        {
            _sections[position].Visibility = position == index
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_sectionTabs[index].IsChecked != true)
        {
            _sectionTabs[index].IsChecked = true;
        }
    }

    private void SettingsTab_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not System.Windows.Controls.RadioButton { Tag: string tag }
            || !int.TryParse(tag, CultureInfo.InvariantCulture, out int index))
        {
            return;
        }

        SelectSection(index);
    }

    private void SettingsView_OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _feedbackReady = true;
            ShowIdleSaveStatus();
        }, DispatcherPriority.ContextIdle);
    }

    private void LoadControlsFromSettings()
    {
        ProviderPresetCombo.ItemsSource = ProviderPresets.All.Select(preset => preset.Name).ToArray();
        RefreshProfiles(_settings.CurrentProfileId);

        RefreshModes(_settings.CurrentModeId);
        TargetLanguageBox.Text = _settings.TargetLanguage;
        FallbackLanguageBox.Text = _settings.FallbackLanguage;
        WordThresholdBox.Text = _settings.WordThreshold.ToString(CultureInfo.CurrentCulture);

        SelectComboItemByContent(TriggerKeyCombo, _settings.TriggerKey);
        DoubleTapIntervalBox.Text = Math
            .Clamp(_settings.DoubleTapIntervalMs, 150, 800)
            .ToString(CultureInfo.CurrentCulture);
        MaxKeyHoldBox.Text = _settings.MaxKeyHoldMs.ToString(CultureInfo.CurrentCulture);
        ClipboardWaitBox.Text = _settings.ClipboardWaitMs.ToString(CultureInfo.CurrentCulture);
        EnabledCheckBox.IsChecked = _settings.Enabled;

        LongTextThresholdBox.Text = _settings.LongTextThreshold.ToString(CultureInfo.CurrentCulture);
        PrivacyModeCheckBox.IsChecked = _settings.PrivacyMode;
        DetailedLoggingCheckBox.IsChecked = _settings.DetailedLogging;

        _startWithWindows = _settings.StartWithWindows;
        RefreshStartWithWindows();
        _popupMaxWidth = _settings.PopupMaxWidth;
        _popupMaxHeight = _settings.PopupMaxHeight;
        _popupFontSize = _settings.PopupFontSize;
        _popupThemeName = PopupThemes.IsKnown(_settings.PopupTheme)
            ? _settings.PopupTheme
            : PopupThemes.DefaultName;
        _popupCustomSurface = _settings.PopupCustomSurface;
        _popupCustomText = _settings.PopupCustomText;
        RefreshPopupPresets();
        RefreshPopupThemes();
        StreamFallbackDelayBox.Text = _settings.StreamFallbackDelayMs.ToString(CultureInfo.CurrentCulture);

        AboutVersionText.Text = $"v{UpdateChecker.CurrentVersion.ToString(3)}";
        AutoCheckUpdatesCheckBox.IsChecked = _settings.AutoCheckUpdates;
        UpdateStatusText.Text = _updateService?.PendingRelease is { } pending
            ? $"发现新版本 {pending.Version.ToString(3)}，点「检查更新」可以立即安装。"
            : $"当前版本 {UpdateChecker.CurrentVersion.ToString(3)}。更新包会先校验 SHA256 再安装，校验不通过一律丢弃。";
    }

    private async void CheckUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_updateService is null)
        {
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "检查中…";
        UpdateStatusText.Text = "正在向 GitHub 查询最新版本…";

        try
        {
            UpdateCheckResult result = await _updateService.CheckManuallyAsync(CancellationToken.None);
            string current = UpdateChecker.CurrentVersion.ToString(3);
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Release is { } release:
                    UpdateStatusText.Text = $"发现新版本 {release.Version.ToString(3)}。";
                    _showUpdateWindow(release);
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateStatusText.Text = $"已经是最新版本 {current}。";
                    break;
                case UpdateCheckStatus.NoRelease:
                    UpdateStatusText.Text =
                        $"暂时查不到发布信息：发布页面不存在，或者还没有发布过正式版本。当前版本 {current}。";
                    break;
                default:
                    UpdateStatusText.Text =
                        $"检查更新没有完成：{result.Detail ?? "原因不明"}。"
                        + "如果你在国内且没开代理，多半是连不上 GitHub。当前版本 " + current + "。";
                    break;
            }
        }
        catch (Exception exception)
        {
            Logger.Error("自动更新", "从设置页检查更新失败。", exception);
            UpdateStatusText.Text = "检查更新没有完成，详情已记录到日志。";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            CheckUpdateButton.Content = "检查更新";
        }
    }

    private bool SaveAll(SettingsChangeKind changeKind = SettingsChangeKind.General)
    {
        if (_loading)
        {
            return false;
        }

        try
        {
            if (!TryReadDouble(TemperatureBox, "Temperature", 0, 2, out double temperature)
                || !TryReadInt(MaxTokensBox, "最大输出长度", 1, 128_000, out int maxTokens)
                || !TryReadInt(RequestTimeoutBox, "请求超时", 2, 15, out int requestTimeout)
                || !TryReadInt(FirstTokenTimeoutBox, "首字超时", 1, 10, out int firstTokenTimeout)
                || !TryReadInt(WordThresholdBox, "词典模式词数阈值", 1, 8, out int wordThreshold)
                || !TryReadInt(DoubleTapIntervalBox, "双击间隔", 150, 800, out int doubleTapInterval)
                || !TryReadInt(MaxKeyHoldBox, "单次按键最长时长", 1, 5_000, out int maxKeyHold)
                || !TryReadInt(ClipboardWaitBox, "取词等待时间", 100, 1_000, out int clipboardWait)
                || !TryReadInt(LongTextThresholdBox, "长文本二次确认阈值", 0, AppSettings.MaxTranslatableChars, out int longTextThreshold)
                || !TryReadInt(StreamFallbackDelayBox, "流式回退延迟", 0, 10_000, out int streamDelay))
            {
                return false;
            }

            if (firstTokenTimeout >= requestTimeout)
            {
                ShowValidationError("首字超时必须小于整体超时，请修改后再试。");
                FirstTokenTimeoutBox.Focus();
                return false;
            }

            bool profileNameChanged = false;
            if (SelectedProfile is { } profile)
            {
                string previousBaseUrl = profile.BaseUrl;
                string previousApiKey = profile.ApiKey;
                string previousModel = profile.Model;

                profile.ProviderPreset = ProviderPresetCombo.SelectedItem as string ?? ProviderPresets.CustomName;
                profile.BaseUrl = BaseUrlBox.Text.Trim();
                profile.ApiKey = CurrentApiKey().Trim();
                profile.Model = ModelCombo.Text.Trim();
                profile.Temperature = temperature;
                profile.MaxTokens = maxTokens;
                profile.RequestTimeoutSeconds = requestTimeout;
                profile.FirstTokenTimeoutSeconds = firstTokenTimeout;
                profile.ExtraRequestBody = ExtraRequestBodyBox.Text.Trim();

                // 用户动手改过连接信息就说明他在修，标记该退场了 —— 否则会出现
                // "我明明换了新 Key，它怎么还在用备用"这种最气人的状态。
                bool connectionEdited = !string.Equals(previousBaseUrl, profile.BaseUrl, StringComparison.Ordinal)
                    || !string.Equals(previousApiKey, profile.ApiKey, StringComparison.Ordinal)
                    || !string.Equals(previousModel, profile.Model, StringComparison.Ordinal);
                if (connectionEdited && profile.IsFailed)
                {
                    profile.ClearFailure();
                    Logger.Info("设置", $"档案「{profile.Name}」的连接信息已修改，失效标记自动解除。");
                    profileNameChanged = true;
                }

                if (connectionEdited)
                {
                    profileNameChanged = true;

                    // 连接信息一改，上一次的检测结论就作废了 —— 否则会出现
                    // 「用对的 Key 测通过、然后改成错的 Key 也能添加」这种漏洞。
                    if (_draftProfile is not null)
                    {
                        _draftTestPassed = false;
                        _testResults.Clear();
                        TestProfileButton.Content = "开始检测";
                        TestStatusText.Text = "连接信息改过了，需要重新检测一次。";
                    }
                }
            }

            _settings.TargetLanguage = TargetLanguageBox.Text.Trim();
            _settings.FallbackLanguage = FallbackLanguageBox.Text.Trim();
            _settings.WordThreshold = wordThreshold;
            _settings.TriggerKey = SelectedComboContent(TriggerKeyCombo, "Ctrl");
            _settings.DoubleTapIntervalMs = doubleTapInterval;
            _settings.MaxKeyHoldMs = maxKeyHold;
            _settings.ClipboardWaitMs = clipboardWait;
            _settings.Enabled = EnabledCheckBox.IsChecked == true;
            _settings.LongTextThreshold = longTextThreshold;
            _settings.PrivacyMode = PrivacyModeCheckBox.IsChecked == true;
            _settings.DetailedLogging = DetailedLoggingCheckBox.IsChecked == true;
            _settings.StartWithWindows = _startWithWindows;
            _settings.PopupFontSize = _popupFontSize;
            _settings.PopupMaxWidth = _popupMaxWidth;
            _settings.PopupMaxHeight = _popupMaxHeight;
            _settings.PopupTheme = _popupThemeName;
            _settings.PopupCustomSurface = _popupCustomSurface;
            _settings.PopupCustomText = _popupCustomText;
            _settings.StreamFallbackDelayMs = streamDelay;
            _settings.AutoCheckUpdates = AutoCheckUpdatesCheckBox.IsChecked == true;

            if (!_settingsStore.TrySave(_settings, out string? errorMessage))
            {
                ShowValidationError(errorMessage ?? "设置保存失败，请稍后重试。");
                return false;
            }

            Logger.SetApiKeys(_settings.Profiles.Select(item => item.ApiKey));
            Logger.Info("设置", "设置窗口中的修改已保存并立即生效。");
            // 三个浮窗数值刚才可能被手改过，预设高亮要跟着走，不能停在旧的那套上。
            RefreshPopupPresets();
            // 草稿期间这两件都要跳过：它还没入库，说"已自动保存"是骗人的
            // （返回时可能就没了）；刷新网格也只会把详情页标题冲回自动生成的名字。
            if (_feedbackReady && _draftProfile is null)
            {
                ShowTemporaryStatus("已自动保存");
            }
            if (profileNameChanged && _draftProfile is null)
            {
                RefreshProfiles();
            }

            UpdateProfileButtons();
            _settingsChanged(changeKind);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "保存设置窗口中的修改失败。", exception);
            ShowValidationError("设置没有保存成功，详情已记录到日志。请稍后重试。");
            return false;
        }
    }

    // ==================== 服务商档案 ====================

    private void RefreshProfiles(string? selectProfileId = null)
    {
        string? desired = selectProfileId ?? SelectedProfile?.Id ?? _settings.CurrentProfileId;

        // 「备用 N」的 N 按列表顺序数，当前那个不占号 —— 卡片上的编号要和实际回退次序一致。
        List<ProfileListItem> items = [];
        int backupOrder = 0;
        foreach (ApiProfile profile in _settings.Profiles)
        {
            bool isCurrent = string.Equals(profile.Id, _settings.CurrentProfileId, StringComparison.Ordinal);
            items.Add(new ProfileListItem(profile, isCurrent, isCurrent ? 0 : ++backupOrder));
        }

        ProfileListItem? currentItem = items.FirstOrDefault(item => item.IsCurrent);
        ProfileOverviewCaption.Text = currentItem is null
            ? "还没有指定当前模型。"
            : $"当前由「{currentItem.Name}」· {currentItem.Detail} 提供翻译；失败时按下面的先后顺序回退。";

        bool previousLoading = _loading;
        _loading = true;
        try
        {
            ProfilesList.ItemsSource = null;
            ProfilesList.ItemsSource = items;
            ProfilesList.SelectedItem = items.FirstOrDefault(item =>
                    string.Equals(item.Profile.Id, desired, StringComparison.Ordinal))
                ?? items.FirstOrDefault();
            LoadProfileDetails();
        }
        finally
        {
            _loading = previousLoading;
        }

        UpdateProfileButtons();
    }

    private void LoadProfileDetails()
    {
        ApiProfile? profile = SelectedProfile;
        if (profile is null)
        {
            ProfileNameBox.Text = string.Empty;
            BaseUrlBox.Text = string.Empty;
            ApiKeyPasswordBox.Password = string.Empty;
            ApiKeyVisibleBox.Text = string.Empty;
            ModelCombo.Text = string.Empty;
            ProfileFailureCard.Visibility = Visibility.Collapsed;
            return;
        }

        ProfileDetailTitle.Text = profile.Name;
        ProfileNameBox.Text = profile.Name;
        ProviderPresetCombo.SelectedItem = ProviderPresets.IsKnown(profile.ProviderPreset)
            ? profile.ProviderPreset
            : ProviderPresets.CustomName;
        BaseUrlBox.Text = profile.BaseUrl;
        ApiKeyPasswordBox.Password = profile.ApiKey;
        ApiKeyVisibleBox.Text = profile.ApiKey;
        // 先用该服务商的常用模型填下拉框，用户不必为了换个模型去等一次网络请求。
        // 点「拉取可用模型」仍然会用服务商返回的权威列表覆盖它。
        ModelCombo.ItemsSource = ProviderPresets.Find(profile.ProviderPreset).Models;
        ModelCombo.Text = profile.Model;
        TemperatureBox.Text = profile.Temperature.ToString("0.##", CultureInfo.CurrentCulture);
        MaxTokensBox.Text = profile.MaxTokens.ToString(CultureInfo.CurrentCulture);
        RequestTimeoutBox.Text = profile.RequestTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        FirstTokenTimeoutBox.Text = profile.FirstTokenTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        ExtraRequestBodyBox.Text = profile.ExtraRequestBody;
        ModelParamsCaption.Text =
            $"下面这些参数只作用于「{profile.Name}」。要改别的模型，回到「全部模型」点那张卡片。";

        UpdateBaseUrlEditability();
        UpdateFailureCard(profile);
        _testResults.Clear();
        _lastTestRunner = null;
        CopyReportButton.IsEnabled = false;
        CopyReportButton.Visibility = Visibility.Collapsed;
        TestProgressBar.Visibility = Visibility.Collapsed;
        TestProfileButton.Content = "开始检测";
        TestStatusText.Text = "检查配置、网络、鉴权、模型和翻译链路，定位连接问题。";
    }

    private void UpdateFailureCard(ApiProfile profile)
    {
        if (!profile.IsFailed)
        {
            ProfileFailureCard.Visibility = Visibility.Collapsed;
            return;
        }

        string when = profile.FailedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "刚刚";
        ProfileFailureText.Text =
            $"这个档案已被标记失效（{when}）：{profile.FailureReason}。划词时会自动跳过它。"
            + "改一下上面的 Base URL / API Key / 模型名就会自动解除标记。";
        ProfileFailureCard.Visibility = Visibility.Visible;
    }

    private void UpdateProfileButtons()
    {
        bool selected = SelectedProfile is not null;
        DeleteProfileButton.IsEnabled = selected && _settings.Profiles.Count > 1;
        SetCurrentProfileButton.IsEnabled = selected
            && !string.Equals(SelectedProfile!.Id, _settings.CurrentProfileId, StringComparison.Ordinal);
        TestProfileButton.IsEnabled = selected && _testCancellation is null;
        AddDraftButton.IsEnabled = _draftProfile is not null && _draftTestPassed;
    }

    /// <summary>
    /// 卡片就是入口：点一张卡片 = 选中它 = 进入它的详情。
    /// <see cref="RefreshProfiles"/> 里的程序性选中被 <c>_loading</c> 挡住，
    /// 所以启动、删除、上下移这些场景不会误把用户拽进详情页。
    /// </summary>
    private void ProfilesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool previousLoading = _loading;
        _loading = true;
        try
        {
            LoadProfileDetails();
        }
        finally
        {
            _loading = previousLoading;
        }

        UpdateProfileButtons();

        // 这里刻意不跳详情：ListBox 是按下鼠标就改选中的，若在这里跳转，
        // 用户刚要拖动卡片，网格已经被详情页顶掉了。跳转放到"确认不是拖动"的抬起时刻。
    }

    // ==================== 卡片拖动排序 ====================

    private void ProfilesList_OnPreviewMouseLeftButtonDown(
        object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _cardDragStart = e.GetPosition(null);
        _cardDragStarted = false;
        _cardDragCandidate = FindAncestor<System.Windows.Controls.ListBoxItem>(
            e.OriginalSource as DependencyObject)?.DataContext as ProfileListItem;
    }

    private void ProfilesList_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_cardDragStarted
            || _cardDragCandidate is null
            || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        System.Windows.Vector moved = e.GetPosition(null) - _cardDragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // 超过系统阈值才算拖动，否则手抖一两像素就再也点不开详情了。
        _cardDragStarted = true;
        System.Windows.DragDrop.DoDragDrop(ProfilesList, _cardDragCandidate, System.Windows.DragDropEffects.Move);
    }

    private void ProfilesList_OnMouseLeftButtonUp(
        object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_cardDragStarted && _cardDragCandidate is not null)
        {
            ShowProfileDetail();
        }

        _cardDragCandidate = null;
        _cardDragStarted = false;
    }

    private void ProfilesList_OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ProfileListItem))
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void ProfilesList_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        ExecuteUiAction("拖动调整模型顺序", () =>
        {
            if (e.Data.GetData(typeof(ProfileListItem)) is not ProfileListItem dragged)
            {
                return;
            }

            int from = _settings.Profiles.IndexOf(dragged.Profile);
            if (from < 0)
            {
                return;
            }

            int insertAt;
            var target = FindAncestor<System.Windows.Controls.ListBoxItem>(
                e.OriginalSource as DependencyObject);
            if (target?.DataContext is ProfileListItem targetItem)
            {
                int targetIndex = _settings.Profiles.IndexOf(targetItem.Profile);
                if (targetIndex < 0 || targetIndex == from)
                {
                    return;
                }

                // 落在目标卡片左半边就插它前面，右半边就插它后面 —— 和文件管理器一致。
                insertAt = e.GetPosition(target).X > target.ActualWidth / 2
                    ? targetIndex + 1
                    : targetIndex;
            }
            else
            {
                // 丢在空白处 = 放到队尾。
                insertAt = _settings.Profiles.Count;
            }

            _settings.Profiles.RemoveAt(from);
            if (from < insertAt)
            {
                insertAt--;
            }

            _settings.Profiles.Insert(Math.Clamp(insertAt, 0, _settings.Profiles.Count), dragged.Profile);
            RefreshProfiles(dragged.Profile.Id);
            SaveAll();
            ShowTemporaryStatus("顺序已保存，卡片顺序就是划词失败时的回退顺序");
        });
    }

    /// <summary>拖动是鼠标专属操作，键盘用户走这里：Enter 打开，Alt+← / Alt+→ 调顺序。</summary>
    private void ProfilesList_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProfileListItem)
        {
            return;
        }

        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        {
            ShowProfileDetail();
            e.Handled = true;
            return;
        }

        if (e.KeyboardDevice.Modifiers != System.Windows.Input.ModifierKeys.Alt)
        {
            return;
        }

        if (e.SystemKey is System.Windows.Input.Key.Left or System.Windows.Input.Key.Up)
        {
            MoveSelectedProfile(-1);
            e.Handled = true;
        }
        else if (e.SystemKey is System.Windows.Input.Key.Right or System.Windows.Input.Key.Down)
        {
            MoveSelectedProfile(1);
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }

    /// <summary>
    /// 返回全部模型。草稿已经通过检测就顺手收下（它已经是个能用的模型，没道理丢掉），
    /// 没通过就丢弃 —— 这正是"没配置好的模型返回即不存在"。
    /// </summary>
    private void BackToProfileGrid_OnClick(object sender, RoutedEventArgs e)
    {
        if (_draftProfile is { } draft && _draftTestPassed)
        {
            CommitDraftProfile(draft);
            return;
        }

        ShowProfileGrid();
    }

    private void AddDraftButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("添加服务商档案", () =>
        {
            if (_draftProfile is not { } draft)
            {
                return;
            }

            if (!_draftTestPassed)
            {
                ShowValidationError(IsProfileUsable(draft)
                    ? "先点「开始检测」，检测通过之后才能添加。"
                    : "接口地址、API Key 和模型名称都填好，再点「开始检测」。");
                FocusFirstEmptyDraftField(draft);
                return;
            }

            CommitDraftProfile(draft);
        });
    }

    private void CommitDraftProfile(ApiProfile draft)
    {
        _settings.Profiles.Add(draft);
        _draftProfile = null;
        RefreshProfiles(draft.Id);
        SaveAll();
        ShowProfileGrid();
    }

    private void FocusFirstEmptyDraftField(ApiProfile draft)
    {
        if (string.IsNullOrWhiteSpace(draft.BaseUrl))
        {
            BaseUrlBox.Focus();
        }
        else if (string.IsNullOrWhiteSpace(draft.ApiKey) && !ApiProfile.IsLocalAddress(draft.BaseUrl))
        {
            ApiKeyPasswordBox.Focus();
        }
        else if (string.IsNullOrWhiteSpace(draft.Model))
        {
            ModelCombo.Focus();
        }
        else
        {
            // 三项都填了，缺的是那一次检测，焦点就该落在检测按钮上。
            TestProfileButton.Focus();
        }
    }

    private void ShowProfileGrid()
    {
        _draftProfile = null;
        _draftTestPassed = false;
        ProfileDetailView.Visibility = Visibility.Collapsed;
        ProfileGridView.Visibility = Visibility.Visible;
        ProfilesSection.ScrollToTop();
    }

    /// <summary>打开某个<b>已存在</b>模型的详情。</summary>
    private void ShowProfileDetail()
    {
        _draftProfile = null;
        EnterDetailView(isDraft: false);
    }

    /// <summary>打开「新建模型」的草稿详情。</summary>
    private void ShowProfileDraft() => EnterDetailView(isDraft: true);

    private void EnterDetailView(bool isDraft)
    {
        ProfileActionButtons.Visibility = isDraft ? Visibility.Collapsed : Visibility.Visible;
        AddDraftButton.Visibility = isDraft ? Visibility.Visible : Visibility.Collapsed;
        DraftHintText.Visibility = isDraft ? Visibility.Visible : Visibility.Collapsed;
        DetailTitleSpacer.Visibility = isDraft ? Visibility.Collapsed : Visibility.Visible;
        ProfileGridView.Visibility = Visibility.Collapsed;
        ProfileDetailView.Visibility = Visibility.Visible;
        ProfilesSection.ScrollToTop();
    }

    private void AddProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("新建服务商档案", () =>
        {
            // 只做草稿，不入库、不落盘、不刷新卡片网格。
            // 它要么被填齐后由 CommitDraftProfile 收下，要么随返回一起消失。
            _draftProfile = ApiProfile.CreateFromPreset(
                ProviderPresets.CustomName,
                _settings.GenerateUniqueProfileName("新建服务商"));
            _draftTestPassed = false;

            bool previousLoading = _loading;
            _loading = true;
            try
            {
                LoadProfileDetails();
            }
            finally
            {
                _loading = previousLoading;
            }

            ShowProfileDraft();
            ProfileDetailTitle.Text = "新建模型";
            ProfileNameBox.Focus();
            ProfileNameBox.SelectAll();
        });
    }

    private void DeleteProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("删除服务商档案", () =>
        {
            if (SelectedProfile is not { } profile)
            {
                ShowValidationError("请先选择要删除的档案。");
                return;
            }

            if (_settings.Profiles.Count <= 1)
            {
                System.Windows.MessageBox.Show(
                    Window.GetWindow(this),
                    "至少要保留一个服务商档案。想换服务商的话，直接改这个档案的内容就行。",
                    "删除服务商档案",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show(
                    Window.GetWindow(this),
                    $"确定删除档案「{profile.Name}」吗？它的 API Key 会一并删除。",
                    "删除服务商档案",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            _settings.Profiles.Remove(profile);
            if (string.Equals(_settings.CurrentProfileId, profile.Id, StringComparison.Ordinal))
            {
                _settings.CurrentProfileId = _settings.Profiles[0].Id;
            }

            RefreshProfiles(_settings.CurrentProfileId);
            SaveAll();
            // 刚删掉的就是正在看的那张卡，详情页已经没有对应对象，退回网格。
            ShowProfileGrid();
        });
    }

    /// <summary>顺序调整的实现。鼠标走拖放，键盘走 Alt+方向键，两条路都汇到这里。</summary>
    private void MoveSelectedProfile(int offset)
    {
        ExecuteUiAction("调整档案顺序", () =>
        {
            if (SelectedProfile is not { } profile)
            {
                return;
            }

            int index = _settings.Profiles.IndexOf(profile);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= _settings.Profiles.Count)
            {
                return;
            }

            _settings.Profiles.RemoveAt(index);
            _settings.Profiles.Insert(target, profile);
            RefreshProfiles(profile.Id);
            SaveAll();
            ShowTemporaryStatus("顺序已保存，列表顺序也是自动降级顺序");
        });
    }

    private void SetCurrentProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("切换当前服务商", () =>
        {
            if (SelectedProfile is not { } profile)
            {
                return;
            }

            _settings.CurrentProfileId = profile.Id;
            RefreshProfiles(profile.Id);
            SaveAll();
            ShowTemporaryStatus($"当前翻译服务已切换为「{profile.Name}」");
        });
    }

    private void ClearFailureButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("解除失效标记", () =>
        {
            if (SelectedProfile is not { } profile)
            {
                return;
            }

            profile.ClearFailure();
            RefreshProfiles(profile.Id);
            SaveAll();
            ShowTemporaryStatus($"已解除档案「{profile.Name}」的失效标记");
        });
    }

    private void ProfileNameBox_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_loading || SelectedProfile is not { } profile)
        {
            return;
        }

        string name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidationError("档案名称不能为空。");
            ProfileNameBox.Text = profile.Name;
            return;
        }

        profile.Name = name;
        ProfileDetailTitle.Text = name;

        // 草稿还不在列表里，刷新网格既没意义也会白白重建一次 ItemsSource。
        if (_draftProfile is null)
        {
            RefreshProfiles(profile.Id);
            SaveAll();
        }
    }

    private async void TestProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunProfileTestAsync();
    }

    private async Task RunProfileTestAsync()
    {
        if (SelectedProfile is not { } profile || _testCancellation is not null)
        {
            return;
        }

        _testResults.Clear();
        CopyReportButton.IsEnabled = false;
        CopyReportButton.Visibility = Visibility.Collapsed;
        TestProgressBar.Visibility = Visibility.Visible;
        _testCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        TestProfileButton.IsEnabled = false;
        TestProfileButton.Content = "检测中…";
        TestStatusText.Text = $"正在检测档案「{profile.Name}」…";

        try
        {
            DiagnosticRunner runner = _diagnosticRunnerFactory(profile);
            _lastTestRunner = runner;
            var progress = new Progress<DiagnosticResult>(_testResults.Add);
            IReadOnlyList<DiagnosticResult> results = await runner.RunAsync(progress, _testCancellation.Token);

            // 检测最长要跑 90 秒，期间用户随时可能切到别的档案。
            // 不比对就会把档案 A 的检测结论显示成档案 B 的体检报告。
            if (!ReferenceEquals(SelectedProfile, profile))
            {
                Logger.Info("服务商测试", $"检测结束时用户已切换档案，档案「{profile.Name}」的结果已丢弃。");
                _testResults.Clear();
                TestStatusText.Text = string.Empty;
                return;
            }

            int failed = results.Count(result => result.Status == DiagnosticStatus.Failed);
            int warnings = results.Count(result => result.Status == DiagnosticStatus.Warning);

            // 警告不算不通过：模型名不在官方列表里这类提醒很常见，但翻译照样能跑。
            if (_draftProfile is not null)
            {
                _draftTestPassed = failed == 0;
            }

            TestStatusText.Text = failed > 0
                ? $"有 {failed} 项没通过。看上面标 ✗ 的那一行就知道卡在哪。"
                : warnings > 0
                    ? $"全部通过，但有 {warnings} 项提醒，可以看一下。"
                    : "全部通过，这个档案可以正常翻译。";
            CopyReportButton.IsEnabled = results.Count > 0;
            CopyReportButton.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            Logger.Info("服务商测试", "测试已取消或超时。");
            TestStatusText.Text = "检测被中断或超时了，可以再试一次。";
        }
        catch (Exception exception)
        {
            Logger.Error("服务商测试", "测试执行失败。", exception);
            TestStatusText.Text = "测试没有跑完，详情已记录到日志。";
        }
        finally
        {
            _testCancellation?.Dispose();
            _testCancellation = null;
            TestProgressBar.Visibility = Visibility.Collapsed;
            TestProfileButton.Content = _testResults.Count > 0 ? "重新检测" : "开始检测";
            UpdateProfileButtons();
        }
    }

    private async void CopyReportButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastTestRunner is null || _testResults.Count == 0)
            {
                return;
            }

            await ClipboardService.SetTextAsync(_lastTestRunner.BuildReport(_testResults));
            ShowTemporaryStatus("诊断报告已复制（API Key 已脱敏）");
        }
        catch (ClipboardUnavailableException exception)
        {
            Logger.Error("设置", "复制诊断报告失败。", exception);
            ShowValidationError("剪贴板正被其他程序占用，没复制上，稍等一下再点一次。");
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "复制诊断报告失败。", exception);
            ShowValidationError("诊断报告没有复制成功，详情已记录到日志。");
        }
    }

    // ==================== 翻译模式 ====================

    private void RefreshModes(string? selectedModeId = null)
    {
        string desiredMode = selectedModeId ?? SelectedMode?.Id ?? _settings.CurrentModeId;
        List<ModeListItem> items = _settings.Modes
            .Select(mode => new ModeListItem(
                mode,
                string.Equals(mode.Id, _settings.CurrentModeId, StringComparison.Ordinal)))
            .ToList();

        bool previousLoading = _loading;
        _loading = true;
        try
        {
            ModesList.ItemsSource = null;
            ModesList.ItemsSource = items;
            ModesList.SelectedItem = items.FirstOrDefault(item => item.Mode.Id == desiredMode)
                ?? items.FirstOrDefault();
        }
        finally
        {
            _loading = previousLoading;
        }

        ModeListItem? current = items.FirstOrDefault(item => item.IsCurrent);
        CurrentModeStatusText.Text = current is null
            ? "还没有选定当前模式。"
            : $"当前由「{current.Mode.Name}」决定译文的语气和取舍。";

        LoadModeDetails();
        UpdateModeButtons();
    }

    private void LoadModeDetails()
    {
        if (SelectedMode is { } mode)
        {
            ModeNameBox.Text = mode.Name;
            ModePromptBox.Text = mode.SystemPromptTemplate;
            ModeDeepPromptBox.Text = mode.DeepPromptTemplate;
        }
        else
        {
            ModeNameBox.Text = string.Empty;
            ModePromptBox.Text = string.Empty;
            ModeDeepPromptBox.Text = string.Empty;
        }

        UpdateDeepPromptNotice();
    }

    /// <summary>
    /// 深度模板为空时，<see cref="DeepPromptBuilder"/> 会静默回退到「通用」那一套 ——
    /// 用户写的提示词对深度翻译一点作用都没有，而界面上原本没有任何迹象。
    /// </summary>
    private void UpdateDeepPromptNotice()
    {
        bool missing = SelectedMode is { } mode && string.IsNullOrWhiteSpace(mode.DeepPromptTemplate);
        DeepPromptMissingNotice.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateExtraRequestBodyValidation()
    {
        bool valid = IsJsonObjectOrEmpty(ExtraRequestBodyBox.Text);
        ExtraRequestBodyError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        ExtraRequestBodyBox.SetResourceReference(
            System.Windows.Controls.Control.BorderBrushProperty,
            valid ? "Brush.BorderStrong" : "Brush.Danger");
        ExtraRequestBodyBox.BorderThickness = valid ? new Thickness(1) : new Thickness(2);
    }

    private static bool IsJsonObjectOrEmpty(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        try
        {
            var reader = new Utf8JsonReader(
                Encoding.UTF8.GetBytes(value),
                isFinalBlock: true,
                state: default);
            if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? document))
            {
                return false;
            }

            using (document)
            {
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && reader.BytesConsumed == Encoding.UTF8.GetByteCount(value);
            }
        }
        catch (JsonException exception)
        {
            Logger.Warning("设置", $"额外请求参数 JSON 校验未通过：{exception.Message}");
            return false;
        }
    }

    private void UpdateModeButtons()
    {
        bool selected = SelectedMode is not null;
        bool builtIn = SelectedMode is { IsBuiltIn: true };
        bool isCurrent = SelectedMode is { } mode
            && string.Equals(mode.Id, _settings.CurrentModeId, StringComparison.Ordinal);
        ApplyModeButton.IsEnabled = selected && !isCurrent;
        DeleteModeButton.IsEnabled = selected && !builtIn;
        RestoreModeButton.IsEnabled = builtIn;
        ModeNameBox.IsEnabled = selected;
        ModePromptBox.IsEnabled = selected;
        ModeDeepPromptBox.IsEnabled = selected;
    }

    private bool TryReadInt(TextBox box, string fieldName, int minimum, int maximum, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
            && value >= minimum
            && value <= maximum)
        {
            return true;
        }

        ShowValidationError($"{fieldName}请输入 {minimum} 到 {maximum} 之间的整数。");
        box.Focus();
        box.SelectAll();
        return false;
    }

    private bool TryReadDouble(TextBox box, string fieldName, double minimum, double maximum, out double value)
    {
        bool parsed = double.TryParse(
            box.Text.Trim(),
            NumberStyles.Float,
            CultureInfo.CurrentCulture,
            out value)
            || double.TryParse(
                box.Text.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        if (parsed && value >= minimum && value <= maximum)
        {
            return true;
        }

        ShowValidationError($"{fieldName}请输入 {minimum} 到 {maximum} 之间的数字。");
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void ShowValidationError(string message)
    {
        _saveFeedbackTimer.Stop();
        StatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            "Brush.Danger");
        StatusText.Text = message;
    }

    private void ShowTemporaryStatus(string message)
    {
        _saveFeedbackTimer.Stop();
        StatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            "Brush.Success");
        StatusText.Text = message;
        _saveFeedbackTimer.Start();
    }

    private void SaveFeedbackTimer_OnTick(object? sender, EventArgs e)
    {
        _saveFeedbackTimer.Stop();
        ShowIdleSaveStatus();
    }

    private void ShowIdleSaveStatus()
    {
        StatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            "Brush.TextTertiary");
        StatusText.Text = "修改后自动保存";
    }

    private string CurrentApiKey() => _apiKeyVisible
        ? ApiKeyVisibleBox.Text
        : ApiKeyPasswordBox.Password;

    private static string SelectedComboContent(ComboBox comboBox, string fallback)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
    }

    private static void SelectComboItemByContent(ComboBox comboBox, string content)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase));
    }

    private void ExecuteUiAction(string actionName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Logger.Error("设置", $"{actionName}失败。", exception);
            ShowValidationError("操作没有完成，详情已记录到日志。请稍后重试。");
        }
    }

    private void ProviderPresetCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderPresetCombo.SelectedItem is not string selected)
        {
            return;
        }

        ExecuteUiAction("切换服务商预设", () =>
        {
            ProviderPreset preset = ProviderPresets.Find(selected);
            ModelCombo.ItemsSource = preset.Models;
            if (preset.Name != ProviderPresets.CustomName)
            {
                BaseUrlBox.Text = preset.BaseUrl;
                ModelCombo.Text = preset.Model;
                // 关闭思考的写法各家不同，切预设必须一并换掉，否则会把上一家的参数发给新服务商。
                ExtraRequestBodyBox.Text = preset.ExtraRequestBody;
                if (SelectedProfile is { } profile
                    && (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.StartsWith("新建服务商", StringComparison.Ordinal)))
                {
                    profile.Name = _settings.GenerateUniqueProfileName(preset.Name);
                    ProfileNameBox.Text = profile.Name;
                }
            }

            UpdateBaseUrlEditability();
            UpdateExtraRequestBodyValidation();
            SaveAll();
            RefreshProfiles();
        });
    }

    /// <summary>
    /// 已知服务商的 Base URL 由预设决定，不用手填；只有「自定义」才需要自己输入。
    /// </summary>
    private void UpdateBaseUrlEditability()
    {
        bool isCustom = ProviderPresetCombo.SelectedItem as string == ProviderPresets.CustomName;
        BaseUrlBox.IsReadOnly = !isCustom;
        BaseUrlHintText.Text = isCustom
            ? "填写服务商文档提供的 OpenAI 兼容接口地址，例如 https://api.example.com/v1。"
            : "当前地址由服务商预设自动填写；需要手动修改时，请先把预设切换为“自定义”。";
    }

    private void ToggleApiKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("显示或隐藏 API Key", () =>
        {
            _apiKeyVisible = !_apiKeyVisible;
            if (_apiKeyVisible)
            {
                ApiKeyVisibleBox.Text = ApiKeyPasswordBox.Password;
                ApiKeyPasswordBox.Visibility = Visibility.Collapsed;
                ApiKeyVisibleBox.Visibility = Visibility.Visible;
                ToggleApiKeyButton.Content = "隐藏";
                ApiKeyVisibleBox.Focus();
            }
            else
            {
                ApiKeyPasswordBox.Password = ApiKeyVisibleBox.Text;
                ApiKeyVisibleBox.Visibility = Visibility.Collapsed;
                ApiKeyPasswordBox.Visibility = Visibility.Visible;
                ToggleApiKeyButton.Content = "显示";
                ApiKeyPasswordBox.Focus();
            }
        });
    }

    private async void FetchModelsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string baseUrl = BaseUrlBox.Text.Trim();
            string apiKey = CurrentApiKey().Trim();
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ShowValidationError("请先填写正确的服务地址，再拉取模型。");
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey) && !uri.IsLoopback)
            {
                ShowValidationError("请先填写 API Key，再拉取模型。");
                return;
            }

            // 请求期间按钮是禁用的，但档案列表没有禁用：用户完全可能在等待时切到别的档案。
            // 记下发起这次请求的档案，结果落地前比对，避免把 A 服务商的模型列表灌进 B 档案。
            ApiProfile? requestedProfile = SelectedProfile;

            _modelFetchCancellation?.Cancel();
            _modelFetchCancellation?.Dispose();
            _modelFetchCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            FetchModelsButton.IsEnabled = false;
            FetchModelsButton.Content = "正在拉取…";
            CancellationToken cancellationToken = _modelFetchCancellation.Token;
            // 用 Task.Run 把请求扔到线程池：首次连接某个服务商时，系统代理探测、DNS
            // 解析等前置开销可能耗时数百毫秒到几秒，且发生在真正的 await 之前，
            // 若不这样处理，这段耗时会直接卡在 UI 线程上，表现为窗口偶尔"卡顿"。
            IReadOnlyList<string> models = await Task.Run(
                () => OpenAiCompatibleClient.FetchModelsAsync(baseUrl, apiKey, cancellationToken),
                cancellationToken);
            if (!ReferenceEquals(SelectedProfile, requestedProfile))
            {
                Logger.Info("设置", "模型列表返回时用户已切换档案，本次结果已丢弃，避免灌进别的档案。");
                return;
            }

            if (models.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "该服务商没有返回可用模型。请到官网查看模型名后手动填写。",
                    "拉取可用模型",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string currentModel = ModelCombo.Text;
            ModelCombo.ItemsSource = models;
            ModelCombo.Text = currentModel;
            ModelCombo.IsDropDownOpen = true;
            ShowTemporaryStatus($"已拉取到 {models.Count} 个模型，请从列表中选择");
        }
        catch (OperationCanceledException exception)
        {
            Logger.Info("设置", $"拉取模型列表已取消或超时：{exception.Message}");
            System.Windows.MessageBox.Show(
                "拉取模型列表超时。该服务商可能不支持列出模型，请到官网查看模型名后手动填写。",
                "拉取可用模型",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is ApiHttpException or HttpRequestException or TranslationJsonException)
        {
            Logger.Error("设置", "拉取可用模型失败。", exception);
            System.Windows.MessageBox.Show(
                DescribeModelFetchFailure(exception),
                "拉取可用模型",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "拉取可用模型时发生意外错误。", exception);
            System.Windows.MessageBox.Show(
                "模型列表没有拉取成功，详情已记录到日志。请稍后再试。",
                "拉取可用模型",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            FetchModelsButton.IsEnabled = true;
            FetchModelsButton.Content = "拉取可用模型";
        }
    }

    /// <summary>
    /// 拉取模型列表失败时说清楚是哪种失败。以前一律说"该服务商不支持列出模型"，
    /// Key 填错了（401）也这么说，用户会以为是服务商的问题，转头去官网抄模型名。
    /// </summary>
    internal static string DescribeModelFetchFailure(Exception exception)
    {
        if (exception is ApiHttpException api
            && api.Kind is not (ApiErrorKind.EndpointNotFound or ApiErrorKind.BadRequest))
        {
            return ErrorMessages.FromException(exception, string.Empty, thinkingDisabled: true).Message;
        }

        if (exception is HttpRequestException)
        {
            return ErrorMessages.FromException(exception, string.Empty, thinkingDisabled: true).Message;
        }

        return "该服务商不支持列出模型，请到官网查看模型名后手动填写。";
    }

    private void ExtraRequestBodyBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateExtraRequestBodyValidation();
        }
    }

    private void ExtraPresetButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("填写额外请求参数预设", () =>
        {
            string preset = (sender as Button)?.Tag?.ToString() ?? string.Empty;
            ExtraRequestBodyBox.Text = preset switch
            {
                "deepseek" => "{\"thinking\": {\"type\": \"disabled\"}}",
                "qwen" => "{\"enable_thinking\": false}",
                "openai" => "{\"reasoning_effort\": \"minimal\"}",
                _ => string.Empty
            };
            SaveAll();
        });
    }

    private void ApplyModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("切换翻译模式", () =>
        {
            if (SelectedMode is not { } mode)
            {
                ShowValidationError("请先在列表中选择要应用的翻译模式。");
                return;
            }

            _settings.CurrentModeId = mode.Id;
            RefreshModes(mode.Id);
            SaveAll();
        });
    }

    private void ModesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadModeDetails();
        UpdateModeButtons();
        // 和模型卡片一致：跳详情放到"确认是一次点击"的抬起时刻，不在选中时跳。
    }

    private void ModesList_OnPreviewMouseLeftButtonDown(
        object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _modeCardPressed = FindAncestor<System.Windows.Controls.ListBoxItem>(
            e.OriginalSource as DependencyObject)?.DataContext is ModeListItem;

    private void ModesList_OnMouseLeftButtonUp(
        object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_modeCardPressed)
        {
            ShowModeDetail();
        }

        _modeCardPressed = false;
    }

    private void ModesList_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (ModesList.SelectedItem is ModeListItem
            && e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        {
            ShowModeDetail();
            e.Handled = true;
        }
    }

    private void BackToModeGrid_OnClick(object sender, RoutedEventArgs e) => ShowModeGrid();

    private void ShowModeGrid()
    {
        ModeDetailView.Visibility = Visibility.Collapsed;
        ModeGridView.Visibility = Visibility.Visible;
        BehaviorSection.ScrollToTop();
    }

    private void ShowModeDetail()
    {
        ModeGridView.Visibility = Visibility.Collapsed;
        ModeDetailView.Visibility = Visibility.Visible;
        BehaviorSection.ScrollToTop();
    }

    private void ModeDeepPromptBox_OnLostKeyboardFocus(
        object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_loading || SelectedMode is not { } mode)
        {
            return;
        }

        // 允许清空：清掉就是明确表示"回到沿用通用"，提示条会跟着出现。
        mode.DeepPromptTemplate = ModeDeepPromptBox.Text.Trim();
        UpdateDeepPromptNotice();
        RefreshModes(mode.Id);
        SaveAll();
    }

    private void CopyDeepPromptFromAuto_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("复制通用的深度提示词", () =>
        {
            if (SelectedMode is not { } mode)
            {
                return;
            }

            // 反正为空时本来就在用「通用」那一套，复制过来只是把这件事摆到明面上、变得可改。
            mode.DeepPromptTemplate = BuiltInModes.CreateDefaults()[0].DeepPromptTemplate;
            ModeDeepPromptBox.Text = mode.DeepPromptTemplate;
            UpdateDeepPromptNotice();
            RefreshModes(mode.Id);
            SaveAll();
            ShowTemporaryStatus("已复制「通用」的深度提示词，现在可以改了");
        });
    }

    private void ModeNameBox_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_loading || SelectedMode is not { } mode)
        {
            return;
        }

        string name = ModeNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidationError("模式名称不能为空。");
            ModeNameBox.Text = mode.Name;
            return;
        }

        mode.Name = name;
        RefreshModes(mode.Id);
        SaveAll();
    }

    private void ModePromptBox_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_loading || SelectedMode is not { } mode)
        {
            return;
        }

        string prompt = ModePromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            ShowValidationError("系统提示词不能为空。");
            ModePromptBox.Text = mode.SystemPromptTemplate;
            return;
        }

        mode.SystemPromptTemplate = prompt;
        SaveAll();
    }

    private void NewModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("新建翻译模式", () =>
        {
            var mode = new TranslationMode
            {
                Id = $"custom-{Guid.NewGuid():N}",
                Name = GenerateUniqueModeName("新建模式"),
                SystemPromptTemplate = DefaultNewModePrompt,
                IsBuiltIn = false
            };
            _settings.Modes.Add(mode);
            _settings.CurrentModeId = mode.Id;
            RefreshModes(mode.Id);
            SaveAll();
            ModeNameBox.Focus();
            ModeNameBox.SelectAll();
        });
    }

    private string GenerateUniqueModeName(string baseName)
    {
        if (_settings.Modes.All(mode => mode.Name != baseName))
        {
            return baseName;
        }

        int suffix = 2;
        while (_settings.Modes.Any(mode => mode.Name == $"{baseName} {suffix}"))
        {
            suffix++;
        }

        return $"{baseName} {suffix}";
    }

    private const string DefaultNewModePrompt = """
        你是一个精准的翻译引擎。将用户提供的文本翻译成{target_lang}。

        规则：
        1. 只输出译文本身，不要任何解释、前言、引号。
        2. 若原文本身已是{target_lang}，则输出{fallback_lang}的译文。
        """;

    private void DeleteModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("删除翻译模式", () =>
        {
            if (SelectedMode is not { } mode)
            {
                ShowValidationError("请先选择要删除的翻译模式。");
                return;
            }

            if (mode.IsBuiltIn)
            {
                System.Windows.MessageBox.Show(
                    "内置模式不能删除，但可以编辑；如果改乱了，可以点「恢复默认」。",
                    "删除翻译模式",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show(
                    $"确定删除翻译模式「{mode.Name}」吗？",
                    "删除翻译模式",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            _settings.Modes.Remove(mode);
            if (_settings.CurrentModeId == mode.Id)
            {
                _settings.CurrentModeId = "auto";
            }

            RefreshModes(_settings.CurrentModeId);
            SaveAll();
        });
    }

    private void RestoreModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("恢复内置翻译模式", () =>
        {
            if (SelectedMode is not { IsBuiltIn: true } mode)
            {
                ShowValidationError("请选择一个内置模式再恢复默认。");
                return;
            }

            TranslationMode original = BuiltInModes.CreateDefaults().First(item => item.Id == mode.Id);
            mode.Name = original.Name;
            mode.SystemPromptTemplate = original.SystemPromptTemplate;
            // 深度模板也要一起恢复：只恢复其中一套，等于把这个模式留在
            // "浮窗是官方的、深度是我改过的"这种一半一半的状态里。
            mode.DeepPromptTemplate = original.DeepPromptTemplate;
            RefreshModes(mode.Id);
            SaveAll();
        });
    }

    private void TriggerSetting_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            SaveAll(SettingsChangeKind.KeyboardHook);
        }
    }

    private void TriggerSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _triggerSaveTimer.Stop();
        SaveAll(SettingsChangeKind.KeyboardHook);
    }

    private void TriggerTextBox_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        SaveAll(SettingsChangeKind.KeyboardHook);
    }

    private async void ClearAllHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // SPEC §7.4：清空操作前必须报出会删多少、保留多少，且绝不删除星标收藏。
            (int nonStarred, int starred) = await _historyRepository.CountAsync(CancellationToken.None);
            if (nonStarred == 0)
            {
                System.Windows.MessageBox.Show(
                    starred == 0
                        ? "还没有任何翻译记录。"
                        : $"没有可清空的历史记录。你收藏的 {starred} 条会一直保留。",
                    "清空历史记录",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show(
                    $"将删除 {nonStarred} 条历史记录。你收藏的 {starred} 条会保留。确定吗？",
                    "清空历史记录",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            await _historyRepository.ClearNonStarredAsync(CancellationToken.None);
            Logger.Info("数据库", $"用户已清空 {nonStarred} 条历史记录，保留收藏 {starred} 条。");
            System.Windows.MessageBox.Show(
                $"已删除 {nonStarred} 条历史记录，收藏的 {starred} 条已保留。",
                "清空历史记录",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            Logger.Error("数据库", "清空历史记录失败。", exception);
            System.Windows.MessageBox.Show("历史记录没有清空成功，详情已记录到日志。", "清空历史记录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ClearCacheButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            (int nonStarred, int starred) = await _historyRepository.CountAsync(CancellationToken.None);
            if (nonStarred == 0)
            {
                System.Windows.MessageBox.Show(
                    "没有可清除的缓存记录。",
                    "清空缓存",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show(
                    $"将清除 {nonStarred} 条缓存记录，下次翻译相同内容需要重新请求。收藏的 {starred} 条会保留。",
                    "清空缓存",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            await _historyRepository.ClearNonStarredAsync(CancellationToken.None);
            Logger.Info("数据库", $"用户已清除 {nonStarred} 条缓存，保留收藏 {starred} 条。");
            System.Windows.MessageBox.Show(
                $"已清除 {nonStarred} 条缓存，收藏的 {starred} 条已保留。",
                "清空缓存",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            Logger.Error("数据库", "清空翻译缓存失败。", exception);
            System.Windows.MessageBox.Show("缓存没有清空成功，详情已记录到日志。", "清空缓存", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartWithWindowsToggle_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("修改开机自动启动", () =>
        {
            // finally 里一定要刷新：开关的位置现在就是状态本身，
            // 中途任何一条失败路径（注册表写不进去、保存失败、抛异常）
            // 都必须把滑块拨回真实状态，否则界面在骗人。
            try
            {
                bool previous = _startWithWindows;

                // CheckBox 的 Click 是在 IsChecked 翻转之后才触发的，所以这里读它就是用户要的新值。
                bool enabled = StartWithWindowsToggle.IsChecked == true;
                if (!StartupRegistration.TrySet(enabled, out string? errorMessage))
                {
                    ShowValidationError(errorMessage ?? "开机自动启动设置没有生效。");
                    return;
                }

                // 注册表已经改了，配置没存住就得原样退回去，否则两边说法不一致。
                _startWithWindows = enabled;
                if (!SaveAll())
                {
                    StartupRegistration.TrySet(previous, out _);
                    _startWithWindows = previous;
                }
            }
            finally
            {
                RefreshStartWithWindows();
            }
        });
    }

    /// <summary>
    /// 点一套浮窗大小或字号预设：把数值填进「自定义」里的输入框再走正常的保存流程，
    /// 这样校验、落盘和"已自动保存"提示都跟手改数字走同一条路，不会出现两套行为。
    /// </summary>
    private void PopupPresetButton_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PopupChoiceItem item)
        {
            return;
        }

        ExecuteUiAction("应用浮窗预设", () =>
        {
            double previousWidth = _popupMaxWidth;
            double previousHeight = _popupMaxHeight;
            double previousFont = _popupFontSize;

            if (item.Size is { } size)
            {
                _popupMaxWidth = size.MaxWidth;
                _popupMaxHeight = size.MaxHeight;
            }

            if (item.Font is { } font)
            {
                _popupFontSize = font.FontSize;
            }

            if (!SaveAll())
            {
                _popupMaxWidth = previousWidth;
                _popupMaxHeight = previousHeight;
                _popupFontSize = previousFont;
            }

            RefreshPopupPresets();
        });
    }

    /// <summary>
    /// 点一套浮窗配色。配色没有对应的输入框，选中项本身就是"控件状态"，
    /// 所以先记在字段里，再由 SaveAll 统一写进设置 —— 和其它字段保持同一条保存路径。
    /// </summary>
    private void PopupThemeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PopupThemeItem item)
        {
            return;
        }

        ExecuteUiAction("切换浮窗配色", () =>
        {
            string previous = _popupThemeName;
            _popupThemeName = item.Theme.Name;
            if (!SaveAll())
            {
                _popupThemeName = previous;
            }

            RefreshPopupThemes();
        });
    }

    /// <summary>重画配色按钮。选了「自定义」时三个内置的都不高亮。</summary>
    private void RefreshPopupThemes()
    {
        PopupThemeList.ItemsSource = PopupThemes.All
            .Select(theme => new PopupThemeItem(
                theme,
                string.Equals(theme.Name, _popupThemeName, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// 重画大小和字号两组按钮，高亮当前生效的那一套。
    /// </summary>
    private void RefreshPopupPresets()
    {
        PopupSizePreset? size = PopupPresets.MatchSize(_settings.PopupMaxWidth, _settings.PopupMaxHeight);
        PopupFontPreset? font = PopupPresets.MatchFont(_settings.PopupFontSize);

        PopupSizeList.ItemsSource = PopupPresets.Sizes
            .Select(item => new PopupChoiceItem(
                item.Name,
                $"{item.MaxWidth:0}×{item.MaxHeight:0}",
                item.Description,
                ReferenceEquals(item, size),
                item,
                null))
            .ToList();
        PopupFontList.ItemsSource = PopupPresets.Fonts
            .Select(item => new PopupChoiceItem(
                item.Name,
                $"{item.FontSize:0.##} 号字",
                item.Description,
                ReferenceEquals(item, font),
                null,
                item))
            .ToList();
    }

    /// <summary>
    /// 开机自启动这一行：开关位置表示状态，下面一行说明当前状态的后果。
    /// <b>读 <c>_startWithWindows</c> 而不是 <c>_settings.StartWithWindows</c></b> ——
    /// 保存失败时前者会被回退、后者可能已经被 SaveAll 写成了新值，
    /// 读错那个会让开关显示成一个并没有真正生效的状态。
    /// </summary>
    private void RefreshStartWithWindows()
    {
        StartWithWindowsStateText.Text = _startWithWindows
            ? "已启用，登录 Windows 后自动运行"
            : "已停用，每次开机需要自己启动一次";
        StartWithWindowsToggle.IsChecked = _startWithWindows;
    }

    private void OpenFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("打开文件夹", () =>
        {
            string target = (sender as Button)?.Tag?.ToString() == "logs"
                ? Logger.LogDirectory
                : Logger.DataDirectory;
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        });
    }

    private async void ExportHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Window owner = Window.GetWindow(this)
                ?? throw new InvalidOperationException("设置视图尚未连接到主窗口。");
            string? path = await HistoryCsvExporter.ExportAsync(
                _historyRepository,
                owner,
                modeId => _settings.Modes.FirstOrDefault(mode => mode.Id == modeId)?.Name
                    ?? BuiltInModes.CreateDefaults().FirstOrDefault(mode => mode.Id == modeId)?.Name
                    ?? modeId,
                CancellationToken.None);
            if (path is not null)
            {
                System.Windows.MessageBox.Show(owner, "历史记录已导出。", "导出完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "从设置窗口导出历史失败。", exception);
            Window? owner = Window.GetWindow(this);
            System.Windows.MessageBox.Show(owner, "导出失败，请确认保存位置可写后重试。", "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EditableControl_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => SaveAll();

    private void ApiKeyControl_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_apiKeyVisible)
        {
            ApiKeyPasswordBox.Password = ApiKeyVisibleBox.Text;
        }
        else
        {
            ApiKeyVisibleBox.Text = ApiKeyPasswordBox.Password;
        }

        SaveAll();
    }

    private void GeneralCheckBox_OnClick(object sender, RoutedEventArgs e) => SaveAll();

    private void SettingsView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _triggerSaveTimer.Stop();
        _saveFeedbackTimer.Stop();
        _feedbackReady = false;
        _modelFetchCancellation?.Cancel();
        _modelFetchCancellation?.Dispose();
        _modelFetchCancellation = null;
        _testCancellation?.Cancel();
    }

    /// <summary>
    /// 档案列表的显示用包装。ApiProfile 是纯数据模型、没有变更通知，
    /// 这里沿用本项目已有的做法：改动后整体重建 ItemsSource。
    /// </summary>
    private sealed record ProfileListItem(ApiProfile Profile, bool IsCurrent, int BackupOrder)
    {
        public string Name => Profile.Name;

        public string Detail => string.IsNullOrWhiteSpace(Profile.Model)
            ? "（还没填模型名）"
            : Profile.Model;

        /// <summary>卡片第三行只显示主机名。整条 Base URL 在 216px 卡片里必然被截断，
        /// 截断后的 "https://open.bigmodel…" 认不出是哪家，主机名反而一眼能认。</summary>
        public string Host => ExtractHost(Profile.BaseUrl);

        /// <summary>徽章种类，模板按它换配色。失效优先于"当前"：坏了比在用更需要先被看见。</summary>
        public string BadgeKind => Profile.IsFailed ? "failed" : IsCurrent ? "current" : "backup";

        public string BadgeText => Profile.IsFailed
            ? "异常"
            : IsCurrent ? "当前" : $"备用 {BackupOrder}";

        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

        public Visibility FailedVisibility => Profile.IsFailed ? Visibility.Visible : Visibility.Collapsed;

        private static string ExtractHost(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "（还没填接口地址）";
            }

            // 用户手输的地址经常没有协议头，Uri 会因此解析失败，所以补一个再试。
            string candidate = baseUrl.Contains("://", StringComparison.Ordinal)
                ? baseUrl
                : "https://" + baseUrl;
            return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0
                ? uri.Host
                : baseUrl;
        }
    }

    /// <summary>模式卡片的显示用包装。做法与档案卡片一致：改动后整体重建 ItemsSource。</summary>
    private sealed record ModeListItem(TranslationMode Mode, bool IsCurrent)
    {
        public string Name => Mode.Name;

        /// <summary>内置模式有写好的一句话摘要；自建的没有，退回提示词开头一小段。</summary>
        public string Summary =>
            BuiltInModes.SummaryOrNull(Mode.Id)
            ?? (string.IsNullOrWhiteSpace(Mode.SystemPromptTemplate)
                ? "自建模式，还没写提示词"
                : Excerpt(Mode.SystemPromptTemplate));

        /// <summary>深度模板为空会静默套用「通用」，这个状态比"当前 / 内置"更该被看见。</summary>
        public string BadgeKind => string.IsNullOrWhiteSpace(Mode.DeepPromptTemplate)
            ? "deepmissing"
            : IsCurrent ? "current" : "normal";

        public string BadgeText => string.IsNullOrWhiteSpace(Mode.DeepPromptTemplate)
            ? "深度未配"
            : IsCurrent ? "当前" : Mode.IsBuiltIn ? "内置" : "自建";

        private static string Excerpt(string prompt)
        {
            // 摘要只取一行，换行一律压成空格 —— 提示词里的换行在卡片上没有意义。
            string first = prompt.ReplaceLineEndings(" ").Trim();
            return first.Length <= 34 ? first : first[..34] + "…";
        }
    }

    /// <summary>
    /// 大小 / 字号预设按钮的显示用包装，做法与档案列表一致：选中项变了就整体重建 ItemsSource。
    /// <paramref name="Size"/> 和 <paramref name="Font"/> 只会有一个非空，点击时按哪个非空来应用。
    /// </summary>
    private sealed record PopupChoiceItem(
        string Name,
        string Detail,
        string Description,
        bool IsSelected,
        PopupSizePreset? Size,
        PopupFontPreset? Font)
    {
        /// <summary>按钮里是两行排版的 TextBlock，屏幕阅读器需要一句完整的话。</summary>
        public string AutomationName => $"{Name}预设，{Detail}，{Description}";

        public Visibility CurrentVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 浮窗配色按钮的显示用包装。三支画笔是给色板预览用的，
    /// 在这里一次性建好并冻结，免得每次绑定求值都新建一支。
    /// </summary>
    private sealed record PopupThemeItem(PopupTheme Theme, bool IsSelected)
    {
        public string Name => Theme.Name;

        public string Description => Theme.Description;

        public System.Windows.Media.SolidColorBrush SurfaceBrush { get; } =
            PopupThemePalette.Swatch(Theme.Surface);

        public System.Windows.Media.SolidColorBrush OutlineBrush { get; } =
            PopupThemePalette.Swatch(Theme.Border);

        public string AutomationName => $"{Name}配色，{Description}";

        public Visibility CurrentVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    }
}
