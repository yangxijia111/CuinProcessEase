using CuinProcessEase.Core.Elevated;
using System.IO.Pipes;
using Xunit;

namespace CuinProcessEase.Core.Tests.Elevated;

/// <summary>
/// Elevated Helper 执行引擎安全不变量（fake interop，无真实进程）：
/// 身份不匹配 / 打不开句柄 / 读不到创建时间 → 0 次 TerminateProcess；
/// 已退出不算失败；Terminate 成功路径有限等待确认；所有句柄全路径释放。
/// </summary>
public sealed class ElevatedKillEngineTests
{
    private const uint WaitObject0 = 0x0000;
    private const uint WaitTimeout = 0x0102;
    private const long BaseFileTime = 133_528_256_000_000_000;

    /// <summary>最小 fake：句柄值 = PID；记录破坏性调用。</summary>
    private sealed class FakeInterop : IElevatedKillInterop
    {
        public Dictionary<uint, FakeProc> Processes { get; } = [];

        public List<uint> TerminatedPids { get; } = [];

        public List<uint> ClosedHandles { get; } = [];

        public IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId, out int win32Error)
        {
            win32Error = 0;
            if (Processes.TryGetValue(processId, out FakeProc? proc) && !proc.OpenFails)
            {
                return new IntPtr(processId);
            }

            win32Error = proc?.OpenError ?? 87;
            return IntPtr.Zero;
        }

        public bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc)
        {
            creationFileTimeUtc = 0;
            if (Processes.TryGetValue((uint)processHandle, out FakeProc? proc) && !proc.TimesFails)
            {
                creationFileTimeUtc = proc.CreationFileTimeUtc;
                return true;
            }

            return false;
        }

        public bool TerminateProcess(IntPtr processHandle, uint exitCode, out int win32Error)
        {
            uint pid = (uint)processHandle;
            TerminatedPids.Add(pid);
            win32Error = 0;
            if (Processes.TryGetValue(pid, out FakeProc? proc))
            {
                proc.TerminateCalled = true;
                return true;
            }

            win32Error = 87;
            return false;
        }

        public uint WaitForSingleObject(IntPtr processHandle, uint milliseconds)
        {
            if (!Processes.TryGetValue((uint)processHandle, out FakeProc? proc))
            {
                return 0xFFFFFFFF;
            }

            return proc switch
            {
                // Terminate 后长等待：按脚本退出或挂死
                { TerminateCalled: true } when milliseconds > 0 => proc.ExitAfterTerminate ? WaitObject0 : WaitTimeout,
                { Exited: true } => WaitObject0,
                _ => WaitTimeout,
            };
        }

        public bool CloseHandle(IntPtr handle)
        {
            ClosedHandles.Add((uint)handle);
            return true;
        }
    }

    private sealed class FakeProc
    {
        public required long CreationFileTimeUtc { get; init; }

        public bool OpenFails { get; init; }

        public int OpenError { get; init; } = 5;

        public bool TimesFails { get; init; }

        public bool Exited { get; init; }

        public bool ExitAfterTerminate { get; init; } = true;

        public bool TerminateCalled { get; set; }
    }

    private static void AddProc(FakeInterop interop, uint pid, FakeProc proc) => interop.Processes[pid] = proc;

    private static ElevatedKillTarget Target(int pid, long expectedFileTime)
        => new(pid, expectedFileTime);

    [Fact]
    public void 身份逐位匹配_终止成功_句柄释放()
    {
        var interop = new FakeInterop();
        AddProc(interop, 100, new FakeProc { CreationFileTimeUtc = BaseFileTime });

        ElevatedKillTargetResult result = ElevatedKillEngine.Execute(Target(100, BaseFileTime), interop);

        Assert.Equal(ElevatedKillOutcome.Terminated, result.Outcome);
        Assert.Equal([100u], interop.TerminatedPids);
        Assert.Equal([100u], interop.ClosedHandles); // 全路径释放
    }

    [Fact]
    public void 差1tick_身份不匹配_绝不终止()
    {
        var interop = new FakeInterop();
        AddProc(interop, 100, new FakeProc { CreationFileTimeUtc = BaseFileTime + 1 });

        ElevatedKillTargetResult result = ElevatedKillEngine.Execute(Target(100, BaseFileTime), interop);

        Assert.Equal(ElevatedKillOutcome.IdentityMismatch, result.Outcome);
        Assert.Empty(interop.TerminatedPids); // 0 破坏性动作
        Assert.Equal([100u], interop.ClosedHandles);
    }

    [Fact]
    public void 打开句柄被拒绝_AccessDenied_绝不终止()
    {
        var interop = new FakeInterop();
        AddProc(interop, 100, new FakeProc { CreationFileTimeUtc = BaseFileTime, OpenFails = true });

        ElevatedKillTargetResult result = ElevatedKillEngine.Execute(Target(100, BaseFileTime), interop);

        Assert.Equal(ElevatedKillOutcome.AccessDenied, result.Outcome);
        Assert.Empty(interop.TerminatedPids);
        Assert.Empty(interop.ClosedHandles); // 未获得句柄，无需释放
    }

    [Fact]
    public void 读不到创建时间_Failed_绝不终止()
    {
        var interop = new FakeInterop();
        AddProc(interop, 100, new FakeProc { CreationFileTimeUtc = BaseFileTime, TimesFails = true });

        ElevatedKillTargetResult result = ElevatedKillEngine.Execute(Target(100, BaseFileTime), interop);

        Assert.Equal(ElevatedKillOutcome.Failed, result.Outcome);
        Assert.Empty(interop.TerminatedPids);
        Assert.Equal([100u], interop.ClosedHandles);
    }

    [Fact]
    public void 执行前已退出_AlreadyExited_不算失败不终止()
    {
        var interop = new FakeInterop();
        AddProc(interop, 100, new FakeProc { CreationFileTimeUtc = BaseFileTime, Exited = true });

        ElevatedKillTargetResult result = ElevatedKillEngine.Execute(Target(100, BaseFileTime), interop);

        Assert.Equal(ElevatedKillOutcome.AlreadyExited, result.Outcome);
        Assert.Empty(interop.TerminatedPids);
    }

    [Fact]
    public void Terminate后挂死不退_TimedOut()
    {
        var interop = new FakeInterop();
        AddProc(interop, 100, new FakeProc
        {
            CreationFileTimeUtc = BaseFileTime,
            ExitAfterTerminate = false,
        });

        ElevatedKillTargetResult result = ElevatedKillEngine.Execute(Target(100, BaseFileTime), interop);

        Assert.Equal(ElevatedKillOutcome.TimedOut, result.Outcome);
        Assert.Equal([100u], interop.TerminatedPids);
    }

    [Fact]
    public void 非法目标_InvalidRequest_绝不打开句柄()
    {
        var interop = new FakeInterop();

        ElevatedKillTargetResult zeroPid = ElevatedKillEngine.Execute(Target(0, BaseFileTime), interop);
        ElevatedKillTargetResult zeroTime = ElevatedKillEngine.Execute(Target(100, 0), interop);

        Assert.Equal(ElevatedKillOutcome.InvalidRequest, zeroPid.Outcome);
        Assert.Equal(ElevatedKillOutcome.InvalidRequest, zeroTime.Outcome);
        Assert.Empty(interop.TerminatedPids);
    }
}

