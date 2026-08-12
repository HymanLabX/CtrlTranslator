using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;
using CtrlTranslator.Translation;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;

namespace CtrlTranslator.UI;

public partial class OnboardingWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Action _settingsApplied;
    private readonly Func<ApiProfile, DiagnosticRunner> _diagnosticRunnerFactory;
    private readonly IReadOnlyList<ProviderChoice> _providers =
        ProviderPresets.All.Select(preset => new ProviderChoice(preset)).ToArray();
    private readonly ObservableCollection<DiagnosticResult> _diagnostics = [];
    private CancellationTokenSource? _testCancellation;
    private int _step = 1;
    private bool _testSucceeded;

    public OnboardingWindow(
        AppSettings settings,
        SettingsStore settingsStore,
        Action settingsApplied,
        Func<ApiProfile, DiagnosticRunner> diagnosticRunnerFactory)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _settingsApplied = settingsApplied;
        _diagnosticRunnerFactory = diagnosticRunnerFactory;
        DiagnosticsList.ItemsSource = _diagnostics;
        ProviderList.ItemsSource = _providers;
        ProviderList.SelectedIndex = Math.Max(0, _providers.ToList().FindIndex(item =>
            string.Equals(item.Name, settings.CurrentProfile.ProviderPreset, StringComparison.Ordinal)));
        Closed += OnboardingWindow_OnClosed;
    }

    private void OnboardingWindow_OnClosed(object? sender, EventArgs e)
    {
        _testCancellation?.Cancel();
        _testCancellation?.Dispose();
    }

    private void ProviderList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderList.SelectedItem is ProviderChoice provider)
        {
            ApplyProvider(provider.Preset);
        }
    }

    /// <summary>
    /// 选中服务商后把这一页能替用户填的都填好：地址、常用模型、申请 Key 的入口。
    /// 只有「自定义」才需要用户自己填地址 —— 填错接口地址是新手最常见的卡点。
    /// </summary>
    private void ApplyProvider(ProviderPreset preset)
    {
        bool isCustom = string.Equals(preset.Name, ProviderPresets.CustomName, StringComparison.Ordinal);

        KnownBaseUrlPanel.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
        CustomBaseUrlPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        BaseUrlDisplayText.Text = preset.BaseUrl;
        BaseUrlBox.Text = isCustom ? string.Empty : preset.BaseUrl;

        ModelBox.ItemsSource = preset.Models;
        ModelBox.Text = preset.Model;

        ApplyKeyButton.Visibility = string.IsNullOrWhiteSpace(preset.KeyUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;

        CredentialsProviderText.Text = isCustom
            ? "你选的是「自定义」，需要自己填写服务地址。"
            : $"已选择「{preset.Name}」，服务地址已经替你填好了。你只需要粘一个 API Key、选个模型。";
        SetStatus(string.Empty, isError: false);
    }

    /// <summary>当前生效的服务地址：已知服务商用预设值，自定义才读输入框。</summary>
    private string CurrentBaseUrl =>
        CustomBaseUrlPanel.Visibility == Visibility.Visible
            ? BaseUrlBox.Text.Trim()
            : SelectedPreset().BaseUrl;

    private void SetStatus(string message, bool isError)
    {
        CredentialsStatusText.Text = message;
        CredentialsStatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            isError ? "Brush.Danger" : "Brush.TextSecondary");
    }

    private void ApplyKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenUrl(SelectedPreset().KeyUrl);
    }

    private void OpenProviderWebsite_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            OpenUrl(url);
        }
    }

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Logger.Error("首次向导", "打开服务商网站失败。", exception);
            MessageBox.Show(this, "暂时无法打开网页，请稍后再试。", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void FetchModelsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryReadCredentials(out string baseUrl, out string apiKey, out _))
            {
                return;
            }

            SetStatus("正在读取可用模型…", isError: false);
            // 见 SettingsWindow 里同名调用的注释：首次连接某个服务商时的代理探测/DNS
            // 解析可能在真正 await 之前同步耗时，扔进线程池避免卡住向导界面。
            string previousModel = ModelBox.Text;
            IReadOnlyList<string> models = await Task.Run(
                () => OpenAiCompatibleClient.FetchModelsAsync(baseUrl, apiKey, CancellationToken.None));
            if (models.Count == 0)
            {
                SetStatus("服务商没有返回模型列表，保留原有选项，你也可以直接手输模型名。", isError: false);
                return;
            }

            ModelBox.ItemsSource = models;
            // 别把用户已经选好的模型冲掉 —— 拉取只是"多给几个选项"，不是"替他重选"。
            ModelBox.Text = previousModel;
            SetStatus($"已读取 {models.Count} 个模型，可以从下拉里换一个。", isError: false);
        }
        catch (Exception exception)
        {
            Logger.Error("首次向导", "拉取模型列表失败。", exception);
            UserFacingError error = ErrorMessages.FromException(exception, ModelBox.Text, thinkingDisabled: false);
            SetStatus(error.Message, isError: true);
        }
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_step > 1)
        {
            ShowStep(_step - 1);
        }
    }

    private async void NextButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_step == 2 && ProviderList.SelectedItem is null)
            {
                MessageBox.Show(this, "请先选择一个服务商。", "还差一步", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_step == 3)
            {
                if (!TryReadCredentials(out _, out _, out _))
                {
                    return;
                }

                ShowStep(4);
                await RunConnectionTestAsync();
                return;
            }

            ShowStep(_step + 1);
        }
        catch (Exception exception)
        {
            Logger.Error("首次向导", "切换向导步骤失败。", exception);
            MessageBox.Show(this, "向导暂时无法继续，请返回检查填写内容。", "无法继续", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RunConnectionTestAsync()
    {
        _testCancellation?.Cancel();
        _testCancellation?.Dispose();
        _testCancellation = new CancellationTokenSource();
        _testSucceeded = false;
        FinishButton.IsEnabled = false;
        ReadyPanel.Visibility = Visibility.Collapsed;
        TestFailureActions.Visibility = Visibility.Collapsed;
        TestResultText.Text = string.Empty;
        _diagnostics.Clear();
        TestStatusText.Text = "正在连接并翻译 Hello, world.…";

        try
        {
            TryReadCredentials(out string baseUrl, out string apiKey, out string model);
            using OpenAiCompatibleClient client = new();
            TranslationClientResult result = await client.TranslateAsync(
                new TranslationRequest(
                    baseUrl,
                    apiKey,
                    model,
                    "把用户文字翻译成中文，只输出译文。",
                    "Hello, world.",
                    0.2,
                    128,
                    AppSettings.DefaultRequestTimeoutSeconds,
                    AppSettings.DefaultFirstTokenTimeoutSeconds,
                    SelectedPreset().ExtraRequestBody),
                null,
                _testCancellation.Token);
            TestStatusText.Text = $"连接成功 · 首字 {result.FirstTokenLatencyMilliseconds}ms · 总耗时 {result.TotalLatencyMilliseconds}ms";
            TestResultText.Text = result.Text;
            ReadyPanel.Visibility = Visibility.Visible;
            _testSucceeded = true;
            FinishButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (_testCancellation.IsCancellationRequested)
        {
            Logger.Info("首次向导", "连接测试已取消。 ");
        }
        catch (Exception exception)
        {
            Logger.Error("首次向导", "连接测试失败。", exception);
            UserFacingError error = ErrorMessages.FromException(exception, ModelBox.Text, thinkingDisabled: false);
            TestStatusText.Text = error.Message;
            TestFailureActions.Visibility = Visibility.Visible;
        }
    }

    private void BackFromFailureButton_OnClick(object sender, RoutedEventArgs e) => ShowStep(3);

    /// <summary>
    /// 独立诊断窗口在 M5 已被移除，这里就地跑同一套 12 项检查并把结果列在向导里。
    /// </summary>
    private async void DiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadCredentials(out string baseUrl, out string apiKey, out string model))
        {
            return;
        }

        _diagnostics.Clear();
        DiagnosticsButton.IsEnabled = false;
        DiagnosticsButton.Content = "诊断中…";

        try
        {
            DiagnosticRunner runner = _diagnosticRunnerFactory(BuildProfile(baseUrl, apiKey, model));
            var progress = new Progress<DiagnosticResult>(_diagnostics.Add);
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(90));
            IReadOnlyList<DiagnosticResult> results = await runner.RunAsync(progress, cancellation.Token);
            int failed = results.Count(result => result.Status == DiagnosticStatus.Failed);
            TestStatusText.Text = failed > 0
                ? $"诊断完成：有 {failed} 项没通过，看下面标 ✗ 的那一行。"
                : "诊断完成：所有检查都通过了，可以再点一次「下一步」重试。";
        }
        catch (OperationCanceledException)
        {
            Logger.Info("首次向导", "诊断已取消或超时。");
            TestStatusText.Text = "诊断被中断或超时了。";
        }
        catch (Exception exception)
        {
            Logger.Error("首次向导", "运行诊断失败。", exception);
            TestStatusText.Text = "诊断没有跑完，详情已记录到日志。";
        }
        finally
        {
            DiagnosticsButton.IsEnabled = true;
            DiagnosticsButton.Content = "详细诊断";
        }
    }

    private void SkipButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void FinishButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_testSucceeded || !TryReadCredentials(out string baseUrl, out string apiKey, out string model))
        {
            return;
        }

        // 首个档案直接落在配置里已有的那个空白档案上，避免留下一个没人用的"默认服务商"。
        ApiProfile profile = _settings.CurrentProfile;
        ProviderPreset preset = SelectedPreset();
        profile.Name = _settings.Profiles.Count == 1
            ? preset.Name
            : _settings.GenerateUniqueProfileName(preset.Name);
        profile.ProviderPreset = preset.Name;
        profile.BaseUrl = baseUrl;
        profile.ApiKey = apiKey;
        profile.Model = model;
        profile.ExtraRequestBody = preset.ExtraRequestBody;
        profile.ClearFailure();

        if (!_settingsStore.TrySave(_settings, out string? errorMessage))
        {
            MessageBox.Show(this, errorMessage ?? "设置没有保存成功，请稍后再试。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Logger.SetApiKeys(_settings.Profiles.Select(item => item.ApiKey));
        _settingsApplied();
        Close();
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 1, 4);
        WelcomePage.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ProviderPage.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        CredentialsPage.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        TestPage.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = $"第 {_step} / 4 步";
        BackButton.IsEnabled = _step > 1 && _step < 4;
        NextButton.Visibility = _step < 4 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ProviderPreset SelectedPreset() =>
        (ProviderList.SelectedItem as ProviderChoice)?.Preset ?? ProviderPresets.Custom;

    private bool TryReadCredentials(out string baseUrl, out string apiKey, out string model)
    {
        baseUrl = CurrentBaseUrl;
        apiKey = ApiKeyBox.Password.Trim();
        model = ModelBox.Text.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            SetStatus("服务地址格式不对，正确的样子像：https://api.deepseek.com/v1", isError: true);
            return false;
        }

        if (string.IsNullOrWhiteSpace(apiKey) && !uri.IsLoopback)
        {
            SetStatus("请粘贴 API Key；本地 Ollama 可以不填。", isError: true);
            return false;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            SetStatus("请选择或填写一个模型。", isError: true);
            return false;
        }

        SetStatus(string.Empty, isError: false);
        return true;
    }

    private ApiProfile BuildProfile(string baseUrl, string apiKey, string model)
    {
        ProviderPreset preset = SelectedPreset();
        return new ApiProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = preset.Name,
            ProviderPreset = preset.Name,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model,
            ExtraRequestBody = preset.ExtraRequestBody
        };
    }

    /// <summary>
    /// 列表显示用的包装。绑定需要一个 Visibility，而 Settings 层的 ProviderPreset
    /// 是纯数据、不该认识 WPF 的类型。
    /// </summary>
    private sealed record ProviderChoice(ProviderPreset Preset)
    {
        public string Name => Preset.Name;

        public string Description => Preset.Description;

        public string KeyUrl => Preset.KeyUrl;

        public Visibility KeyButtonVisibility => string.IsNullOrWhiteSpace(Preset.KeyUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
