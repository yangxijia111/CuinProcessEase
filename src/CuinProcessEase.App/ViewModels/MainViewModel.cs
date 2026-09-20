using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CuinProcessEase.App.Mvvm;
using CuinProcessEase.App.Services;
using CuinProcessEase.App.Themes;
using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Gui;
using CuinProcessEase.Core.Interfaces;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Resources;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Windows.ProcessApi;
using CuinProcessEase.Windows.Resources;
using CuinProcessEase.Windows.Safety;
using CuinProcessEase.Windows.Services;

namespace CuinProcessEase.App.ViewModels;

/// <summary>
/// 主视图模型：把 Snapshot → Grouping → Safety → 资源采样 整合成"一行一个应用"的应用管理器。
/// </summary>
/// <remarks>
/// 刷新管线（Phase 5）：定时器 → 后台采集/分组/安全/资源 → Diff → UI 线程轻量应用。
/// - 全部扫描与计算在线程池执行；UI 线程只做行数据原地更新与少量 Move；
/// - PeriodicTimer 慢消费自动丢弃刻度 + busy 门闩：刷新永不堆积；
/// - 列表稳定更新：Diff 匹配既有行原地对齐，选择/展开/顺序不因每秒刷新丢失。
/// </remarks>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly IProcessSnapshotService _snapshotService = new ProcessSnapshotService();
    private readonly CachedProcessSafetyService _safetyService = new(new ProcessSafetyService());
    private readonly ProcessResourceSampler _sampler = new();
    private readonly SystemResourceMonitor _systemMonitor = new();
    private readonly ProcessCommandLineProvider _commandLineProvider = new();
    private readonly Dispatcher _dispatcher;
    private readonly IconCacheService _iconCache;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _refreshLoop;

    /// <summary>全部应用行（含默认隐藏的系统行），Key = 稳定标识。</summary>
    private readonly Dictionary<string, ApplicationRowViewModel> _rowsByKey = new(StringComparer.Ordinal);
    private List<ApplicationGroup> _lastGroups = [];

    /// <summary>应用自身所在的交互会话 ID（"后台运行"页判定基准）。</summary>
    private readonly int _currentSessionId;

    /// <summary>搜索输入防抖（避免每个字符触发一次全列表重算）。</summary>
    private readonly DispatcherTimer _searchDebounce;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _iconCache = new IconCacheService(_dispatcher);
        _currentSessionId = Process.GetCurrentProcess().SessionId;

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            SyncVisible();
        };

        ToggleThemeCommand = new RelayCommand(_ => IsDarkTheme = !IsDarkTheme);

        // 刷新循环：立即执行首轮，之后每秒一轮（在线程池运行）
        _refreshLoop = Task.Run(() => RunRefreshLoopAsync(_cts.Token));
    }

    // ================= 列表 =================

    /// <summary>当前筛选页可见的应用行（ListBox 数据源；仅增量增删与 Move，绝不 Clear 重建）。</summary>
    public ObservableCollection<ApplicationRowViewModel> Applications { get; } = new();

    private ApplicationRowViewModel? _selectedRow;
    public ApplicationRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedRow is not null;

    // ================= 工具栏状态 =================

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                // 实时搜索 + 防抖：停顿 200ms 后应用
                _searchDebounce.Stop();
                _searchDebounce.Start();
            }
        }
    }

    private ApplicationListTab _selectedTab = ApplicationListTab.All;
    public ApplicationListTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                SyncVisible();
            }
        }
    }

    private ApplicationSortMode _sortMode = ApplicationSortMode.Name;
    public ApplicationSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
            {
                SyncVisible();
            }
        }
    }

    /// <summary>ComboBox 绑定（0 名称 / 1 CPU / 2 内存 / 3 进程数）。</summary>
    public int SelectedSortIndex
    {
        get => (int)SortMode;
        set => SortMode = (ApplicationSortMode)value;
    }

    private bool _lockList;
    public bool LockList
    {
        get => _lockList;
        set
        {
            if (SetProperty(ref _lockList, value))
            {
                SyncVisible();
            }
        }
    }

    private bool _pauseRefresh;
    public bool PauseRefresh
    {
        get => _pauseRefresh;
        set => SetProperty(ref _pauseRefresh, value);
    }

    private bool _isDarkTheme = true;
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                ThemeManager.Apply(value);
            }
        }
    }

    public RelayCommand ToggleThemeCommand { get; }

    // ================= Overview =================

    private string _overviewCpu = "--";
    public string OverviewCpu { get => _overviewCpu; private set => SetProperty(ref _overviewCpu, value); }

    private string _overviewMemory = "--";
    public string OverviewMemory { get => _overviewMemory; private set => SetProperty(ref _overviewMemory, value); }

    private string _overviewApplications = "--";
    public string OverviewApplications { get => _overviewApplications; private set => SetProperty(ref _overviewApplications, value); }

    private string _overviewProcesses = "--";
    public string OverviewProcesses { get => _overviewProcesses; private set => SetProperty(ref _overviewProcesses, value); }

    private string _statusText = "正在加载…";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    // ================= 刷新管线 =================

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 首轮立即执行；单轮失败只报状态，循环继续（下一刻度重试）
            await RunTickAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (PauseRefresh)
                {
                    continue; // 暂停：Snapshot / CPU / RAM / 分组全部停更
                }

                await RunTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            PostStatus($"刷新循环异常停止：{ex.Message}");
        }
    }

    /// <summary>单次刷新：防重入 + 单轮异常不终止循环（PeriodicTimer 慢消费自动合并刻度，不堆积）。</summary>
    private async Task RunTickAsync(CancellationToken cancellationToken)
    {
        // 防堆积：上一轮未完成时直接放弃本轮
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await RunOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PostStatus($"刷新失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>刷新防重入门闩（0 = 空闲）。</summary>
    private int _busy;

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        ProcessSnapshotCollection snapshot = await _snapshotService
            .CaptureAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ApplicationGroup> groups = ApplicationGroupingEngine.Group(snapshot);

        // Safety（带缓存）：新身份才真正调用 Safety API
        _safetyService.RetainLive(snapshot.Processes.Select(p => p.Identity));

        var safetyByGroup = new Dictionary<string, ApplicationSafetyResult>(groups.Count, StringComparer.Ordinal);
        var safetyByPid = new Dictionary<int, ProcessSafetyResult>(snapshot.Count);
        foreach (ApplicationGroup group in groups)
        {
            ApplicationSafetyResult groupSafety = _safetyService.Assess(group);
            string key = ApplicationStableKey.Compute(group);
            if (!safetyByGroup.TryAdd(key, groupSafety))
            {
                // 身份系统故障：同帧重复 StableKey，绝不静默覆盖（正常 StableKey 规则下不会发生）
                throw new InvalidOperationException(
                    $"StableKey 冲突：'{key}' 同时属于 '{safetyByGroup[key].DisplayName}' 与 " +
                    $"'{groupSafety.DisplayName}'。");
            }

            foreach (ProcessSafetyResult member in groupSafety.MemberResults)
            {
                safetyByPid[member.Identity.ProcessId] = member;
            }
        }

        IReadOnlyDictionary<int, ProcessResourceSample> resources = _sampler.Sample(snapshot);
        SystemResourceSample system = _systemMonitor.Sample();

        stopwatch.Stop();
        long scanMs = stopwatch.ElapsedMilliseconds;

        // UI 线程只做轻量的行更新
        await _dispatcher.InvokeAsync(() => ApplyToUi(
            snapshot, groups, safetyByGroup, safetyByPid, resources, system, scanMs));
    }

    /// <summary>UI 线程：Diff 应用 + 可见性同步 + Overview。</summary>
    private void ApplyToUi(
        ProcessSnapshotCollection snapshot,
        IReadOnlyList<ApplicationGroup> groups,
        IReadOnlyDictionary<string, ApplicationSafetyResult> safetyByGroup,
        IReadOnlyDictionary<int, ProcessSafetyResult> safetyByPid,
        IReadOnlyDictionary<int, ProcessResourceSample> resources,
        SystemResourceSample system,
        long scanMs)
    {
        ApplicationDiffResult diff = ApplicationDiffEngine.ComputeDiff(_lastGroups, groups);

        foreach (ApplicationGroup added in diff.Added)
        {
            string key = ApplicationStableKey.Compute(added);
            var row = new ApplicationRowViewModel(
                added, resources, safetyByPid,
                safetyByGroup[key],
                _commandLineProvider.Capture,
                _iconCache);
            if (!_rowsByKey.TryAdd(key, row))
            {
                // Diff 已保证 Added 的 Key 唯一；此防护保证未来回归立刻显式失败
                throw new InvalidOperationException($"StableKey 冲突：'{key}' 试图重复添加行。");
            }
        }

        foreach (ApplicationGroup updated in diff.Updated)
        {
            string key = ApplicationStableKey.Compute(updated);
            if (_rowsByKey.TryGetValue(key, out ApplicationRowViewModel? row))
            {
                row.UpdateFrom(updated, resources, safetyByPid, safetyByGroup[key]);
                row.EnsureIcon(_iconCache);
            }
        }

        foreach (string removedKey in diff.RemovedKeys)
        {
            if (_rowsByKey.TryGetValue(removedKey, out ApplicationRowViewModel? row))
            {
                if (ReferenceEquals(SelectedRow, row))
                {
                    SelectedRow = null;
                }

                _rowsByKey.Remove(removedKey);
            }
        }

        _lastGroups = groups.ToList();

        SyncVisible();

        OverviewCpu = system.CpuPercent is { } cpu ? $"{cpu:F0}%" : "--";
        OverviewMemory = $"{system.MemoryPercent:F0}%";
        OverviewApplications = _rowsByKey.Count.ToString();
        OverviewProcesses = snapshot.Count.ToString();
        StatusText = $"应用 {_rowsByKey.Count} · 进程 {snapshot.Count} · 扫描 {scanMs} ms · 完成于 {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>
    /// 可见列表同步：筛选页 + 搜索 → 目标集合；锁定列表只追加不重排，否则按排序 Move 对齐。
    /// 全程无 Clear / 全量重建，行对象引用保持，选择与展开状态不丢。
    /// </summary>
    private void SyncVisible()
    {
        string searchText = SearchText;
        bool hasSearch = !string.IsNullOrWhiteSpace(searchText);

        var desired = new List<ApplicationRowViewModel>(_rowsByKey.Count);
        foreach (ApplicationRowViewModel row in _rowsByKey.Values)
        {
            if (!ApplicationListFilter.MatchesTab(
                    SelectedTab, row.Group, row.IsSystemRow,
                    row.CpuUsage, row.MemoryUsage, _currentSessionId))
            {
                continue;
            }

            if (hasSearch && !row.SearchHaystack.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            desired.Add(row);
        }

        Comparison<ApplicationSortKey> comparison = ApplicationListSorter.GetComparison(SortMode);
        desired.Sort((a, b) => comparison(ToSortKey(a), ToSortKey(b)));

        SyncCollection(Applications, desired, LockList);
    }

    private static ApplicationSortKey ToSortKey(ApplicationRowViewModel row) => new(
        row.DisplayName,
        row.CpuUsage,
        row.MemoryUsage,
        row.ProcessCount,
        row.StableKey);

    /// <summary>对可见集合做最小增量同步：移除多余 → 追加新增（锁定时）/ 按目标顺序 Move 对齐。</summary>
    private static void SyncCollection(
        ObservableCollection<ApplicationRowViewModel> collection,
        List<ApplicationRowViewModel> desired,
        bool lockOrder)
    {
        var desiredSet = new HashSet<ApplicationRowViewModel>(desired);
        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }

        if (lockOrder)
        {
            // 锁定列表：新应用追加到末尾，绝不改变现有顺序
            foreach (ApplicationRowViewModel row in desired)
            {
                if (!collection.Contains(row))
                {
                    collection.Add(row);
                }
            }

            return;
        }

        // 按目标顺序对齐：缺失的行插入目标位，已在的行只在不符位时 Move
        //（行对象不变，无闪烁；绝不 Clear 重建）
        int target = 0;
        foreach (ApplicationRowViewModel row in desired)
        {
            int current = collection.IndexOf(row);
            if (current == target)
            {
                // 已在目标位
            }
            else if (current >= 0)
            {
                collection.Move(current, target);
            }
            else
            {
                collection.Insert(target, row);
            }

            target++;
        }
    }

    private void PostStatus(string message)
    {
        _dispatcher.BeginInvoke(() => StatusText = message);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _searchDebounce.Stop();
    }
}
