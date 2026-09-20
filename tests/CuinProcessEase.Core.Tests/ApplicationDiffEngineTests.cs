using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Gui;
using CuinProcessEase.Core.Models;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// 应用 Diff 引擎测试：新增 / 原地更新 / 移除三类操作，稳定列表的核心。
/// </summary>
public sealed class ApplicationDiffEngineTests
{
    private static ProcessSnapshot Snap(int pid, string name, string? path = null) => new()
    {
        Identity = new ProcessIdentity(pid, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(pid)),
        Name = name,
        ExecutablePath = path,
    };

    private static ApplicationGroup Group(string displayName, string mainExe, int pid) => new()
    {
        Identity = new ApplicationIdentity { DisplayName = displayName, MainExecutable = mainExe },
        Processes = [Snap(pid, displayName)],
        RootProcesses = [Snap(pid, displayName)],
    };

    [Fact]
    public void Diff_旧列表为空_全部为新增()
    {
        var newGroups = new List<ApplicationGroup> { Group("Chrome", @"c:\chrome.exe", 1) };

        ApplicationDiffResult diff = ApplicationDiffEngine.ComputeDiff([], newGroups);

        Assert.Single(diff.Added);
        Assert.Empty(diff.Updated);
        Assert.Empty(diff.RemovedKeys);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void Diff_相同应用_识别为原地更新而非增删()
    {
        var oldGroups = new List<ApplicationGroup> { Group("Chrome", @"c:\chrome.exe", 1) };
        var newGroups = new List<ApplicationGroup> { Group("Chrome", @"c:\chrome.exe", 1) };

        ApplicationDiffResult diff = ApplicationDiffEngine.ComputeDiff(oldGroups, newGroups);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.RemovedKeys);
        Assert.Single(diff.Updated);
        // 更新操作携带的是"新快照的组对象"，供 UI 原地刷新行数据
        Assert.Same(newGroups[0], diff.Updated[0]);
        Assert.True(diff.IsEmpty is false);
    }

    [Fact]
    public void Diff_应用退出_输出其稳定Key()
    {
        var oldGroups = new List<ApplicationGroup>
        {
            Group("Chrome", @"c:\chrome.exe", 1),
            Group("Gone", @"c:\gone.exe", 2),
        };
        var newGroups = new List<ApplicationGroup> { Group("Chrome", @"c:\chrome.exe", 1) };

        ApplicationDiffResult diff = ApplicationDiffEngine.ComputeDiff(oldGroups, newGroups);

        Assert.Empty(diff.Added);
        var removed = Assert.Single(diff.RemovedKeys);
        Assert.Equal(ApplicationStableKey.Compute(oldGroups[1]), removed);
    }

    [Fact]
    public void Diff_增删改并存()
    {
        var oldGroups = new List<ApplicationGroup>
        {
            Group("Chrome", @"c:\chrome.exe", 1),
            Group("Gone", @"c:\gone.exe", 2),
        };
        var newGroups = new List<ApplicationGroup>
        {
            Group("Chrome", @"c:\chrome.exe", 1),
            Group("NewApp", @"c:\new.exe", 3),
        };

        ApplicationDiffResult diff = ApplicationDiffEngine.ComputeDiff(oldGroups, newGroups);

        Assert.Equal("NewApp", Assert.Single(diff.Added).Identity.DisplayName);
        Assert.Equal("Chrome", Assert.Single(diff.Updated).Identity.DisplayName);
        Assert.Single(diff.RemovedKeys);
    }

    [Fact]
    public void Diff_两侧都为空_无变化()
    {
        ApplicationDiffResult diff = ApplicationDiffEngine.ComputeDiff([], []);

        Assert.True(diff.IsEmpty);
    }

    // ================= 重复 Key 防护（P5.1） =================

    [Fact]
    public void Diff_新帧出现重复StableKey_抛出异常并指明冲突双方_绝不静默覆盖()
    {
        // 两个无 exe、无 Root 的显示名兜底组必然产生相同 name: Key，
        // 用于构造身份系统故障场景
        var mystery1 = new ApplicationGroup
        {
            Identity = new ApplicationIdentity { DisplayName = "Ghost App" },
            Processes = [],
            RootProcesses = [],
        };
        var mystery2 = new ApplicationGroup
        {
            Identity = new ApplicationIdentity { DisplayName = "Ghost App" },
            Processes = [],
            RootProcesses = [],
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ApplicationDiffEngine.ComputeDiff([], [mystery1, mystery2]));

        Assert.Contains("StableKey 冲突", ex.Message);
        Assert.Contains("Ghost App", ex.Message);
        Assert.Contains("拒绝静默覆盖", ex.Message);
    }

    [Fact]
    public void Diff_旧帧出现重复StableKey_同样抛出()
    {
        var mystery1 = new ApplicationGroup
        {
            Identity = new ApplicationIdentity { DisplayName = "Ghost App" },
            Processes = [],
            RootProcesses = [],
        };
        var mystery2 = new ApplicationGroup
        {
            Identity = new ApplicationIdentity { DisplayName = "Ghost App" },
            Processes = [],
            RootProcesses = [],
        };
        var normal = Group("Chrome", @"c:\chrome.exe", 1);

        Assert.Throws<InvalidOperationException>(
            () => ApplicationDiffEngine.ComputeDiff([mystery1, mystery2], [normal]));
    }

    [Fact]
    public void Diff_两个独立Python同路径_修复后同帧不再冲突()
    {
        // P5.1 回归：修复前两个 python 组会生成相同 exe: Key 并被静默吞掉一个；
        // 修复后 runtime-root 身份保证 Key 唯一，Diff 正常输出两个新增
        var pyA = new ApplicationGroup
        {
            Identity = new ApplicationIdentity
            {
                DisplayName = "Python Task A",
                MainExecutable = @"C:\Python312\python.exe",
            },
            Processes = [Snap(100, "python.exe", @"C:\Python312\python.exe")],
            RootProcesses = [Snap(100, "python.exe", @"C:\Python312\python.exe")],
        };
        var pyB = new ApplicationGroup
        {
            Identity = new ApplicationIdentity
            {
                DisplayName = "Python Task B",
                MainExecutable = @"C:\Python312\python.exe",
            },
            Processes = [Snap(200, "python.exe", @"C:\Python312\python.exe")],
            RootProcesses = [Snap(200, "python.exe", @"C:\Python312\python.exe")],
        };

        ApplicationDiffResult diff = ApplicationDiffEngine.ComputeDiff([], [pyA, pyB]);

        Assert.Equal(2, diff.Added.Count);
        Assert.Empty(diff.Updated);
        Assert.Empty(diff.RemovedKeys);
    }
}
