using System.Windows;
using CuinProcessEase.App.ViewModels;

namespace CuinProcessEase.App.Views;

/// <summary>
/// 主窗口：承载 Phase 1 快照验证界面。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.CaptureCommand.ExecuteAsync();
        Closed += (_, _) => _viewModel.Dispose();
    }
}
