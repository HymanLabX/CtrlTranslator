namespace CtrlTranslator.Settings;

/// <summary>一套浮窗尺寸预设，只管窗口多大，不管字多大。</summary>
public sealed record PopupSizePreset(
    string Name,
    string Description,
    double MaxWidth,
    double MaxHeight);

/// <summary>一套字号预设，只管字多大，不管窗口多大。</summary>
public sealed record PopupFontPreset(
    string Name,
    string Description,
    double FontSize);

/// <summary>
/// 浮窗的尺寸与字号预设。
/// 数值必须落在设置页的校验区间内：宽 260–900、高 180–900、字号 10–24，
/// 否则点了预设反而弹一句校验错误。
/// </summary>
public static class PopupPresets
{
    /// <summary>三个数值都对不上任何一套预设时的叫法。</summary>
    public const string CustomName = "自定义";

    private static readonly PopupSizePreset[] SizeItems =
    [
        new("紧凑", "只看短句，占地方小", 380, 300),
        // 「标准」必须与 AppSettings 里的默认值一致，否则新装的用户一进设置就看到「自定义」。
        new("标准", "适合大多数人", 480, 400),
        new("宽松", "整段翻译看得更完整", 620, 520)
    ];

    private static readonly PopupFontPreset[] FontItems =
    [
        new("小字", "屏幕小或习惯小字", 12),
        new("标准", "适合大多数人", 14),
        new("大字", "看着不费眼", 18)
    ];

    public static IReadOnlyList<PopupSizePreset> Sizes => SizeItems;

    public static IReadOnlyList<PopupFontPreset> Fonts => FontItems;

    /// <summary>找出与当前宽高一致的尺寸预设；对不上就返回 null，即「自定义」。</summary>
    public static PopupSizePreset? MatchSize(double maxWidth, double maxHeight) =>
        SizeItems.FirstOrDefault(item =>
            IsSame(item.MaxWidth, maxWidth) && IsSame(item.MaxHeight, maxHeight));

    /// <summary>找出与当前字号一致的字号预设；对不上就返回 null，即「自定义」。</summary>
    public static PopupFontPreset? MatchFont(double fontSize) =>
        FontItems.FirstOrDefault(item => IsSame(item.FontSize, fontSize));

    // 这些值一路经过手输、字符串解析和 JSON 往返，直接用 == 比 double 不可靠，
    // 容差取半个 DIP —— 小于它的差别屏幕上也看不出来。
    private static bool IsSame(double left, double right) => Math.Abs(left - right) < 0.5;
}
