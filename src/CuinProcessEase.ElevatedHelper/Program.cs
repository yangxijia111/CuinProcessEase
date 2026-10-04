using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using CuinProcessEase.Core.Elevated;
using CuinProcessEase.Windows.Termination;

namespace CuinProcessEase.ElevatedHelper;

/// <summary>
/// Elevated Helper 进程入口：一次性、单请求生命周期。
/// </summary>
/// <remarks>
/// 职责（刻意受限，绝不扩展）：
/// - 解析主程序传入的一次性管道名与允许连接的用户 SID；
/// - 创建 ACL 受限的命名管道（仅该 SID 可读写）；
/// - 处理单条 Kill 请求（<see cref="ElevatedHelperServer"/> 校验协议后由
///   <see cref="ElevatedKillEngine"/> 逐目标重新验证 exact identity 并终止）；
/// - 写回响应后立即退出；全程受看门狗保护，绝不长期驻留提权进程。
/// 明确禁止：任意命令执行、Shell Execute 任意字符串、运行用户指定 CMD、加载任意 DLL。
/// </remarks>
internal static class Program
{
    /// <summary>等待主程序连接管道的时限（超时自杀，绝不驻留提权进程）。</summary>
    private const int WaitForConnectionMs = 20_000;

    /// <summary>整个 Helper 生命周期的硬上限（覆盖一切挂死路径）。</summary>
    private const int TotalLifetimeMs = 120_000;

    /// <summary>日志文件（追加写；写失败不影响操作，仅供诊断）。</summary>
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CuinProcessEase", "logs", "elevated-helper.log");

    private static int Main(string[] args)
    {
        // 看门狗：无论协议循环卡在何处，硬上限到达即退出提权进程
        using var watchdog = new CancellationTokenSource(TotalLifetimeMs);
        watchdog.Token.Register(() => Environment.Exit(3));

        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"fatal: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out string? pipeName, out string? allowedSid))
        {
            Log("启动参数无效：需要 --pipe <name> --allowed-sid <sid>。");
            return 2;
        }

        Log($"started pid={Environment.ProcessId} pipe={pipeName}");

        using NamedPipeServerStream pipe = CreateSecuredPipe(pipeName!, allowedSid!);
        bool connected;
        try
        {
            using var connectCts = new CancellationTokenSource(WaitForConnectionMs);
            await pipe.WaitForConnectionAsync(connectCts.Token);
            connected = true;
        }
        catch (OperationCanceledException)
        {
            connected = false;
        }

        if (!connected)
        {
            Log("等待主程序连接超时，退出。");
            return 4;
        }

        // 单请求生命周期：处理完（或失败）即返回退出
        bool served = ElevatedHelperServer.ServeOnceAsync(
            pipe, new Win32TerminationInterop(), WaitForConnectionMs).GetAwaiter().GetResult();

        Log(served ? "request served, exiting." : "no valid request, exiting.");
        return served ? 0 : 5;
    }

    /// <summary>创建仅允许指定用户 SID 读写的命名管道（管道名本身也是一次性随机 GUID）。</summary>
    private static NamedPipeServerStream CreateSecuredPipe(string pipeName, string allowedSid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(allowedSid),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.None,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private static bool TryParseArgs(string[] args, out string? pipeName, out string? allowedSid)
    {
        pipeName = null;
        allowedSid = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--pipe" when pipeName is null:
                    pipeName = args[++i];
                    break;
                case "--allowed-sid" when allowedSid is null:
                    allowedSid = args[++i];
                    break;
            }
        }

        return !string.IsNullOrWhiteSpace(pipeName)
               && !string.IsNullOrWhiteSpace(allowedSid)
               && pipeName.StartsWith("cuinpe-helper-", StringComparison.Ordinal)
               && allowedSid.StartsWith('S');
    }

    private static void Log(string message)
    {
        try
        {
            string directory = Path.GetDirectoryName(LogFile)!;
            Directory.CreateDirectory(directory);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // 日志失败绝不影响 Helper 的受限操作职责
        }
    }
}
