using System.Collections.ObjectModel;
using System.Windows.Media;
using CuinProcessEase.App.Mvvm;
using CuinProcessEase.App.Services;
using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Gui;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Resources;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Windows.Resources;

namespace CuinProcessEase.App.ViewModels;

/// <summary>
/// 主列表一行 = 一个应用组（UI 行模型，不直接绑定 Core Model）。
/// </summary>
/// <remarks>
/// 行对象跨刷新复用（由 Diff 引擎匹配），UpdateFrom 只改字段值：
/// WPF 绑定与选择状态自然保持，列表绝不 Clear + 全量重建。
/// </remarks>
public sealed class ApplicationRowViewModel : ObservableObject
{
    private readonly Func<int, string?> _commandLineReader;

    public ApplicationRowViewModel(
        ApplicationGroup group,
        IReadOnlyDictionary<int, ProcessResourceSample> samples,
        IReadOnlyDictionary<int, ProcessSafetyResult> safetyByPid,
        ApplicationSafetyResult safety,
        Func<int, string?> commandLineReader,
        IconCacheService iconCache)
    {
        _commandLineReader = commandLineReader;
        StableKey = ApplicationStableKey.Compute(group);
        Processes = new ObservableCollection<ProcessRowViewModel>();
        UpdateFrom(group, samples, safetyByPid, safety);
        EnsureIcon(iconCache);
    }

    /// <summary>稳定标识（主程序路径 → 根进程身份 → 显示名），跨刷新识别同一应用。</summary>
    public string StableKey { get; }

    /// <summary>当前快照的组对象（详情面板数据源）。</summary>
    public ApplicationGroup Group { get; private set; } = null!;

    // ---- 展示字段 ----

    private string _displayName = "";
    public string DisplayName { get => _displayName; private set => SetProperty(ref _displayName, value); }

    private int _processCount;
    public int ProcessCount
    {
        get => _processCount;
        private set
        {
            if (SetProperty(ref _processCount, value))
            {
                OnPropertyChanged(nameof(ProcessCountText));
            }
        }
    }

    public string ProcessCountText => $"{ProcessCount} 个进程";

    private double? _cpuUsage;
    public double? CpuUsage
    {
        get => _cpuUsage;
        private set
        {
            if (SetProperty(ref _cpuUsage, value))
            {
                OnPropertyChanged(nameof(CpuText));
            }
        }
    }

    public string CpuText => CpuUsage is { } cpu ? $"{cpu:F1}%" : "--";

    private long? _memoryUsage;
    public long? MemoryUsage
    {
        get => _memoryUsage;
        private set
        {
            if (SetProperty(ref _memoryUsage, value))
            {
                OnPropertyChanged(nameof(MemoryText));
            }
        }
    }

    public string MemoryText => FormatBytes(MemoryUsage);

    private SafetyDecision _safetyDecision;
    public SafetyDecision SafetyDecision
    {
        get => _safetyDecision;
        private set
        {
            if (SetProperty(ref _safetyDecision, value))
            {
                OnPropertyChanged(nameof(SafetyText));
            }
        }
    }

    private RiskLevel _riskLevel;
    public RiskLevel RiskLevel { get => _riskLevel; private set => SetProperty(ref _riskLevel, value); }

    public string SafetyText => SafetyDecision switch
    {
        SafetyDecision.Allowed => "正常",
        SafetyDecision.RequiresElevation => "需要管理员权限",
        SafetyDecision.Indeterminate => "状态未知",
        SafetyDecision.Blocked => "系统保护",
        _ => "状态未知",
    };

    private GroupingConfidence _confidence;
    public GroupingConfidence Confidence
    {
        get => _confidence;
        private set
        {
            if (SetProperty(ref _confidence, value))
            {
                OnPropertyChanged(nameof(ConfidenceText));
            }
        }
    }

    public string ConfidenceText => Confidence switch
    {
        GroupingConfidence.VeryHigh => "可信度 极高",
        GroupingConfidence.High => "可信度 高",
        GroupingConfidence.Medium => "可信度 中",
        GroupingConfidence.Low => "可信度 低",
        _ => "可信度 未知",
    };

    private bool _isSystemRow;
    public bool IsSystemRow { get => _isSystemRow; private set => SetProperty(ref _isSystemRow, value); }

