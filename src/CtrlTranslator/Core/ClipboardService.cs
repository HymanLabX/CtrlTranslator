using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Interop;

namespace CtrlTranslator.Core;

/// <summary>
/// 一次剪贴板快照：当时剪贴板上能安全复制的每一种格式和它的原始字节。
/// </summary>
public sealed class ClipboardSnapshot
{
    internal ClipboardSnapshot(IReadOnlyList<ClipboardItem> items, int skippedFormats, long totalBytes)
    {
        Items = items;
        SkippedFormats = skippedFormats;
        TotalBytes = totalBytes;
    }

    internal IReadOnlyList<ClipboardItem> Items { get; }

    /// <summary>没能保存下来的格式数（图元文件这类句柄格式，或超出体积上限的）。</summary>
    public int SkippedFormats { get; }

    public long TotalBytes { get; }

    public bool IsEmpty => Items.Count == 0;

    internal readonly record struct ClipboardItem(uint Format, byte[] Data);
}

/// <summary>
/// 剪贴板的全部读写都走这里：取词前的快照与还原、读复制结果、各处「复制」按钮。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么直接调 Win32 而不用 WPF / WinForms 的 Clipboard 类</b>：
/// WPF 的 <c>Clipboard.GetDataObject</c> 底层是 OLE 剪贴板，剪贴板被别的程序占着时，
/// 它内部会 <c>Thread.Sleep(100)</c> 重试 10 次 —— 在界面线程上一卡就是一秒，
/// 再套上我们自己的 5 次重试，最坏能卡死五秒。<c>Clipboard.SetText</c> 更是 SPEC §3.2
/// 点名不许用的（会随机抛 COM 异常）。这里统一按 AGENTS.md §5.3 的规矩：
/// 打开失败重试 5 次、每次间隔 20ms，全部失败才报错。
/// </para>
/// <para>
/// <b>完整还原</b>（负责人 2026-10 决定）：以前只能备份还原纯文字，
/// 实际日志里 17 天约 70 次划词就有 6 次把用户剪贴板里的截图或文件清掉了。
/// 现在把剪贴板上所有"内存块"类型的格式（文字、带格式文字、HTML、图片、文件列表……）
/// 原样复制下来，取完词再原样放回去。图元文件、位图句柄这类 GDI 句柄格式没法安全复制，跳过 ——
/// 位图会被系统自动转成 DIB 格式一并保存，所以截图照样能还原。
/// </para>
/// <para>
/// 所有操作都在线程池里同步执行（打开、读写、关闭必须在同一个线程上完成），
/// 不占用界面线程：剪贴板里东西很大（Excel 复制了几万格）时，复制那一下可能要几百毫秒，
/// 界面不该跟着卡。
/// </para>
/// </remarks>
public static class ClipboardService
{
    private const int RetryCount = 5;
    private const int RetryDelayMs = 20;

    /// <summary>快照总体积上限。4K 截图（各种图片格式加起来）约 70MB，留足余量。</summary>
    private const long MaxSnapshotBytes = 128L * 1024 * 1024;

    /// <summary>快照最多花这么久。超过后不再读后面的格式（读到的部分照样还原）。</summary>
    private static readonly TimeSpan SnapshotTimeBudget = TimeSpan.FromSeconds(2);

    private static HwndSource? _ownerWindow;

    /// <summary>所有者窗口的句柄。在界面线程上取好存下来，线程池里只用这个数值。</summary>
    private static nint _ownerHandle;

    /// <summary>
    /// 建一个消息专用的隐藏窗口当剪贴板"所有者"。必须在界面线程上调用一次。
    /// </summary>
    /// <remarks>
    /// 往剪贴板写东西必须有一个所有者窗口：传空句柄打开剪贴板的话，
    /// 清空之后所有者变成空，接下来的 SetClipboardData 会直接失败（Win32 文档明确写了）。
    /// </remarks>
    public static void Initialize()
    {
        if (_ownerWindow is not null)
        {
            return;
        }

        _ownerWindow = new HwndSource(new HwndSourceParameters("CtrlTranslator.ClipboardOwner")
        {
            ParentWindow = NativeMethods.HwndMessage,
            Width = 0,
            Height = 0,
            WindowStyle = 0
        });
        Volatile.Write(ref _ownerHandle, _ownerWindow.Handle);
    }

