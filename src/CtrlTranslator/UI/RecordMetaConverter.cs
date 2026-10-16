using System.Globalization;
using System.Text;
using System.Windows.Data;
using CtrlTranslator.Data;

namespace CtrlTranslator.UI;

/// <summary>
/// 把一条翻译记录的来源信息压成一行说明，供展开的历史卡片使用。
/// </summary>
/// <remarks>
/// 这些字段单独看都不值得占一行，凑在一起才回答同一个问题：这条译文是哪儿来的。
/// 缺的字段直接不出现，而不是写「未知」—— 一行里挂三个「未知」比不写更难读。
/// </remarks>
public sealed class RecordMetaConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TranslationRecord record)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        Append(builder, record.Model);
        Append(builder, record.ProfileName);
        Append(builder, string.IsNullOrWhiteSpace(record.AppName) ? null : $"来自 {FriendlyAppName(record.AppName)}");
        Append(builder, record.LatencyMilliseconds is { } ms ? $"{ms / 1000d:0.0}s" : null);
        return builder.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("来源说明只用于显示。");
    }

    /// <summary>
    /// 记录里存的是进程名（<c>AcroRd32</c>、<c>WINWORD</c>），这是给程序看的；
    /// 常见的几个换成人认得的名字。认不出的原样显示 —— 进程名至少还能搜得到。
    /// 只影响这一行显示，数据库和 CSV 里仍是原始进程名。
    /// </summary>
    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AcroRd32"] = "Adobe Acrobat",
        ["Acrobat"] = "Adobe Acrobat",
        ["WINWORD"] = "Word",
        ["EXCEL"] = "Excel",
        ["POWERPNT"] = "PowerPoint",
        ["OUTLOOK"] = "Outlook",
        ["ONENOTE"] = "OneNote",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["Code"] = "VS Code",
        ["explorer"] = "文件资源管理器",
        ["notepad"] = "记事本",
        ["WeChat"] = "微信",
        ["Weixin"] = "微信",
        ["wps"] = "WPS",
        ["et"] = "WPS 表格",
        ["wpp"] = "WPS 演示",
        ["zotero"] = "Zotero",
        ["SumatraPDF"] = "SumatraPDF"
    };

    private static string FriendlyAppName(string processName) =>
        KnownApps.TryGetValue(processName.Trim(), out string? friendly) ? friendly : processName;

    private static void Append(StringBuilder builder, string? part)
    {
        if (string.IsNullOrWhiteSpace(part))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(" · ");
        }

        builder.Append(part);
    }
}
