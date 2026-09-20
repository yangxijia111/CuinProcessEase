namespace CuinProcessEase.Core.Grouping;

/// <summary>
/// 安装目录规则：判定一个目录是否"足够具体"、可作为同一软件的分组依据。
/// </summary>
/// <remarks>
/// 公共目录（Windows / System32 / Program Files 根 / 用户根 / Temp 等）
/// 绝不能作为"同一软件"的直接分组依据，否则会把大量不相关软件错误合并。
/// </remarks>
public static class InstallDirectoryRules
{
    /// <summary>公共目录黑名单（规范化后小写比较；不含尾部斜杠）。</summary>
    private static readonly HashSet<string> CommonDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        @"c:\windows",
        @"c:\windows\system32",
        @"c:\windows\syswow64",
        @"c:\windows\system",
        @"c:\windows\winsxs",
        @"c:\windows\temp",
        @"c:\program files",
        @"c:\program files (x86)",
        @"c:\program files\common files",
        @"c:\program files (x86)\common files",
        @"c:\programdata",
        @"c:\programdata\microsoft\windows",
        @"c:\users\public",
        @"c:\users\public\desktop",
    };

    /// <summary>
    /// 判断目录是否为足够具体的软件安装目录。
    /// 要求：非空、非公共目录、深度至少 3 段（如 C:\Program Files\AppX）、
    /// 路径中不含 Temp 段、不是用户目录根（C:\Users\X）。
    /// </summary>
    public static bool IsSpecificApplicationDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string normalized = Normalize(directory);

        if (CommonDirectories.Contains(normalized))
        {
            return false;
        }

        // 按反斜杠分段（忽略尾部空段）
        string[] segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        // 用户目录根（C:\Users\<name>）不具体
        if (segments.Length == 2
            && segments[0].EndsWith(':')
            && segments[0].Length == 2
            && string.Equals(segments[1], "users", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segments.Length == 3
            && string.Equals(segments[1], "users", StringComparison.OrdinalIgnoreCase))
        {
            // C:\Users\<name>：用户根，不具体
            return false;
        }

        // 任意层级包含 Temp 段：临时目录不作安装目录
        for (int i = 1; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], "temp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segments[i], "tmp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // C:\ 根下第一级目录（如 C:\Tools）仍过浅：需 C:\A\B 起步
        return segments.Length >= 3;
    }

    /// <summary>提取 exe 的安装目录；路径无效时返回 null。</summary>
    public static string? GetApplicationDirectory(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            string? directory = Path.GetDirectoryName(executablePath.Trim());
            return string.IsNullOrWhiteSpace(directory) ? null : directory.TrimEnd('\\');
        }
        catch
        {
            return null;
        }
    }

    /// <summary>规范化目录：去首尾空白与尾部反斜杠（供黑名单比较）。</summary>
    public static string Normalize(string directory)
        => directory.Trim().TrimEnd('\\');
}