    /// <summary>搜索索引串（显示名/进程名/路径/产品/公司），随组更新而重建。</summary>
    public string SearchHaystack { get; private set; } = "";

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(IconLetter));
            }
        }
    }

    /// <summary>图标缺失时的首字母占位。</summary>
    public string IconLetter => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.Trim()[..1].ToUpperInvariant();

    /// <summary>组内进程行（详情面板；键为 ProcessIdentity，原地更新）。</summary>
    public ObservableCollection<ProcessRowViewModel> Processes { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>图标提取是否已发起（幂等守卫：只发起一次，绝不每秒重试）。</summary>
    private bool _iconRequested;

    /// <summary>
    /// 用新快照原地刷新行数据（对象引用不变，WPF 绑定自动更新）。
    /// </summary>
    public void UpdateFrom(
        ApplicationGroup group,
        IReadOnlyDictionary<int, ProcessResourceSample> samples,
        IReadOnlyDictionary<int, ProcessSafetyResult> safetyByPid,
        ApplicationSafetyResult safety)
    {
        Group = group;
        DisplayName = group.Identity.DisplayName;
        ProcessCount = group.ProcessCount;
        Confidence = group.Confidence;

        ApplicationResourceAggregator.GroupResource resources =
            ApplicationResourceAggregator.Aggregate(group, samples);
        CpuUsage = resources.CpuPercent;
        MemoryUsage = resources.MemoryBytes;

        SafetyDecision = safety.Decision;
        RiskLevel = safety.OverallRiskLevel;
        IsSystemRow = ApplicationListFilter.IsSystemRow(safety.Decision, safety.OverallRiskLevel);
        SearchHaystack = ApplicationSearchMatcher.BuildHaystack(group);

        SyncProcesses(group, samples, safetyByPid);
    }

    /// <summary>
    /// 确保图标已加载（跨刷新幂等：每行只发起一次提取，结果由缓存回调补上）。
    /// </summary>
    public void EnsureIcon(IconCacheService iconCache)
    {
        if (_iconRequested || Group.Identity.MainExecutable is not { } mainExe)
        {
            return;
        }

        _iconRequested = true;

        if (iconCache.IsCached(mainExe))
        {
            if (iconCache.TryGet(mainExe, out ImageSource? cached))
            {
                Icon = cached;
            }
            return;
        }

        iconCache.BeginGet(mainExe, icon => Icon = icon);
    }

    /// <summary>进程行集合按键（PID + StartTime）同步：更新原行 / 新增 / 移除。</summary>
    private void SyncProcesses(
        ApplicationGroup group,
        IReadOnlyDictionary<int, ProcessResourceSample> samples,
        IReadOnlyDictionary<int, ProcessSafetyResult> safetyByPid)
    {
        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            ProcessRowViewModel existing = Processes[i];
            ProcessSnapshot? match = group.Processes.FirstOrDefault(p => p.Identity == existing.Identity);
            if (match is null)
            {
                Processes.RemoveAt(i);
            }
            else
            {
                existing.UpdateFrom(match, samples.GetValueOrDefault(match.ProcessId), safetyByPid.GetValueOrDefault(match.ProcessId));
            }
        }

        foreach (ProcessSnapshot process in group.Processes)
        {
            if (Processes.All(p => p.Identity != process.Identity))
            {
                // 新建的行在构造时已应用当前帧数据，无需二次补写
                Processes.Add(new ProcessRowViewModel(
                    process,
                    samples.GetValueOrDefault(process.ProcessId),
                    safetyByPid.GetValueOrDefault(process.ProcessId),
                    _commandLineReader));
            }
        }
    }

    private void TryLoadIcon(string mainExecutable, IconCacheService iconCache)
    {
        if (iconCache.IsCached(mainExecutable))
        {
            iconCache.TryGet(mainExecutable, out ImageSource? icon);
            Icon = icon;
            return;
        }

        iconCache.BeginGet(mainExecutable, icon =>
        {
            // 回调发生在 UI 线程；行可能已被移除，但设置一个无人绑定的属性无副作用
            Icon = icon;
        });
    }

    private static string FormatBytes(long? bytes) => bytes switch
    {
        null => "--",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
