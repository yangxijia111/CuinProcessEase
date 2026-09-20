namespace CuinProcessEase.Core.Grouping;

/// <summary>
/// 应用分组的可信度。
/// </summary>
/// <remarks>
/// 原则：宁可拆开，不要错合。低可信度关系禁止强制合并。
/// </remarks>
public enum GroupingConfidence
{
    /// <summary>无法判断（如单进程组，无任何合并依据）。</summary>
    Unknown = 0,

    /// <summary>低可信度：单独存在时不足以合并。</summary>
    Low = 1,

    /// <summary>中等可信度：可作为合并依据（如相同具体安装目录）。</summary>
    Medium = 2,

    /// <summary>高可信度（如已验证的父子进程树关系、相同 exe 路径）。</summary>
    High = 3,

    /// <summary>极高可信度（如相同软件包身份；预留）。</summary>
    VeryHigh = 4,
}
