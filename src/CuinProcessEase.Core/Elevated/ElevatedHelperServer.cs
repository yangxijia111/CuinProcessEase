using System.Buffers.Binary;
using System.Text;

namespace CuinProcessEase.Core.Elevated;

/// <summary>
/// Elevated Helper 的流式协议服务循环：读取单条请求 → 校验 → 逐目标执行 → 写回单条响应。
/// 与进程壳（UAC / 管道 ACL / 看门狗）解耦，可直接在测试进程内以任意双工流驱动。
/// </summary>
/// <remarks>
/// 协议安全：
/// - 只处理一条连接上的一条请求（单请求生命周期，处理完由调用方退出进程）；
/// - 操作名 / 目标数 / PID / 预期 FILETIME 语义校验失败 → 全部目标 InvalidRequest，
///   绝不执行任何 TerminateProcess；
/// - 帧长上限 <see cref="ElevatedKillProtocol.MaxFrameBytes"/>，超限直接协议错误，
///   绝不缓冲任意大小的 payload。
/// </remarks>
public static class ElevatedHelperServer
{
    /// <summary>
    /// 在给定双工流上执行一次"请求 → 响应"往返。
    /// 返回 false 表示读帧阶段失败/超时/超限（调用方应记录并退出，不写任何响应）。
    /// </summary>
    /// <param name="stream">已连接的双工流（命名管道 / 测试用内存流）。</param>
    /// <param name="interop">Win32 原语实现。</param>
    /// <param name="readTimeoutMs">读请求的有限等待（超时即放弃，绝不无限阻塞提权进程）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<bool> ServeOnceAsync(
        Stream stream,
        IElevatedKillInterop interop,
        int readTimeoutMs = 15_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(interop);

        byte[]? payload = await ReadFrameAsync(stream, readTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return false;
        }

        ElevatedKillRequest? request = ElevatedKillProtocol.DeserializeRequest(payload);
        string? validationError = ElevatedKillProtocol.ValidateRequest(request);

        ElevatedKillResponse response;
        if (validationError is not null)
        {
            // 协议拒绝：不执行任何终止动作，全部目标返回 InvalidRequest
            var rejected = (request?.Targets ?? Array.Empty<ElevatedKillTarget>())
                .Select(t => new ElevatedKillTargetResult(t.ProcessId, ElevatedKillOutcome.InvalidRequest, validationError))
                .ToList();
            response = new ElevatedKillResponse(request?.OperationId ?? string.Empty, rejected);
        }
        else
        {
            var results = new List<ElevatedKillTargetResult>(request!.Targets.Count);
            foreach (ElevatedKillTarget target in request.Targets)
            {
                results.Add(ElevatedKillEngine.Execute(target, interop));
            }

            response = new ElevatedKillResponse(request.OperationId, results);
        }

        byte[] responsePayload = ElevatedKillProtocol.SerializeResponse(response);
        await WriteFrameAsync(stream, responsePayload, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 读一帧：4 字节大端长度 + payload；超时 / 断开 / 超限返回 null。
    /// 主程序客户端与 Helper 进程共用（响应/请求帧格式一致）。
    /// </summary>
    public static async Task<byte[]?> ReadFrameAsync(
        Stream stream, int timeoutMs, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            byte[] header = await ReadExactlyAsync(stream, 4, timeoutCts.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is <= 0 or > ElevatedKillProtocol.MaxFrameBytes)
            {
                return null;
            }

            return await ReadExactlyAsync(stream, length, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or IOException or ObjectDisposedException
            or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        payload.AsSpan().CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("对端在帧完整前断开。");
            }

            read += n;
        }

        return buffer;
    }
}
