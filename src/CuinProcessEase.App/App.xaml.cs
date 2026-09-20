using System.IO;
using System.Windows;

namespace CuinProcessEase.App;

/// <summary>
/// 应用程序入口：装配主题字典 + 全局异常兜底（记录到日志，不让 UI 静默崩溃）。
/// </summary>
public partial class App : Application
{
    /// <summary>UI 线程未处理异常日志（稳定性验收依据：10 分钟运行应保持不存在/为空）。</summary>
    public static readonly string DispatcherLogPath = Path.Combine(
        Path.GetTempPath(), "CuinProcessEase.dispatcher.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            File.Delete(DispatcherLogPath);
        }
        catch
        {
            // 无旧日志或清理失败都不影响启动
        }

        base.OnStartup(e);
    }

    private void Application_DispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
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
