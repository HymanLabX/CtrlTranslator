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
    public static async Task<string?> ExportAsync(
        HistoryRepository repository,
        System.Windows.Window owner,
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
                Escape(record.ModeId),
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

    private static string Escape(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
