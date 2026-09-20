using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// 安全评估缓存测试：以 PID + StartTime 为身份，新身份才重新评估，过期身份被清理。
/// </summary>
public sealed class SafetyResultCacheTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProcessSafetyResult Result(int pid) => new()
    {
        Identity = new ProcessIdentity(pid, BaseTime),
        ProcessName = "app.exe",
        RiskLevel = RiskLevel.Normal,
        Decision = SafetyDecision.Allowed,
    };

    [Fact]
    public void 缓存_同一身份只评估一次()
    {
        var cache = new SafetyResultCache();
        var identity = new ProcessIdentity(100, BaseTime);
        int factoryCalls = 0;

        cache.GetOrAdd(identity, _ => { factoryCalls++; return Result(100); });
        cache.GetOrAdd(identity, _ => { factoryCalls++; return Result(100); });

        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void 缓存_PID相同但StartTime不同_视为不同身份()
    {
        // PID 重用场景：绝不允许把旧进程的安全结果用在新进程上
        var cache = new SafetyResultCache();
        var original = new ProcessIdentity(100, BaseTime);
        var reused = new ProcessIdentity(100, BaseTime.AddMinutes(5));
        int factoryCalls = 0;

        cache.GetOrAdd(original, _ => { factoryCalls++; return Result(100); });
        cache.GetOrAdd(reused, _ => { factoryCalls++; return Result(100); });

        Assert.Equal(2, factoryCalls);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void 缓存_Retain只保留存活身份()
    {
        var cache = new SafetyResultCache();
        var live1 = new ProcessIdentity(1, BaseTime);
        var live2 = new ProcessIdentity(2, BaseTime);
        var dead = new ProcessIdentity(3, BaseTime);
        cache.GetOrAdd(live1, _ => Result(1));
        cache.GetOrAdd(live2, _ => Result(2));
        cache.GetOrAdd(dead, _ => Result(3));

        cache.Retain([live1, live2]);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(live1, out _));
        Assert.True(cache.TryGet(live2, out _));
        Assert.False(cache.TryGet(dead, out _));
    }

    [Fact]
    public void 缓存_TryGet_未命中返回false()
    {
        var cache = new SafetyResultCache();

        Assert.False(cache.TryGet(new ProcessIdentity(9, BaseTime), out ProcessSafetyResult? result));
        Assert.Null(result);
    }

    [Fact]
    public void 缓存_Clear_清空全部()
    {
        var cache = new SafetyResultCache();
        cache.GetOrAdd(new ProcessIdentity(1, BaseTime), _ => Result(1));

        cache.Clear();

        Assert.Equal(0, cache.Count);
    }

    // ================= StartTime = null（P5.1 身份加固） =================

    [Fact]
    public void 缓存_StartTime为null_每次都重新评估_绝不当作可靠身份复用()
    {
        // PID + null 无法防止 PID 重用：直接绕过缓存，绝不长期缓存
        var cache = new SafetyResultCache();
        var unreliable = new ProcessIdentity(100, null);
        int factoryCalls = 0;

        cache.GetOrAdd(unreliable, _ => { factoryCalls++; return Result(100); });
        cache.GetOrAdd(unreliable, _ => { factoryCalls++; return Result(100); });
        cache.GetOrAdd(unreliable, _ => { factoryCalls++; return Result(100); });

        Assert.Equal(3, factoryCalls);
        Assert.Equal(0, cache.Count); // 绝不进入缓存
    }

    [Fact]
    public void 缓存_StartTime为null_TryGet永不命中()
    {
        var cache = new SafetyResultCache();

        Assert.False(cache.TryGet(new ProcessIdentity(100, null), out ProcessSafetyResult? result));
        Assert.Null(result);
    }

    [Fact]
    public void 缓存_StartTime为null与有时间的同PID_互不干扰()
    {
        var cache = new SafetyResultCache();
        var withTime = new ProcessIdentity(100, BaseTime);
        var withoutTime = new ProcessIdentity(100, null);
        int factoryCalls = 0;

        cache.GetOrAdd(withTime, _ => { factoryCalls++; return Result(100); });
        cache.GetOrAdd(withoutTime, _ => { factoryCalls++; return Result(100); });
        cache.GetOrAdd(withTime, _ => { factoryCalls++; return Result(100); });

        // 有时间的命中缓存（共 2 次评估），null 的每次都评估（3 次中占 1 次）
        Assert.Equal(2, factoryCalls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void 缓存_正常身份_仍然只评估一次()
    {
        var cache = new SafetyResultCache();
        var identity = new ProcessIdentity(100, BaseTime);
        int factoryCalls = 0;

        cache.GetOrAdd(identity, _ => { factoryCalls++; return Result(100); });
        cache.GetOrAdd(identity, _ => { factoryCalls++; return Result(100); });

        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, cache.Count);
    }
}
