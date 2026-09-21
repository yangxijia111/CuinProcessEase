using System.Runtime.InteropServices;

namespace CuinProcessEase.TerminationTestApp;

/// <summary>
/// 终止引擎专用测试目标：打开真实顶层窗口、响应 WM_CLOSE 正常退出、
/// 可按需忽略 WM_CLOSE（测试 Force）、可派生 child helper（测试同组终止）。
/// 仅被自动化测试启动与结束，绝不用于结束系统或用户程序。
/// </summary>
/// <remarks>
/// 用法：
///   TerminationTestApp --window &lt;title&gt; [--ignore-close] [--hidden] [--no-window] [--spawn-child &lt;title&gt;]
/// --no-window：不创建任何窗口（测试"无顶层窗口"路径），进程持续休眠直到被终止。
/// 启动后向 stdout 输出：
///   PID=&lt;自身进程 ID&gt;
///   CHILD=&lt;child 进程 ID&gt;   （仅 --spawn-child 时）
/// </remarks>
public static class Program
{
    /// <summary>类型锚点：测试工程通过本类型定位 TestApp 程序集/exe 路径。</summary>
    public static void Marker()
    {
    }

    private static bool _ignoreClose;

    private static IntPtr _hwnd;

    public static int Main(string[] args)
    {
        string? title = null;
        string? childTitle = null;
        bool hidden = false;
        bool noWindow = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--window" when i + 1 < args.Length:
                    title = args[++i];
                    break;
                case "--spawn-child" when i + 1 < args.Length:
                    childTitle = args[++i];
                    break;
                case "--ignore-close":
                    _ignoreClose = true;
                    break;
                case "--hidden":
                    hidden = true;
                    break;
                case "--no-window":
                    noWindow = true;
                    break;
            }
        }

        if (title is null)
        {
            return 2;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 派生 child helper（同 exe 路径，分组引擎按 SameExecutable 聚成一组）
        if (childTitle is not null)
        {
            string self = Environment.ProcessPath!;
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(self)
            {
                Arguments = $"--window \"{childTitle}\" --ignore-close --hidden --no-window",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Console.WriteLine($"CHILD={child!.Id}");
        }

        Console.WriteLine($"PID={Environment.ProcessId}");
        Console.Out.Flush();

        if (noWindow)
        {
            // 无窗口模式：持续休眠直到被终止（WM_CLOSE 不适用路径）
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        if (!CreateMainWindow(title, hidden))
        {
            return 3;
        }

        return RunMessageLoop();
    }

    private static bool CreateMainWindow(string title, bool hidden)
    {
        // 窗口类名带 PID 防止并发测试实例相互冲突
        string className = $"CuinTerminationTest_{Environment.ProcessId}";

        var wndClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcCallback),
            lpszClassName = className,
            hInstance = Marshal.GetHINSTANCE(typeof(Program).Module),
        };

        ushort atom = RegisterClassExW(ref wndClass);
        if (atom == 0)
        {
            return false;
        }

        uint style = hidden ? WS_OVERLAPPEDWINDOW : WS_OVERLAPPEDWINDOW | WS_VISIBLE;
        _hwnd = CreateWindowExW(
            0, className, title, style,
            100, 100, 320, 200,
            IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

        return _hwnd != IntPtr.Zero;
    }

    private static int RunMessageLoop()
    {
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        return 0;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_CLOSE = 0x0010;
        const uint WM_DESTROY = 0x0002;

        if (msg == WM_CLOSE && _ignoreClose)
        {
            // 测试 Force 场景：吞掉 WM_CLOSE，不关闭窗口
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    // ---------- Win32 ----------

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_VISIBLE = 0x10000000;

    /// <summary>WndProc 委托实例：必须保持存活，防止回调被 GC 回收。</summary>
    private static readonly WndProcDelegate WndProcCallback = WndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
}
