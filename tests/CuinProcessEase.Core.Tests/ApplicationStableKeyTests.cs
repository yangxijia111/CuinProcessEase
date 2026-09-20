using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Gui;
using CuinProcessEase.Core.Models;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// 应用稳定 Key 测试：跨刷新识别同一应用的根基。
/// </summary>
public sealed class ApplicationStableKeyTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProcessSnapshot Snap(int pid, string name, string? path = null, int? session = 1) => new()
    {
        Identity = new ProcessIdentity(pid, BaseTime.AddSeconds(pid)),
        Name = name,
        ExecutablePath = path,
        SessionId = session,
    };

    private static ApplicationGroup Group(
        string displayName,
        string? mainExe = null,
        ProcessSnapshot[]? processes = null,
        ProcessSnapshot[]? roots = null)
    {
        var members = processes ?? [Snap(100, displayName)];
        return new ApplicationGroup
        {
            Identity = new ApplicationIdentity
            {
                DisplayName = displayName,
                MainExecutable = mainExe,
            },
            Processes = members,
            RootProcesses = roots ?? members[..1],
        };
    }

    [Fact]
    public void 稳定Key_主程序路径优先_且大小写归一()
    {
        var group = Group("Google Chrome", mainExe: @"C:\Program Files\Chrome\chrome.exe");

        string key = ApplicationStableKey.Compute(group);

        Assert.StartsWith(ApplicationStableKey.ExePrefix, key);
        Assert.Equal(ApplicationStableKey.ExePrefix + @"c:\program files\chrome\chrome.exe", key);
    }

    [Fact]
    public void 稳定Key_同内容不同实例_跨刷新得到相同Key()
    {
        var g1 = Group("Google Chrome", mainExe: @"C:\Program Files\Chrome\chrome.exe");
        var g2 = Group("Google Chrome", mainExe: @"C:\Program Files\Chrome\chrome.exe");

        // 两次快照构建的对象完全不同，但 Key 必须一致（稳定列表的根基）
        Assert.Equal(ApplicationStableKey.Compute(g1), ApplicationStableKey.Compute(g2));
    }

    [Fact]
    public void 稳定Key_无主程序时回退根进程身份_含StartTime防PID重用()
    {
        var root = Snap(42, "multi.exe");
        var group = Group("Multi Root App", processes: [root, Snap(43, "helper.exe")], roots: [root]);

        string key = ApplicationStableKey.Compute(group);

        Assert.Equal($"{ApplicationStableKey.RootPrefix}42:{BaseTime.AddSeconds(42).Ticks}", key);
    }

    [Fact]
    public void 稳定Key_多Root时取最小PID_与顺序无关()
    {
        var low = Snap(7, "a.exe");
        var high = Snap(9, "b.exe");
        var byOrder = Group("App", processes: [high, low], roots: [high, low]);
        var byReverse = Group("App", processes: [low, high], roots: [low, high]);

        Assert.Equal(ApplicationStableKey.Compute(byOrder), ApplicationStableKey.Compute(byReverse));
        Assert.Contains(":7:", ApplicationStableKey.Compute(byOrder));
    }

    [Fact]
    public void 稳定Key_主程序优先于根进程()
    {
        var root = Snap(42, "app.exe", path: @"C:\Apps\app.exe");
        var group = Group("App", mainExe: @"C:\Apps\app.exe", processes: [root], roots: [root]);

        Assert.StartsWith(ApplicationStableKey.ExePrefix, ApplicationStableKey.Compute(group));
    }

    [Fact]
    public void 稳定Key_无主程序无Root_回退显示名()
    {
        var group = Group("Mystery App", processes: [], roots: []);

        string key = ApplicationStableKey.Compute(group);
        Assert.Equal(ApplicationStableKey.NamePrefix + "mystery app", key);
    }

    [Fact]
    public void 稳定Key_不同应用得到不同Key()
    {
        string k1 = ApplicationStableKey.Compute(Group("Chrome", mainExe: @"C:\A\chrome.exe"));
        string k2 = ApplicationStableKey.Compute(Group("Firefox", mainExe: @"C:\B\firefox.exe"));
        string k3 = ApplicationStableKey.Compute(Group("Chrome", mainExe: @"C:\C\chrome.exe"));

        Assert.NotEqual(k1, k2);
        Assert.NotEqual(k1, k3); // 同名但路径不同：绝不允许同 Key
    }
}
