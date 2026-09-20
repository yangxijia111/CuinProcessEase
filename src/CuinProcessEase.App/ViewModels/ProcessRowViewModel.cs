using System.Windows.Input;
using System.Windows.Media;
using CuinProcessEase.App.Mvvm;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Resources;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Windows.Native;

namespace CuinProcessEase.App.ViewModels;

/// <summary>
/// 详情面板中的单进程行模型：基础信息每秒原地更新，
/// 高级信息（含命令行）仅在用户展开时按需读取。
/// </summary>
public sealed class ProcessRowViewModel : ObservableObject
{
    private readonly Func<int, string?> _commandLineReader;

    public ProcessRowViewModel(
        ProcessSnapshot process,
        ProcessResourceSample sample,
        ProcessSafetyResult? safety,
        Func<int, string?> commandLineReader)
    {
        _commandLineReader = commandLineReader;
        Identity = process.Identity;
        Pid = process.ProcessId;
        Name = process.Name;
        LoadCommandLineCommand = new AsyncRelayCommand(_ => LoadCommandLineAsync());
        ApplyFrom(process, sample, safety);
    }

    public ProcessIdentity Identity { get; }

    public int Pid { get; }

    public string Name { get; private set; }

    public AsyncRelayCommand LoadCommandLineCommand { get; }

    // ---- 基础字段（每秒更新，全部预先格式化，XAML 零转换器） ----

    private double? _cpu;
    public double? Cpu
    {
        get => _cpu;
        private set
        {
            if (SetProperty(ref _cpu, value))
            {
                OnPropertyChanged(nameof(CpuText));
            }
        }
    }

    public string CpuText => Cpu is { } cpu ? $"{cpu:F1}%" : "--";

    private long? _memory;
    public long? Memory
    {
        get => _memory;
        private set
        {
            if (SetProperty(ref _memory, value))
            {
                OnPropertyChanged(nameof(MemoryText));
            }
        }
    }

    public string MemoryText => FormatBytes(Memory);

    private string _path = "未知";
    public string Path { get => _path; private set => SetProperty(ref _path, value); }

    private string _user = "未知";
    public string User { get => _user; private set => SetProperty(ref _user, value); }

    private string _startTime = "未知";
    public string StartTime { get => _startTime; private set => SetProperty(ref _startTime, value); }

    private string _architecture = "未知";
    public string Architecture { get => _architecture; private set => SetProperty(ref _architecture, value); }

    private string _elevated = "未知";
    public string Elevated { get => _elevated; private set => SetProperty(ref _elevated, value); }

    private string _session = "?";
    public string Session { get => _session; private set => SetProperty(ref _session, value); }

    private int? _ppid;
    public int? Ppid { get => _ppid; private set => SetProperty(ref _ppid, value); }

    public string PpidText => Ppid?.ToString() ?? "未知";

    // ---- 安全字段 ----

    private string _safetyText = "状态未知";
    public string SafetyText { get => _safetyText; private set => SetProperty(ref _safetyText, value); }

    private string _isCriticalText = "未知";
    public string IsCriticalText { get => _isCriticalText; private set => SetProperty(ref _isCriticalText, value); }

    private string _protectionLevelText = "未知";
    public string ProtectionLevelText { get => _protectionLevelText; private set => SetProperty(ref _protectionLevelText, value); }

    // ---- 高级展开 / 命令行 ----

    private bool _isAdvancedExpanded;
    public bool IsAdvancedExpanded
    {
        get => _isAdvancedExpanded;
        set => SetProperty(ref _isAdvancedExpanded, value);
    }

    private bool _isCommandLineLoaded;
    private bool _isCommandLineLoading;
    private string? _commandLine;

    public string CommandLineText
    {
        get
        {
            if (_isCommandLineLoading)
            {
                return "读取中…";
            }

            return _isCommandLineLoaded ? (_commandLine ?? "（读取失败或不可用）") : string.Empty;
        }
    }

    public bool IsLoadCommandLineEnabled => !_isCommandLineLoaded && !_isCommandLineLoading;

    private async Task LoadCommandLineAsync()
    {
        if (_isCommandLineLoaded || _isCommandLineLoading)
        {
            return;
        }

        _isCommandLineLoading = true;
        OnPropertyChanged(nameof(IsLoadCommandLineEnabled));
        OnPropertyChanged(nameof(CommandLineText));

        try
        {
            string? line = await Task.Run(() => _commandLineReader(Pid)).ConfigureAwait(true);
            _commandLine = line;
            _isCommandLineLoaded = true;
        }
        finally
        {
            _isCommandLineLoading = false;
            OnPropertyChanged(nameof(IsLoadCommandLineEnabled));
            OnPropertyChanged(nameof(CommandLineText));
        }
    }

    /// <summary>原地更新：对象引用保持不变，列表不重建，展开/命令行状态自然保留。</summary>
    public void UpdateFrom(
        ProcessSnapshot process,
        ProcessResourceSample sample,
        ProcessSafetyResult? safety)
    {
        ApplyFrom(process, sample, safety);
    }

    private void ApplyFrom(ProcessSnapshot process, ProcessResourceSample sample, ProcessSafetyResult? safety)
    {
        Cpu = sample.CpuPercent;
        Memory = sample.WorkingSetBytes;
        Name = process.Name;
        Path = process.ExecutablePath ?? "未知";
        User = process.UserName ?? "未知";
        StartTime = process.StartTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
        Architecture = process.Architecture switch
        {
            ProcessArchitecture.X86 => "x86",
            ProcessArchitecture.X64 => "x64",
            ProcessArchitecture.Arm64 => "ARM64",
            _ => "未知",
        };
        Elevated = process.IsElevated switch
        {
            true => "是",
            false => "否",
            null => "未知",
        };
        Session = process.SessionId?.ToString() ?? "?";
        Ppid = process.ParentProcessId;

        if (safety is { } s)
        {
            SafetyText = DescribeSafety(s.Decision);
            IsCriticalText = s.IsCritical switch
            {
                true => "是（结束将导致系统崩溃）",
                false => "否",
                null => "未知",
            };
            ProtectionLevelText = DescribeProtectionLevel(s.ProtectionLevel);
        }

        OnPropertyChanged(nameof(Name));
    }

    private static string DescribeSafety(SafetyDecision decision) => decision switch
    {
        SafetyDecision.Allowed => "正常",
        SafetyDecision.RequiresElevation => "需要管理员权限",
        SafetyDecision.Indeterminate => "状态未知",
        SafetyDecision.Blocked => "系统保护",
        _ => "状态未知",
    };

    private static string DescribeProtectionLevel(uint? level)
    {
        if (level is null)
        {
            return "未知（查询失败）";
        }

        return level.Value switch
        {
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_NONE => "无保护（NONE, 0xFFFFFFFE）",
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_WINTCB_LIGHT => "PPL WinTcb-Light（0）",
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_WINDOWS => "PP Windows（1）",
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_WINDOWS_LIGHT => "PPL Windows-Light（2）",
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_ANTIMALWARE_LIGHT => "PPL Antimalware-Light（3）",
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_LSA_LIGHT => "PPL LSA-Light（4）",
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_WINTCB => "PP WinTcb（5）",
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_CODEGEN_LIGHT => "PPL CodeGen-Light（6）",
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_AUTHENTICODE => "PP Authenticode（7）",
            (uint)PROTECTION_LEVEL.PROTECTION_LEVEL_PPL_APP => "PPL App（8）",
            _ => $"原始值 {level}（0x{level:X8}）",
        };
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
