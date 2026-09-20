using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CuinProcessEase.App.Mvvm;
using CuinProcessEase.Core.Interfaces;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Windows.Services;

namespace CuinProcessEase.App.ViewModels;

/// <summary>
/// 主窗口视图模型：Phase 1 快照引擎的最小验证宿主。
/// 仅提供手动捕获 + 1 秒自动刷新 + 列表展示，不含分组 / 树 / 结束进程等后续 Phase 功能。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IProcessSnapshotService _snapshotService = new ProcessSnapshotService();
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>扫描互斥门：同一时刻最多一次 Capture，拿不到门直接跳过本次刷新（不排队积压）。</summary>
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    private string _statusText = "就绪";

    public MainViewModel()
    {
        CaptureCommand = new AsyncRelayCommand(ct => CaptureAsync());

        // 自动刷新定时器：tick 里仅发起异步捕获，
        // 真正的枚举/查询全部发生在线程池，UI 线程只在收到结果后做一次绑定更新
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _refreshTimer.Tick += async (_, _) => await CaptureAsync();
    }

    public AsyncRelayCommand CaptureCommand { get; }

    /// <summary>状态栏文本：进程数量、扫描耗时、完成时刻。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private bool _autoRefresh;

    /// <summary>是否开启 1 秒自动刷新。</summary>
    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (SetProperty(ref _autoRefresh, value))
            {
                if (value)
                {
                    _refreshTimer.Start();
                }
                else
                {
                    _refreshTimer.Stop();
                }
            }
        }
    }

    /// <summary>进程列表（每帧整体重建，Phase 1 验证宿主足够；稳定列表属于后续 Phase）。</summary>
    public ObservableCollection<ProcessRow> Processes { get; } = new();

    private async Task CaptureAsync()
    {
        // in-flight guard：上一次扫描未完成（慢扫描 > 刷新间隔）时直接放弃本次触发，
        // 请求不排队，避免自动刷新叠加出多个并发 Capture
        if (!await _captureGate.WaitAsync(0))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            ProcessSnapshotCollection snapshot = await _snapshotService.CaptureAsync();

            stopwatch.Stop();

            // 回到 UI 线程更新绑定数据
            Processes.Clear();
            foreach (ProcessSnapshot p in snapshot.Processes)
            {
                Processes.Add(ProcessRow.From(p));
            }

            StatusText = $"共 {snapshot.Count} 个进程 · 扫描耗时 {stopwatch.ElapsedMilliseconds} ms · 完成于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            // 扫描整体失败（极罕见的系统级错误）只更新状态栏，不让 UI 崩溃
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _captureGate.Dispose();
    }
}

/// <summary>ListView 显示行模型：所有字段预先格式化为字符串，避免 XAML 转换器。</summary>
public sealed record ProcessRow(
    int Pid,
    int? Ppid,
    string Name,
    string? Path,
    string? User,
    string Architecture,
    string Elevated,
    string Session,
    string WorkingSet,
    string PrivateMemory,
    string StartTime)
{
    public static ProcessRow From(ProcessSnapshot p) => new(
        p.ProcessId,
        p.ParentProcessId,
        p.Name,
        p.ExecutablePath,
        p.UserName,
        p.Architecture.ToString(),
        p.IsElevated switch
        {
            true => "是",
            false => "否",
            null => "?",
        },
        p.SessionId?.ToString() ?? "?",
        FormatBytes(p.WorkingSetBytes),
        FormatBytes(p.PrivateMemoryBytes),
        p.StartTimeUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "?");

    private static string FormatBytes(long? bytes)
        => bytes switch
        {
            null => "?",
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        };
}
