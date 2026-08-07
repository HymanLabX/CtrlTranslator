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
    private readonly Action<AppSettings> _diagnosticsRequested;
    private readonly IReadOnlyList<ProviderPreset> _providers = ProviderPreset.CreateAll();
    private CancellationTokenSource? _testCancellation;
    private int _step = 1;
    private bool _testSucceeded;

    public OnboardingWindow(
        AppSettings settings,
        SettingsStore settingsStore,
        Action settingsApplied,
        Action<AppSettings> diagnosticsRequested)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _settingsApplied = settingsApplied;
        _diagnosticsRequested = diagnosticsRequested;
        ProviderList.ItemsSource = _providers;
        ProviderList.SelectedIndex = Math.Max(0, _providers.ToList().FindIndex(item =>
            string.Equals(item.Name, settings.ProviderPreset, StringComparison.Ordinal)));
        Closed += OnboardingWindow_OnClosed;
    }

    private void OnboardingWindow_OnClosed(object? sender, EventArgs e)
    {
        _testCancellation?.Cancel();
        _testCancellation?.Dispose();
    }

    private void ProviderList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderList.SelectedItem is not ProviderPreset provider)
        {
            return;
        }

        BaseUrlBox.Text = provider.BaseUrl;
        ModelBox.Text = provider.Model;
    }

    private void OpenProviderWebsite_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
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

            CredentialsStatusText.Text = "正在读取可用模型…";
            IReadOnlyList<string> models = await OpenAiCompatibleClient.FetchModelsAsync(
                baseUrl, apiKey, CancellationToken.None);
            ModelBox.ItemsSource = models;
            if (models.Count > 0 && string.IsNullOrWhiteSpace(ModelBox.Text))
            {
                ModelBox.SelectedIndex = 0;
            }
            CredentialsStatusText.Text = models.Count == 0
                ? "服务商没有返回模型列表，可以手动填写模型名。"
                : $"已读取 {models.Count} 个模型，请选择一个。";
        }
        catch (Exception exception)
        {
            Logger.Error("首次向导", "拉取模型列表失败。", exception);
            UserFacingError error = ErrorMessages.FromException(exception, ModelBox.Text, thinkingDisabled: false);
            CredentialsStatusText.Text = error.Message;
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
        ReadyText.Visibility = Visibility.Collapsed;
        TestFailureActions.Visibility = Visibility.Collapsed;
        TestResultText.Text = string.Empty;
        TestStatusText.Text = "正在连接并翻译 Hello, world.…";

        try
        {
            TryReadCredentials(out string baseUrl, out string apiKey, out string model);
            AppSettings temporarySettings = CreateTemporarySettings(baseUrl, apiKey, model);
            using OpenAiCompatibleClient client = new(() => temporarySettings);
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
                    AppSettings.DefaultExtraRequestBody),
                null,
                _testCancellation.Token);
            TestStatusText.Text = $"连接成功 · 首字 {result.FirstTokenLatencyMilliseconds}ms · 总耗时 {result.TotalLatencyMilliseconds}ms";
            TestResultText.Text = result.Text;
            ReadyText.Visibility = Visibility.Visible;
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

    private void DiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TryReadCredentials(out string baseUrl, out string apiKey, out string model))
            {
                _diagnosticsRequested(CreateTemporarySettings(baseUrl, apiKey, model));
            }
        }
        catch (Exception exception)
        {
            Logger.Error("首次向导", "打开诊断窗口失败。", exception);
            MessageBox.Show(this, "暂时无法打开诊断窗口，详情已写入日志。", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        ProviderPreset provider = (ProviderPreset)ProviderList.SelectedItem;
        _settings.ProviderPreset = provider.Name;
        _settings.BaseUrl = baseUrl;
        _settings.ApiKey = apiKey;
        _settings.Model = model;
        if (!_settingsStore.TrySave(_settings, out string? errorMessage))
        {
            MessageBox.Show(this, errorMessage ?? "设置没有保存成功，请稍后再试。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Logger.SetApiKey(_settings.ApiKey);
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

    private bool TryReadCredentials(out string baseUrl, out string apiKey, out string model)
    {
        baseUrl = BaseUrlBox.Text.Trim();
        apiKey = ApiKeyBox.Password.Trim();
        model = ModelBox.Text.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            CredentialsStatusText.Text = "服务地址格式不对，正确的样子像：https://api.deepseek.com/v1";
            return false;
        }

        if (string.IsNullOrWhiteSpace(apiKey) && !uri.IsLoopback)
        {
            CredentialsStatusText.Text = "请填写 API Key；本地 Ollama 可以不填。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            CredentialsStatusText.Text = "请填写或选择一个模型。";
            return false;
        }

        CredentialsStatusText.Text = string.Empty;
        return true;
    }

    private static AppSettings CreateTemporarySettings(string baseUrl, string apiKey, string model) => new()
    {
        BaseUrl = baseUrl,
        ApiKey = apiKey,
        Model = model
    };

    private sealed record ProviderPreset(
        string Name,
        string BaseUrl,
        string Model,
        string Description,
        string KeyUrl)
    {
        public Visibility KeyButtonVisibility => string.IsNullOrWhiteSpace(KeyUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public static IReadOnlyList<ProviderPreset> CreateAll() =>
        [
            new("DeepSeek", "https://api.deepseek.com/v1", "deepseek-v4-flash", "速度快，适合日常划词翻译", "https://platform.deepseek.com/api_keys"),
            new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", "国内服务，提供快速模型", "https://open.bigmodel.cn/usercenter/apikeys"),
            new("阿里通义", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-turbo", "阿里云百炼 OpenAI 兼容接口", "https://help.aliyun.com/zh/model-studio/get-api-key"),
            new("月之暗面 Kimi", "https://api.moonshot.cn/v1", "moonshot-v1-8k", "适合通用文本翻译", "https://platform.moonshot.cn/console/api-keys"),
            new("硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct", "提供多种开源模型", "https://cloud.siliconflow.cn/account/ak"),
            new("OpenRouter", "https://openrouter.ai/api/v1", "", "可从多个服务商和模型中选择", "https://openrouter.ai/docs/quickstart"),
            new("本地 Ollama", "http://localhost:11434/v1", "qwen2.5:7b", "数据留在本机，无需 API Key", "https://ollama.com/download"),
            new("自定义", "", "", "填写任意 OpenAI 兼容接口", "")
        ];
    }
}
