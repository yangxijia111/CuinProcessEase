namespace CuinProcessEase.Core.Grouping;

/// <summary>
/// 进程名规则：识别"歧义进程名"（通用运行时 / 通用宿主），并过滤无效的产品显示名。
/// </summary>
/// <remarks>
/// 歧义进程的父子关系与同名合并需要附加证据，防止把不相关软件错误聚合。
/// </remarks>
public static class ProcessNameRules
{
    /// <summary>
    /// 歧义进程名集合：通用语言运行时 + 通用 Shell 宿主 + 常驻系统宿主。
    /// 这些进程作为父节点或同路径实例时，都不能仅凭树边/路径直接传递"同应用"关系。
    /// </summary>
    private static readonly HashSet<string> AmbiguousProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // 通用运行时
        "python.exe", "pythonw.exe", "node.exe", "java.exe", "javaw.exe", "dotnet.exe",
        // 脚本宿主
        "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "wscript.exe", "cscript.exe",
        // 系统 Shell / 服务宿主
        "explorer.exe", "svchost.exe", "services.exe", "smss.exe", "csrss.exe",
        "wininit.exe", "winlogon.exe", "dwm.exe", "sihost.exe", "taskhostw.exe",
        "runtimebroker.exe", "ctfmon.exe", "dllhost.exe", "wermgr.exe",
    };

    /// <summary>
    /// 明显无效的产品名（泛化占位文本），不能作为 SameProduct / DisplayName 依据。
    /// </summary>
    private static readonly HashSet<string> InvalidProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "",
        ".exe",
        "windows",
        "microsoft windows",
        "microsoft® windows® operating system",
        "microsoft windows operating system",
        "operating system",
        "@string_table",
    };

    /// <summary>进程名（如 chrome.exe）是否为歧义进程（通用运行时 / 宿主）。</summary>
    public static bool IsAmbiguousProcessName(string? processName)
        => processName is not null && AmbiguousProcessNames.Contains(processName.Trim());

    /// <summary>
    /// 产品名是否有效：非空、非纯 ".exe"、非泛化系统占位文本。
    /// exeFileName 传入时，产品名等于 exe 文件本身也不算有效（无信息量）。
    /// </summary>
    public static bool IsValidProductName(string? productName, string? exeFileName = null)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return false;
        }

        string trimmed = productName.Trim();

        if (InvalidProductNames.Contains(trimmed))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(exeFileName)
            && string.Equals(trimmed, exeFileName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>从完整路径提取文件名；无效路径返回 null。</summary>
    public static string? GetFileName(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        try
        {
            return Path.GetFileName(fullPath.Trim());
        }
        catch
        {
            return null;
        }
    }
}
