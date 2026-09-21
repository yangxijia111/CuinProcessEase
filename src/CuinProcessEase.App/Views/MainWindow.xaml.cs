using System.Windows;
using System.Windows.Controls;
using CuinProcessEase.App.ViewModels;
using CuinProcessEase.Core.Gui;

namespace CuinProcessEase.App.Views;

/// <summary>
/// 主窗口：应用管理器（一行 = 一个应用组）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // 工具栏胶水：筛选页 / 锁定 / 暂停（纯 UI 状态转发，不含业务逻辑）
        TabAll.Checked += OnTabChecked;
        TabHighResource.Checked += OnTabChecked;
        TabBackground.Checked += OnTabChecked;
        TabSystem.Checked += OnTabChecked;
        LockListCheck.Checked += (_, _) => _viewModel.LockList = true;
        LockListCheck.Unchecked += (_, _) => _viewModel.LockList = false;
        PauseCheck.Checked += (_, _) => _viewModel.PauseRefresh = true;
        PauseCheck.Unchecked += (_, _) => _viewModel.PauseRefresh = false;

        // 终止操作确认对话框（引擎的安全门禁在 ViewModel/Engine 内，不在此处）
        _viewModel.Confirm = (message, title) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;
        _viewModel.Alert = (message, title) =>
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }

        _viewModel.SelectedTab = tag switch
        {
            "HighResource" => ApplicationListTab.HighResource,
            "Background" => ApplicationListTab.Background,
            "System" => ApplicationListTab.System,
            _ => ApplicationListTab.All,
        };
    }
}
