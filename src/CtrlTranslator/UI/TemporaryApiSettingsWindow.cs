using System.Windows;
using System.Windows.Controls;
using CtrlTranslator.Diagnostics;
using CtrlTranslator.Settings;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace CtrlTranslator.UI;

/// <summary>
/// M2 临时翻译服务入口；M3 的正式设置页完成后会移除。
/// </summary>
internal sealed class TemporaryApiSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly System.Windows.Controls.TextBox _baseUrlBox;
    private readonly PasswordBox _apiKeyBox;
    private readonly System.Windows.Controls.TextBox _modelBox;

    public TemporaryApiSettingsWindow(AppSettings settings, SettingsStore settingsStore)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        Title = "填写翻译服务";
        Width = 500;
        Height = 270;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;

        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int index = 0; index < 5; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        _baseUrlBox = new System.Windows.Controls.TextBox { Text = settings.BaseUrl, Margin = new Thickness(10, 5, 0, 5) };
        _apiKeyBox = new PasswordBox { Password = settings.ApiKey, Margin = new Thickness(10, 5, 0, 5) };
        _modelBox = new System.Windows.Controls.TextBox { Text = settings.Model, Margin = new Thickness(10, 5, 0, 5) };
        AddRow(grid, 0, "服务地址", _baseUrlBox);
        AddRow(grid, 1, "API Key", _apiKeyBox);
        AddRow(grid, 2, "模型名", _modelBox);

        var hint = new TextBlock
        {
            Text = "API Key 会用当前 Windows 账户加密保存，不会以明文写入配置文件。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 12)
        };
        Grid.SetRow(hint, 3);
        Grid.SetColumnSpan(hint, 2);
        grid.Children.Add(hint);

        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var saveButton = new WpfButton { Content = "保存", Width = 88, IsDefault = true };
        saveButton.Click += SaveButton_OnClick;
        var cancelButton = new WpfButton { Content = "取消", Width = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);
        Grid.SetRow(buttons, 4);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
    }

    private static void AddRow(Grid grid, int row, string label, System.Windows.Controls.Control control)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 5)
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(labelBlock);
        grid.Children.Add(control);
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string baseUrl = _baseUrlBox.Text.Trim();
            string apiKey = _apiKeyBox.Password.Trim();
            string model = _modelBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl)
                || string.IsNullOrWhiteSpace(apiKey)
                || string.IsNullOrWhiteSpace(model))
            {
                WpfMessageBox.Show(
                    "服务地址、API Key 和模型名都需要填写。",
                    "CtrlTranslator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _settings.BaseUrl = baseUrl;
            _settings.ApiKey = apiKey;
            _settings.Model = model;
            if (!_settingsStore.TrySave(_settings, out string? errorMessage))
            {
                WpfMessageBox.Show(
                    errorMessage ?? "设置保存失败，请稍后重试。",
                    "CtrlTranslator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            Logger.SetApiKey(apiKey);
            Logger.Info("设置", "临时翻译服务配置已保存。");
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "保存临时翻译服务配置失败。", exception);
            WpfMessageBox.Show(
                "设置没有保存成功，详情已记录到日志。",
                "CtrlTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
