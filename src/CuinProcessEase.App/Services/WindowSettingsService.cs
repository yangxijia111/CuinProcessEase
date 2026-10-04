using System.IO;
using System.Text.Json;
using System.Windows;

namespace CuinProcessEase.App.Services;

/// <summary>窗口位置/尺寸持久化模型（%LOCALAPPDATA%\CuinProcessEase\window.json）。</summary>
public sealed class WindowSettings
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>
/// 窗口设置的保存与恢复（Phase 9：窗口尺寸恢复）。
/// </summary>
/// <remarks>
/// - 恢复时做屏幕边界校验：位置不在任何可视屏幕内（显示器变更/拔除）则回退居中默认；
/// - 尺寸低于窗口 MinWidth/MinHeight 时不应用；
/// - 保存/读 取失败一律静默回退默认（设置文件绝不阻塞启动）。
/// </remarks>
public static class WindowSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CuinProcessEase", "window.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static WindowSettings? TryLoad()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(SettingsPath), JsonOptions);
        }
        catch
        {
            // 设置损坏/读取失败 → 回退默认，绝不阻塞启动
            return null;
        }
    }

    public static void Save(Window window)
    {
        try
        {
            // 最大化时保存还原尺寸（RestoreBounds），下次启动先正常显示再最大化
            bool maximized = window.WindowState == WindowState.Maximized;
            Rect bounds = maximized ? window.RestoreBounds : new Rect(window.Left, window.Top, window.Width, window.Height);

            var settings = new WindowSettings
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = maximized,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // 保存失败（磁盘满/权限）不影响退出
        }
    }

    /// <summary>把保存的设置应用到窗口（越界回退默认，不应用非法尺寸）。</summary>
    public static void Apply(Window window, WindowSettings? settings)
    {
        if (settings is null
            || settings.Width < window.MinWidth
            || settings.Height < window.MinHeight)
        {
            return;
        }

        // 虚拟屏幕边界校验：保存的位置必须与整体可视区域大量重叠（显示器变更/拔除后回退默认）
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var saved = new Rect(settings.Left, settings.Top, settings.Width, settings.Height);
        Rect intersection = Rect.Intersect(virtualScreen, saved);
        if (intersection.IsEmpty || intersection.Width < settings.Width * 0.3)
        {
            return;
        }

        window.Left = settings.Left;
        window.Top = settings.Top;
        window.Width = settings.Width;
        window.Height = settings.Height;
        if (settings.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}
