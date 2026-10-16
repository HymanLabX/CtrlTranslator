using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CtrlTranslator.Core;
using CtrlTranslator.Settings;
using System.Windows.Threading;
using CtrlTranslator.Data;
using CtrlTranslator.Diagnostics;
using MessageBox = System.Windows.MessageBox;

namespace CtrlTranslator.UI;

public partial class MainWindow : Window
{
    private const int PageSize = 500;
    // 设置窗口是就地改这个实例（不是换一个新的），所以这里存引用就够，
    // 读到的永远是用户当前的值。
    private readonly AppSettings _settings;

    private readonly HistoryRepository _repository;
    private readonly SettingsWindow _settingsView;
    private readonly DeepTranslationView _deepTranslationView;
    private readonly ObservableCollection<TranslationRecord> _records = [];
    private CancellationTokenSource? _loadCancellation;
    private bool _loading;
    private bool _hasMore = true;

    /// <summary>加载进行中又来了一次整表重载：记下来，等本次加载结束立刻补做，不能丢。</summary>
    private bool _reloadPending;

    /// <summary>
    /// 搜索关键词。仓储层的 QueryAsync 一开始就支持它（同时匹配原文和译文、
    /// LIKE 也做了转义），只是界面上一直传空字符串 —— 这次只是把入口接上。
    /// </summary>
    private string _searchKeyword = string.Empty;

    /// <summary>
    /// 输入防抖。每敲一个字就查一次库，长列表下会连着发好几次查询，
    /// 而且前面几次的结果还会闪一下再被覆盖。
    /// </summary>
    private DispatcherTimer? _searchTimer;

    /// <summary>
    /// 用户点一下就该立刻见效的数据库操作的等待上限。库被别的进程锁住时，
    /// 无限等待的表现就是"点了没反应"，还不如超时后明确说一句。
    /// </summary>
    private static readonly TimeSpan DatabaseInteractionTimeout = TimeSpan.FromSeconds(5);

