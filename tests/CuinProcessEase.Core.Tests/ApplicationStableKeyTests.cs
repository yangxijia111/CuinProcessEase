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

        Assert.Equal($"{ApplicationStableKey.RootPrefix}|42:{BaseTime.AddSeconds(42).Ticks}", key);
    }

    [Fact]
    public void 稳定Key_多Root时_按PID与StartTime排序确定性_与顺序无关()
    {
        var low = Snap(7, "a.exe");
        var high = Snap(9, "b.exe");
        var byOrder = Group("App", processes: [high, low], roots: [high, low]);
        var byReverse = Group("App", processes: [low, high], roots: [low, high]);

        string key = ApplicationStableKey.Compute(byOrder);

        Assert.Equal(ApplicationStableKey.Compute(byReverse), key);
        Assert.Contains("|7:", key);
        Assert.Contains("|9:", key);
        // 排序确定性：低 PID 的身份在前
        Assert.True(key.IndexOf("|7:", StringComparison.Ordinal) < key.IndexOf("|9:", StringComparison.Ordinal));
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

    // ================= 歧义运行时（P5.1 身份加固） =================

    [Theory]
    [InlineData("python.exe")]
    [InlineData("node.exe")]
    [InlineData("java.exe")]
    [InlineData("dotnet.exe")]
    [InlineData("cmd.exe")]
    public void 歧义运行时_两个独立任务同exe路径_绝不允许同Key(string runtimeName)
    {
        // GroupingEngine 故意不合并同路径歧义运行时；GUI 身份必须能表示两个任务
        var taskA = Group("Task A", mainExe: @"C:\Python312\python.exe",
            processes: [Snap(100, runtimeName, @"C:\Python312\python.exe")],
            roots: [Snap(100, runtimeName, @"C:\Python312\python.exe")]);
        var taskB = Group("Task B", mainExe: @"C:\Python312\python.exe",
            processes: [Snap(200, runtimeName, @"C:\Python312\python.exe")],
            roots: [Snap(200, runtimeName, @"C:\Python312\python.exe")]);

        string keyA = ApplicationStableKey.Compute(taskA);
        string keyB = ApplicationStableKey.Compute(taskB);

        Assert.NotEqual(keyA, keyB);
        Assert.StartsWith(ApplicationStableKey.RuntimeRootPrefix, keyA);
        Assert.StartsWith(ApplicationStableKey.RuntimeRootPrefix, keyB);
    }

    [Fact]
    public void 歧义运行时_key包含归一化路径与根身份PID加StartTime()
    {
        var root = Snap(100, "python.exe", path: @"C:\Python312\python.exe");
        var group = Group("Python Task", mainExe: @"C:\Python312\python.exe",
            processes: [root], roots: [root]);

        string key = ApplicationStableKey.Compute(group);

        Assert.Equal(
            $"{ApplicationStableKey.RuntimeRootPrefix}c:\\python312\\python.exe|100:{BaseTime.AddSeconds(100).Ticks}",
            key);
    }

    [Fact]
    public void 歧义运行时_无StartTime的根_仍产生确定性key()
    {
        var root = new ProcessSnapshot
        {
            Identity = new ProcessIdentity(100, null),
            Name = "python.exe",
            ExecutablePath = @"C:\Python312\python.exe",
        };
        var group = Group("Python Task", mainExe: @"C:\Python312\python.exe",
            processes: [root], roots: [root]);

        string key = ApplicationStableKey.Compute(group);

        Assert.EndsWith("|100:-1", key);
    }

    [Fact]
    public void 歧义运行时_多Root组_按全部Root身份排序_与顺序无关()
    {
        var rootA = Snap(100, "python.exe", path: @"C:\Python312\python.exe");
        var childA = Snap(101, "python.exe", path: @"C:\Python312\python.exe");
        var rootB = Snap(200, "python.exe", path: @"C:\Python312\python.exe");
        var childB = Snap(201, "python.exe", path: @"C:\Python312\python.exe");

        var frame1 = Group("Py Tasks", mainExe: @"C:\Python312\python.exe",
            processes: [rootA, childA, rootB, childB], roots: [rootA, rootB]);
        var frame2 = Group("Py Tasks", mainExe: @"C:\Python312\python.exe",
            processes: [rootB, childB, rootA, childA], roots: [rootB, rootA]);

        Assert.Equal(ApplicationStableKey.Compute(frame1), ApplicationStableKey.Compute(frame2));
        Assert.StartsWith(ApplicationStableKey.RuntimeRootPrefix, ApplicationStableKey.Compute(frame1));
    }

    [Fact]
    public void 歧义运行时_旧任务退出新任务进来_key必须变化()
    {
        // 任务级身份语义：PID 100 的 python 任务结束后新起的 PID 200 任务是"新行"
        var oldTask = Group("Python Task", mainExe: @"C:\Python312\python.exe",
            processes: [Snap(100, "python.exe", @"C:\Python312\python.exe")],
            roots: [Snap(100, "python.exe", @"C:\Python312\python.exe")]);
        var newTask = Group("Python Task", mainExe: @"C:\Python312\python.exe",
            processes: [Snap(200, "python.exe", @"C:\Python312\python.exe")],
            roots: [Snap(200, "python.exe", @"C:\Python312\python.exe")]);

        Assert.NotEqual(ApplicationStableKey.Compute(oldTask), ApplicationStableKey.Compute(newTask));
    }

    [Fact]
    public void 普通应用_重启换PID_同exe路径_保持同一应用级Key()
    {
        var oldInstance = Group("Google Chrome", mainExe: @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            processes: [Snap(1000, "chrome.exe")], roots: [Snap(1000, "chrome.exe")]);
        var restarted = Group("Google Chrome", mainExe: @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            processes: [Snap(2000, "chrome.exe")], roots: [Snap(2000, "chrome.exe")]);

        Assert.Equal(
            ApplicationStableKey.Compute(oldInstance),
            ApplicationStableKey.Compute(restarted));
        Assert.StartsWith(ApplicationStableKey.ExePrefix, ApplicationStableKey.Compute(restarted));
    }

    [Fact]
    public void 混合场景_全部组的Key互不相同()
    {
        var chrome = Group("Google Chrome", mainExe: @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            processes: [Snap(10, "chrome.exe"), Snap(11, "chrome.exe")], roots: [Snap(10, "chrome.exe")]);
        var pyA = Group("Python Task A", mainExe: @"C:\Python312\python.exe",
            processes: [Snap(100, "python.exe", @"C:\Python312\python.exe")],
            roots: [Snap(100, "python.exe", @"C:\Python312\python.exe")]);
        var pyB = Group("Python Task B", mainExe: @"C:\Python312\python.exe",
            processes: [Snap(200, "python.exe", @"C:\Python312\python.exe")],
            roots: [Snap(200, "python.exe", @"C:\Python312\python.exe")]);
        var nodeA = Group("Node Task A", mainExe: @"D:\Develop\nodejs\node.exe",
            processes: [Snap(300, "node.exe", @"D:\Develop\nodejs\node.exe")],
            roots: [Snap(300, "node.exe", @"D:\Develop\nodejs\node.exe")]);
        var nodeB = Group("Node Task B", mainExe: @"D:\Develop\nodejs\node.exe",
            processes: [Snap(400, "node.exe", @"D:\Develop\nodejs\node.exe")],
            roots: [Snap(400, "node.exe", @"D:\Develop\nodejs\node.exe")]);
        var unknown = Group("Mystery App", processes: [], roots: []);

        var groups = new List<ApplicationGroup> { chrome, pyA, pyB, nodeA, nodeB, unknown };
        var keys = groups.Select(ApplicationStableKey.Compute).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
