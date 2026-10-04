using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using CuinProcessEase.Core.Elevated;

namespace CuinProcessEase.Windows.Elevation;

/// <summary>主程序端 Elevated Helper 调用失败原因。</summary>
public enum ElevatedHelperFailure
{
    /// <summary>用户在 UAC 对话框中取消（ERROR_CANCELLED）。</summary>
    UacCancelled,

    /// <summary>Helper 可执行文件缺失。</summary>
    HelperMissing,

    /// <summary>Helper 启动后未在有限时间内建立管道连接（用户未确认 UAC / Helper 异常）。</summary>
    ConnectTimeout,

    /// <summary>管道往返 / 协议解析失败。</summary>
    ProtocolError,
}

/// <summary>Helper 调用异常：携带结构化失败原因，由终止服务映射为用户可读结果。</summary>
public sealed class ElevatedHelperException : Exception
{
    public ElevatedHelperException(ElevatedHelperFailure reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public ElevatedHelperFailure Reason { get; }
}

/// <summary>
/// 主程序端的 Elevated Helper 客户端：以 runas（UAC）启动 Helper 进程，
/// 通过 ACL 受限的命名管道完成一次"请求 → 响应"往返。
/// </summary>
/// <remarks>
/// 安全设计：
/// - 管道名为一次性随机 GUID（不可猜测），连接窗口有限；
/// - 管道 ACL 限定为启动主程序的当前用户 SID（--allowed-sid 传参），
///   Helper 端创建管道时显式设置，其他账户无法连接；
/// - 主程序绝不以管理员身份常驻：每次提权操作仅 Helper 进程短暂存活，
///   处理完单条请求即退出；
/// - 所有等待均有限（连接 45s 容忍用户慢慢确认 UAC，读写 60s），绝不无限阻塞。
/// </remarks>
public interface IElevatedHelperClient
{
    /// <summary>执行一次 Helper 终止往返；失败抛 <see cref="ElevatedHelperException"/>。</summary>
    Task<ElevatedKillResponse> KillAsync(
        ElevatedKillRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ElevatedHelperClient : IElevatedHelperClient
{
    /// <summary>Helper exe 文件名（部署在主程序同目录）。</summary>
    public const string HelperFileName = "CuinProcessEase.ElevatedHelper.exe";

    private const string PipePrefix = "cuinpe-helper-";

    /// <summary>等待 Helper 管道就绪的总时限（用户确认 UAC 可能较慢）。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    /// <summary>单次读写管道的总时限。</summary>
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(60);

    private readonly string _helperPath;

    public ElevatedHelperClient()
        : this(Path.Combine(AppContext.BaseDirectory, HelperFileName))
    {
    }

    /// <summary>测试构造：指定 Helper exe 路径。</summary>
    public ElevatedHelperClient(string helperPath)
    {
        _helperPath = helperPath;
    }

    /// <inheritdoc />
    public async Task<ElevatedKillResponse> KillAsync(
        ElevatedKillRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!File.Exists(_helperPath))
        {
            throw new ElevatedHelperException(
                ElevatedHelperFailure.HelperMissing,
                $"未找到管理员 Helper（{_helperPath}）。");
        }

        string pipeName = PipePrefix + Guid.NewGuid().ToString("N");
        string? sid = WindowsIdentity.GetCurrent().User?.Value
                      ?? throw new ElevatedHelperException(
                          ElevatedHelperFailure.ProtocolError, "无法获取当前用户 SID。");

        // UAC 启动 Helper（ShellExecute runas；用户取消 → Win32Exception 1223）
        try
        {
            using var helper = Process.Start(new ProcessStartInfo
            {
                FileName = _helperPath,
                Arguments = $"--pipe {pipeName} --allowed-sid {sid}",
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new ElevatedHelperException(
                ElevatedHelperFailure.UacCancelled, "已取消管理员授权（UAC）。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new ElevatedHelperException(
                ElevatedHelperFailure.HelperMissing, "管理员 Helper 文件不存在。");
        }

        // 有限等待管道就绪（Helper 需经 UAC 启动，用户确认可能较慢）
        using var pipe = await ConnectAsync(pipeName, cancellationToken).ConfigureAwait(false);

        byte[] payload = ElevatedKillProtocol.SerializeRequest(request);
        await WriteFrameAsync(pipe, payload, cancellationToken).ConfigureAwait(false);

        byte[]? responsePayload = await ElevatedHelperServer.ReadFrameAsync(
            pipe, (int)IoTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        if (responsePayload is null)
        {
            throw new ElevatedHelperException(
                ElevatedHelperFailure.ProtocolError, "管理员 Helper 响应读取失败或超时。");
        }

        ElevatedKillResponse? response = ElevatedKillProtocol.DeserializeResponse(responsePayload);
        if (response is null || !string.Equals(response.OperationId, request.OperationId, StringComparison.Ordinal))
        {
            throw new ElevatedHelperException(
                ElevatedHelperFailure.ProtocolError, "管理员 Helper 响应无法解析或 OperationId 不匹配。");
        }

        return response;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName, CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)ConnectTimeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            try
            {
                await candidate.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
                return candidate;
            }
            catch (TimeoutException)
            {
                candidate.Dispose();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                candidate.Dispose();
                throw;
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new ElevatedHelperException(
                    ElevatedHelperFailure.ConnectTimeout,
                    "管理员 Helper 未在有限时间内就绪（可能未确认 UAC），操作已取消。");
            }
        }
    }

    private static async Task WriteFrameAsync(
        NamedPipeClientStream pipe, byte[] payload, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        payload.AsSpan().CopyTo(frame.AsSpan(4));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(IoTimeout);
        try
        {
            await pipe.WriteAsync(frame, timeoutCts.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            throw new ElevatedHelperException(
                ElevatedHelperFailure.ProtocolError, "向管理员 Helper 发送请求失败或超时。");
        }
    }
}
