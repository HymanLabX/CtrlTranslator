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
    private bool _apiKeyVisible;
    private bool _feedbackReady;
    private int _testTapCount;

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
            PrivacySection,
            AboutSection
        ];
        _sectionTabs =
        [
            GeneralTabButton,
            ServiceTabButton,
            PreferenceTabButton,
            PrivacyTabButton,
            AboutTabButton
        ];
        TestResultsList.ItemsSource = _testResults;

        LoadControlsFromSettings();
        RefreshProcessList();
        SelectSection(GeneralSectionIndex);
        _loading = false;
        UpdateExtraRequestBodyValidation();
        UpdateModeButtons();
    }

    /// <summary>打开「服务商档案」页；需要时把焦点直接落到额外请求参数上。</summary>
    internal void ShowTranslationService(bool focusExtraRequestBody)
    {
        if (focusExtraRequestBody)
        {
            SelectSection(ProfilesSectionIndex);
            AdvancedModelSettingsExpander.IsExpanded = true;
            Dispatcher.BeginInvoke(() =>
            {
                ExtraRequestBodyBox.Focus();
                ExtraRequestBodyBox.SelectAll();
            }, DispatcherPriority.Input);
            return;
        }

        SelectSection(ProfilesSectionIndex);
    }

    /// <summary>打开「服务商档案」页并立刻跑一遍测试。浮窗上那些"查不出原因"的错误走这条路。</summary>
    internal void ShowProfileTest()
    {
        SelectSection(ProfilesSectionIndex);
        _ = RunProfileTestAsync();
    }

    internal void RecordValidTap()
    {
        if (!IsVisible)
        {
            return;
        }

        _testTapCount = Math.Min(2, _testTapCount + 1);
        DoubleTapTestText.Text = string.Join(' ', Enumerable.Repeat("●", _testTapCount));
        DoubleTapTestText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            "Brush.TextSecondary");
    }

    internal void RecordDoubleTap()
    {
        if (!IsVisible)
        {
            return;
        }

        _testTapCount = 0;
        DoubleTapTestText.Text = "✓ 双击已识别";
        DoubleTapTestText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            "Brush.Success");
    }

    private ApiProfile? SelectedProfile => (ProfilesList.SelectedItem as ProfileListItem)?.Profile;

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
        DoubleTapSlider.Value = Math.Clamp(_settings.DoubleTapIntervalMs, 150, 800);
        DoubleTapValueText.Text = $"{(int)DoubleTapSlider.Value} ms";
        MaxKeyHoldBox.Text = _settings.MaxKeyHoldMs.ToString(CultureInfo.CurrentCulture);
        ClipboardWaitBox.Text = _settings.ClipboardWaitMs.ToString(CultureInfo.CurrentCulture);
        EnabledCheckBox.IsChecked = _settings.Enabled;

        RefreshBlacklist();
        LongTextThresholdBox.Text = _settings.LongTextThreshold.ToString(CultureInfo.CurrentCulture);
        PrivacyModeCheckBox.IsChecked = _settings.PrivacyMode;
        DetailedLoggingCheckBox.IsChecked = _settings.DetailedLogging;

        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        PopupFontSizeBox.Text = _settings.PopupFontSize.ToString("0.##", CultureInfo.CurrentCulture);
        PopupMaxWidthBox.Text = _settings.PopupMaxWidth.ToString("0.##", CultureInfo.CurrentCulture);
        PopupMaxHeightBox.Text = _settings.PopupMaxHeight.ToString("0.##", CultureInfo.CurrentCulture);
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
            ReleaseInfo? release = await _updateService.CheckManuallyAsync(CancellationToken.None);
            if (release is null)
            {
                UpdateStatusText.Text =
                    $"已经是最新版本 {UpdateChecker.CurrentVersion.ToString(3)}。"
                    + "（如果你在国内且没开代理，也可能是连不上 GitHub —— 详情见日志。）";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 {release.Version.ToString(3)}。";
            _showUpdateWindow(release);
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
                || !TryReadInt(MaxKeyHoldBox, "单次按键最长时长", 1, 5_000, out int maxKeyHold)
                || !TryReadInt(ClipboardWaitBox, "取词等待时间", 100, 1_000, out int clipboardWait)
                || !TryReadInt(LongTextThresholdBox, "长文本二次确认阈值", 0, 20_000, out int longTextThreshold)
                || !TryReadDouble(PopupFontSizeBox, "浮窗字号", 10, 24, out double popupFontSize)
                || !TryReadDouble(PopupMaxWidthBox, "浮窗最大宽度", 260, 900, out double popupMaxWidth)
                || !TryReadDouble(PopupMaxHeightBox, "浮窗最大高度", 180, 900, out double popupMaxHeight)
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
                }
            }

            _settings.TargetLanguage = TargetLanguageBox.Text.Trim();
            _settings.FallbackLanguage = FallbackLanguageBox.Text.Trim();
            _settings.WordThreshold = wordThreshold;
            _settings.TriggerKey = SelectedComboContent(TriggerKeyCombo, "Ctrl");
            _settings.DoubleTapIntervalMs = (int)Math.Round(DoubleTapSlider.Value);
            _settings.MaxKeyHoldMs = maxKeyHold;
            _settings.ClipboardWaitMs = clipboardWait;
            _settings.Enabled = EnabledCheckBox.IsChecked == true;
            _settings.LongTextThreshold = longTextThreshold;
            _settings.PrivacyMode = PrivacyModeCheckBox.IsChecked == true;
            _settings.DetailedLogging = DetailedLoggingCheckBox.IsChecked == true;
            _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
            _settings.PopupFontSize = popupFontSize;
            _settings.PopupMaxWidth = popupMaxWidth;
            _settings.PopupMaxHeight = popupMaxHeight;
            _settings.StreamFallbackDelayMs = streamDelay;
            _settings.AutoCheckUpdates = AutoCheckUpdatesCheckBox.IsChecked == true;

            if (!_settingsStore.TrySave(_settings, out string? errorMessage))
            {
                ShowValidationError(errorMessage ?? "设置保存失败，请稍后重试。");
                return false;
            }

            Logger.SetApiKeys(_settings.Profiles.Select(item => item.ApiKey));
            Logger.Info("设置", "设置窗口中的修改已保存并立即生效。");
            if (_feedbackReady)
            {
                ShowTemporaryStatus("已自动保存");
            }
            if (profileNameChanged)
            {
                RefreshProfiles();
            }

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
        List<ProfileListItem> items = _settings.Profiles
            .Select(profile => new ProfileListItem(
                profile,
                string.Equals(profile.Id, _settings.CurrentProfileId, StringComparison.Ordinal)))
            .ToList();

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
            $"下面这些参数只作用于档案「{profile.Name}」。要改别的档案，先去「服务商档案」里选中它。";

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
        int index = ProfilesList.SelectedIndex;
        bool selected = SelectedProfile is not null;
        MoveProfileUpButton.IsEnabled = selected && index > 0;
        MoveProfileDownButton.IsEnabled = selected && index >= 0 && index < _settings.Profiles.Count - 1;
        DeleteProfileButton.IsEnabled = selected && _settings.Profiles.Count > 1;
        SetCurrentProfileButton.IsEnabled = selected
            && !string.Equals(SelectedProfile!.Id, _settings.CurrentProfileId, StringComparison.Ordinal);
        TestProfileButton.IsEnabled = selected && _testCancellation is null;
    }

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
    }

    private void AddProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("新建服务商档案", () =>
        {
            ApiProfile profile = ApiProfile.CreateFromPreset(
                ProviderPresets.CustomName,
                _settings.GenerateUniqueProfileName("新建服务商"));
            _settings.Profiles.Add(profile);
            RefreshProfiles(profile.Id);
            SaveAll();
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
        });
    }

    private void MoveProfileUpButton_OnClick(object sender, RoutedEventArgs e) => MoveSelectedProfile(-1);

    private void MoveProfileDownButton_OnClick(object sender, RoutedEventArgs e) => MoveSelectedProfile(1);

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
        RefreshProfiles(profile.Id);
        SaveAll();
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

            int failed = results.Count(result => result.Status == DiagnosticStatus.Failed);
            int warnings = results.Count(result => result.Status == DiagnosticStatus.Warning);
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

    private void CopyReportButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("复制诊断报告", () =>
        {
            if (_lastTestRunner is null || _testResults.Count == 0)
            {
                return;
            }

            System.Windows.Clipboard.SetText(_lastTestRunner.BuildReport(_testResults));
            ShowTemporaryStatus("诊断报告已复制（API Key 已脱敏）");
        });
    }

    // ==================== 翻译模式 ====================

    private void RefreshModes(string? selectedModeId = null)
    {
        string desiredMode = selectedModeId ?? _settings.CurrentModeId;
        ModesList.ItemsSource = null;
        ModesList.ItemsSource = _settings.Modes;
        ModesList.SelectedItem = _settings.Modes.FirstOrDefault(mode => mode.Id == desiredMode)
            ?? _settings.Modes.FirstOrDefault();

        TranslationMode? current = _settings.Modes.FirstOrDefault(mode => mode.Id == _settings.CurrentModeId);
        CurrentModeStatusText.Text = current is null
            ? "当前使用：（未选择）"
            : $"当前使用：{current.Name}";

        LoadModeDetails();
        UpdateModeButtons();
    }

    private void LoadModeDetails()
    {
        if (ModesList.SelectedItem is TranslationMode mode)
        {
            ModeNameBox.Text = mode.Name;
            ModePromptBox.Text = mode.SystemPromptTemplate;
        }
        else
        {
            ModeNameBox.Text = string.Empty;
            ModePromptBox.Text = string.Empty;
        }
    }

    private void RefreshBlacklist()
    {
        BlacklistList.ItemsSource = null;
        BlacklistList.ItemsSource = _settings.AppBlacklist;
    }

    private void RefreshProcessList()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(process.ProcessName)
                        && process.Id != Environment.ProcessId)
                    {
                        names.Add(process.ProcessName);
                    }
                }
                catch (Exception exception)
                {
                    Logger.Warning("设置", $"读取一个运行中程序的名称失败，已跳过：{exception.Message}");
                }
            }
        }

        string currentText = RunningProcessesCombo.Text;
        RunningProcessesCombo.ItemsSource = names;
        RunningProcessesCombo.Text = currentText;
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
        bool selected = ModesList.SelectedItem is TranslationMode;
        bool builtIn = ModesList.SelectedItem is TranslationMode { IsBuiltIn: true };
        bool isCurrent = ModesList.SelectedItem is TranslationMode mode && mode.Id == _settings.CurrentModeId;
        ApplyModeButton.IsEnabled = selected && !isCurrent;
        DeleteModeButton.IsEnabled = selected && !builtIn;
        RestoreModeButton.IsEnabled = builtIn;
        ModeNameBox.IsEnabled = selected;
        ModePromptBox.IsEnabled = selected;
    }

    private void ResetDoubleTapTest()
    {
        _testTapCount = 0;
        DoubleTapTestText.Text = "在这里双击触发键试试";
        DoubleTapTestText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            "Brush.Text");
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

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ShowValidationError("请先填写 API Key，再拉取模型。");
                return;
            }

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
                "该服务商不支持列出模型，请到官网查看模型名后手动填写。",
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
            if (ModesList.SelectedItem is not TranslationMode mode)
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
    }

    private void ModeNameBox_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_loading || ModesList.SelectedItem is not TranslationMode mode)
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
        if (_loading || ModesList.SelectedItem is not TranslationMode mode)
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
            if (ModesList.SelectedItem is not TranslationMode mode)
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
            if (ModesList.SelectedItem is not TranslationMode { IsBuiltIn: true } mode)
            {
                ShowValidationError("请选择一个内置模式再恢复默认。");
                return;
            }

            TranslationMode original = BuiltInModes.CreateDefaults().First(item => item.Id == mode.Id);
            mode.Name = original.Name;
            mode.SystemPromptTemplate = original.SystemPromptTemplate;
            RefreshModes(mode.Id);
            SaveAll();
        });
    }

    private void TriggerSetting_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            ResetDoubleTapTest();
            SaveAll(SettingsChangeKind.KeyboardHook);
        }
    }

    private void DoubleTapSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DoubleTapValueText is null)
        {
            return;
        }

        DoubleTapValueText.Text = $"{(int)Math.Round(e.NewValue)} ms";
        if (!_loading)
        {
            ResetDoubleTapTest();
            _triggerSaveTimer.Stop();
            _triggerSaveTimer.Start();
        }
    }

    private void TriggerSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _triggerSaveTimer.Stop();
        SaveAll(SettingsChangeKind.KeyboardHook);
    }

    private void TriggerTextBox_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        ResetDoubleTapTest();
        SaveAll(SettingsChangeKind.KeyboardHook);
    }

    private void AddBlacklistButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("添加应用黑名单", () =>
        {
            string processName = Path.GetFileNameWithoutExtension(RunningProcessesCombo.Text.Trim());
            if (string.IsNullOrWhiteSpace(processName))
            {
                ShowValidationError("请从列表选择程序，或输入进程名后再添加。");
                return;
            }

            if (!_settings.AppBlacklist.Contains(processName, StringComparer.OrdinalIgnoreCase))
            {
                _settings.AppBlacklist.Add(processName);
                _settings.AppBlacklist.Sort(StringComparer.OrdinalIgnoreCase);
                RefreshBlacklist();
                SaveAll();
            }
        });
    }

    private void RemoveBlacklistButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("移除应用黑名单", () =>
        {
            if (BlacklistList.SelectedItem is not string selected)
            {
                ShowValidationError("请先选择要移除的程序。");
                return;
            }

            _settings.AppBlacklist.Remove(selected);
            RefreshBlacklist();
            SaveAll();
        });
    }

    private void RefreshProcessesButton_OnClick(object sender, RoutedEventArgs e) =>
        ExecuteUiAction("刷新运行程序列表", RefreshProcessList);

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

    private void StartWithWindowsCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("修改开机自动启动", () =>
        {
            bool previous = _settings.StartWithWindows;
            bool enabled = StartWithWindowsCheckBox.IsChecked == true;
            if (!StartupRegistration.TrySet(enabled, out string? errorMessage))
            {
                StartWithWindowsCheckBox.IsChecked = previous;
                ShowValidationError(errorMessage ?? "开机自动启动设置没有生效。");
                return;
            }

            if (!SaveAll())
            {
                StartupRegistration.TrySet(previous, out _);
                StartWithWindowsCheckBox.IsChecked = previous;
            }
        });
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
                _historyRepository, owner, CancellationToken.None);
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
    private sealed record ProfileListItem(ApiProfile Profile, bool IsCurrent)
    {
        public string Name => Profile.Name;

        public string Detail => string.IsNullOrWhiteSpace(Profile.Model)
            ? "（还没填模型名）"
            : Profile.Model;

        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

        public Visibility FailedVisibility => Profile.IsFailed ? Visibility.Visible : Visibility.Collapsed;
    }
}
