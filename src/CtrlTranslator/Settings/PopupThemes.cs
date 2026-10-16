namespace CtrlTranslator.Settings;

/// <summary>
/// 一套翻译浮窗的配色。<b>只作用于浮窗</b>，主窗口、设置页和向导始终是浅色，不跟着变。
/// </summary>
/// <remarks>
/// 这里只给 5 个基准色。悬停底色、次要文字、分隔线这些过渡色不写死，
/// 由 <see cref="CtrlTranslator.UI.PopupThemePalette"/> 按同一套规则算出来 ——
/// 每套配色手写十几个十六进制值，迟早会出现"这套的次要文字比正文还深"这类走样。
/// </remarks>
/// <param name="Surface">浮窗底色。</param>
/// <param name="Border">浮窗外框色，分隔线由它和底色混出来。</param>
/// <param name="Text">正文色，必须与 <paramref name="Surface"/> 有足够对比度。</param>
/// <param name="Accent">图标激活态（已收藏 / 已复制）、加载环和主按钮的底色。</param>
/// <param name="OnAccent">压在 <paramref name="Accent"/> 上的文字色。</param>
public sealed record PopupTheme(
    string Name,
    string Description,
    string Surface,
    string Border,
    string Text,
    string Accent,
    string OnAccent);

/// <summary>
/// 内置浮窗配色清单。设置页的配色预设按钮和浮窗本身共用这一份。
/// </summary>
public static class PopupThemes
{
    /// <summary>没配过或配了个不认识的名字时用哪套。</summary>
    public const string DefaultName = "浅色";

    /// <summary>用户在「自定义」里自己填底色和文字色时用这个名字。</summary>
    public const string CustomName = "自定义";

    private static readonly PopupTheme[] Items =
    [
        // 与 Tokens.Light.xaml 的浅色令牌一致，保证"默认配色"看起来和以前一模一样。
        new(DefaultName, "跟软件本身一致的白底", "#FFFFFF", "#D1D1D1", "#171717", "#171717", "#FFFFFF"),
        new("深色", "夜里或在深色软件上不刺眼", "#23262B", "#454A52", "#ECEDEE", "#ECEDEE", "#23262B"),
        new("护眼", "暖纸色底，久看不累", "#F4EDDD", "#D6CBB2", "#2B2620", "#3F3629", "#F4EDDD")
    ];

    public static IReadOnlyList<PopupTheme> All => Items;

    /// <summary>按名字取配色；名字不认识（比如手改过配置文件）就退回默认那套。</summary>
    public static PopupTheme Find(string? name) =>
        Items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal))
        ?? Items[0];

    public static bool IsKnown(string? name) =>
        string.Equals(name, CustomName, StringComparison.Ordinal)
        || Items.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// 由用户自填的底色和文字色拼一套配色出来。
    /// 外框、图标激活色这些不再问用户要 —— 问全了没人填得完，按底色和文字色推出来更稳。
    /// </summary>
    public static PopupTheme CreateCustom(string surface, string text) => new(
        CustomName,
        "你自己填的颜色",
        surface,
        Blend(text, surface, 0.62),
        text,
        text,
        surface);

    /// <summary>
    /// 把两个 <c>#RRGGBB</c> 按比例混一混。外框直接用文字色会显得又黑又重，
    /// 混向底色六成之后才是一条克制的边。解析不了就原样返回，交给上层兜底。
    /// </summary>
    private static string Blend(string from, string to, double amount)
    {
        if (!TryParse(from, out int fr, out int fg, out int fb)
            || !TryParse(to, out int tr, out int tg, out int tb))
        {
            return from;
        }

        int r = (int)Math.Round(fr + ((tr - fr) * amount));
        int g = (int)Math.Round(fg + ((tg - fg) * amount));
        int b = (int)Math.Round(fb + ((tb - fb) * amount));
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static bool TryParse(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        string value = hex.TrimStart('#');
        return value.Length == 6
            && int.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out r)
            && int.TryParse(value[2..4], System.Globalization.NumberStyles.HexNumber, null, out g)
            && int.TryParse(value[4..], System.Globalization.NumberStyles.HexNumber, null, out b);
    }
}
