using System.Text.Json;

namespace CuinProcessEase.Core.Elevated;

/// <summary>
/// 主程序 ↔ Elevated Helper 的跨进程终止协议（JSON、长度前缀帧）。
/// </summary>
/// <remarks>
/// Helper 唯一职责是执行本协议定义的有限高权限操作：
/// 不执行任意命令、不 Shell Execute、不运行用户指定的 CMD、不加载任意 DLL。
/// <see cref="ElevatedKillRequest.Operation"/> 必须为 <see cref="KillOperation"/>，
/// 其他操作一律 <see cref="ElevatedKillOutcome.InvalidRequest"/> 拒绝。
/// </remarks>
public sealed record ElevatedKillRequest(
    string Operation,
    string OperationId,
    IReadOnlyList<ElevatedKillTarget> Targets)
{
    /// <summary>协议唯一支持的操作名。</summary>
    public const string KillOperation = "kill";

    /// <summary>单次请求的终止目标数上限（防御性上限，正常应用组远小于此值）。</summary>
    public const int MaxTargets = 64;
}

/// <summary>单个终止目标：PID + 预期 CreationTime 原始 FILETIME（Helper 必须重新验证，绝不盲目信任）。</summary>
public sealed record ElevatedKillTarget(int ProcessId, long ExpectedStartTimeUtcFileTime);

/// <summary>单个目标的执行结果。</summary>
public sealed record ElevatedKillTargetResult(int ProcessId, ElevatedKillOutcome Outcome, string? Detail);

/// <summary>整批执行结果（与请求 OperationId 一一对应）。</summary>
public sealed record ElevatedKillResponse(
    string OperationId,
    IReadOnlyList<ElevatedKillTargetResult> Results);

/// <summary>协议序列化 / 校验（两端共用，纯逻辑可单测）。</summary>
public static class ElevatedKillProtocol
{
    /// <summary>单帧 payload 上限（1 MB），防御异常客户端。</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // 两端同一程序集家族，保持 PascalCase 默认即可；禁用宽松选项保证严格契约
    };

    /// <summary>序列化请求为 UTF-8 JSON。</summary>
    public static byte[] SerializeRequest(ElevatedKillRequest request)
        => JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);

    /// <summary>反序列化请求；格式非法返回 null（绝不抛异常跨协议边界）。</summary>
    public static ElevatedKillRequest? DeserializeRequest(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<ElevatedKillRequest>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>序列化响应为 UTF-8 JSON。</summary>
    public static byte[] SerializeResponse(ElevatedKillResponse response)
        => JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions);

    /// <summary>反序列化响应；格式非法返回 null。</summary>
    public static ElevatedKillResponse? DeserializeResponse(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<ElevatedKillResponse>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 语义校验：操作名、目标数、PID 与预期 FILETIME 范围。
    /// 返回 null 表示合法；否则返回人类可读的拒绝原因（写入 InvalidRequest 响应）。
    /// </summary>
    public static string? ValidateRequest(ElevatedKillRequest? request)
    {
        if (request is null)
        {
            return "请求体为空或格式非法。";
        }

        if (!string.Equals(request.Operation, ElevatedKillRequest.KillOperation, StringComparison.Ordinal))
        {
            return $"不支持的操作 “{request.Operation}”：Helper 仅执行 kill。";
        }

        if (string.IsNullOrWhiteSpace(request.OperationId))
        {
            return "缺少 OperationId。";
        }

        if (request.Targets.Count == 0)
        {
            return "终止目标列表为空。";
        }

        if (request.Targets.Count > ElevatedKillRequest.MaxTargets)
        {
            return $"终止目标数 {request.Targets.Count} 超过上限 {ElevatedKillRequest.MaxTargets}。";
        }

        foreach (ElevatedKillTarget target in request.Targets)
        {
            if (target.ProcessId <= 0)
            {
                return $"目标 PID {target.ProcessId} 非法。";
            }

            if (target.ExpectedStartTimeUtcFileTime <= 0)
            {
                return $"目标 PID {target.ProcessId} 缺少有效的预期启动时间。";
            }
        }

        return null;
    }
}
