using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Interop;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataFormats = System.Windows.DataFormats;
using FormsClipboard = System.Windows.Forms.Clipboard;

namespace CtrlTranslator.Core;

/// <summary>
/// 严格按 SPEC §3.1 的七步流程，通过剪贴板取得当前选中文本并还原现场。
/// </summary>
public sealed class TextCapture
{
    private const int ClipboardRetryCount = 5;
    private const int ClipboardRetryDelayMs = 20;
    private const int ClipboardPollIntervalMs = 10;
    private const int ClipboardRestoreDelayMs = 120;

    private static readonly ushort[] ModifierKeys =
    [
        NativeMethods.VkLControl,
        NativeMethods.VkRControl,
        NativeMethods.VkLShift,
        NativeMethods.VkRShift,
        NativeMethods.VkLMenu,
        NativeMethods.VkRMenu,
        NativeMethods.VkLWin,
        NativeMethods.VkRWin
    ];

    private readonly Dispatcher _dispatcher;

    public TextCapture(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task<string?> CaptureAsync(int clipboardWaitMs)
    {
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("剪贴板取词必须在 WPF 主线程执行。");
        }

        // 1. 记录复制前的序列号。
        uint sequenceBefore = NativeMethods.GetClipboardSequenceNumber();

        // 2. 只备份 CF_UNICODETEXT；其他格式按 v1.0 限制记录日志。
        ClipboardSnapshot snapshot = await ReadClipboardSnapshotAsync();
        if (snapshot.HadUnsupportedData)
        {
            Logger.Warning("剪贴板", "原剪贴板包含图片、文件或其他非 Unicode 文本，v1.0 无法完整还原。");
        }

        // 3. 释放仍处于按下状态的物理修饰键。
        ReleasePressedModifierKeys();

        // 4. 模拟 Ctrl+C。
        SendCopyShortcut();

        // 5/6. 每 10ms 轮询序列号，超时则静默返回。
        bool clipboardChanged = await WaitForClipboardChangeAsync(sequenceBefore, clipboardWaitMs);
        if (!clipboardChanged)
        {
            Logger.Info("取词", "剪贴板序列号未变化，未检测到选中文字。");
            return null;
        }

        string? capturedText;
        try
        {
            capturedText = await ReadUnicodeTextAsync("读取复制结果");
        }
        finally
        {
            // 7. 必须延迟 120ms，避开目标程序自己的后续剪贴板操作。
            await Task.Delay(ClipboardRestoreDelayMs);
            await RestoreClipboardAsync(snapshot);
        }

        string cleanedText = capturedText?.Trim() ?? string.Empty;
        return cleanedText.Length == 0 ? null : cleanedText;
    }

