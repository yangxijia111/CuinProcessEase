using CuinProcessEase.Core.Termination;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// ProcessIdentityMatcher 精确身份匹配测试：
/// CreationTime 以原始 FILETIME 逐位比较，绝无任何时间容差（1 tick 也不允许）。
/// </summary>
public sealed class ProcessIdentityMatcherTests
{
    /// <summary>合法 FILETIME 基准值（2026-01-01 附近，100ns 单位）。</summary>
    private const long BaseFileTime = 133_528_256_000_000_000;

    [Theory]
    [InlineData(1)]         // 1 tick（100ns）
    [InlineData(2)]         // 2 tick
    [InlineData(10_000)]    // 1ms = 10,000 ticks
    [InlineData(5_000_000)] // 500ms
    [InlineData(10_000_000)]// 1s（P6 旧实现曾被允许的容差）
    public void 匹配_期望与实测存在差值_一律视为身份不匹配(long delta)
    {
        Assert.False(ProcessIdentityMatcher.IsExactProcessIdentityMatch(
            BaseFileTime, BaseFileTime + delta));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]      // 1ms
    [InlineData(10_000_000)]  // 1s
    public void 匹配_差值为负_同样视为身份不匹配(long delta)
    {
        Assert.False(ProcessIdentityMatcher.IsExactProcessIdentityMatch(
            BaseFileTime, BaseFileTime - delta));
    }

    [Fact]
    public void 匹配_完全相同_视为同一进程()
    {
        Assert.True(ProcessIdentityMatcher.IsExactProcessIdentityMatch(
            BaseFileTime, BaseFileTime));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 匹配_期望值非法_绝不视为匹配(long expected)
    {
        Assert.False(ProcessIdentityMatcher.IsExactProcessIdentityMatch(expected, expected));
    }
}
