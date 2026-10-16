using System.Globalization;
using System.Speech.Synthesis;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Translation;

namespace CtrlTranslator.Speech;

/// <summary>
/// 基于 SAPI5 的朗读服务（SPEC §8）。朗读的是<b>原文</b>，不是译文。
/// 全局单例，由 App 持有并在退出时释放。
/// </summary>
public sealed class SpeechService : IDisposable
{
    // 英文优先挑这几个音色，它们在中文 Windows 上也普遍存在且发音可用。
    private static readonly string[] PreferredEnglishVoices = ["Zira", "David", "Mark", "Hazel"];

    private readonly SpeechSynthesizer? _synthesizer;
    private readonly List<InstalledVoice> _voices = [];

    /// <summary>
    /// 系统默认语音。原文语言找不到对应语音时退回它 —— 以前是什么都不做，
    /// 结果沿用上一次选中的语音，拿英文语音去念中文。
    /// </summary>
    private readonly string? _defaultVoiceName;
    private bool _disposed;

    public SpeechService()
    {
        try
        {
            SpeechSynthesizer synthesizer = new();
            _voices = synthesizer
                .GetInstalledVoices()
                .Where(voice => voice.Enabled)
                .ToList();

            if (_voices.Count == 0)
            {
                synthesizer.Dispose();
                UnavailableReason =
                    "系统里没有安装可用的语音包。可到 Windows 设置 → 时间和语言 → 语音 → 添加语音 安装。";
                Logger.Warning("朗读", "未找到任何可用的 SAPI 语音，朗读功能已禁用。");
                return;
            }

            synthesizer.SpeakCompleted += OnSpeakCompleted;
            _synthesizer = synthesizer;
            _defaultVoiceName = synthesizer.Voice?.Name;
            Logger.Info(
                "朗读",
                $"朗读服务就绪，可用语音 {_voices.Count} 个："
                    + string.Join("、", _voices.Select(v => v.VoiceInfo.Name)));
        }
        catch (Exception exception)
        {
            // 某些精简版 Windows 缺少语音组件，构造 SpeechSynthesizer 本身就会抛异常。
            UnavailableReason = "这台电脑的语音组件不可用，朗读功能无法使用。详情见日志。";
            Logger.Error("朗读", "初始化朗读服务失败，功能已禁用。", exception);
        }
    }

    /// <summary>朗读结束或被取消时触发，用于让界面把按钮恢复成「朗读」态。</summary>
    public event EventHandler? SpeakCompleted;

    public bool IsAvailable => _synthesizer is not null;

    /// <summary>不可用时给用户看的人话说明，可用时为 null。</summary>
    public string? UnavailableReason { get; }

    public bool IsSpeaking { get; private set; }

    /// <summary>
    /// 朗读指定文本。若正在朗读则先停止再读新的。
    /// </summary>
    public bool Speak(string text)
    {
        if (_synthesizer is null || _disposed || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            _synthesizer.SpeakAsyncCancelAll();
            SelectVoiceFor(text);
            IsSpeaking = true;
            _synthesizer.SpeakAsync(text);
            return true;
        }
        catch (Exception exception)
        {
            IsSpeaking = false;
            Logger.Error("朗读", "开始朗读失败。", exception);
            return false;
        }
    }

    /// <summary>停止朗读。浮窗关闭、程序退出时必须调用。</summary>
    public void Stop()
    {
        if (_synthesizer is null || _disposed)
        {
            return;
        }

        try
        {
            _synthesizer.SpeakAsyncCancelAll();
        }
        catch (Exception exception)
        {
            Logger.Error("朗读", "停止朗读失败。", exception);
        }
        finally
        {
            IsSpeaking = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_synthesizer is null)
        {
            return;
        }

        try
        {
            _synthesizer.SpeakCompleted -= OnSpeakCompleted;
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Error("朗读", "释放朗读服务失败。", exception);
        }
    }

    /// <summary>
    /// 按原文语言挑语音：先按语言匹配，英文再优先挑指定音色，都挑不到就用当前默认。
    /// </summary>
    private void SelectVoiceFor(string text)
    {
        if (_synthesizer is null)
        {
            return;
        }

        string language = DetectLanguage(text);
        List<InstalledVoice> candidates = _voices
            .Where(voice => string.Equals(
                voice.VoiceInfo.Culture.TwoLetterISOLanguageName,
                language,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            RestoreDefaultVoice();
            return;
        }

        InstalledVoice? chosen = null;
        if (language == "en")
        {
            chosen = PreferredEnglishVoices
                .Select(preferred => candidates.FirstOrDefault(voice =>
                    voice.VoiceInfo.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(voice => voice is not null);
        }

        chosen ??= candidates[0];

        try
        {
            _synthesizer.SelectVoice(chosen.VoiceInfo.Name);
        }
        catch (Exception exception)
        {
            // 选不中就用默认音色继续读，不让朗读整个失败。
            Logger.Warning("朗读", $"选择语音「{chosen.VoiceInfo.Name}」失败，改用默认音色：{exception.Message}");
        }
    }

    private void RestoreDefaultVoice()
    {
        if (_synthesizer is null || string.IsNullOrEmpty(_defaultVoiceName))
        {
            return;
        }

        try
        {
            _synthesizer.SelectVoice(_defaultVoiceName);
        }
        catch (Exception exception)
        {
            Logger.Warning("朗读", $"恢复默认语音「{_defaultVoiceName}」失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 原文的语言。与 §5.4 的翻译方向判定用同一份判断（<see cref="TextLanguage"/>），
    /// 日文、韩文能挑到对应语音；其余文字按英文处理。
    /// </summary>
    private static string DetectLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        }

        return TextLanguage.Detect(text) switch
        {
            TextScript.Chinese => "zh",
            TextScript.Japanese => "ja",
            TextScript.Korean => "ko",
            _ => "en"
        };
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        IsSpeaking = false;
        SpeakCompleted?.Invoke(this, EventArgs.Empty);
    }
}
