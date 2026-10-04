using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CuinProcessEase.App.Services;
using CuinProcessEase.App.ViewModels;
using CuinProcessEase.Core.Gui;

namespace CuinProcessEase.App.Views;

/// <summary>
/// 主窗口：应用管理器（一行 = 一个应用组）。
/// </summary>
/// <remarks>
/// Phase 9 快捷键（删除必须走与按钮完全相同的确认链）：
/// Ctrl+F 聚焦搜索 / F5 立即刷新 / Delete 结束所选（弹全部既有确认）/ Space 切换暂停刷新。
/// Space 不用 InputBinding 绑定：空格同时是按钮/复选框的点击键，聚焦在控件上时不能拦截。
/// </remarks>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Phase 9：恢复上次窗口位置/尺寸（越界/缺失回退默认）
        SourceInitialized += (_, _) =>
            WindowSettingsService.Apply(this, WindowSettingsService.TryLoad());

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

        Closing += (_, _) => WindowSettingsService.Save(this);
        Closed += (_, _) => _viewModel.Dispose();

        // 快捷键：Ctrl+F / F5 / Delete（命令绑定，输入控件聚焦时同样生效）
        InputBindings.Add(new KeyBinding(_viewModel.FocusSearchCommand, Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_viewModel.RefreshNowCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(_viewModel.KillCommand, Key.Delete, ModifierKeys.None));
        _viewModel.FocusSearchRequested += FocusSearchBox;

        // Space：仅当焦点在窗口自身/列表（非 TextBox、非 Button/CheckBox 等点击控件）时切换暂停
        PreviewKeyDown += OnPreviewSpaceKeyDown;
    }

    /// <summary>空格切换"暂停刷新"（焦点在输入/点击控件上时不拦截其默认行为）。</summary>
    private void OnPreviewSpaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBox or Button or CheckBox or RadioButton or ComboBox
            or ListBoxItem)
        {
            return;
        }

        _viewModel.PauseRefresh = !_viewModel.PauseRefresh;
        PauseCheck.IsChecked = _viewModel.PauseRefresh; // 同步工具栏复选框
        e.Handled = true;
    }

    /// <summary>Ctrl+F 的落点：把键盘焦点移入搜索框并全选。</summary>
    private void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
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
