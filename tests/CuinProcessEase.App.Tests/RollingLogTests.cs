using System.IO;
using CuinProcessEase.App.Services;
using Xunit;

namespace CuinProcessEase.App.Tests;

/// <summary>Phase 10 滚动日志：2MB 上限自动滚动、文件总数上限 5。</summary>
public sealed class RollingLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuinpe-log-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void 超限写入_自动滚动_文件数不超过上限()
    {
        var listener = new RollingTraceListener(_dir);

        // 每行 ~1KB，写入约 3×2MB，强制多轮滚动
        string line = new('x', 1024);
        for (int i = 0; i < 3 * 2100; i++)
        {
            listener.WriteLine($"[{i}] {line}");
        }

        string[] files = Directory.GetFiles(_dir, "app*.log");
        Assert.True(files.Length <= RollingTraceListener.MaxFiles,
            $"日志文件数 {files.Length} 超过上限 {RollingTraceListener.MaxFiles}");

        // 活动文件存在且未超限过多（滚动失败容差：允许单次缓冲超出，但不得无限增长）
        var active = new FileInfo(Path.Combine(_dir, "app.log"));
        Assert.True(active.Exists);
        Assert.True(active.Length < RollingTraceListener.MaxFileBytes * 2,
            $"活动文件 {active.Length} 字节未按预期滚动");
    }

    [Fact]
    public void 少量写入_全部落在活动文件()
    {
        var listener = new RollingTraceListener(_dir);
        listener.WriteLine("hello-1");
        listener.WriteLine("hello-2");

        string content = File.ReadAllText(Path.Combine(_dir, "app.log"));
        Assert.Contains("hello-1", content, StringComparison.Ordinal);
        Assert.Contains("hello-2", content, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_dir, "app*.log"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败无碍
        }
    }
}
