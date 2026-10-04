using System.Diagnostics;
using System.IO;

namespace CuinProcessEase.App.Services;

/// <summary>
/// 滚动文件 TraceListener（Phase 10：logs/ 目录，最多 5 个文件 × 2 MB 自动滚动）。
/// </summary>
/// <remarks>
/// - 活动日志 app.log；超 2 MB 时整链滚动：app.4 删除，app.3→app.4 … app.log→app.1；
/// - 任何 IO 失败静默忽略（日志绝不影响主功能，更不能在异常路径上再抛异常）；
/// - 写入加锁（Trace 可能来自任意线程，含终止引擎的后台线程）。
/// </remarks>
public sealed class RollingTraceListener : TraceListener
{
    /// <summary>单文件大小上限（字节）。</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>文件总数上限（活动文件 + 4 个滚动历史）。</summary>
    public const int MaxFiles = 5;

    private readonly string _directory;

    private readonly string _activePath;

    private readonly Lock _lock = new();

    private long _activeLength;

    private bool _lengthKnown;

    public RollingTraceListener()
        : this(DefaultDirectory())
    {
    }

    /// <summary>测试构造：指定日志目录。</summary>
    public RollingTraceListener(string directory)
    {
        _directory = directory;
        _activePath = Path.Combine(directory, "app.log");
    }

    /// <summary>默认日志目录：%LOCALAPPDATA%\CuinProcessEase\logs（与 Elevated Helper 日志同目录）。</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CuinProcessEase", "logs");

    public override void Write(string? message)
    {
        ArgumentNullException.ThrowIfNull(message);
        WriteInternal(message);
    }

    public override void WriteLine(string? message)
    {
        ArgumentNullException.ThrowIfNull(message);
        WriteInternal(message + Environment.NewLine);
    }

    private void WriteInternal(string text)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_directory);
                EnsureLengthKnown();
                if (_activeLength + text.Length > MaxFileBytes)
                {
                    RollFiles();
                }

                File.AppendAllText(_activePath, text);
                _activeLength += text.Length;
            }
        }
        catch
        {
            // 日志 IO 失败静默忽略：绝不影响主功能，也不在异常路径上再抛
        }
    }

    private void EnsureLengthKnown()
    {
        if (_lengthKnown)
        {
            return;
        }

        FileInfo info = new(_activePath);
        _activeLength = info.Exists ? info.Length : 0;
        _lengthKnown = true;
    }

    /// <summary>整链滚动：删除最旧历史，逐级重命名，活动文件从零开始。</summary>
    private void RollFiles()
    {
        try
        {
            string oldest = HistoryPath(MaxFiles - 1);
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int i = MaxFiles - 2; i >= 1; i--)
            {
                string source = HistoryPath(i);
                if (File.Exists(source))
                {
                    File.Move(source, HistoryPath(i + 1));
                }
            }

            if (File.Exists(_activePath))
            {
                File.Move(_activePath, HistoryPath(1));
            }
        }
        catch
        {
            // 滚动失败（文件被占用等）：继续写活动文件（可能超限），绝不丢日志
        }

        _activeLength = 0;
        _lengthKnown = true;
    }

    private string HistoryPath(int index) => Path.Combine(_directory, $"app.{index}.log");
}