    public static void Shutdown()
    {
        Volatile.Write(ref _ownerHandle, nint.Zero);
        _ownerWindow?.Dispose();
        _ownerWindow = null;
    }

    /// <summary>剪贴板上现在有没有文字。不需要打开剪贴板，随时可以问。</summary>
    public static bool HasText() => NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfUnicodeText);

    public static Task<ClipboardSnapshot> SnapshotAsync() => Task.Run(Snapshot);

    public static Task RestoreAsync(ClipboardSnapshot snapshot) => Task.Run(() => Restore(snapshot));

    public static Task<string?> ReadTextAsync() => Task.Run(ReadText);

    /// <summary>把一段文字放进剪贴板（各处「复制」按钮用）。重试仍失败时抛 <see cref="ClipboardUnavailableException"/>。</summary>
    public static Task SetTextAsync(string text) => Task.Run(() => WriteText(text));

    private static ClipboardSnapshot Snapshot()
    {
        List<ClipboardSnapshot.ClipboardItem> items = [];
        int skipped = 0;
        long total = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        using (OpenWithRetry("备份剪贴板"))
        {
            uint format = 0;
            while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
            {
                if (stopwatch.Elapsed > SnapshotTimeBudget)
                {
                    skipped++;
                    continue;
                }

                if (IsHandleFormat(format))
                {
                    skipped++;
                    continue;
                }

                // 取数据时，"延迟渲染"的格式会让原程序现场生成内容，这一步可能比较慢。
                nint handle = NativeMethods.GetClipboardData(format);
                if (handle == nint.Zero)
                {
                    continue;
                }

                byte[]? data = CopyGlobal(handle, MaxSnapshotBytes - total);
                if (data is null)
                {
                    skipped++;
                    continue;
                }

                items.Add(new ClipboardSnapshot.ClipboardItem(format, data));
                total += data.Length;
            }
        }

        if (skipped > 0)
        {
            Logger.Warning(
                "剪贴板",
                $"原剪贴板有 {skipped} 种格式没能备份（图元文件等句柄格式、或体积超过 {MaxSnapshotBytes / 1024 / 1024}MB），"
                + $"已备份 {items.Count} 种共 {total / 1024}KB。");
        }

        return new ClipboardSnapshot(items, skipped, total);
    }

    private static void Restore(ClipboardSnapshot snapshot)
    {
        using (OpenWithRetry("还原剪贴板"))
        {
            if (!NativeMethods.EmptyClipboard())
            {
                throw new ClipboardUnavailableException(
                    "剪贴板正被其他程序占用，稍等一下再试。",
                    new Win32Exception(Marshal.GetLastWin32Error(), "清空剪贴板失败。"));
            }

            int failed = 0;
            foreach (ClipboardSnapshot.ClipboardItem item in snapshot.Items)
            {
                if (!TrySetData(item.Format, item.Data))
                {
                    failed++;
                }
            }

            if (failed > 0)
            {
                Logger.Warning("剪贴板", $"还原剪贴板时有 {failed} 种格式没能写回。");
            }
        }
    }

    private static string? ReadText()
    {
        using (OpenWithRetry("读取复制结果"))
        {
            nint handle = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
            if (handle == nint.Zero)
            {
                return null;
            }

            byte[]? data = CopyGlobal(handle, MaxSnapshotBytes);
            if (data is null)
            {
                return null;
            }

            // 按 UTF-16 解码，截到第一个结束符为止（内存块常常比字符串本身长一截）。
            string text = System.Text.Encoding.Unicode.GetString(data, 0, data.Length - (data.Length % 2));
            int terminator = text.IndexOf('\0');
            return terminator >= 0 ? text[..terminator] : text;
        }
    }

    private static void WriteText(string text)
    {
        byte[] data = System.Text.Encoding.Unicode.GetBytes(text + '\0');
        using (OpenWithRetry("写入剪贴板"))
        {
            if (!NativeMethods.EmptyClipboard() || !TrySetData(NativeMethods.CfUnicodeText, data))
            {
                throw new ClipboardUnavailableException(
                    "剪贴板正被其他程序占用，稍等一下再试。",
                    new Win32Exception(Marshal.GetLastWin32Error(), "写入剪贴板失败。"));
            }
        }
    }

    /// <summary>
    /// 打开剪贴板，失败重试 5 次、间隔 20ms（AGENTS.md §5.3）。返回的对象释放时关闭剪贴板。
    /// </summary>
    private static ClipboardLease OpenWithRetry(string operationName)
    {
        nint owner = Volatile.Read(ref _ownerHandle);
        int lastError = 0;
        for (int attempt = 1; attempt <= RetryCount; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner))
            {
                return new ClipboardLease();
            }

            lastError = Marshal.GetLastWin32Error();
            if (attempt < RetryCount)
            {
                // 这里跑在线程池上，睡一下不会卡住界面。
                Thread.Sleep(RetryDelayMs);
            }
        }

        Logger.Warning("剪贴板", $"{operationName}：剪贴板连续 {RetryCount} 次打不开（Windows 错误码 {lastError}）。");
        throw new ClipboardUnavailableException(
            "剪贴板正被其他程序占用，稍等一下再试。",
            new Win32Exception(lastError, $"{operationName}时打不开剪贴板。"));
    }

    /// <summary>把一块全局内存的内容复制出来；超过 <paramref name="maxBytes"/> 或读不了时返回 null。</summary>
    private static byte[]? CopyGlobal(nint handle, long maxBytes)
    {
        long size = (long)NativeMethods.GlobalSize(handle);
        if (size <= 0 || size > maxBytes || size > int.MaxValue)
        {
            return null;
        }

        nint pointer = NativeMethods.GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            return null;
        }

        try
        {
            byte[] data = new byte[size];
            Marshal.Copy(pointer, data, 0, data.Length);
            return data;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>把字节写成一块全局内存交给剪贴板。交出去之后内存归系统管，失败时才由我们释放。</summary>
    private static bool TrySetData(uint format, byte[] data)
    {
        nint memory = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (nuint)Math.Max(1, data.Length));
        if (memory == nint.Zero)
        {
            return false;
        }

        nint pointer = NativeMethods.GlobalLock(memory);
        if (pointer == nint.Zero)
        {
            NativeMethods.GlobalFree(memory);
            return false;
        }

        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(memory);
        }

        if (NativeMethods.SetClipboardData(format, memory) == nint.Zero)
        {
            NativeMethods.GlobalFree(memory);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 数据是 GDI 句柄或私有句柄、不是内存块的格式。这些格式的"内容"只是一个句柄值，
    /// 原数据一释放句柄就失效，复制字节没有意义（还可能让别的程序读到野句柄）。
    /// </summary>
    private static bool IsHandleFormat(uint format) => format switch
    {
        2 => true,                       // CF_BITMAP（系统会同时提供可复制的 CF_DIB）
        3 => true,                       // CF_METAFILEPICT（内存块里装的是图元文件句柄）
        9 => true,                       // CF_PALETTE
        14 => true,                      // CF_ENHMETAFILE
        0x0080 => true,                  // CF_OWNERDISPLAY
        0x0082 or 0x0083 or 0x008E => true, // CF_DSPBITMAP / CF_DSPMETAFILEPICT / CF_DSPENHMETAFILE
        >= 0x0200 and <= 0x03FF => true, // CF_PRIVATEFIRST…CF_GDIOBJLAST：私有句柄与 GDI 对象
        _ => false
    };

    /// <summary>用 using 包住"打开着的剪贴板"，保证任何路径都会关掉它 —— 忘了关，全系统都没法复制粘贴。</summary>
    private readonly struct ClipboardLease : IDisposable
    {
        public void Dispose()
        {
            if (!NativeMethods.CloseClipboard())
            {
                Logger.Warning("剪贴板", $"关闭剪贴板失败（Windows 错误码 {Marshal.GetLastWin32Error()}）。");
            }
        }
    }
}
