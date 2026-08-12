using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CtrlTranslator.Data;
using CtrlTranslator.Diagnostics;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace CtrlTranslator.UI;

public partial class MainWindow : Window
{
    private const int PageSize = 500;
    private readonly HistoryRepository _repository;
    private readonly SettingsWindow _settingsView;
    private readonly ObservableCollection<TranslationRecord> _records = [];
    private CancellationTokenSource? _loadCancellation;
    private bool _loading;
    private bool _hasMore = true;

    public MainWindow(HistoryRepository repository, SettingsWindow settingsView)
    {
        InitializeComponent();
        _repository = repository;
        _settingsView = settingsView;
        SettingsHost.Content = settingsView;
        HistoryGrid.ItemsSource = _records;
        Loaded += MainWindow_OnLoaded;
        Closed += MainWindow_OnClosed;
    }

    public async Task RefreshAsync()
    {
        await ReloadAsync();
    }

    /// <summary>切换到历史工作台。主窗口被第二个实例唤醒时也统一回到这里。</summary>
    public void ShowHistory()
    {
        AllTabButton.IsChecked = true;
        RecordsTabButton.IsChecked = true;
        ShowHistoryContent();
    }

    /// <summary>在主窗口内部打开设置，不再创建第二个顶级窗口。</summary>
    public void ShowSettings(bool focusExtraRequestBody)
    {
        SettingsTabButton.IsChecked = true;
        ShowSettingsContent();
        _settingsView.ShowTranslationService(focusExtraRequestBody);
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
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
    }

    /// <summary>
    /// 一级导航只负责切换“翻译记录 / 设置”，收藏是记录页内部的筛选条件。
    /// </summary>
    private async void HistoryTab_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ShowHistoryContent();
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
    /// 「全部记录 / 已收藏」内容筛选。IsLoaded 的判断不能去掉：
    /// XAML 默认选中项会在 InitializeComponent 期间先触发一次，此时仓库尚未赋值。
    /// </summary>
    private async void HistoryFilter_OnChecked(object sender, RoutedEventArgs e)
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
            DetailCards.Visibility = Visibility.Collapsed;
            DetailEmptyState.Visibility = Visibility.Visible;
            return;
        }

        DetailEmptyState.Visibility = Visibility.Collapsed;
        DetailCards.Visibility = Visibility.Visible;

        // 多档案上线后，"这条是谁翻的"是个真实存在的问题，所以在这里补上服务商。
        string provider = string.IsNullOrWhiteSpace(record.ProfileName)
            ? string.Empty
            : $"服务商：{record.ProfileName}　";
        MetadataText.Text =
            $"{provider}模型：{record.Model}　来源程序：{record.AppName ?? "未知"}　"
            + $"耗时：{record.LatencyMilliseconds?.ToString() ?? "未知"} 毫秒";
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
        TranslationRecord? record = RecordFromActionSource(sender);
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
        TranslationRecord? record = RecordFromActionSource(sender);
        if (record is null)
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

    private void SettingsTab_OnChecked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ShowSettingsContent();
        }
    }

    private void ShowHistoryContent()
    {
        HistoryContent.Visibility = Visibility.Visible;
        SettingsHost.Visibility = Visibility.Collapsed;
    }

    private void ShowSettingsContent()
    {
        HistoryContent.Visibility = Visibility.Collapsed;
        SettingsHost.Visibility = Visibility.Visible;
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
                string.Empty, starredOnly, _records.Count, PageSize, token);
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

        EmptyStateText.Text = starredOnly
            ? "还没有收藏。点记录右侧的五角星，就能把它放进这里。"
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

    /// <summary>列表内的直接操作使用按钮自身的数据项，不依赖“先选中再右键”。</summary>
    private static TranslationRecord? RecordFromActionSource(object sender) =>
        (sender as FrameworkElement)?.DataContext as TranslationRecord;
}