    // 构造函数是 internal 而不是 public：唯一的调用方是 App，而深度翻译页是内部类型，
    // 签名里出现它就要求整个构造函数不能是 public。
    internal MainWindow(
        AppSettings settings,
        HistoryRepository repository,
        SettingsWindow settingsView,
        DeepTranslationView deepTranslationView)
    {
        InitializeComponent();
        _settings = settings;
        _repository = repository;
        _settingsView = settingsView;
        _deepTranslationView = deepTranslationView;
        SettingsHost.Content = settingsView;
        DeepTranslationHost.Content = deepTranslationView;
        // 按日期分组：今天 / 昨天 / 10月12日 周日。列表本来就按 created_at 倒序，
        // 分组只是在日期变化的地方插一行小标题，不改变顺序。
        // 新记录 Insert(0) 时视图会自动归进「今天」组，不用另外处理。
        var historyView = new ListCollectionView(_records);
        historyView.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(TranslationRecord.CreatedAt),
            new HistoryDateGroupConverter()));
        HistoryGrid.ItemsSource = historyView;
        _searchTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(220),
            DispatcherPriority.Background,
            SearchTimer_OnTick,
            Dispatcher);
        _searchTimer.Stop();
        Loaded += MainWindow_OnLoaded;
        Closed += MainWindow_OnClosed;
    }

    /// <summary>
    /// 列表和数据库对不上了、但当时不方便重载（有搜索词或开着"已收藏"筛选）时记一笔，
    /// 等下次回到历史页再整表重载。
    /// </summary>
    private bool _historyDirty;

    /// <summary>
    /// 刚翻译完一条：插到列表顶部，而不是整表重载。
    /// </summary>
    /// <remarks>
    /// 以前每翻译一次就整表重载 —— 主窗口开着、正往下翻历史的时候划个词，
    /// 列表就跳回顶部，展开的卡片也收起了。插入一条不会打乱已加载的分页：
    /// 新记录在数据库里同样排在最前，下一页的偏移量正好跟着多一。
    /// </remarks>
    public async Task AddRecordAsync(long recordId)
    {
        if (!IsLoaded)
        {
            return;
        }

        // 有搜索词或"已收藏"筛选时，这一条该不该出现说不准，干脆留到下次回到历史页时整表重载。
        if (_searchKeyword.Length > 0 || StarredTabButton.IsChecked == true)
        {
            _historyDirty = true;
            return;
        }

        if (_records.Any(record => record.Id == recordId))
        {
            return;
        }

        TranslationRecord? added = await _repository.GetAsync(recordId, CancellationToken.None);
        if (added is null)
        {
            return;
        }

        _records.Insert(0, added);
        UpdateEmptyState(starredOnly: false);
    }

    /// <summary>浮窗上点了收藏：只改那一条的星标，不整表重载。</summary>
    public async Task UpdateStarredAsync(long recordId, bool starred)
    {
        if (!IsLoaded)
        {
            return;
        }

        bool starredOnly = StarredTabButton.IsChecked == true;
        TranslationRecord? record = _records.FirstOrDefault(item => item.Id == recordId);
        if (record is null)
        {
            // 刚收藏的这条不在当前列表里（正开着"已收藏"筛选）：重载一次它就出现了。
            if (starred && starredOnly)
            {
                await ReloadAsync();
            }

            return;
        }

        if (starredOnly && !starred)
        {
            _records.Remove(record);
            UpdateEmptyState(starredOnly);
            return;
        }

        record.Starred = starred;
        object? expanded = HistoryGrid.SelectedItem;
        HistoryGrid.Items.Refresh();
        HistoryGrid.SelectedItem = expanded;
    }

    /// <summary>切换到历史工作台。主窗口被第二个实例唤醒时也统一回到这里。</summary>
    public void ShowHistory()
    {
        AllTabButton.IsChecked = true;
        RecordsTabButton.IsChecked = true;
        ShowHistoryContent();
        if (_historyDirty && IsLoaded)
        {
            _ = ReloadWithErrorReportAsync();
        }
    }

    private async Task ReloadWithErrorReportAsync()
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

        // 深度翻译可能还在跑（它一次要几十秒）。不掐掉的话，回调会打在已经关掉的窗口上。
        _deepTranslationView.CancelPending();
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

    /// <summary>再点一次已经展开的卡片就收起来。</summary>
    /// <remarks>
    /// ListBox 自己不做这件事：它按下即选中，已经选中时再按下什么都不发生。
    /// ⚠️ 这是隧道事件，先于卡片里的按钮拿到点击，所以按钮上的点击必须原样放过 ——
    /// 一旦在这里 Handled，收藏 / 复制 / 删除就全哑了。
    /// </remarks>
    private void HistoryGrid_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(source) is { IsSelected: true } item)
        {
            item.IsSelected = false;
            e.Handled = true;
        }
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
        await CopyAsync(sender, useResult: false);

    private async void CopyResult_OnClick(object sender, RoutedEventArgs e) =>
        await CopyAsync(sender, useResult: true);

    private async Task CopyAsync(object sender, bool useResult)
    {
        TranslationRecord? record = RecordFromActionSource(sender);
        if (record is null)
        {
            return;
        }

        try
        {
            await ClipboardService.SetTextAsync(useResult ? record.ResultText : record.SourceText);
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
            using var timeout = new CancellationTokenSource(DatabaseInteractionTimeout);
            await _repository.SetStarredAsync(record.Id, !record.Starred, timeout.Token);

            // 先把内存里的这一条改掉再刷新：万一刷新被推迟，界面也已经是对的了。
            // 不改的话，下一次点同一颗星发出去的还是旧值，数据库会在两个值之间来回翻。
            record.Starred = !record.Starred;

            // TranslationRecord 没有变更通知，星形图标的 DataTrigger 只能靠 Refresh 重算。
            // Refresh 会重建容器，所以把展开的那张卡记下来再放回去 —— 否则用户点一颗星，
            // 正在读的卡片就自己收起来了。
            object? expanded = HistoryGrid.SelectedItem;
            HistoryGrid.Items.Refresh();
            HistoryGrid.SelectedItem = expanded;

            // ⚠️ 只有在「已收藏」筛选下取消收藏，这一条才需要从列表里消失。
            // 「全部」筛选下也整表重载的话，_records.Clear() 会把已经翻出来的几页全丢掉：
            // 滚动位置回到顶部、展开的卡片收起 —— 而用户只是点了一颗星。
            if (StarredTabButton.IsChecked == true)
            {
                await ReloadAsync();
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("历史窗口", "更新收藏状态超时，数据库可能被其它程序占用。");
            MessageBox.Show(this, "数据库正忙，收藏没有保存成功，请稍后再试。", "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            using var timeout = new CancellationTokenSource(DatabaseInteractionTimeout);
            await _repository.DeleteAsync(record.Id, timeout.Token);

            // 就地拿掉这一条，不整表重载：删的往往是翻到很深处的某条，重载会把列表甩回顶部。
            _records.Remove(record);
            UpdateEmptyState(StarredTabButton.IsChecked == true);
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

    private void DeepTranslationTab_OnChecked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ShowDeepTranslationContent();
        }
    }

    private void ShowHistoryContent()
    {
        HistoryContent.Visibility = Visibility.Visible;
        SettingsHost.Visibility = Visibility.Collapsed;
        DeepTranslationHost.Visibility = Visibility.Collapsed;
    }

    private void ShowSettingsContent()
    {
        HistoryContent.Visibility = Visibility.Collapsed;
        SettingsHost.Visibility = Visibility.Visible;
        DeepTranslationHost.Visibility = Visibility.Collapsed;
    }

    private void ShowDeepTranslationContent()
    {
        HistoryContent.Visibility = Visibility.Collapsed;
        SettingsHost.Visibility = Visibility.Collapsed;
        DeepTranslationHost.Visibility = Visibility.Visible;
    }

    private Task ReloadAsync() => LoadNextPageAsync(reset: true);

    private async Task LoadNextPageAsync(bool reset)
    {
        if (_loading)
        {
            // 整表重载不能丢。以前这里连 reset 一起 return，导致：切到"已收藏"页签
            // 列表还是全部记录、点了星标界面毫无变化、翻译完新记录不出现在历史里 ——
            // 全都没有报错，用户只会以为功能坏了。改成记下欠账，本次加载结束后补上。
            if (reset)
            {
                _reloadPending = true;
            }

            return;
        }

        if (!reset && !_hasMore)
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
                _historyDirty = false;
            }

            CancellationToken token = _loadCancellation?.Token ?? CancellationToken.None;
            bool starredOnly = StarredTabButton.IsChecked == true;
            IReadOnlyList<TranslationRecord> page = await _repository.QueryAsync(
                _searchKeyword, starredOnly, _records.Count, PageSize, token);
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

        // 加载期间攒下的重载请求在这里补上。放在 finally 之外，避免递归调用被算进同一个 try。
        if (_reloadPending)
        {
            _reloadPending = false;
            await LoadNextPageAsync(reset: true);
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

        EmptyStateText.Text = _searchKeyword.Length > 0
            ? $"没有匹配「{_searchKeyword}」的记录。换个词试试，原文和译文都会被搜到。"
            : starredOnly
                ? "还没有收藏。点记录右侧的五角星，就能把它放进这里。"
                : $"还没有翻译记录。选中文字后双击 {_settings.TriggerKey} 试试。";
        EmptyStateText.Visibility = Visibility.Visible;
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _searchTimer?.Stop();
        _searchTimer?.Start();
    }

    private async void SearchTimer_OnTick(object? sender, EventArgs e)
    {
        _searchTimer?.Stop();
        string keyword = SearchBox.Text.Trim();
        if (string.Equals(keyword, _searchKeyword, StringComparison.Ordinal))
        {
            return;
        }

        _searchKeyword = keyword;
        try
        {
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            ShowLoadError(exception);
        }
    }

    private void ShowLoadError(Exception exception)
    {
        Logger.Error("历史窗口", "加载翻译历史失败。", exception);
        MessageBox.Show(this, "暂时无法读取翻译历史，请稍后再试。", "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>列表内的直接操作使用按钮自身的数据项，不依赖“先选中再右键”。</summary>
    private static TranslationRecord? RecordFromActionSource(object sender) =>
        (sender as FrameworkElement)?.DataContext as TranslationRecord;

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }
}
