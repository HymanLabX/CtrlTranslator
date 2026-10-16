using System.Windows.Media;
using CtrlTranslator.Settings;
// 项目开了 UseWindowsForms，System.Drawing.Color 也在全局 using 里，不指名就是二义引用。
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CtrlTranslator.UI;

/// <summary>
/// 把一套浮窗配色摊开成浮窗真正要用的那批画笔。
/// </summary>
/// <remarks>
/// 浮窗里的颜色（含 Controls.xaml 里的按钮模板）全部走 DynamicResource / SetResourceReference，
/// 而 DynamicResource 的查找顺序是"元素 → 窗口 → 应用"。所以只要把这些同名键塞进
/// <c>PopupWindow.Resources</c>，整扇浮窗就跟着换色，应用的其余部分一个像素都不受影响 ——
/// 这也是"只给浮窗换肤、不做全局深色主题"能成立的原因。
/// <para>
/// 故意<b>不</b>覆盖 Brush.Danger / Brush.Warning 那几个：错误和警告提示在深色底上
/// 仍然是"浅底深字的一小块"，照样看得清，换成暗色反而容易糊成一片。
/// </para>
/// </remarks>
internal static class PopupThemePalette
{
    /// <summary>生成"资源键 → 画笔"的映射，直接灌进浮窗的资源字典即可。</summary>
    public static IReadOnlyDictionary<string, SolidColorBrush> Build(PopupTheme theme)
    {
        Color surface = Parse(theme.Surface);
        Color border = Parse(theme.Border);
        Color text = Parse(theme.Text);
        Color accent = Parse(theme.Accent);
        Color onAccent = Parse(theme.OnAccent);

        // 过渡色一律"从某个基准色往另一个基准色挪一点"：
        // 浅色配色自然变深，深色配色自然变浅，不用为每套配色单独调一遍。
        Color divider = Mix(border, surface, 0.5);
        Color textSecondary = Mix(text, surface, 0.32);
        Color textTertiary = Mix(text, surface, 0.5);
        Color hover = Mix(surface, text, 0.06);
        Color pressed = Mix(surface, text, 0.12);

        return new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal)
        {
            ["Brush.Bg"] = Freeze(surface),
            ["Brush.Surface"] = Freeze(surface),
            ["Brush.SurfaceRaised"] = Freeze(surface),
            ["Brush.SurfaceSubtle"] = Freeze(hover),
            ["Brush.Border"] = Freeze(divider),
            ["Brush.BorderStrong"] = Freeze(border),
            ["Brush.Text"] = Freeze(text),
            ["Brush.TextSecondary"] = Freeze(textSecondary),
            ["Brush.TextTertiary"] = Freeze(textTertiary),
            ["Brush.Accent"] = Freeze(accent),
            ["Brush.AccentHover"] = Freeze(Mix(accent, surface, 0.18)),
            ["Brush.AccentPressed"] = Freeze(accent),
            ["Brush.AccentSubtle"] = Freeze(hover),
            ["Brush.Primary"] = Freeze(accent),
            ["Brush.OnAccent"] = Freeze(onAccent),
            ["Brush.Hover"] = Freeze(hover),
            ["Brush.Pressed"] = Freeze(pressed)
        };
    }

    /// <summary>设置页的配色预览小色块用，不走资源字典。</summary>
    public static SolidColorBrush Swatch(string hex) => Freeze(Parse(hex));

    /// <summary>
    /// 用户填的颜色规范成 <c>#RRGGBB</c>。
    /// 「255,0,0」「#f00」「#FF0000」「red」都收，认不出来就返回 false 让上层报人话错误。
    /// </summary>
    public static bool TryNormalize(string? input, out string hex)
    {
        hex = string.Empty;
        string value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        // 先认「R,G,B」这种最像"输入 RGB"的写法，再交给 WPF 认 # 开头和颜色名。
        string[] parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3)
        {
            if (!byte.TryParse(parts[0], out byte r)
                || !byte.TryParse(parts[1], out byte g)
                || !byte.TryParse(parts[2], out byte b))
            {
                return false;
            }

            hex = $"#{r:X2}{g:X2}{b:X2}";
            return true;
        }

        try
        {
            if (ColorConverter.ConvertFromString(value) is not Color color)
            {
                return false;
            }

            hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Color Parse(string hex)
    {
        // 值全部来自 PopupThemes 里的常量，不是用户输入；真写错了就退回黑白，
        // 至少还能看清字，不至于让浮窗整个变透明。
        try
        {
            return ColorConverter.ConvertFromString(hex) is Color color ? color : Colors.Black;
        }
        catch (FormatException)
        {
            return Colors.Black;
        }
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    // 浮窗是每次划词新建一扇，画笔冻结之后可以跨窗口复用，也省掉 WPF 的变更通知开销。
    private static SolidColorBrush Freeze(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
