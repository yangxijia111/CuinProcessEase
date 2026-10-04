using System.Diagnostics;
using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Windows.Services;
using Xunit;

namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// Phase 10 Process Stress：100 / 300 / 500 / 800 进程环境下的快照与分组性能。
/// 需要显式开启（设置环境变量 CUINPE_STRESS=1 后运行），普通全量测试自动跳过：
/// 压测会短暂制造数百个 cmd 宿主进程，不适合每次 CI 全跑。
/// </summary>
/// <remarks>
/// 目标（开发文档 Phase 10）：高进程数下扫描与分组耗时保持可用、无异常；
/// 清理保证：测试结束杀掉自己启动的全部 cmd 宿主（绝不遗留）。
/// </remarks>
[Trait("Category", "Stress")]
public sealed class ProcessStressTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CUINPE_STRESS") == "1";

    public static IEnumerable<object[]> StressLevels()
    {
        yield return [100];
        yield return [300];
        yield return [500];
        yield return [800];
    }

    [Theory]
    [MemberData(nameof(StressLevels))]
    public async Task 高进程数环境_快照与分组保持可用且无异常(int count)
    {
        if (!Enabled)
        {
            return; // 普通全量测试自动跳过（显式设置 CUINPE_STRESS=1 才压测）
        }

        List<Process> spawned = [];
        try
        {
            for (int i = 0; i < count; i++)
            {
                // cmd /k rem：最轻量的常驻宿主（歧义进程名，恰好考验分组引擎的分类压力）
                Process proc = Process.Start(new ProcessStartInfo("cmd.exe", "/k rem")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                spawned.Add(proc);
            }

            await Task.Delay(3000); // 等待全部进程可见
            var service = new ProcessSnapshotService();

            // 两轮预热 + 一轮计时（首轮含版本资源缓存冷启动）
            await service.CaptureAsync();
            var stopwatch = Stopwatch.StartNew();
            var snapshot = await service.CaptureAsync();
            stopwatch.Stop();
            long scanMs = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            IReadOnlyList<ApplicationGroup> groups = ApplicationGroupingEngine.Group(snapshot);
            stopwatch.Stop();
            long groupMs = stopwatch.ElapsedMilliseconds;

            // 快照必须包含全部启动的 cmd（全部可见才算环境稳定）
            int cmdCount = snapshot.Processes.Count(p => p.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase));
            Assert.True(cmdCount >= spawned.Count,
                $"快照只看到 {cmdCount}/{spawned.Count} 个 cmd，环境未稳定");

            // 可用性阈值（宽松：本机负载波动大；主要验证不超时/不异常/不崩溃）
            Assert.True(scanMs < 10_000, $"快照耗时 {scanMs}ms 超阈值");
            Assert.True(groupMs < 2_000, $"分组耗时 {groupMs}ms 超阈值");

            Trace.WriteLine($"[Stress] count={count} processes={snapshot.Count} scan={scanMs}ms group={groupMs}ms groups={groups.Count}");
        }
        finally
        {
            foreach (Process proc in spawned)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // 进程已自行退出
                }

                proc.Dispose();
            }
        }
    }
}
