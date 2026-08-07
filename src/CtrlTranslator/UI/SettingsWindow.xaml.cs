using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CtrlTranslator.Data;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;
using CtrlTranslator.Translation;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using SystemColors = System.Windows.SystemColors;
using TextBox = System.Windows.Controls.TextBox;

namespace CtrlTranslator.UI;

public enum SettingsChangeKind
{
    General,
    KeyboardHook
}

/// <summary>
/// 五页设置窗口。字段离开编辑状态后立即保存，开关和下拉选择则点击后立即保存。
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly ProviderPreset[] ProviderPresets =
    [
        new("自定义", string.Empty, string.Empty),
        new("DeepSeek", "https://api.deepseek.com/v1", "deepseek-v4-flash"),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        new("阿里通义", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-turbo"),
        new("月之暗面 Kimi", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        new("硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct"),
        new("OpenRouter", "https://openrouter.ai/api/v1", string.Empty),
        new("本地 Ollama", "http://localhost:11434/v1", "qwen2.5:7b")
    ];

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryRepository _historyRepository;
    private readonly Action<SettingsChangeKind> _settingsChanged;
    private readonly Action _diagnosticsRequested;
    private readonly DispatcherTimer _triggerSaveTimer;
    private CancellationTokenSource? _modelFetchCancellation;
    private bool _loading = true;
    private bool _apiKeyVisible;
    private int _testTapCount;

    public SettingsWindow(
        AppSettings settings,
        SettingsStore settingsStore,
        HistoryRepository historyRepository,
        Action<SettingsChangeKind> settingsChanged,
        Action diagnosticsRequested)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _historyRepository = historyRepository;
        _settingsChanged = settingsChanged;
        _diagnosticsRequested = diagnosticsRequested;
        _triggerSaveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            TriggerSaveTimer_OnTick,
            Dispatcher);

        LoadControlsFromSettings();
        RefreshProcessList();
        _loading = false;
        UpdateExtraRequestBodyValidation();
        UpdateModeButtons();
    }

    internal void ShowTranslationService(bool focusExtraRequestBody)
    {
        SettingsTabs.SelectedIndex = 0;
        if (focusExtraRequestBody)
        {
            ExtraRequestBodyBox.Focus();
            ExtraRequestBodyBox.SelectAll();
        }
    }

    internal void RecordValidTap()
    {
        if (!IsVisible)
        {
            return;
        }

        _testTapCount = Math.Min(2, _testTapCount + 1);
        DoubleTapTestText.Text = string.Join(' ', Enumerable.Repeat("●", _testTapCount));
        DoubleTapTestText.Foreground = Brushes.DimGray;
    }

    internal void RecordDoubleTap()
    {
        if (!IsVisible)
        {
            return;
        }

        _testTapCount = 0;
        DoubleTapTestText.Text = "✓ 双击已识别";
        DoubleTapTestText.Foreground = new SolidColorBrush(Color.FromRgb(39, 106, 54));
    }

    private void LoadControlsFromSettings()
    {
        ProviderPresetCombo.ItemsSource = ProviderPresets.Select(preset => preset.Name);
        ProviderPresetCombo.SelectedItem = ProviderPresets.Any(preset => preset.Name == _settings.ProviderPreset)
            ? _settings.ProviderPreset
            : "自定义";
        BaseUrlBox.Text = _settings.BaseUrl;
        ApiKeyPasswordBox.Password = _settings.ApiKey;
        ApiKeyVisibleBox.Text = _settings.ApiKey;
        ModelCombo.Text = _settings.Model;
        TemperatureBox.Text = _settings.Temperature.ToString("0.##", CultureInfo.CurrentCulture);
        MaxTokensBox.Text = _settings.MaxTokens.ToString(CultureInfo.CurrentCulture);
        RequestTimeoutBox.Text = _settings.RequestTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        FirstTokenTimeoutBox.Text = _settings.FirstTokenTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        ExtraRequestBodyBox.Text = _settings.ExtraRequestBody;

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
        SelectComboItemByTag(ThemeCombo, _settings.Theme);
        PopupFontSizeBox.Text = _settings.PopupFontSize.ToString("0.##", CultureInfo.CurrentCulture);
        PopupMaxWidthBox.Text = _settings.PopupMaxWidth.ToString("0.##", CultureInfo.CurrentCulture);
        PopupMaxHeightBox.Text = _settings.PopupMaxHeight.ToString("0.##", CultureInfo.CurrentCulture);
        StreamFallbackDelayBox.Text = _settings.StreamFallbackDelayMs.ToString(CultureInfo.CurrentCulture);
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

            _settings.ProviderPreset = ProviderPresetCombo.SelectedItem as string ?? "自定义";
            _settings.BaseUrl = BaseUrlBox.Text.Trim();
            _settings.ApiKey = CurrentApiKey().Trim();
            _settings.Model = ModelCombo.Text.Trim();
            _settings.Temperature = temperature;
            _settings.MaxTokens = maxTokens;
            _settings.RequestTimeoutSeconds = requestTimeout;
            _settings.FirstTokenTimeoutSeconds = firstTokenTimeout;
            _settings.ExtraRequestBody = ExtraRequestBodyBox.Text.Trim();
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
            _settings.Theme = SelectedComboTag(ThemeCombo, "System");
            _settings.PopupFontSize = popupFontSize;
            _settings.PopupMaxWidth = popupMaxWidth;
            _settings.PopupMaxHeight = popupMaxHeight;
            _settings.StreamFallbackDelayMs = streamDelay;

            if (!_settingsStore.TrySave(_settings, out string? errorMessage))
            {
                ShowValidationError(errorMessage ?? "设置保存失败，请稍后重试。");
                return false;
            }

            Logger.SetApiKey(_settings.ApiKey);
            Logger.Info("设置", "设置窗口中的修改已保存并立即生效。");
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(39, 106, 54));
            StatusText.Text = "已保存，设置已立即生效。";
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

    private void RefreshModes(string? selectedModeId = null)
    {
        string desiredMode = selectedModeId ?? _settings.CurrentModeId;
        CurrentModeCombo.ItemsSource = null;
        ModesList.ItemsSource = null;
        CurrentModeCombo.ItemsSource = _settings.Modes;
        ModesList.ItemsSource = _settings.Modes;
        CurrentModeCombo.SelectedValue = desiredMode;
        ModesList.SelectedItem = _settings.Modes.FirstOrDefault(mode => mode.Id == desiredMode)
            ?? _settings.Modes.FirstOrDefault();
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
        ExtraRequestBodyBox.BorderBrush = valid
            ? SystemColors.ControlDarkBrush
            : new SolidColorBrush(Color.FromRgb(179, 38, 30));
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
        EditModeButton.IsEnabled = selected;
        DeleteModeButton.IsEnabled = selected && !builtIn;
        RestoreModeButton.IsEnabled = builtIn;
    }

    private void ResetDoubleTapTest()
    {
        _testTapCount = 0;
        DoubleTapTestText.Text = "在这里双击触发键试试";
        DoubleTapTestText.Foreground = Brushes.Black;
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
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(179, 38, 30));
        StatusText.Text = message;
    }

    private string CurrentApiKey() => _apiKeyVisible
        ? ApiKeyVisibleBox.Text
        : ApiKeyPasswordBox.Password;

    private static string SelectedComboContent(ComboBox comboBox, string fallback)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
    }

    private static string SelectedComboTag(ComboBox comboBox, string fallback)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    }

    private static void SelectComboItemByContent(ComboBox comboBox, string content)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase));
    }

    private static void SelectComboItemByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
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
            ProviderPreset preset = ProviderPresets.First(item => item.Name == selected);
            if (preset.Name != "自定义")
            {
                BaseUrlBox.Text = preset.BaseUrl;
                ModelCombo.Text = preset.Model;
            }

            SaveAll();
        });
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
            IReadOnlyList<string> models = await OpenAiCompatibleClient.FetchModelsAsync(
                baseUrl,
                apiKey,
                _modelFetchCancellation.Token);
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
            StatusText.Text = $"已拉取到 {models.Count} 个模型，请从列表中选择。";
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

    private void DiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("打开一键诊断", _diagnosticsRequested);
    }

    private void CurrentModeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CurrentModeCombo.SelectedValue is not string modeId)
        {
            return;
        }

        ExecuteUiAction("切换翻译模式", () =>
        {
            _settings.CurrentModeId = modeId;
            SaveAll();
            ModesList.SelectedItem = _settings.Modes.FirstOrDefault(mode => mode.Id == modeId);
        });
    }

    private void ModesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateModeButtons();

    private void NewModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("新建翻译模式", () =>
        {
            var editor = new ModeEditorWindow("新建翻译模式", string.Empty, string.Empty) { Owner = this };
            if (editor.ShowDialog() != true)
            {
                return;
            }

            var mode = new TranslationMode
            {
                Id = $"custom-{Guid.NewGuid():N}",
                Name = editor.ModeName,
                SystemPromptTemplate = editor.PromptTemplate,
                IsBuiltIn = false
            };
            _settings.Modes.Add(mode);
            _settings.CurrentModeId = mode.Id;
            RefreshModes(mode.Id);
            SaveAll();
        });
    }

    private void EditModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction("编辑翻译模式", () =>
        {
            if (ModesList.SelectedItem is not TranslationMode mode)
            {
                ShowValidationError("请先选择要编辑的翻译模式。");
                return;
            }

            var editor = new ModeEditorWindow("编辑翻译模式", mode.Name, mode.SystemPromptTemplate) { Owner = this };
            if (editor.ShowDialog() != true)
            {
                return;
            }

            mode.Name = editor.ModeName;
            mode.SystemPromptTemplate = editor.PromptTemplate;
            RefreshModes(mode.Id);
            SaveAll();
        });
    }

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
            string? path = await HistoryCsvExporter.ExportAsync(
                _historyRepository, this, CancellationToken.None);
            if (path is not null)
            {
                System.Windows.MessageBox.Show(this, "历史记录已导出。", "导出完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "从设置窗口导出历史失败。", exception);
            System.Windows.MessageBox.Show(this, "导出失败，请确认保存位置可写后重试。", "导出失败",
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

    private void GeneralComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => SaveAll();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        _triggerSaveTimer.Stop();
        _modelFetchCancellation?.Cancel();
        _modelFetchCancellation?.Dispose();
        _modelFetchCancellation = null;
    }

    private sealed record ProviderPreset(string Name, string BaseUrl, string Model);
}

/// <summary>
/// 翻译模式的名称与提示词编辑器。
/// </summary>
internal sealed class ModeEditorWindow : Window
{
    private readonly TextBox _nameBox;
    private readonly TextBox _promptBox;

    public ModeEditorWindow(string title, string modeName, string promptTemplate)
    {
        Title = title;
        Width = 700;
        Height = 560;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var nameLabel = new TextBlock { Text = "名称", Margin = new Thickness(0, 0, 0, 5) };
        _nameBox = new TextBox { Text = modeName, MinHeight = 28, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(_nameBox, 1);
        var promptLabel = new TextBlock { Text = "系统提示词", Margin = new Thickness(0, 0, 0, 5) };
        Grid.SetRow(promptLabel, 2);
        _promptBox = new TextBox
        {
            Text = promptTemplate,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 26, 0, 8)
        };
        Grid.SetRow(_promptBox, 2);
        var hint = new TextBlock
        {
            Text = "可用占位符：{target_lang}  {fallback_lang}",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(hint, 3);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var saveButton = new Button { Content = "保存", Width = 88, IsDefault = true };
        saveButton.Click += SaveButton_OnClick;
        var cancelButton = new Button { Content = "取消", Width = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);
        Grid.SetRow(buttons, 4);

        grid.Children.Add(nameLabel);
        grid.Children.Add(_nameBox);
        grid.Children.Add(promptLabel);
        grid.Children.Add(_promptBox);
        grid.Children.Add(hint);
        grid.Children.Add(buttons);
        Content = grid;
    }

    public string ModeName => _nameBox.Text.Trim();

    public string PromptTemplate => _promptBox.Text.Trim();

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ModeName) || string.IsNullOrWhiteSpace(PromptTemplate))
            {
                System.Windows.MessageBox.Show(
                    "模式名称和系统提示词都需要填写。",
                    "翻译模式",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "保存翻译模式编辑内容失败。", exception);
            System.Windows.MessageBox.Show(
                "翻译模式没有保存成功，详情已记录到日志。",
                "翻译模式",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
