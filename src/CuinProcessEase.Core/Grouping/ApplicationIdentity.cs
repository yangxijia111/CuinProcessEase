namespace CuinProcessEase.Core.Grouping;

/// <summary>
/// 应用身份：一个软件的可识别信息。
/// </summary>
public sealed class ApplicationIdentity
{
    /// <summary>显示名称（按 ProductName → FileDescription → 根进程名回退挑选）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>主可执行文件完整路径；无法可靠判断时为 null（多 Root 且路径不一致）。</summary>
    public string? MainExecutable { get; init; }

    /// <summary>软件专属安装目录（足够具体，公共目录不算）。</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>产品名（组内多数一致的有效值）。</summary>
    public string? ProductName { get; init; }

    /// <summary>公司名（组内多数一致的有效值）。</summary>
    public string? CompanyName { get; init; }
}