/// <summary>Helper 协议服务循环：双工内存流上的完整往返与安全拒绝路径。</summary>
public sealed class ElevatedHelperServerTests
{
    private const long BaseFileTime = 133_528_256_000_000_000;

    /// <summary>
    /// 双工流对（纯托管）：两侧各一条字节队列 + 信号量，读写即时往返。
    /// 不依赖 OS 管道（Core.Tests 为纯 net10.0 TFM，真实命名管道行为由 Windows.Tests 覆盖）。
    /// </summary>
    private sealed class DuplexPipePair : IDisposable
    {
        public Stream ClientSide { get; }

        public Stream ServerSide { get; }

        public DuplexPipePair()
        {
            (Stream a, Stream b) = QueueStream.CreatePair();
            ClientSide = a;
            ServerSide = b;
        }

        public void Dispose()
        {
            ClientSide.Dispose();
            ServerSide.Dispose();
        }
    }

    /// <summary>字节队列双工流：写入立即入对端队列并释放读信号；关闭即广播 EOF。</summary>
    private sealed class QueueStream : Stream
    {
        private QueueStream? _peer;

        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _incoming = new();

        private readonly SemaphoreSlim _dataAvailable = new(0, int.MaxValue);

        private byte[] _pending = Array.Empty<byte>();

        private int _pendingOffset;

        private volatile bool _closed;

        private QueueStream()
        {
        }

        public static (Stream A, Stream B) CreatePair()
        {
            var a = new QueueStream();
            var b = new QueueStream();
            a._peer = b;
            b._peer = a;
            return (a, b);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get; set; }

        public override void Flush() { }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_closed && _incoming.IsEmpty)
                {
                    throw new EndOfStreamException("对端已关闭。");
                }

                if (_pending.Length > _pendingOffset)
                {
                    int n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
                    _pending.AsSpan(_pendingOffset, n).CopyTo(buffer.Span);
                    _pendingOffset += n;
                    return n;
                }

