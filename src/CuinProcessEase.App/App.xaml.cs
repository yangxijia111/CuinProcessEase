using System.Diagnostics;
using System.IO;
using System.Windows;
using CuinProcessEase.App.Services;

namespace CuinProcessEase.App;

/// <summary>
/// 应用程序入口：装配主题字典 + 滚动日志 + 全局异常兜底（记录到日志，不让 UI 静默崩溃）。
/// </summary>
/// <remarks>
/// Phase 10 稳定性约定：
/// - 日志：logs/（%LOCALAPPDATA%\CuinProcessEase\logs），5 文件 × 2 MB 自动滚动；
/// - 崩溃策略：未处理异常记录后保持存活；崩溃绝不影响系统其他进程；
///   程序自身绝不自动重放上一次 Kill（终止引擎无持久状态，每次操作都是用户显式发起）。
/// </remarks>
public partial class App : Application
{
    /// <summary>UI 线程未处理异常日志（稳定性验收依据：长时间运行应保持为空）。</summary>
    public static readonly string DispatcherLogPath = Path.Combine(
        RollingTraceListener.DefaultDirectory(), "dispatcher.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RollingTraceListener.DefaultDirectory());
            File.Delete(DispatcherLogPath);
        }
        catch
        {
            // 无旧日志或清理失败都不影响启动
        }

        // 全部 Trace 输出（终止链路诊断等）进入滚动日志：5 × 2 MB 上限，长期运行不撑爆磁盘
        Trace.Listeners.Add(new RollingTraceListener());
        Trace.AutoFlush = true;

        base.OnStartup(e);
    }

    private void Application_DispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Trace.WriteLine($"[DispatcherUnhandled] {e.Exception}");
            File.AppendAllText(DispatcherLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {e.Exception}\n\n");
        }
        catch
        {
            // 日志写入失败只能吞掉（避免异常处理本身再抛异常）
        }

        // 保持应用存活；刷新循环的异常在 MainViewModel 内部已兜底
        e.Handled = true;
    }
}
