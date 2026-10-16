using System.Globalization;
using System.IO;
using System.Text;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace CtrlTranslator.Data;

/// <summary>
/// 为主窗口和设置窗口提供完全一致的历史导出格式。
/// </summary>
public static class HistoryCsvExporter
{
    /// <param name="modeNameOf">
    /// 把模式 Id 换成名字（auto → 通用）。以前「模式」一列直接写 Id，
    /// 用 Excel 打开看到的是 auto、academic，用户认不出来。
    /// </param>
    public static async Task<string?> ExportAsync(
        HistoryRepository repository,
        System.Windows.Window owner,
        Func<string, string> modeNameOf,
        CancellationToken cancellationToken)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出翻译历史",
            Filter = "CSV 文件 (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"CtrlTranslator-历史-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return null;
        }

        IReadOnlyList<TranslationRecord> records = await repository.GetAllAsync(cancellationToken);
        await using StreamWriter writer = new(
            dialog.FileName,
            false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("时间,模式,模型,原文,译文,已收藏,来源程序,耗时毫秒");
        foreach (TranslationRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = string.Join(",",
                Escape(record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)),
                Escape(modeNameOf(record.ModeId)),
                Escape(record.Model),
                Escape(record.SourceText),
                Escape(record.ResultText),
                Escape(record.Starred ? "是" : "否"),
                Escape(record.AppName ?? string.Empty),
                Escape(record.LatencyMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }

        return dialog.FileName;
    }

    /// <summary>
    /// RFC 4180 转义：整体加引号、内部引号翻倍，逗号和换行因此都是安全的。
    /// </summary>
    /// <remarks>
    /// 首字符是 <c>=</c> <c>+</c> <c>-</c> <c>@</c> 时额外补一个单引号：Excel 会把加了引号的
    /// <c>=1+1</c> 仍然当公式求值，导出的表格内容就和原文对不上了。
    /// 原文来自用户自己选中的文字，不是外来攻击面，这里只求"导出什么就显示什么"。
    /// </remarks>
    private static string Escape(string value)
    {
        string safe = value.Length > 0 && "=+-@\t\r".Contains(value[0], StringComparison.Ordinal)
            ? "'" + value
            : value;
        return $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