                if (_incoming.TryDequeue(out byte[]? chunk))
                {
                    _pending = chunk;
                    _pendingOffset = 0;
                    continue;
                }

                if (_closed)
                {
                    throw new EndOfStreamException("对端已关闭。");
                }

                await _dataAvailable.WaitAsync(cancellationToken);
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ArgumentNullException.ThrowIfNull(_peer);
            if (_closed)
            {
                throw new IOException("对端已关闭。");
            }

            _peer._incoming.Enqueue(buffer.ToArray());
            _peer._dataAvailable.Release();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closed = true;
            }

            base.Dispose(disposing);
        }
    }

    private static async Task<byte[]> FrameAsync(byte[] payload)
    {
        byte[] frame = new byte[4 + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        payload.AsSpan().CopyTo(frame.AsSpan(4));
        await Task.Yield();
        return frame;
    }

    [Fact]
    public async Task 完整往返_合法请求_逐目标执行并回写结果()
    {
        var interop = new RecordingInterop();
        interop.CreationTimes[100u] = BaseFileTime; // fake 句柄视角：与请求预期逐位一致
        using var pair = new DuplexPipePair();
        var request = new ElevatedKillRequest(
            ElevatedKillRequest.KillOperation, "op-1",
            [new ElevatedKillTarget(100, BaseFileTime)]);
        byte[] framed = await FrameAsync(ElevatedKillProtocol.SerializeRequest(request));
        await pair.ClientSide.WriteAsync(framed);

        bool served = await ElevatedHelperServer.ServeOnceAsync(pair.ServerSide, interop);

        Assert.True(served);
        Assert.Equal([100u], interop.TerminatedPids);

        byte[]? responsePayload = await ElevatedHelperServer.ReadFrameAsync(
            pair.ClientSide, 5000, CancellationToken.None);
        Assert.NotNull(responsePayload);
        ElevatedKillResponse? response = ElevatedKillProtocol.DeserializeResponse(responsePayload);
        Assert.NotNull(response);
        Assert.Equal("op-1", response.OperationId);
        Assert.Equal(ElevatedKillOutcome.Terminated, Assert.Single(response.Results).Outcome);
    }

    [Fact]
    public async Task 非kill操作_全部目标InvalidRequest_绝不执行终止()
    {
        var interop = new RecordingInterop();
        using var pair = new DuplexPipePair();
        var request = new ElevatedKillRequest("arbitrary-command", "op-2",
            [new ElevatedKillTarget(100, BaseFileTime)]);
        byte[] framed = await FrameAsync(ElevatedKillProtocol.SerializeRequest(request));
        await pair.ClientSide.WriteAsync(framed);

        bool served = await ElevatedHelperServer.ServeOnceAsync(pair.ServerSide, interop);

        Assert.True(served);
        Assert.Empty(interop.TerminatedPids); // 0 破坏性动作
        byte[]? responsePayload = await ElevatedHelperServer.ReadFrameAsync(
            pair.ClientSide, 5000, CancellationToken.None);
        ElevatedKillResponse? response = ElevatedKillProtocol.DeserializeResponse(responsePayload!);
        Assert.Equal(ElevatedKillOutcome.InvalidRequest, Assert.Single(response!.Results).Outcome);
        Assert.Contains("仅执行 kill", response.Results[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空流_读帧失败_返回false不写响应()
    {
        var interop = new RecordingInterop();
        using var pair = new DuplexPipePair();        pair.ClientSide.Close();

        bool served = await ElevatedHelperServer.ServeOnceAsync(pair.ServerSide, interop, readTimeoutMs: 500);

        Assert.False(served);
        Assert.Empty(interop.TerminatedPids);
    }

    private sealed class RecordingInterop : IElevatedKillInterop
    {
        public List<uint> TerminatedPids { get; } = [];

        public Dictionary<uint, long> CreationTimes { get; } = [];

        public IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId, out int win32Error)
        {
            win32Error = 0;
            return new IntPtr(processId);
        }

        public bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc)
        {
            creationFileTimeUtc = CreationTimes.GetValueOrDefault((uint)processHandle, 0);
            return creationFileTimeUtc > 0;
        }

        public bool TerminateProcess(IntPtr processHandle, uint exitCode, out int win32Error)
        {
            TerminatedPids.Add((uint)processHandle);
            win32Error = 0;
            return true;
        }

        public uint WaitForSingleObject(IntPtr processHandle, uint milliseconds)
            => TerminatedPids.Contains((uint)processHandle) ? 0x0000u : 0x0102u;

        public bool CloseHandle(IntPtr handle) => true;
    }
}
