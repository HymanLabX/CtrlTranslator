using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CtrlTranslator.Data;
using CtrlTranslator.Diagnostics;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace CtrlTranslator.UI;

public partial class MainWindow : Window
{
    private const int PageSize = 500;
    private readonly HistoryRepository _repository;
    private readonly ObservableCollection<TranslationRecord> _records = [];
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _loadCancellation;
    private bool _loading;
    private bool _hasMore = true;

    public MainWindow(HistoryRepository repository)
    {
        InitializeComponent();
        _repository = repository;
        HistoryGrid.ItemsSource = _records;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += SearchTimer_OnTick;
        Loaded += MainWindow_OnLoaded;
        Closed += MainWindow_OnClosed;
    }

    public async Task RefreshAsync()
    {
        await ReloadAsync();
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowLoadError(exception);
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void SearchTimer_OnTick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        try
        {
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowLoadError(exception);
        }
    }

    /// <summary>
    /// 「全部历史 / 我的收藏」切换。IsLoaded 的判断不能去掉 ——
    /// XAML 里 AllTabButton 带 IsChecked="True"，这个事件会在 InitializeComponent 期间就触发一次，
    /// 那时候 _repository 还没赋值。
    /// </summary>
    private async void HistoryTab_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowLoadError(exception);
        }
    }

    private void HistoryGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TranslationRecord? record = SelectedRecord;
        SourceDetail.Text = record?.SourceText ?? string.Empty;
        ResultDetail.Text = record?.ResultText ?? string.Empty;
        if (record is null)
        {
            MetadataText.Text = "请选择一条记录查看详情";
            return;
        }

        // 多档案上线后，"这条是谁翻的"是个真实存在的问题，所以在这里补上服务商。
        string provider = string.IsNullOrWhiteSpace(record.ProfileName)
            ? string.Empty
            : $"服务商：{record.ProfileName}　";
        MetadataText.Text =
            $"{provider}模型：{record.Model}　来源：{record.AppName ?? "未知"}　"
            + $"耗时：{record.LatencyMilliseconds?.ToString() ?? "未知"} 毫秒　"
            + $"时间：{record.CreatedAt:yyyy-MM-dd HH:mm:ss}";
    }

    private async void HistoryGrid_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 1)
        {
            return;
        }

        try
        {
            await LoadNextPageAsync(reset: false);
        }
        catch (Exception exception)
        {
            ShowLoadError(exception);
        }
    }

    private async void CopySource_OnClick(object sender, RoutedEventArgs e) =>
        await CopySelectedAsync(useResult: false);

    private async void CopyResult_OnClick(object sender, RoutedEventArgs e) =>
        await CopySelectedAsync(useResult: true);

    private async Task CopySelectedAsync(bool useResult)
    {
        TranslationRecord? record = SelectedRecord;
        if (record is null)
        {
            return;
        }

        try
        {
            await SetClipboardTextWithRetryAsync(useResult ? record.ResultText : record.SourceText);
        }
        catch (Exception exception)
        {
            Logger.Error("历史窗口", "复制历史文本失败。", exception);
            MessageBox.Show(this, "暂时无法使用剪贴板，请稍后再试。", "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ToggleStar_OnClick(object sender, RoutedEventArgs e)
    {
        TranslationRecord? record = SelectedRecord;
        if (record is null)
        {
            return;
        }

        try
        {
            await _repository.SetStarredAsync(record.Id, !record.Starred, CancellationToken.None);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            Logger.Error("历史窗口", "更新收藏状态失败。", exception);
            MessageBox.Show(this, "收藏状态没有保存成功，请稍后再试。", "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteRecord_OnClick(object sender, RoutedEventArgs e)
    {
        TranslationRecord? record = SelectedRecord;
        if (record is null || MessageBox.Show(this, "确定删除这条翻译记录吗？", "删除记录",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _repository.DeleteAsync(record.Id, CancellationToken.None);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            Logger.Error("历史窗口", "删除历史记录失败。", exception);
            MessageBox.Show(this, "这条记录没有删除成功，请稍后再试。", "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await HistoryCsvExporter.ExportAsync(_repository, this, CancellationToken.None);
            if (path is not null)
            {
                MessageBox.Show(this, "历史记录已导出。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            Logger.Error("历史窗口", "导出 CSV 失败。", exception);
            MessageBox.Show(this, "导出失败，请确认保存位置可写后重试。", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Task ReloadAsync() => LoadNextPageAsync(reset: true);

    private async Task LoadNextPageAsync(bool reset)
    {
        if (_loading || (!reset && !_hasMore))
        {
            return;
        }

        _loading = true;
        try
        {
            if (reset)
            {
                _loadCancellation?.Cancel();
                _loadCancellation?.Dispose();
                _loadCancellation = new CancellationTokenSource();
                _records.Clear();
                _hasMore = true;
            }

            CancellationToken token = _loadCancellation?.Token ?? CancellationToken.None;
            bool starredOnly = StarredTabButton.IsChecked == true;
            IReadOnlyList<TranslationRecord> page = await _repository.QueryAsync(
                SearchBox.Text.Trim(), starredOnly, _records.Count, PageSize, token);
            foreach (TranslationRecord record in page)
            {
                _records.Add(record);
            }
            _hasMore = page.Count == PageSize;
            UpdateEmptyState(starredOnly);
        }
        catch (OperationCanceledException)
        {
            // 新查询会取消旧查询，这是正常流程。
            Logger.Info("历史窗口", "旧的历史查询已被新查询取消。");
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 空列表时给一句人话，而不是一片空白 —— 空白会让人以为是程序坏了。
    /// 三种"空"的原因不同，给的下一步也该不同。
    /// </summary>
    private void UpdateEmptyState(bool starredOnly)
    {
        if (_records.Count > 0)
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            return;
        }

        bool searching = SearchBox.Text.Trim().Length > 0;
        EmptyStateText.Text = searching
            ? "没有匹配的记录。换个关键词试试。"
            : starredOnly
                ? "还没有收藏。翻译出来后点浮窗上的 ☆ 就能存进这里。"
                : "还没有翻译记录。选中文字后双击 Ctrl 试试。";
        EmptyStateText.Visibility = Visibility.Visible;
    }

    private void ShowLoadError(Exception exception)
    {
        Logger.Error("历史窗口", "加载翻译历史失败。", exception);
        MessageBox.Show(this, "暂时无法读取翻译历史，请稍后再试。", "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static async Task SetClipboardTextWithRetryAsync(string text)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Logger.Warning("历史窗口", $"剪贴板正忙，第 {attempt} 次复制失败，将在 20ms 后重试。");
                await Task.Delay(20);
            }
        }

        Clipboard.SetText(text);
    }

    private TranslationRecord? SelectedRecord => HistoryGrid.SelectedItem as TranslationRecord;
}
