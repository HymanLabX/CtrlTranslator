using System.Threading;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;
using CtrlTranslator.UI;

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
    private bool _loggerInitialized;

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
            AppSettings settings = settingsStore.LoadOrCreate();
            Logger.SetApiKey(settings.ApiKey);

            _trayIcon = new TrayIconController(settings, settingsStore);
            if (!string.IsNullOrWhiteSpace(settingsStore.LastWarning))
            {
                _trayIcon.ShowError(settingsStore.LastWarning);
            }

            Logger.Info("应用", "CtrlTranslator 已启动，托盘图标已显示。");
        }
        catch (Exception exception)
        {
            TryLogStartupFailure(exception);
            System.Windows.MessageBox.Show(
                "程序启动失败，详情已记录到日志。请检查数据文件夹权限后重试。",
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
        _trayIcon?.ShowError(UnexpectedErrorMessage);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception? exception = e.ExceptionObject as Exception;
        Logger.Error("全局异常", "后台线程发生未处理异常。", exception);
        _trayIcon?.ShowError(UnexpectedErrorMessage);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("全局异常", "后台任务发生未观察异常。", e.Exception);
        _trayIcon?.ShowError(UnexpectedErrorMessage);
        e.SetObserved();
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
