using System.Globalization;
using System.Windows.Data;

namespace CtrlTranslator.UI;

/// <summary>
/// 翻译历史的日期分组标题：今天 / 昨天 / 10月12日 周日 / 2025年10月12日。
/// </summary>
/// <remarks>
/// 卡片本身仍然不显示时间（UI 改版时定的：一条一个时间戳是噪音）。
/// 但完全没有时间线索的话，「我昨天查过的那个词」这种最自然的找法就用不了 ——
/// 负责人 2026-10 决定加一行分组小标题，只在日期变化的地方出现一次。
/// <para>
/// 「今天」「昨天」按转换那一刻的日期算。窗口开着跨过午夜不会自己改名，
/// 下次重新加载列表（切页、搜索、筛选）就对了，不值得为此挂一个定时器。
/// </para>
/// </remarks>
public sealed class HistoryDateGroupConverter : IValueConverter
{
    private static readonly string[] WeekdayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime createdAt || createdAt == default)
        {
            return "更早";
        }

        DateTime day = createdAt.Date;
        DateTime today = DateTime.Today;
        if (day == today)
        {
            return "今天";
        }

        if (day == today.AddDays(-1))
        {
            return "昨天";
        }

        return day.Year == today.Year
            ? $"{day.Month}月{day.Day}日 {WeekdayNames[(int)day.DayOfWeek]}"
            : $"{day.Year}年{day.Month}月{day.Day}日";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("日期分组标题只用于显示。");
    }
}
