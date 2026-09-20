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
}
