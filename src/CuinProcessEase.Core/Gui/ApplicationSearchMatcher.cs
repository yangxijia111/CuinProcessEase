using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Gui;

/// <summary>
/// 应用搜索匹配器：在应用级搜索，命中即整组显示——
/// 搜 "chrome" 得到的是一个 Google Chrome 应用组，而不是 16 个 chrome.exe。
/// </summary>
public static class ApplicationSearchMatcher
{
    /// <summary>
    /// 判断应用组是否命中搜索文本（大小写不敏感的子串匹配）。
    /// 搜索范围为：显示名、产品名、公司名、组内每个进程的进程名与可执行路径。
    /// 空白搜索文本视为命中全部。
    /// </summary>
    public static bool Matches(ApplicationGroup group, string? searchText)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return BuildHaystack(group).Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构建应用组的搜索索引串。UI 层可缓存该串（组内容不变则不重建），
    /// 每秒刷新时仅做一次子串查找，避免逐字段重复分配。
    /// </summary>
    public static string BuildHaystack(ApplicationGroup group)
    {
        var identity = group.Identity;
        var builder = new System.Text.StringBuilder(256);
        builder.Append(identity.DisplayName).Append('\n');

        if (identity.ProductName is { } product)
        {
            builder.Append(product).Append('\n');
        }

        if (identity.CompanyName is { } company)
        {
            builder.Append(company).Append('\n');
        }

        if (identity.MainExecutable is { } mainExe)
        {
            builder.Append(mainExe).Append('\n');
        }

        foreach (ProcessSnapshot process in group.Processes)
        {
            builder.Append(process.Name).Append('\n');
            if (process.ExecutablePath is { } path)
            {
                builder.Append(path).Append('\n');
            }
        }

        return builder.ToString();
    }
}
