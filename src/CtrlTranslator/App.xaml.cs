using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CtrlTranslator.Core;
using CtrlTranslator.Data;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Interop;
using CtrlTranslator.Settings;
using CtrlTranslator.UI;
using CtrlTranslator.Translation;

namespace CtrlTranslator;

/// <summary>
/// 应用入口：负责单实例、基础设施初始化和全局异常兜底。
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\CtrlTranslator.SingleInstance";
    private const string ActivationEventName = "Local\\CtrlTranslator.Activate";
    private const string UnexpectedErrorMessage =
        "程序遇到了意外错误，已记录到日志。如果反复出现，请把日志发给开发者。";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private TrayIconController? _trayIcon;
    private AppSettings? _settings;
    private AppState? _appState;
    private TextCapture? _textCapture;
    private LowLevelKeyboardHook? _keyboardHook;
    private SettingsStore? _settingsStore;
    private TranslationService? _translationService;
    private PopupWindow? _popupWindow;
    private bool _loggerInitialized;
    private int _unhandledExceptionReported;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Logger.Initialize();
            _loggerInitialized = true;
            RegisterGlobalExceptionHandlers();
            Logger.Info("应用", "CtrlTranslator 正在启动。");

            if (!AcquireSingleInstance())
            {
                Logger.Info("应用", "检测到已有实例，本实例即将退出。");
                SignalExistingInstance();
                Shutdown();
                return;
            }

            StartActivationListener();

            SettingsStore settingsStore = new();
            _settingsStore = settingsStore;
            AppSettings settings = settingsStore.LoadOrCreate();
            _settings = settings;
            Logger.SetApiKey(settings.ApiKey);

            _trayIcon = new TrayIconController(settings, settingsStore);
            if (!string.IsNullOrWhiteSpace(settingsStore.LastWarning))
            {
                System.Windows.MessageBox.Show(
                    settingsStore.LastWarning,
                    "CtrlTranslator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            Database database = new();
            try
            {
                database.Initialize();
            }
            catch (Exception exception)
            {
                Logger.Error("数据库", "初始化历史数据库失败，翻译功能将继续运行。", exception);
            }

            _translationService = new TranslationService(
                () => _settings ?? settings,
                new HistoryRepository(database));

            _appState = new AppState(settings);
            _textCapture = new TextCapture(Dispatcher);
            DoubleTapDetector detector = new(
                settings.TriggerKey,
                settings.DoubleTapIntervalMs,
                settings.MaxKeyHoldMs);
            _keyboardHook = new LowLevelKeyboardHook(Dispatcher, detector);
            _keyboardHook.DoubleTapped += OnDoubleTapped;
            _keyboardHook.EscapePressed += OnEscapePressed;
            _keyboardHook.Install();

            Logger.Info("应用", "CtrlTranslator 已启动，托盘图标已显示。");
        }
        catch (Exception exception)
        {
            TryLogStartupFailure(exception);
            System.Windows.MessageBox.Show(
                "程序启动失败，详情已记录到日志。请重试；如果反复失败，可能是安全软件拦截了全局按键监听。",
                "CtrlTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UnregisterGlobalExceptionHandlers();

        try
        {
            _popupWindow?.Close();
            _translationService?.Dispose();

            if (_keyboardHook is not null)
            {
                _keyboardHook.DoubleTapped -= OnDoubleTapped;
                _keyboardHook.EscapePressed -= OnEscapePressed;
                _keyboardHook.Dispose();
            }
        }
        catch (Exception exception)
        {
            Logger.Error("应用", "卸载全局键盘钩子失败。", exception);
        }

        try
        {
            _activationRegistration?.Unregister(null);
            _activationEvent?.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Error("应用", "停止实例激活监听失败。", exception);
        }

        try
        {
            _trayIcon?.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Error("应用", "移除托盘图标失败。", exception);
        }

        try
        {
            if (_ownsMutex)
            {
                _instanceMutex?.ReleaseMutex();
            }

            _instanceMutex?.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Error("应用", "释放单实例互斥量失败。", exception);
        }

        if (_loggerInitialized)
        {
            Logger.Info("应用", "CtrlTranslator 已退出。");
            Logger.Shutdown();
        }
        base.OnExit(e);
    }

    private bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;
        return createdNew;
    }

    private void StartActivationListener()
    {
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName,
            out _);

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    Dispatcher.BeginInvoke(ActivateExistingWindow);
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using EventWaitHandle activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException exception)
        {
            Logger.Error("应用", "已有实例尚未准备好接收激活请求。", exception);
        }
    }

    private void ActivateExistingWindow()
    {
        try
        {
            Window? window = Windows.Cast<Window>().FirstOrDefault(candidate => candidate.IsVisible);
            if (window is not null)
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Show();
                window.Activate();
            }
            else
            {
                _trayIcon?.ShowInformation("CtrlTranslator", "程序已经在运行。主窗口将在后续里程碑提供。");
            }

            Logger.Info("应用", "已处理第二个实例的激活请求。");
        }
        catch (Exception exception)
        {
            Logger.Error("应用", "激活现有实例失败。", exception);
            _trayIcon?.ShowError(UnexpectedErrorMessage);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void UnregisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("全局异常", "界面线程发生未处理异常。", e.Exception);
        ReportUnhandledException();
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception? exception = e.ExceptionObject as Exception;
        Logger.Error("全局异常", "后台线程发生未处理异常。", exception);
        ReportUnhandledException();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("全局异常", "后台任务发生未观察异常。", e.Exception);
        ReportUnhandledException();
        e.SetObserved();
    }

    private async void OnDoubleTapped(object? sender, EventArgs e)
    {
        try
        {
            AppState? appState = _appState;
            TextCapture? textCapture = _textCapture;
            if (appState is null || textCapture is null)
            {
                return;
            }

            // SPEC §2.2 的四项前置检查必须保持此顺序。
            if (!appState.Enabled)
            {
                return;
            }

            ForegroundApp? foregroundApp = ForegroundApp.GetCurrent();
            if (appState.IsBlacklisted(foregroundApp?.ProcessName))
            {
                return;
            }

            if (!appState.TryEnterDebounce(Stopwatch.GetTimestamp()))
            {
                return;
            }

            if (foregroundApp?.ProcessId == Environment.ProcessId)
            {
                return;
            }

            string? text = await textCapture.CaptureAsync(
                clipboardWaitMs: GetClipboardWaitMilliseconds());
            if (text is null)
            {
                return;
            }

            if (appState.DetailedLogging)
            {
                Logger.Info(
                    "取词",
                    $"取词成功，共 {text.Length} 字，来源程序：{foregroundApp?.ProcessName ?? "未知"}，原文：{text}");
            }
            else
            {
                Logger.Info(
                    "取词",
                    $"取词成功，共 {text.Length} 字，来源程序：{foregroundApp?.ProcessName ?? "未知"}。原文未记录。");
            }

            if (text.Length > 20_000)
            {
                PopupWindow popup = CreatePopup(text, foregroundApp?.ProcessName);
                popup.ShowTranslationError(new UserFacingError(
                    $"选中内容过长（{text.Length} 字符），请分段翻译。单次上限 20000 字符。",
                    ErrorAction.None,
                    null));
                return;
            }

            int threshold = _settings?.LongTextThreshold ?? 2_000;
            if (threshold > 0 && text.Length > threshold)
            {
                double estimatedSeconds = text.Length / 1_000d * 3;
                MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                    $"你选中了 {text.Length} 个字符，翻译它大约需要 {estimatedSeconds:F1} 秒并消耗较多额度。确定要翻译吗？",
                    "确认翻译长文本",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
                if (confirmation != MessageBoxResult.OK)
                {
                    return;
                }
            }

            await StartTranslationAsync(text, foregroundApp?.ProcessName);
        }
        catch (ClipboardUnavailableException exception)
        {
            Logger.Error("取词", "剪贴板在重试后仍不可用。", exception);
            ShowCaptureError(ErrorMessages.FromException(exception, _settings?.Model ?? string.Empty));
        }
        catch (Exception exception)
        {
            Logger.Error("取词", "划词取词失败。", exception);
            ShowCaptureError(new UserFacingError(
                "取词没有完成，请重新选中文字后再试一次。详情已记录到日志。",
                ErrorAction.None,
                null));
        }
    }

    private async Task StartTranslationAsync(string text, string? appName)
    {
        TranslationService? service = _translationService;
        if (service is null)
        {
            return;
        }

        PopupWindow popup = CreatePopup(text, appName);
        popup.StartLoading();
        var progress = new Progress<TranslationStreamUpdate>(update =>
        {
            if (ReferenceEquals(_popupWindow, popup))
            {
                popup.AppendStream(update);
            }
        });

        try
        {
            TranslationResult result = await service.TranslateAsync(
                text,
                appName,
                progress,
                CancellationToken.None);
            if (ReferenceEquals(_popupWindow, popup))
            {
                popup.ShowResult(result);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Info("翻译", "翻译请求已取消。");
        }
        catch (Exception exception)
        {
            Logger.Error("翻译", "翻译请求失败。", exception);
            if (ReferenceEquals(_popupWindow, popup))
            {
                popup.ShowTranslationError(ErrorMessages.FromException(
                    exception,
                    _settings?.Model ?? string.Empty));
            }
        }
    }

    private PopupWindow CreatePopup(string text, string? appName)
    {
        if (_popupWindow is not null)
        {
            PopupWindow oldPopup = _popupWindow;
            _popupWindow = null;
            oldPopup.Close();
        }

        var popup = new PopupWindow(text, appName, _settings ?? new AppSettings());
        popup.ClosingRequested += Popup_OnClosingRequested;
        popup.ActionRequested += Popup_OnActionRequested;
        popup.StarredChanged += Popup_OnStarredChanged;
        popup.SuppressReasoningWarningRequested += Popup_OnSuppressReasoningWarningRequested;
        popup.Closed += Popup_OnClosed;
        _popupWindow = popup;
        popup.Show();
        return popup;
    }

    private void ShowCaptureError(UserFacingError error)
    {
        PopupWindow popup = CreatePopup(string.Empty, null);
        popup.ShowTranslationError(error);
    }

    private void OnEscapePressed(object? sender, EventArgs e)
    {
        _popupWindow?.Close();
    }

    private void Popup_OnClosingRequested(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_popupWindow, sender))
        {
            _translationService?.CancelCurrent();
        }
    }

    private void Popup_OnClosed(object? sender, EventArgs e)
    {
        if (sender is PopupWindow popup)
        {
            popup.ClosingRequested -= Popup_OnClosingRequested;
            popup.ActionRequested -= Popup_OnActionRequested;
            popup.StarredChanged -= Popup_OnStarredChanged;
            popup.SuppressReasoningWarningRequested -= Popup_OnSuppressReasoningWarningRequested;
            popup.Closed -= Popup_OnClosed;
            if (ReferenceEquals(_popupWindow, popup))
            {
                _popupWindow = null;
            }
        }
    }

    private void Popup_OnActionRequested(object? sender, PopupActionEventArgs e)
    {
        try
        {
            switch (e.Action)
            {
                case ErrorAction.Settings:
                    _trayIcon?.OpenTemporaryApiSettings();
                    break;
                case ErrorAction.Retry when sender is PopupWindow retryPopup:
                    _ = StartTranslationAsync(retryPopup.SourceText, retryPopup.AppName);
                    break;
                case ErrorAction.Diagnostics:
                    System.Windows.MessageBox.Show(
                        "请先检查网络和翻译服务配置。完整的一键诊断将在下一里程碑提供；当前可打开日志查看详情。",
                        "一键诊断",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    break;
                case ErrorAction.OpenLog:
                    _trayIcon?.OpenLogDirectory();
                    break;
            }
        }
        catch (Exception exception)
        {
            Logger.Error("浮窗", "执行错误修复操作失败。", exception);
        }
    }

    private async void Popup_OnStarredChanged(object? sender, StarredChangedEventArgs e)
    {
        try
        {
            if (_translationService is not null)
            {
                await _translationService.SetStarredAsync(e.RecordId, e.Starred, CancellationToken.None);
                Logger.Info("收藏", e.Starred ? "翻译已收藏。" : "翻译已取消收藏。");
            }
        }
        catch (Exception exception)
        {
            Logger.Error("收藏", "收藏状态没有保存成功。", exception);
        }
    }

    private void Popup_OnSuppressReasoningWarningRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_settings is null || _settingsStore is null)
            {
                return;
            }

            _settings.SuppressReasoningWarning = true;
            if (!_settingsStore.TrySave(_settings, out string? errorMessage))
            {
                Logger.Warning("设置", errorMessage ?? "保存推理模型提示设置失败。");
            }
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "保存推理模型提示设置失败。", exception);
        }
    }

    private void ReportUnhandledException()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ReportUnhandledException));
            return;
        }

        _trayIcon?.SetWarningState();
        if (Interlocked.Exchange(ref _unhandledExceptionReported, 1) != 0)
        {
            return;
        }

        MessageBoxResult result = System.Windows.MessageBox.Show(
            $"{UnexpectedErrorMessage}\n\n是否打开日志文件夹？",
            "CtrlTranslator",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);
        if (result == MessageBoxResult.Yes)
        {
            _trayIcon?.OpenLogDirectory();
        }
    }

    private int GetClipboardWaitMilliseconds()
    {
        // M3 接入设置界面前，此值由启动时读取的 settings.json 提供。
        return Math.Clamp(_settings?.ClipboardWaitMs ?? 300, 100, 1000);
    }

    private static void TryLogStartupFailure(Exception exception)
    {
        try
        {
            Logger.Error("应用", "程序启动失败。", exception);
        }
        catch (Exception loggingException)
        {
            // 主日志目录不可用时，仍要把异常写入 Windows 临时目录，避免静默丢失。
            string emergencyPath = Path.Combine(Path.GetTempPath(), "CtrlTranslator-emergency.log");
            string safeMessage = Logger.Redact($"启动失败且无法写入主日志：{loggingException}");
            File.AppendAllText(emergencyPath, $"[{DateTime.Now:O}] [错误] [应用] {safeMessage}{Environment.NewLine}");
        }
    }
}
