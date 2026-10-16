using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Interop;

namespace CtrlTranslator.Core;

/// <summary>
/// 按 SPEC §3.1 的流程，通过剪贴板取得当前选中文本并还原现场。
/// </summary>
/// <remarks>
/// 剪贴板的读写全部交给 <see cref="ClipboardService"/>：它负责重试规则，
/// 也负责把剪贴板<b>完整</b>备份、还原（不只是文字，截图、文件、带格式文字都在内）。
/// </remarks>
public sealed class TextCapture
{
    private const int ClipboardPollIntervalMs = 10;
    private const int ClipboardRestoreDelayMs = 120;

    /// <summary>剪贴板序列号变了之后，最多再等多久让文字格式到位。</summary>
    private const int TextArrivalGraceMs = 200;

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

        // 1. 先把剪贴板原样备份下来。
        ClipboardSnapshot snapshot = await ClipboardService.SnapshotAsync();

        // 2. 记录复制前的序列号。⚠️ 必须放在备份之后：备份时，原程序为"延迟渲染"的格式
        //    现场生成内容也会让序列号变化；记早了，这次变化会被误当成"复制成功"，
        //    读到的就是旧剪贴板里的东西。
        uint sequenceBefore = NativeMethods.GetClipboardSequenceNumber();

        // 3. 释放仍处于按下状态的物理修饰键。
        ReleasePressedModifierKeys();

        // 4. 模拟 Ctrl+C。
        SendCopyShortcut();

        // 5/6. 每 10ms 轮询序列号，超时则静默返回。剪贴板没被动过，也就不用还原。
        bool clipboardChanged = await WaitForClipboardChangeAsync(sequenceBefore, clipboardWaitMs);
        if (!clipboardChanged)
        {
            Logger.Info("取词", "剪贴板序列号未变化，未检测到选中文字。");
            return null;
        }

        string? capturedText;
        try
        {
            // 序列号一变就读可能早了一拍：有的程序先清空剪贴板、再分几次放各种格式，
            // 文字不一定是第一个到的。以前这种情况直接读到空，表现为"偶尔取不到词"。
            await WaitForTextAsync();
            capturedText = await ClipboardService.ReadTextAsync();
        }
        finally
        {
            // 7. 必须延迟 120ms，避开目标程序自己的后续剪贴板操作。
            await Task.Delay(ClipboardRestoreDelayMs);
            await ClipboardService.RestoreAsync(snapshot);
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

    private static async Task WaitForTextAsync()
    {
        long deadline = Stopwatch.GetTimestamp() + (long)TextArrivalGraceMs * Stopwatch.Frequency / 1000;
        while (!ClipboardService.HasText() && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(ClipboardPollIntervalMs);
        }
    }
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