    private static void ReleasePressedModifierKeys()
    {
        List<NativeMethods.Input> releases = [];
        foreach (ushort virtualKey in ModifierKeys)
        {
            if ((NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0)
            {
                releases.Add(CreateKeyboardInput(virtualKey, keyUp: true));
            }
        }

        if (releases.Count > 0)
        {
            SendInputs(releases.ToArray(), "释放物理修饰键");
        }
    }

    private static void SendCopyShortcut()
    {
        NativeMethods.Input[] inputs =
        [
            CreateKeyboardInput(NativeMethods.VkControl, keyUp: false),
            CreateKeyboardInput(NativeMethods.VkC, keyUp: false),
            CreateKeyboardInput(NativeMethods.VkC, keyUp: true),
            CreateKeyboardInput(NativeMethods.VkControl, keyUp: true)
        ];
        SendInputs(inputs, "发送 Ctrl+C");
    }

    private static NativeMethods.Input CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? NativeMethods.KeyEventFKeyUp : 0
                }
            }
        };
    }

    private static void SendInputs(NativeMethods.Input[] inputs, string operationName)
    {
        uint sent = NativeMethods.SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<NativeMethods.Input>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{operationName}失败。");
        }
    }

    private static async Task<bool> WaitForClipboardChangeAsync(uint sequenceBefore, int clipboardWaitMs)
    {
        int safeWaitMs = Math.Clamp(clipboardWaitMs, 100, 1000);
        long deadline = Stopwatch.GetTimestamp() + (long)safeWaitMs * Stopwatch.Frequency / 1000;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (NativeMethods.GetClipboardSequenceNumber() != sequenceBefore)
            {
                return true;
            }

            await Task.Delay(ClipboardPollIntervalMs);
        }

        return NativeMethods.GetClipboardSequenceNumber() != sequenceBefore;
    }

    private static async Task<ClipboardSnapshot> ReadClipboardSnapshotAsync()
    {
        return await RetryClipboardAsync(
            () =>
            {
                System.Windows.IDataObject? dataObject = WpfClipboard.GetDataObject();
                if (dataObject is null)
                {
                    return new ClipboardSnapshot(Text: null, HadUnsupportedData: false);
                }

                bool hasUnicodeText = dataObject.GetDataPresent(WpfDataFormats.UnicodeText, autoConvert: false);
                if (hasUnicodeText)
                {
                    string? text = dataObject.GetData(WpfDataFormats.UnicodeText, autoConvert: false) as string;
                    return new ClipboardSnapshot(text, HadUnsupportedData: false);
                }

                bool hasAnyData = dataObject.GetFormats(autoConvert: false).Length > 0;
                return new ClipboardSnapshot(Text: null, HadUnsupportedData: hasAnyData);
            },
            "备份剪贴板");
    }

    private static async Task<string?> ReadUnicodeTextAsync(string operationName)
    {
        return await RetryClipboardAsync(
            () =>
            {
                System.Windows.IDataObject? dataObject = WpfClipboard.GetDataObject();
                if (dataObject is null
                    || !dataObject.GetDataPresent(WpfDataFormats.UnicodeText, autoConvert: false))
                {
                    return null;
                }

                return dataObject.GetData(WpfDataFormats.UnicodeText, autoConvert: false) as string;
            },
            operationName);
    }

    private static async Task RestoreClipboardAsync(ClipboardSnapshot snapshot)
    {
        if (snapshot.Text is not null)
        {
            try
            {
                FormsClipboard.SetDataObject(
                    snapshot.Text,
                    copy: true,
                    ClipboardRetryCount,
                    ClipboardRetryDelayMs);
                return;
            }
            catch (Exception exception)
            {
                Logger.Error("剪贴板", "还原剪贴板文本失败（已完成 5 次重试）。", exception);
                throw new ClipboardUnavailableException(
                    "剪贴板正被其他程序占用，稍等一下再试。",
                    exception);
            }
        }

        await RetryClipboardAsync(
            () =>
            {
                WpfClipboard.Clear();
                return true;
            },
            "清空复制产生的剪贴板内容");
    }

    private static async Task<T> RetryClipboardAsync<T>(Func<T> operation, string operationName)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= ClipboardRetryCount; attempt++)
        {
            try
            {
                return operation();
            }
            catch (Exception exception)
            {
                lastException = exception;
                Logger.Warning(
                    "剪贴板",
                    $"{operationName}第 {attempt} 次失败，将按规则重试。 {exception.GetType().Name}: {exception.Message}");

                if (attempt < ClipboardRetryCount)
                {
                    await Task.Delay(ClipboardRetryDelayMs);
                }
            }
        }

        throw new ClipboardUnavailableException(
            "剪贴板正被其他程序占用，稍等一下再试。",
            lastException);
    }

    private sealed record ClipboardSnapshot(string? Text, bool HadUnsupportedData);
}

/// <summary>
/// 所有剪贴板重试均失败时抛出的可识别异常。
/// </summary>
public sealed class ClipboardUnavailableException : Exception
{
    public ClipboardUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
