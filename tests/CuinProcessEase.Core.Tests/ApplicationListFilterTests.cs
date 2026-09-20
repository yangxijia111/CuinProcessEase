using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Gui;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// 筛选与搜索测试：筛选页归属、系统行隐藏规则、应用级搜索。
/// </summary>
public sealed class ApplicationListFilterTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const int CurrentSession = 1;

    private static ProcessSnapshot Snap(int pid, string name, string? path = null, int? session = 1) => new()
    {
        Identity = new ProcessIdentity(pid, BaseTime),
        Name = name,
        ExecutablePath = path,
        SessionId = session,
    };

    private static ApplicationGroup Group(params ProcessSnapshot[] processes) => new()
    {
        Identity = new ApplicationIdentity { DisplayName = "App" },
        Processes = processes,
        RootProcesses = processes[..^0].Take(1).ToList(),
    };

    // ================= 系统行判定 =================

    [Theory]
    [InlineData(SafetyDecision.Blocked, RiskLevel.System, true)]
    [InlineData(SafetyDecision.Blocked, RiskLevel.Protected, true)]
    [InlineData(SafetyDecision.Blocked, RiskLevel.Unknown, false)]
    [InlineData(SafetyDecision.Blocked, RiskLevel.Normal, false)]
    [InlineData(SafetyDecision.Allowed, RiskLevel.System, false)]
    [InlineData(SafetyDecision.Indeterminate, RiskLevel.Unknown, false)]
    [InlineData(SafetyDecision.RequiresElevation, RiskLevel.Elevated, false)]
    public void 系统行判定_仅Blocked且System或Protected(SafetyDecision decision, RiskLevel risk, bool expected)
    {
        Assert.Equal(expected, ApplicationListFilter.IsSystemRow(decision, risk));
    }

    // ================= 筛选页 =================

    [Fact]
    public void 筛选_全部应用页_隐藏系统行但保留Unknown()
    {
        var group = Group(Snap(1, "a.exe"));

        Assert.True(ApplicationListFilter.MatchesTab(
            ApplicationListTab.All, group, isSystemRow: false, null, null, CurrentSession));
        Assert.False(ApplicationListFilter.MatchesTab(
            ApplicationListTab.All, group, isSystemRow: true, null, null, CurrentSession));
    }

    [Fact]
    public void 筛选_系统进程页_只显示系统行()
    {
        var group = Group(Snap(1, "a.exe"));

        Assert.True(ApplicationListFilter.MatchesTab(
            ApplicationListTab.System, group, isSystemRow: true, null, null, CurrentSession));
        Assert.False(ApplicationListFilter.MatchesTab(
            ApplicationListTab.System, group, isSystemRow: false, null, null, CurrentSession));
    }

    [Theory]
    [InlineData(1.0, null, true)]   // CPU 恰好达到阈值
    [InlineData(0.9, null, false)]
    [InlineData(null, 200L * 1024 * 1024, true)]  // 内存恰好达到阈值
    [InlineData(null, 199L * 1024 * 1024, false)]
    [InlineData(null, null, false)] // 首帧 CPU 未知且内存未知 → 不算高资源
    public void 筛选_高资源页_按CPU或内存阈值(double? cpu, long? memory, bool expected)
    {
        var group = Group(Snap(1, "a.exe"));

        Assert.Equal(expected, ApplicationListFilter.MatchesTab(
            ApplicationListTab.HighResource, group, isSystemRow: false, cpu, memory, CurrentSession));
    }

    [Fact]
    public void 筛选_高资源页_系统行即使高资源也不出现()
    {
        var group = Group(Snap(1, "a.exe"));

        Assert.False(ApplicationListFilter.MatchesTab(
            ApplicationListTab.HighResource, group, isSystemRow: true, 50.0, 4L * 1024 * 1024 * 1024, CurrentSession));
    }

    [Fact]
    public void 筛选_后台页_组内无当前会话进程才算后台()
    {
        var foreground = Group(Snap(1, "ui.exe", session: CurrentSession));
        var background = Group(Snap(2, "svc.exe", session: 0));
        var mixed = Group(Snap(3, "mix.exe", session: 0), Snap(4, "ui2.exe", session: CurrentSession));
        var unknownSession = Group(Snap(5, "mystery.exe", session: null));

        Assert.False(ApplicationListFilter.MatchesTab(
            ApplicationListTab.Background, foreground, isSystemRow: false, null, null, CurrentSession));
        Assert.True(ApplicationListFilter.MatchesTab(
            ApplicationListTab.Background, background, isSystemRow: false, null, null, CurrentSession));
        Assert.False(ApplicationListFilter.MatchesTab(
            ApplicationListTab.Background, mixed, isSystemRow: false, null, null, CurrentSession));
        // 会话未知不视为当前会话（服务组 / 权限不足）
        Assert.True(ApplicationListFilter.MatchesTab(
            ApplicationListTab.Background, unknownSession, isSystemRow: false, null, null, CurrentSession));
    }

    // ================= 搜索 =================

    [Fact]
    public void 搜索_空白文本_命中全部()
    {
        var group = Group(Snap(1, "a.exe"));

        Assert.True(ApplicationSearchMatcher.Matches(group, null));
        Assert.True(ApplicationSearchMatcher.Matches(group, "   "));
        Assert.True(ApplicationSearchMatcher.Matches(group, ""));
    }

    [Fact]
    public void 搜索_chrome命中GoogleChrome应用组_而非单个进程()
    {
        // 显示名 "Google Chrome" 本身不含 "chrome" 小写串以外的直配？——含（Chrome）。
        // 关键场景：显示名完全无关时，靠组内进程名命中
        var chrome = new ApplicationGroup
        {
            Identity = new ApplicationIdentity { DisplayName = "Google Chrome" },
            Processes = [Snap(1, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe")],
            RootProcesses = [],
        };

        Assert.True(ApplicationSearchMatcher.Matches(chrome, "chrome"));
        Assert.True(ApplicationSearchMatcher.Matches(chrome, "CHROME")); // 大小写不敏感
        Assert.True(ApplicationSearchMatcher.Matches(chrome, "Google"));
    }

    [Fact]
    public void 搜索_命中组内任一进程名或路径()
    {
        var group = new ApplicationGroup
        {
            Identity = new ApplicationIdentity { DisplayName = "Totally Unrelated Name" },
            Processes =
            [
                Snap(1, "app.exe", @"C:\Apps\app.exe"),
                Snap(2, "crashpad_handler.exe", @"C:\Apps\crashpad_handler.exe"),
            ],
            RootProcesses = [],
        };

        Assert.True(ApplicationSearchMatcher.Matches(group, "crashpad"));
        Assert.True(ApplicationSearchMatcher.Matches(group, @"c:\apps\app.exe"));
        Assert.False(ApplicationSearchMatcher.Matches(group, "firefox"));
    }

    [Fact]
    public void 搜索_命中产品名与公司名()
    {
        var group = new ApplicationGroup
        {
            Identity = new ApplicationIdentity
            {
                DisplayName = "神秘应用",
                ProductName = "SuperProduct",
                CompanyName = "Acme Corp",
            },
            Processes = [Snap(1, "mystery.exe")],
            RootProcesses = [],
        };

        Assert.True(ApplicationSearchMatcher.Matches(group, "superproduct"));
        Assert.True(ApplicationSearchMatcher.Matches(group, "acme"));
    }

    [Fact]
    public void 搜索_索引串包含全部字段()
    {
        var group = new ApplicationGroup
        {
            Identity = new ApplicationIdentity
            {
                DisplayName = "Name",
                ProductName = "Product",
                CompanyName = "Company",
                MainExecutable = @"C:\Apps\main.exe",
            },
            Processes = [Snap(1, "main.exe", @"C:\Apps\main.exe")],
            RootProcesses = [],
        };

        string haystack = ApplicationSearchMatcher.BuildHaystack(group);

        foreach (string expected in new[] { "Name", "Product", "Company", @"C:\Apps\main.exe", "main.exe" })
        {
            Assert.Contains(expected, haystack, StringComparison.OrdinalIgnoreCase);
        }
    }
}
