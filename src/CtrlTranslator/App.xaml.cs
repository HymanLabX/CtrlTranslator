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
using CtrlTranslator.Speech;
using CtrlTranslator.UI;
using CtrlTranslator.Translation;
using CtrlTranslator.Update;

namespace CtrlTranslator;

/// <summary>
/// 应用入口：负责单实例、基础设施初始化和全局异常兜底。
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\CtrlTranslator.SingleInstance";
    private const string ActivationEventName = "Local\\CtrlTranslator.Activate";

    /// <summary>
    /// 用户点一下就该立刻见效的数据库操作（收藏等）的等待上限。
    /// 超过这个时间说明库被别的进程锁住了，与其无限转下去，不如告诉用户稍后再试。
    /// </summary>
    private static readonly TimeSpan DatabaseInteractionTimeout = TimeSpan.FromSeconds(5);
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
    private HistoryRepository? _historyRepository;
    private PopupWindow? _popupWindow;
    private SettingsWindow? _settingsWindow;
    private MainWindow? _mainWindow;
    private OnboardingWindow? _onboardingWindow;
    private UpdateWindow? _updateWindow;
    private UpdateService? _updateService;
    private SpeechService? _speechService;
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

            // 剪贴板需要一个所有者窗口才能写入，必须在界面线程上建好。
            ClipboardService.Initialize();

            SettingsStore settingsStore = new();
            _settingsStore = settingsStore;
            bool isFirstRun = !File.Exists(settingsStore.SettingsPath);
            AppSettings settings = settingsStore.LoadOrCreate();
            _settings = settings;
            Logger.SetApiKeys(settings.Profiles.Select(profile => profile.ApiKey));

            // 朗读服务是全局单例，构造时会枚举 SAPI 语音；无语音时它自己降级，不影响启动。
            _speechService = new SpeechService();

            _trayIcon = new TrayIconController(settings, settingsStore);
            _trayIcon.SettingsRequested += TrayIcon_OnSettingsRequested;
            _trayIcon.MainWindowRequested += TrayIcon_OnMainWindowRequested;
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

            _historyRepository = new HistoryRepository(database);
            _translationService = new TranslationService(
                () => _settings ?? settings,
                _historyRepository,
                OnProfileMarkedFailed);

            _appState = new AppState(settings);
            _textCapture = new TextCapture(Dispatcher);
            InstallKeyboardHook(settings);

            _updateService = new UpdateService(
                () => _settings ?? settings,
                Dispatcher,
                ShowUpdateWindow);
            _updateService.Start();

            Logger.Info("应用", "CtrlTranslator 已启动，托盘图标已显示。");
            if (isFirstRun)
            {
                ShowOnboardingWindow();
            }
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
            _mainWindow?.Close();
            _onboardingWindow?.Close();
            _updateWindow?.Close();
            _updateService?.Dispose();
            _translationService?.Dispose();
            DisposeKeyboardHook();
            if (HookThread.IsCreated)
            {
                HookThread.Instance.Dispose();
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
            if (_trayIcon is not null)
            {
                _trayIcon.SettingsRequested -= TrayIcon_OnSettingsRequested;
                _trayIcon.MainWindowRequested -= TrayIcon_OnMainWindowRequested;
            }

            _trayIcon?.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Error("应用", "移除托盘图标失败。", exception);
        }

        try
        {
            _speechService?.Dispose();
            _speechService = null;
        }
        catch (Exception exception)
        {
            Logger.Error("应用", "释放朗读服务失败。", exception);
        }

        try
        {
            ClipboardService.Shutdown();
        }
        catch (Exception exception)
        {
            Logger.Error("应用", "释放剪贴板所有者窗口失败。", exception);
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
                ShowMainWindow();
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
                Logger.Error("取词", "用户触发划词，但取词服务尚未初始化。");
                return;
            }

            // SPEC §2.2 的四项前置检查必须保持此顺序。
            if (!appState.Enabled)
            {
                Logger.Info("取词", "用户触发划词，但当前处于暂停状态，已忽略。");
                return;
            }

            ForegroundApp? foregroundApp = ForegroundApp.GetCurrent();
            if (!appState.TryEnterDebounce(Stopwatch.GetTimestamp()))
            {
                Logger.Info("取词", "用户触发划词，但命中了防抖间隔，已忽略。");
                return;
            }

            if (foregroundApp?.ProcessId == Environment.ProcessId)
            {
                Logger.Info("取词", "用户在 CtrlTranslator 自身窗口内触发划词，已忽略。");
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

            if (text.Length > AppSettings.MaxTranslatableChars)
            {
                PopupWindow popup = CreatePopup(text, foregroundApp?.ProcessName);
                popup.ShowTranslationError(new UserFacingError(
                    $"选中内容过长（{text.Length} 字符），请分段翻译。"
                    + $"单次上限 {AppSettings.MaxTranslatableChars} 字符。",
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
            ShowCaptureError(ErrorMessages.FromException(
                exception,
                _settings?.CurrentProfile.Model ?? string.Empty,
                IsThinkingDisabled()));
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

    private async Task StartTranslationAsync(string text, string? appName, bool bypassCache = false)
    {
        TranslationService? service = _translationService;
        if (service is null)
        {
            Logger.Error("翻译", "用户已触发翻译，但翻译服务尚未初始化。");
            System.Windows.MessageBox.Show(
                "翻译服务还没有准备好，请退出程序后重新打开再试。",
                "CtrlTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
                CancellationToken.None,
                bypassCache);
            if (ReferenceEquals(_popupWindow, popup))
            {
                popup.ShowResult(result);
            }

            // 命中缓存不产生新记录（历史按翻译发生的时间排，顺序不变），只有新译文才需要进列表。
            if (_mainWindow is not null && !result.FromCache && result.RecordId is long recordId)
            {
                try
                {
                    await _mainWindow.AddRecordAsync(recordId);
                }
                catch (Exception exception)
                {
                    Logger.Error("主窗口", "翻译完成后刷新历史列表失败。", exception);
                }
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
                    _settings?.CurrentProfile.Model ?? string.Empty,
                    IsThinkingDisabled()));
            }
        }
    }

    /// <summary>
    /// 浮窗上「重试」按钮的入口。点击事件是同步的，这里必须自己兜住所有异常：
    /// <see cref="StartTranslationAsync"/> 开头新建浮窗那几步不在它自己的 try 里，
    /// 一旦在那儿出错，没人等这个 Task，异常就成了"未观察的任务异常"，只能等垃圾回收时才冒出来。
    /// </summary>
    private async Task RetryTranslationAsync(string text, string appName)
    {
        try
        {
            await StartTranslationAsync(text, appName);
        }
        catch (Exception exception)
        {
            Logger.Error("翻译", "重试翻译失败。", exception);
            ShowCaptureError(new UserFacingError(
                "重试没有完成，详情已记录到日志。请重新选中文字再试一次。",
                ErrorAction.None,
                null));
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

        var popup = new PopupWindow(text, appName, _settings ?? new AppSettings(), _speechService);
        popup.ClosingRequested += Popup_OnClosingRequested;
        popup.ActionRequested += Popup_OnActionRequested;
        popup.StarredChanged += Popup_OnStarredChanged;
        popup.SuppressReasoningWarningRequested += Popup_OnSuppressReasoningWarningRequested;
        popup.DisableThinkingRequested += Popup_OnDisableThinkingRequested;
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
            popup.DisableThinkingRequested -= Popup_OnDisableThinkingRequested;
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
                    ShowSettingsWindow(focusExtraRequestBody: false);
                    break;
                case ErrorAction.Retry when sender is PopupWindow retryPopup:
                    _ = RetryTranslationAsync(retryPopup.SourceText, retryPopup.AppName);
                    break;
                case ErrorAction.Diagnostics:
                    ShowProfileTest();
                    break;
                case ErrorAction.OpenLog:
                    _trayIcon?.OpenLogDirectory();
                    break;
                case ErrorAction.DisableThinking when sender is PopupWindow thinkingPopup:
                    Popup_OnDisableThinkingRequested(thinkingPopup, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception exception)
        {
            Logger.Error("浮窗", "执行错误修复操作失败。", exception);
        }
    }

    /// <summary>
    /// 深度翻译页出错时的修复按钮。和浮窗的修复按钮（<see cref="Popup_OnActionRequested"/>）同一套去处；
    /// 「重试」页面自己处理，「关闭思考模式」在这里改成带用户去高级设置里的额外参数框 ——
    /// 深度翻译不像浮窗那样有一个现成的"关了马上重翻"的流程。
    /// </summary>
    private void DeepTranslation_OnActionRequested(object? sender, PopupActionEventArgs e)
    {
        try
        {
            switch (e.Action)
            {
                case ErrorAction.Settings:
                    ShowSettingsWindow(focusExtraRequestBody: false);
                    break;
                case ErrorAction.DisableThinking:
                    ShowSettingsWindow(focusExtraRequestBody: true);
                    break;
                case ErrorAction.Diagnostics:
                    ShowProfileTest();
                    break;
                case ErrorAction.OpenLog:
                    _trayIcon?.OpenLogDirectory();
                    break;
                default:
                    Logger.Warning("深度翻译", $"出错提示上的操作 {e.Action} 没有对应的处理。");
                    break;
            }
        }
        catch (Exception exception)
        {
            Logger.Error("深度翻译", "执行错误修复操作失败。", exception);
        }
    }

    private async void Popup_OnStarredChanged(object? sender, StarredChangedEventArgs e)
    {
        try
        {
            if (_translationService is not null)
            {
                // 库文件被同步盘/杀毒软件/DB 工具占住排他锁时，不给超时就是无限等待，
                // 而这里没有任何进度反馈，用户只会觉得点了没反应。
                using var timeout = new CancellationTokenSource(DatabaseInteractionTimeout);
                await _translationService.SetStarredAsync(e.RecordId, e.Starred, timeout.Token);
                Logger.Info("收藏", e.Starred ? "翻译已收藏。" : "翻译已取消收藏。");
                if (_mainWindow is not null)
                {
                    await _mainWindow.UpdateStarredAsync(e.RecordId, e.Starred);
                }
            }
            else
            {
                Logger.Error("收藏", "用户点击收藏，但翻译服务尚未初始化。");
                System.Windows.MessageBox.Show(
                    "收藏服务还没有准备好，请退出程序后重新打开再试。",
                    "CtrlTranslator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception exception)
        {
            // 只写日志是不够的：图标已经点亮了，用户会以为收藏成功。必须把它改回去。
            Logger.Error("收藏", "收藏状态没有保存成功。", exception);
            if (sender is PopupWindow popup)
            {
                popup.RevertStar(e.Starred);
            }
        }
    }

    private void Popup_OnSuppressReasoningWarningRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_settings is null || _settingsStore is null)
            {
                Logger.Error("设置", "用户点击不再提示，但设置服务尚未初始化。");
                System.Windows.MessageBox.Show(
                    "设置服务还没有准备好，请退出程序后重新打开再试。",
                    "CtrlTranslator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _settings.SuppressReasoningWarning = true;
            if (!_settingsStore.TrySave(_settings, out string? errorMessage))
            {
                _settings.SuppressReasoningWarning = false;
                Logger.Warning("设置", errorMessage ?? "保存推理模型提示设置失败。");
                System.Windows.MessageBox.Show(
                    errorMessage ?? "设置保存失败，请稍后重试。",
                    "CtrlTranslator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "保存推理模型提示设置失败。", exception);
        }
    }

    private async void Popup_OnDisableThinkingRequested(object? sender, EventArgs e)
    {
        try
        {
            if (sender is not PopupWindow popup || _settings is null || _settingsStore is null)
            {
                Logger.Error("设置", "用户点击关闭思考模式，但浮窗或设置服务尚未初始化。");
                if (sender is PopupWindow unavailablePopup)
                {
                    unavailablePopup.RestoreDisableThinkingButton();
                }

                System.Windows.MessageBox.Show(
                    "设置服务还没有准备好，请退出程序后重新打开再试。",
                    "CtrlTranslator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            // 改的是这次真正出结果（或出错）的那个档案：自动降级之后它不一定是当前档案。
            ApiProfile profile = _settings.FindProfile(popup.ProfileId) ?? _settings.CurrentProfile;
            string previousExtraRequestBody = profile.ExtraRequestBody;
            profile.ExtraRequestBody = ExtraRequestBodyHelper.SetThinkingDisabled(
                previousExtraRequestBody,
                disabled: true,
                profile.BaseUrl);
            if (!_settingsStore.TrySave(_settings, out string? errorMessage))
            {
                profile.ExtraRequestBody = previousExtraRequestBody;
                System.Windows.MessageBox.Show(
                    errorMessage ?? "关闭思考模式的设置没有保存成功，请稍后再试。",
                    "CtrlTranslator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                popup.RestoreDisableThinkingButton();
                return;
            }

            Logger.Info("设置", "已关闭模型思考模式，正在用新参数重新翻译。");
            await StartTranslationAsync(popup.SourceText, popup.AppName, bypassCache: true);
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "关闭思考模式并重新翻译失败。", exception);
            if (sender is PopupWindow popup && popup.IsVisible)
            {
                popup.RestoreDisableThinkingButton();
            }

            System.Windows.MessageBox.Show(
                "关闭思考模式没有完成，详情已记录到日志。请稍后再试。",
                "CtrlTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

    private void TrayIcon_OnMainWindowRequested(object? sender, EventArgs e) => ShowMainWindow();

    private void ShowMainWindow()
    {
        try
        {
            MainWindow window = GetOrCreateMainWindow();
            window.ShowHistory();
            ActivateMainWindow(window);
        }
        catch (Exception exception)
        {
            Logger.Error("主窗口", "打开主窗口失败。", exception);
            System.Windows.MessageBox.Show("主窗口没有打开成功，详情已写入日志。", "CtrlTranslator",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Closed -= MainWindow_OnClosed;
            if (ReferenceEquals(_mainWindow, window))
            {
                _mainWindow = null;
                _settingsWindow = null;
            }
        }
    }

    /// <summary>
    /// 造一个针对某个档案的诊断器。M5 之后诊断没有独立窗口了，由设置页的「测试」按钮
    /// 和引导向导各自就地展示结果。
    /// </summary>
    private DiagnosticRunner CreateDiagnosticRunner(ApiProfile profile)
    {
        AppSettings settings = _settings
            ?? throw new InvalidOperationException("设置尚未初始化。");
        HistoryRepository repository = _historyRepository
            ?? throw new InvalidOperationException("历史服务尚未初始化。");
        return new DiagnosticRunner(
            profile,
            settings,
            repository,
            () => _keyboardHook?.IsInstalled == true);
    }

    /// <summary>打开设置窗口的「服务商档案」页并立刻测一遍当前档案。</summary>
    private void ShowProfileTest()
    {
        ShowSettingsWindow(focusExtraRequestBody: false);
        _settingsWindow?.ShowProfileTest();
    }

    /// <summary>
    /// 某个档案因持久故障（Key 失效 / 余额不足 / 模型不存在）被自动标记失效。
    /// 必须落盘 + 提醒，否则用户会一直不知道自己的主服务商已经废了。
    /// </summary>
    private void OnProfileMarkedFailed(ApiProfile profile, string reason)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnProfileMarkedFailed(profile, reason)));
            return;
        }

        try
        {
            if (_settings is not null
                && _settingsStore is not null
                && !_settingsStore.TrySave(_settings, out string? errorMessage))
            {
                Logger.Warning("设置", errorMessage ?? "保存档案失效标记失败。");
            }

            _trayIcon?.RefreshFromSettings();
            _trayIcon?.ShowInformation(
                "CtrlTranslator",
                $"「{profile.Name}」{reason}，已自动改用备用服务商。到设置里修好后会自动恢复。");
        }
        catch (Exception exception)
        {
            Logger.Error("翻译", "处理档案失效标记失败。", exception);
        }
    }

    private void ShowOnboardingWindow()
    {
        try
        {
            if (_settings is null || _settingsStore is null)
            {
                throw new InvalidOperationException("首次设置服务尚未初始化。");
            }

            if (_onboardingWindow is null)
            {
                _onboardingWindow = new OnboardingWindow(
                    _settings,
                    _settingsStore,
                    () => OnSettingsChanged(SettingsChangeKind.General),
                    CreateDiagnosticRunner);
                _onboardingWindow.Closed += OnboardingWindow_OnClosed;
            }

            _onboardingWindow.Show();
            _onboardingWindow.Activate();
        }
        catch (Exception exception)
        {
            Logger.Error("首次向导", "打开首次设置向导失败。", exception);
            System.Windows.MessageBox.Show("首次设置向导没有打开成功，请从托盘菜单打开“设置”。", "CtrlTranslator",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnboardingWindow_OnClosed(object? sender, EventArgs e)
    {
        if (sender is OnboardingWindow window)
        {
            window.Closed -= OnboardingWindow_OnClosed;
            if (ReferenceEquals(_onboardingWindow, window))
            {
                _onboardingWindow = null;
            }
        }
    }

    private void TrayIcon_OnSettingsRequested(object? sender, EventArgs e)
    {
        ShowSettingsWindow(focusExtraRequestBody: false);
    }

    /// <summary>
    /// 弹出更新窗口。同一时刻只允许存在一个，重复触发时把已有的那个激活到前面。
    /// </summary>
    internal void ShowUpdateWindow(ReleaseInfo release)
    {
        try
        {
            if (_updateWindow is not null)
            {
                _updateWindow.Activate();
                return;
            }

            _updateWindow = new UpdateWindow(
                release,
                InstallationInfo.Detect(),
                ReleaseSingleInstanceMutex,
                () => DismissUpdate(release));
            _updateWindow.Closed += UpdateWindow_OnClosed;
            _updateWindow.Show();
            _updateWindow.Activate();
        }
        catch (Exception exception)
        {
            Logger.Error("自动更新", "打开更新窗口失败。", exception);
            _trayIcon?.ShowError("更新提示窗口没有打开成功，详情已记录到日志。");
        }
    }

    private void UpdateWindow_OnClosed(object? sender, EventArgs e)
    {
        if (sender is UpdateWindow window)
        {
            window.Closed -= UpdateWindow_OnClosed;
            if (ReferenceEquals(_updateWindow, window))
            {
                _updateWindow = null;
            }
        }
    }

    private void DismissUpdate(ReleaseInfo release)
    {
        if (_settings is null || _settingsStore is null)
        {
            return;
        }

        _settings.DismissedUpdateVersion = release.Version.ToString();
        if (!_settingsStore.TrySave(_settings, out string? errorMessage))
        {
            Logger.Warning("自动更新", errorMessage ?? "记录已跳过版本失败。");
            return;
        }

        Logger.Info("自动更新", $"用户选择稍后更新 {release.Tag}，本版本不再自动弹窗。");
    }

    /// <summary>
    /// 在启动安装程序之前放开单实例互斥体。
    /// </summary>
    /// <remarks>
    /// 安装脚本的 InitializeSetup 会等这个互斥体消失才继续（最多 10 秒）。
    /// 本进程紧接着就 Shutdown，OnExit 里不会重复释放 —— <see cref="_ownsMutex"/> 已被置回 false。
    /// </remarks>
    private void ReleaseSingleInstanceMutex()
    {
        try
        {
            if (_ownsMutex)
            {
                _instanceMutex?.ReleaseMutex();
                _ownsMutex = false;
            }

            _instanceMutex?.Dispose();
            _instanceMutex = null;
            Logger.Info("自动更新", "已释放单实例互斥体，安装程序可以继续。");
        }
        catch (Exception exception)
        {
            Logger.Error("自动更新", "释放单实例互斥体失败，安装程序可能会等待超时。", exception);
        }
    }

    private void ShowSettingsWindow(bool focusExtraRequestBody)
    {
        try
        {
            MainWindow window = GetOrCreateMainWindow();
            window.ShowSettings(focusExtraRequestBody);
            ActivateMainWindow(window);
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "在主窗口中打开设置失败。", exception);
            System.Windows.MessageBox.Show(
                "设置页面没有打开成功，详情已记录到日志。请稍后再试。",
                "CtrlTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private MainWindow GetOrCreateMainWindow()
    {
        if (_mainWindow is not null)
        {
            return _mainWindow;
        }

        if (_settings is null
            || _settingsStore is null
            || _historyRepository is null
            || _translationService is null)
        {
            throw new InvalidOperationException("设置、历史或翻译服务尚未初始化。");
        }

        _settingsWindow = new SettingsWindow(
            _settings,
            _settingsStore,
            _historyRepository,
            OnSettingsChanged,
            CreateDiagnosticRunner,
            _updateService,
            ShowUpdateWindow);
        var deepTranslationView = new DeepTranslationView(_settings, _translationService, _speechService);
        deepTranslationView.ActionRequested += DeepTranslation_OnActionRequested;
        _mainWindow = new MainWindow(
            _settings, _historyRepository, _settingsWindow, deepTranslationView);
        _mainWindow.Closed += MainWindow_OnClosed;
        return _mainWindow;
    }

    private static void ActivateMainWindow(MainWindow window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }

    private void OnSettingsChanged(SettingsChangeKind changeKind)
    {
        _trayIcon?.RefreshFromSettings();
        if (changeKind == SettingsChangeKind.KeyboardHook)
        {
            ReinstallKeyboardHook();
        }
    }

    private void InstallKeyboardHook(AppSettings settings)
    {
        DoubleTapDetector detector = new(
            settings.TriggerKey,
            settings.DoubleTapIntervalMs,
            settings.MaxKeyHoldMs);
        var keyboardHook = new LowLevelKeyboardHook(Dispatcher, detector);
        keyboardHook.DoubleTapped += OnDoubleTapped;
        keyboardHook.EscapePressed += OnEscapePressed;
        try
        {
            keyboardHook.Install();
            _keyboardHook = keyboardHook;
        }
        catch
        {
            keyboardHook.DoubleTapped -= OnDoubleTapped;
            keyboardHook.EscapePressed -= OnEscapePressed;
            keyboardHook.Dispose();
            throw;
        }
    }

    private void DisposeKeyboardHook()
    {
        if (_keyboardHook is null)
        {
            return;
        }

        LowLevelKeyboardHook keyboardHook = _keyboardHook;
        _keyboardHook = null;
        keyboardHook.DoubleTapped -= OnDoubleTapped;
        keyboardHook.EscapePressed -= OnEscapePressed;
        keyboardHook.Dispose();
    }

    private void ReinstallKeyboardHook()
    {
        try
        {
            AppSettings settings = _settings
                ?? throw new InvalidOperationException("设置尚未初始化。");
            DisposeKeyboardHook();
            InstallKeyboardHook(settings);
            Logger.Info(
                "键盘钩子",
                $"触发设置已即时生效：{settings.TriggerKey}，双击间隔 {settings.DoubleTapIntervalMs}ms。" );
        }
        catch (Exception exception)
        {
            Logger.Error("键盘钩子", "应用新触发设置时重装键盘钩子失败。", exception);
            System.Windows.MessageBox.Show(
                "新的触发设置已保存，但按键监听没有重新启动成功。请退出 CtrlTranslator 后重新打开；如果仍失败，请检查安全软件是否拦截。",
                "CtrlTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private bool IsThinkingDisabled()
    {
        return ExtraRequestBodyHelper.IsThinkingDisabled(_settings?.CurrentProfile.ExtraRequestBody);
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
