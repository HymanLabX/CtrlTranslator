using System.Windows.Threading;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;

namespace CtrlTranslator.Update;

/// <summary>
/// 决定"什么时候查、查到了要不要打扰用户"。查本身交给 <see cref="UpdateChecker"/>。
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>启动后不立刻查：先把托盘和键盘钩子这些正事办完，别让一个网络请求拖慢启动。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly Func<AppSettings> _settingsProvider;
    private readonly Action<ReleaseInfo> _onUpdateAvailable;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cancellation = new();
    private System.Threading.Timer? _timer;
    private int _checking;
    private bool _disposed;

    public UpdateService(
        Func<AppSettings> settingsProvider,
        Dispatcher dispatcher,
        Action<ReleaseInfo> onUpdateAvailable)
    {
        _settingsProvider = settingsProvider;
        _dispatcher = dispatcher;
        _onUpdateAvailable = onUpdateAvailable;
    }

    /// <summary>最近一次检查到的新版本；设置页用它显示"有新版本可用"。</summary>
    public ReleaseInfo? PendingRelease { get; private set; }

    public void Start()
    {
        _timer = new System.Threading.Timer(
            static state => ((UpdateService)state!).RunScheduledCheck(),
            this,
            StartupDelay,
            Interval);
    }

    /// <summary>
    /// 用户主动点「检查更新」。跟定时检查的区别：不看"是否已跳过这个版本"，
    /// 也会把"已经是最新版"这个结果如实返回给调用方去显示。
    /// </summary>
    public async Task<ReleaseInfo?> CheckManuallyAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cancellation.Token);
        ReleaseInfo? release = await UpdateChecker.CheckAsync(linked.Token);
        PendingRelease = release;
        return release;
    }

    private void RunScheduledCheck()
    {
        // 上一次还没跑完就别叠加了（24 小时间隔理论上不会撞，但手动检查可能刚好并发）。
        if (Interlocked.Exchange(ref _checking, 1) != 0)
        {
            return;
        }

        _ = CheckInBackgroundAsync();
    }

    private async Task CheckInBackgroundAsync()
    {
        try
        {
            AppSettings settings = _settingsProvider();
            if (!settings.AutoCheckUpdates)
            {
                return;
            }

            ReleaseInfo? release = await UpdateChecker.CheckAsync(_cancellation.Token);
            if (release is null)
            {
                return;
            }

            PendingRelease = release;

            // 用户对这个版本点过「稍后」就不再自动弹了。设置页还是会显示，不会真的丢掉。
            if (string.Equals(
                    settings.DismissedUpdateVersion,
                    release.Version.ToString(),
                    StringComparison.Ordinal))
            {
                Logger.Info("自动更新", $"{release.Tag} 用户已选择稍后，本次不再弹窗。");
                return;
            }

            _ = _dispatcher.BeginInvoke(new Action(() => _onUpdateAvailable(release)));
        }
        catch (OperationCanceledException)
        {
            Logger.Info("自动更新", "定时检查已取消。");
        }
        catch (Exception exception)
        {
            Logger.Error("自动更新", "定时检查更新失败。", exception);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _timer?.Dispose();
        _timer = null;
        _cancellation.Dispose();
    }
}
