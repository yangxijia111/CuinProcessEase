using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// ApplicationGroupingEngine 单元测试：全部人工构造数据，不依赖真实 Windows 进程。
/// 场景覆盖任务要求的 15 类软件模式 + 核心不变量。
/// </summary>
public sealed class ApplicationGroupingEngineTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProcessSnapshot Snap(
        int pid,
        int? ppid,
        string name,
        string? path = null,
        DateTime? startTimeUtc = null,
        string? product = null,
        string? company = null,
        string? fileDescription = null) => new()
    {
        Identity = new ProcessIdentity(pid, startTimeUtc),
        ParentProcessId = ppid,
        Name = name,
        ExecutablePath = path,
        ProductName = product,
        CompanyName = company,
        FileDescription = fileDescription,
    };

    private static IReadOnlyList<ApplicationGroup> Group(params ProcessSnapshot[] processes)
        => ApplicationGroupingEngine.Group(new ProcessSnapshotCollection
        {
            CapturedAtUtc = DateTime.UtcNow,
            Processes = processes,
        });

    // 1. Chrome 多进程：主进程 Verified 子 + 同目录 crashpad
    [Fact]
    public void Chrome多进程_树边与同目录证据合并为一个应用()
    {
        const string chromeDir = @"C:\Program Files\Google\Chrome\Application";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "chrome.exe", $@"{chromeDir}\chrome.exe", BaseTime,
                "Google Chrome", "Google LLC", "Google Chrome"),
            Snap(200, 100, "chrome.exe", $@"{chromeDir}\chrome.exe", BaseTime.AddSeconds(5),
                "Google Chrome", "Google LLC"),
            Snap(300, 100, "chrome.exe", $@"{chromeDir}\chrome.exe", BaseTime.AddSeconds(6),
                "Google Chrome", "Google LLC"),
            // crashpad 与主进程无直接父子（Unverified），但同目录
            Snap(400, 100, "crashpad_handler.exe", $@"{chromeDir}\crashpad_handler.exe", BaseTime.AddSeconds(7),
                "Google Chrome", "Google LLC", "Google Chrome"));

        ApplicationGroup chrome = Assert.Single(groups);
        Assert.Equal(4, chrome.ProcessCount);
        Assert.Equal("Google Chrome", chrome.Identity.DisplayName);
        Assert.True(chrome.Confidence >= GroupingConfidence.High);
        Assert.True(chrome.Reasons.HasFlag(GroupingReason.VerifiedParentChild));
        Assert.True(chrome.Reasons.HasFlag(GroupingReason.SameInstallDirectory));
        Assert.Single(chrome.RootProcesses); // 只有 chrome 主进程是根
    }

    // 2. Electron 类应用：Discord.exe → Discord.exe → crashpad_handler
    [Fact]
    public void Electron应用_主进程与崩溃处理器合并()
    {
        const string dir = @"C:\Users\me\AppData\Local\Discord\app-1.0.0";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "Discord.exe", $@"{dir}\Discord.exe", BaseTime, "Discord", "Discord Inc."),
            Snap(200, 100, "Discord.exe", $@"{dir}\Discord.exe", BaseTime.AddSeconds(1), "Discord", "Discord Inc."),
            Snap(300, 200, "crashpad_handler.exe", $@"{dir}\crashpad_handler.exe", BaseTime.AddSeconds(2), "Discord", "Discord Inc."));

        ApplicationGroup discord = Assert.Single(groups);
        Assert.Equal(3, discord.ProcessCount);
        Assert.Equal("Discord", discord.Identity.DisplayName);
    }

    // 3. Launcher + Helper：Steam → steamwebhelper（Verified，同产品可合并）
    [Fact]
    public void Launcher与Helper_Verified父子同产品合并()
    {
        const string steamDir = @"C:\Program Files (x86)\Steam";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "steam.exe", $@"{steamDir}\steam.exe", BaseTime, "Steam Client", "Valve Corporation"),
            // 同产品（Steam 客户端组件）即使目录不同，Verified 树边 + 同产品同公司可合并
            Snap(200, 100, "steamwebhelper.exe", $@"{steamDir}\bin\cef\cef.win7x64\steamwebhelper.exe",
                BaseTime.AddSeconds(3), "Steam Client", "Valve Corporation"),
            // 同目录 updater（无树边）→ SameInstallDirectory Medium
            Snap(300, null, "updater.exe", $@"{steamDir}\updater.exe", BaseTime.AddSeconds(1), null, "Valve Corporation"));

        ApplicationGroup steam = Assert.Single(groups);
        Assert.Equal(3, steam.ProcessCount);
        Assert.Equal(2, steam.RootProcesses.Count); // steam.exe 与 updater.exe 都是组内根
        Assert.True(steam.Reasons.HasFlag(GroupingReason.SameInstallDirectory));
        Assert.True(steam.Reasons.HasFlag(GroupingReason.SameProduct));
    }

    // 4. 同目录多个相关 exe
    [Fact]
    public void 同目录多个exe_Medium合并()
    {
        const string dir = @"C:\Program Files\AppX";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "app.exe", $@"{dir}\app.exe", BaseTime),
            Snap(200, null, "tool.exe", $@"{dir}\tool.exe", BaseTime),
            Snap(300, null, "helper.exe", $@"{dir}\helper.exe", BaseTime));

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal(3, group.ProcessCount);
        Assert.Equal(GroupingConfidence.Medium, group.Confidence);
        Assert.Equal(GroupingReason.SameInstallDirectory, group.Reasons);
        // 无有效产品名 → 显示名回退根进程名
        Assert.Equal("app", group.Identity.DisplayName);
    }

    // 5. 同 Company 不同 Product → 不得合并
    [Fact]
    public void 同公司不同产品_不得合并()
    {
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "word.exe", @"C:\Program Files\Microsoft Office\root\Office16\word.exe",
                BaseTime, "Microsoft Word", "Microsoft Corporation"),
            Snap(200, null, "excel.exe", @"C:\Program Files\Microsoft Office\root\Office16\excel.exe",
                BaseTime, "Microsoft Excel", "Microsoft Corporation"));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(1, g.ProcessCount));
        Assert.False(groups[0].Reasons.HasFlag(GroupingReason.SameCompany)); // 单进程组无依据
    }

    // 6. 两个独立 python.exe → 不得错误合并
    [Fact]
    public void 两个独立python进程_不得合并()
    {
        // 即使同一路径安装、同产品同公司：运行时进程无树边时绝不合并
        const string pythonExe = @"C:\Python312\python.exe";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "python.exe", pythonExe, BaseTime, "Python 3.12", "Python Software Foundation"),
            Snap(200, null, "python.exe", pythonExe, BaseTime, "Python 3.12", "Python Software Foundation"));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));
    }

    // python 任务链：主 python → 子 python（Verified + 同路径）可合并
    [Fact]
    public void python任务链_Verified父子同路径可合并()
    {
        const string pythonExe = @"C:\Python312\python.exe";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "python.exe", pythonExe, BaseTime, "Python 3.12", "Python Software Foundation"),
            Snap(200, 100, "python.exe", pythonExe, BaseTime.AddSeconds(2), "Python 3.12", "Python Software Foundation"));

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal(2, group.ProcessCount);
        // 歧义进程合并只能到 Medium（Verified 边 + 附加证据补强）
        Assert.Equal(GroupingConfidence.Medium, group.Confidence);
    }

    // cmd.exe 启动无关软件：不得把子软件并入 cmd
    [Fact]
    public void cmd启动的无关进程_不得并入cmd组()
    {
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "cmd.exe", @"C:\Windows\System32\cmd.exe", BaseTime),
            Snap(200, 100, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                BaseTime.AddSeconds(1), "Google Chrome", "Google LLC"));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));
    }

    // 7a. Verified Parent + 不同 exe 且无附加证据 → 不得合并（P3.1 加固）
    [Fact]
    public void Verified父子不同exe无证据_不得合并()
    {
        // IDE 启动浏览器：真实父子但不是同一软件
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "IDE.exe", @"C:\Program Files\IDE\IDE.exe", BaseTime, "My IDE", "IDE Corp"),
            Snap(200, 100, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                BaseTime.AddSeconds(1), "Google Chrome", "Google LLC"));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));
    }

    // 7b. Verified Parent + 不同 exe + 同目录证据 → High 合并
    [Fact]
    public void Verified父子不同exe同目录_High合并()
    {
        const string dir = @"C:\Program Files\Game";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "launcher.exe", $@"{dir}\launcher.exe", BaseTime, "Cool Game", "GameSoft"),
            Snap(200, 100, "worker.exe", $@"{dir}\worker.exe", BaseTime.AddSeconds(1)));

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal(2, group.ProcessCount);
        Assert.Equal(GroupingConfidence.High, group.Confidence);
        Assert.True(group.Reasons.HasFlag(GroupingReason.VerifiedParentChild));
        Assert.True(group.Reasons.HasFlag(GroupingReason.SameInstallDirectory));
    }

    // 7c. Verified 父子 + 仅公司相同 → 不得合并（P4 收紧：Company 永不单独触发合并）
    [Fact]
    public void Verified父子仅公司相同_不得合并()
    {
        // 同为 Microsoft Corporation 的 Word → Excel 不能仅因此归为同一应用
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "launcher.exe", @"C:\Program Files\Game\launcher.exe", BaseTime, "Cool Game", "GameSoft"),
            Snap(200, 100, "worker.exe", @"C:\Program Files\Game\bin\worker.exe", BaseTime.AddSeconds(1), null, "GameSoft"));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));
    }

    // 8. Unverified Parent 单独存在 → 不足以强制合并
    [Fact]
    public void Unverified父子无附加证据_不得合并()
    {
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "app1.exe", @"C:\Program Files\App1\app1.exe"),   // StartTime null
            Snap(200, 100, "app2.exe", @"C:\Program Files\App2\app2.exe"));   // StartTime null → Unverified 边

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));
    }

    // 9. Unverified Parent + SameDirectory → 可提高可信度合并
    [Fact]
    public void Unverified父子同目录_Medium合并()
    {
        const string dir = @"C:\Program Files\AppX";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "app.exe", $@"{dir}\app.exe"),          // 时间未知
            Snap(200, 100, "helper.exe", $@"{dir}\helper.exe"));    // 时间未知 → Unverified + 同目录

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal(2, group.ProcessCount);
        Assert.Equal(GroupingConfidence.Medium, group.Confidence);
        Assert.True(group.Reasons.HasFlag(GroupingReason.UnverifiedParentChild));
        Assert.True(group.Reasons.HasFlag(GroupingReason.SameInstallDirectory));
    }

    // 10. helper.exe 位于两个不同软件目录 → 不得合并
    [Fact]
    public void 同名helper不同目录_不得合并()
    {
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "helper.exe", @"C:\Program Files\SoftwareA\helper.exe", BaseTime, "Software A", "A Corp"),
            Snap(200, null, "helper.exe", @"C:\Program Files\SoftwareB\helper.exe", BaseTime, "Software B", "B Corp"));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));
    }

    // 11. 父进程已退出的 Orphan → 独立成组
    [Fact]
    public void Orphan进程_独立成组()
    {
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, 9999, "orphan.exe", @"C:\Program Files\Orphan\orphan.exe", BaseTime));

        ApplicationGroup group = Assert.Single(groups);
        Assert.Single(group.Processes);
        Assert.Equal(GroupingConfidence.Unknown, group.Confidence); // 无合并依据
        Assert.Equal(GroupingReason.None, group.Reasons);
        Assert.Single(group.RootProcesses);
    }

    // 12. 元数据全部缺失
    [Fact]
    public void 元数据全部缺失_独立成组_显示名回退进程名()
    {
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "unknownapp.exe"));

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal("unknownapp", group.Identity.DisplayName);
        Assert.Null(group.Identity.MainExecutable);
        Assert.Null(group.Identity.ProductName);
        Assert.Null(group.Identity.CompanyName);
    }

    // 13. Portable 应用
    [Fact]
    public void Portable应用_同目录合并()
    {
        const string dir = @"D:\Portable\Toolbox";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "Toolbox.exe", $@"{dir}\Toolbox.exe", BaseTime, "Toolbox", "Toolbox Ltd."),
            Snap(200, null, "tb-helper.exe", $@"{dir}\tb-helper.exe", BaseTime));

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal(2, group.ProcessCount);
        Assert.Equal("Toolbox", group.Identity.DisplayName);
        Assert.Equal(dir, group.Identity.InstallDirectory);
    }

    // 14. 相同 exe 名但路径不同 → 不得仅因名称相同合并
    [Fact]
    public void 相同exe名不同路径_不得合并()
    {
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "tool.exe", @"C:\Program Files\Alpha\tool.exe", BaseTime, "Alpha Tool", "Alpha"),
            Snap(200, null, "tool.exe", @"D:\Beta\tool.exe", BaseTime, "Beta Tool", "Beta"));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));
    }

    // 15. 多 Root 同一应用：两个独立启动的 chrome 主进程
    [Fact]
    public void 多Root同一应用_SameExecutable合并()
    {
        const string chromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "chrome.exe", chromeExe, BaseTime, "Google Chrome", "Google LLC", "Google Chrome"),
            Snap(200, 100, "chrome.exe", chromeExe, BaseTime.AddSeconds(1), "Google Chrome", "Google LLC"),
            // 第二个独立主进程（父已退出）→ SameExecutable
            Snap(300, 9999, "chrome.exe", chromeExe, BaseTime.AddMinutes(5), "Google Chrome", "Google LLC"));

        ApplicationGroup chrome = Assert.Single(groups);
        Assert.Equal(3, chrome.ProcessCount);
        Assert.Equal(2, chrome.RootProcesses.Count); // PID 100 与 300 都是组内根
        Assert.True(chrome.Reasons.HasFlag(GroupingReason.SameExecutable));
        Assert.True(chrome.Reasons.HasFlag(GroupingReason.VerifiedParentChild));
        // MainExecutable 可确定：全部根路径一致
        Assert.Equal(chromeExe, chrome.Identity.MainExecutable);
    }

    // ================= P3.1 加固回归 =================

    // Group Confidence = 组内最弱有效合并关系（weakest bridge）
    [Fact]
    public void 最弱桥语义_High与Medium组成的组为Medium()
    {
        const string dir = @"C:\Program Files\AppX";

        // app↔app2 同路径 High；helper 与 app2 Unverified+同目录 Medium 桥
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "app.exe", $@"{dir}\app.exe", BaseTime, "AppX", "AppX Corp"),
            Snap(200, null, "app2.exe", $@"{dir}\app2.exe", BaseTime.AddSeconds(1)), // 同目录 Medium
            Snap(300, 200, "helper.exe", $@"{dir}\helper.exe", null));              // Unverified+同目录 Medium

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal(3, group.ProcessCount);
        // 同目录 Medium 桥存在 → 组整体不超过 Medium（最弱桥）
        Assert.Equal(GroupingConfidence.Medium, group.Confidence);
    }

    [Fact]
    public void 最弱桥语义_全High链保持High()
    {
        const string exe = @"C:\Program Files\Chrome\chrome.exe";

        // 同路径 High + Verified+SameExecutable High → 组 High
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "chrome.exe", exe, BaseTime, "Chrome", "Google LLC"),
            Snap(200, 100, "chrome.exe", exe, BaseTime.AddSeconds(1), "Chrome", "Google LLC"));

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal(GroupingConfidence.High, group.Confidence);
    }

    // 同 component 的冗余弱边不降级已有组
    [Fact]
    public void 冗余弱边_不降低组置信度()
    {
        const string exe = @"C:\Program Files\App\app.exe";

        // 三个同路径进程（High 链）；100→200 另有 Unverified 树边（Medium，冗余路径）
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "app.exe", exe, BaseTime, "App", "App Corp"),
            Snap(200, 100, "app.exe", exe, null),   // 时间未知 → Unverified 边 + SameExecutable
            Snap(300, null, "app.exe", exe, BaseTime.AddSeconds(2), "App", "App Corp"));

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal(3, group.ProcessCount);
        // 冗余 Medium 边不把 High 组降级
        Assert.Equal(GroupingConfidence.High, group.Confidence);
    }

    // WebView2 多宿主：webview 与宿主无强证据（公司不同或产品不同）时各自独立，
    // 绝不因 SameExecutable 跨宿主全局合并
    [Fact]
    public void WebView2多宿主_不得因同路径全局合并()
    {
        const string webviewExe = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\1.0\msedgewebview2.exe";

        IReadOnlyList<ApplicationGroup> groups = Group(
            // 宿主一：SearchHost（与 webview 无产品/目录证据，公司单独不足以合并）
            Snap(100, null, "SearchHost.exe", @"C:\Windows\System32\SearchHost.exe", BaseTime,
                "Microsoft® Windows® Operating System", "Microsoft Corporation"),
            Snap(110, 100, "msedgewebview2.exe", webviewExe, BaseTime.AddSeconds(1),
                "Microsoft Edge WebView2", "Microsoft Corporation"),
            // 宿主二：第三方应用
            Snap(200, null, "MyApp.exe", @"C:\Program Files\MyApp\MyApp.exe", BaseTime, "My App", "My Company"),
            Snap(210, 200, "msedgewebview2.exe", webviewExe, BaseTime.AddSeconds(1),
                "Microsoft Edge WebView2", "Microsoft Corporation"));

        // P4 收紧后（Company 不再单独触发合并）：
        // 两个宿主各自单进程组；webview 们不因 SameExecutable 串成一组
        Assert.Equal(4, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));

        // 没有任何组同时包含两个宿主或跨宿主的 webview
        Assert.DoesNotContain(groups, g => g.Processes.Any(p => p.Name == "SearchHost.exe")
                                         && g.Processes.Any(p => p.Name == "MyApp.exe"));
    }

    // 第三方宿主与 webview 无任何共同证据（公司不同）→ 宁拆不合，webview 独立
    [Fact]
    public void 第三方宿主无共同证据_webview保持独立()
    {
        const string webviewExe = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\1.0\msedgewebview2.exe";

        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(200, null, "MyApp.exe", @"C:\Program Files\MyApp\MyApp.exe", BaseTime, "My App", "My Company"),
            Snap(210, 200, "msedgewebview2.exe", webviewExe, BaseTime.AddSeconds(1),
                "Microsoft Edge WebView2", "Microsoft Corporation"));

        // 无目录/产品/公司证据：即使 Verified 父子也宁可拆开
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));
    }

    // 应用身份以 Root 为中心：helper 数量不能覆盖宿主身份
    [Fact]
    public void 应用身份_Root优先_不被helper覆盖()
    {
        const string helperDir = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\1.0";

        // Root 与 helper 同产品（收编证据），但 helper 元数据不能覆盖宿主身份
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "SearchHost.exe", @"C:\Program Files\WindowsSearch\SearchHost.exe", BaseTime,
                "Windows Search", "Microsoft Corporation"),
            Snap(110, 100, "searchhelper.exe", $@"{helperDir}\searchhelper.exe", BaseTime.AddSeconds(1),
                "Windows Search", "Microsoft Corporation"),
            Snap(111, 100, "searchhelper.exe", $@"{helperDir}\searchhelper.exe", BaseTime.AddSeconds(2),
                "Windows Search", "Microsoft Corporation"),
            Snap(112, 100, "searchindexer.exe", $@"{helperDir}\searchindexer.exe", BaseTime.AddSeconds(3),
                "Windows Search", "Microsoft Corporation"));

        ApplicationGroup group = Assert.Single(groups);
        Assert.Equal(4, group.ProcessCount);

        // DisplayName 来自 Root，不受 helper 影响
        Assert.Equal("Windows Search", group.Identity.DisplayName);
        // 安装目录来自 Root/MainExecutable，不被 helper 目录多数票覆盖
        Assert.Equal(@"C:\Program Files\WindowsSearch", group.Identity.InstallDirectory);
    }

    [Fact]
    public void 所有输入进程恰好属于一个组_不丢失不重复()
    {
        ProcessSnapshot[] processes =
        [
            Snap(100, null, "app.exe", @"C:\Program Files\App\app.exe", BaseTime, "App", "AppCorp"),
            Snap(101, 100, "app.exe", @"C:\Program Files\App\app.exe", BaseTime.AddSeconds(1)),
            Snap(200, null, "python.exe", @"C:\Python312\python.exe", BaseTime),
            Snap(201, null, "python.exe", @"C:\Python312\python.exe", BaseTime),
            Snap(300, 9999, "orphan.exe"),
            Snap(400, null, "cmd.exe", @"C:\Windows\System32\cmd.exe", BaseTime),
        ];

        IReadOnlyList<ApplicationGroup> groups = ApplicationGroupingEngine.Group(
            new ProcessSnapshotCollection { CapturedAtUtc = DateTime.UtcNow, Processes = processes });

        List<int> grouped = groups.SelectMany(g => g.Processes).Select(p => p.ProcessId).ToList();
        Assert.Equal(processes.Length, grouped.Count);
        Assert.Equal(processes.Length, grouped.Distinct().Count());
        Assert.All(processes, p => Assert.Contains(p.ProcessId, grouped));
    }

    [Fact]
    public void 空快照_返回空分组()
    {
        IReadOnlyList<ApplicationGroup> groups = ApplicationGroupingEngine.Group(
            new ProcessSnapshotCollection { CapturedAtUtc = DateTime.UtcNow, Processes = [] });

        Assert.Empty(groups);
    }

    // 泛化产品名不作为显示名/依据
    [Fact]
    public void 泛化Windows产品名_不作为分组依据与显示名()
    {
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "notepad.exe", @"C:\Windows\System32\notepad.exe", BaseTime,
                "Microsoft® Windows® Operating System", "Microsoft Corporation"),
            Snap(200, null, "mspaint.exe", @"C:\Windows\System32\mspaint.exe", BaseTime,
                "Microsoft® Windows® Operating System", "Microsoft Corporation"));

        // System32 目录 + 泛化产品名：两条 Medium 路线全部失效 → 不合并
        Assert.Equal(2, groups.Count);
        Assert.Equal("notepad", groups.Single(g => g.Processes[0].ProcessId == 100).Identity.DisplayName);
    }

    // System32 公共目录不得作为同目录依据
    [Fact]
    public void 公共目录System32_不得作为同目录合并依据()
    {
        IReadOnlyList<ApplicationGroup> groups = Group(
            Snap(100, null, "unrelated1.exe", @"C:\Windows\System32\unrelated1.exe", BaseTime),
            Snap(200, null, "unrelated2.exe", @"C:\Windows\System32\unrelated2.exe", BaseTime));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Processes));
    }
}
