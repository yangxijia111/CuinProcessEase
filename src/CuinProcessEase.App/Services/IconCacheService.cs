using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CuinProcessEase.App.Services;

/// <summary>
/// 应用图标缓存：按 exe 完整路径提取并缓存图标（冻结的 BitmapSource，跨线程安全）。
/// </summary>
/// <remarks>
/// - 同一路径只提取一次；提取失败也缓存（null），绝不每秒重试；
/// - 提取在线程池执行，结果经 Dispatcher 回 UI 线程，不阻塞界面；
/// - 失败或无路径的行由 UI 显示首字母占位，无需默认图标资源。
/// </remarks>
public sealed class IconCacheService
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dispatcher _dispatcher;

    public IconCacheService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>是否已有缓存结果（含失败结果）。</summary>
    public bool IsCached(string path) => _cache.ContainsKey(path);

    /// <summary>取缓存图标；未命中返回 false。</summary>
    public bool TryGet(string path, out ImageSource? icon) => _cache.TryGetValue(path, out icon);

    /// <summary>
    /// 异步获取图标，完成后在 UI 线程回调（无论成功与否都恰好回调一次）。
    /// </summary>
    public void BeginGet(string path, Action<ImageSource?> onLoaded)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(onLoaded);

        if (_cache.TryGetValue(path, out ImageSource? cached))
        {
            onLoaded(cached);
            return;
        }

        _ = Task.Run(() =>
        {
            ImageSource? icon = Extract(path);
            _cache[path] = icon;
            _dispatcher.BeginInvoke(() => onLoaded(icon), DispatcherPriority.Background);
        });
    }

    /// <summary>从 exe 提取 32×32 图标并冻结；任何失败返回 null。</summary>
    private static ImageSource? Extract(string path)
    {
        try
        {
            // 仅本地磁盘文件可提取；UWP/虚拟路径直接失败走占位
            if (!File.Exists(path))
            {
                return null;
            }

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze(); // 冻结后可跨线程共享，且 UI 线程渲染零拷贝
                return source;
            }
            finally
            {
                _ = DeleteObject(hBitmap);
            }
        }
        catch
        {
            // 文件被锁 / 无图标资源 / 非 Win32 程序：显示首字母占位
            return null;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
