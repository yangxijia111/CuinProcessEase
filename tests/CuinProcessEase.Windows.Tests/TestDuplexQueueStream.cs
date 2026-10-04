namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// 测试用纯托管双工流对：写入即时入对端队列。用于驱动 ElevatedHelperServer
/// 协议往返（OS 命名管道传输由 Helper 进程真实路径覆盖，测试主机环境下行为不稳定）。
/// </summary>
internal static class TestDuplexQueueStream
{
    public static (Stream A, Stream B) CreatePair() => QueueStream.CreatePair();

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
}
