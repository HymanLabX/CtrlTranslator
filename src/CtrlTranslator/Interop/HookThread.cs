using System.Windows.Threading;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Interop;

/// <summary>
/// 专门承载低级键盘 / 鼠标钩子的线程。这条线程上除了钩子回调什么都不跑。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不放在界面线程上</b>（负责人 2026-10 同意调整 SPEC §2.1）：
/// 低级钩子的回调由安装它的那个线程的消息循环来执行。系统给每次回调的时间有上限
/// （注册表 LowLevelHooksTimeout），Windows 7 起超时一次就会<b>悄悄卸掉这个钩子</b>，
/// 程序完全无从得知 —— 表现就是双击 Ctrl 突然没反应了，直到重启软件。
/// 界面线程偶尔会被卡住（第一次打开主窗口要解析上千行界面描述、剪贴板被别的程序占着、
/// 磁盘慢的时候写日志），只要卡顿时用户正好在打字或动鼠标，钩子就可能被系统撤掉。
/// </para>
/// <para>
/// 独立线程上只有钩子回调这一件事，回调本身又只是比较几个数字，永远不会超时。
/// SPEC 当初要求"装在主线程"的理由是"主线程有消息循环"，这条线程同样有自己的消息循环。
/// </para>
/// </remarks>
internal sealed class HookThread : IDisposable
{
    private static readonly Lazy<HookThread> SharedInstance = new(() => new HookThread(), isThreadSafe: true);

    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    private HookThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "CtrlTranslator 按键监听",
            // 回调响应越及时，系统输入越不会被它拖慢；这条线程几乎不占 CPU，调高优先级没有代价。
            Priority = ThreadPriority.AboveNormal
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
        Logger.Info("键盘钩子", "按键监听线程已启动。");
    }

    /// <summary>全程序共用的那一条钩子线程，第一次用到时才创建。</summary>
    public static HookThread Instance => SharedInstance.Value;

    /// <summary>钩子线程是否已经创建过（退出时据此决定要不要关它）。</summary>
    public static bool IsCreated => SharedInstance.IsValueCreated;

    /// <summary>在钩子线程上同步执行（安装 / 卸载钩子必须在钩子线程上做）。</summary>
    public void Invoke(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (_dispatcher.HasShutdownStarted)
        {
            // 程序正在退出、钩子线程已经停了：直接在当前线程做。
            // 卸载钩子从别的线程调用也是允许的，而线程结束时系统本来就会收回它装的钩子。
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    public T Invoke<T>(Func<T> function)
    {
        if (_dispatcher.CheckAccess() || _dispatcher.HasShutdownStarted)
        {
            return function();
        }

        return _dispatcher.Invoke(function);
    }

    public void Dispose()
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        _dispatcher.InvokeShutdown();
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            Logger.Warning("键盘钩子", "按键监听线程没有在 2 秒内退出，程序退出时由系统回收。");
        }
    }
}
