using CuinProcessEase.Core.Elevated;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Termination;
using Xunit;

namespace CuinProcessEase.Core.Tests.Elevated;

/// <summary>Elevated Helper 协议：序列化往返与语义校验决策矩阵。</summary>
public sealed class ElevatedKillProtocolTests
{
    private static ElevatedKillRequest SampleRequest(params int[] pids) => new(
        ElevatedKillRequest.KillOperation,
        $"op-{Guid.NewGuid():N}",
        pids.Select(p => new ElevatedKillTarget(p, 133_528_256_000_000_000L + p)).ToList());

    [Fact]
    public void 请求响应_序列化往返_字段无损()
    {
        ElevatedKillRequest request = SampleRequest(1234, 5678);

        byte[] payload = ElevatedKillProtocol.SerializeRequest(request);
        ElevatedKillRequest? roundTrip = ElevatedKillProtocol.DeserializeRequest(payload);

        Assert.NotNull(roundTrip);
        Assert.Equal(request.Operation, roundTrip.Operation);
        Assert.Equal(request.OperationId, roundTrip.OperationId);
        Assert.Equal(request.Targets, roundTrip.Targets);

        var response = new ElevatedKillResponse(request.OperationId,
        [
            new ElevatedKillTargetResult(1234, ElevatedKillOutcome.Terminated, "已强制终止并确认退出。"),
            new ElevatedKillTargetResult(5678, ElevatedKillOutcome.IdentityMismatch, "PID 已被复用。"),
        ]);
        ElevatedKillResponse? responseRoundTrip =
            ElevatedKillProtocol.DeserializeResponse(ElevatedKillProtocol.SerializeResponse(response));

        Assert.NotNull(responseRoundTrip);
        Assert.Equal(response.OperationId, responseRoundTrip.OperationId);
        Assert.Equal(response.Results, responseRoundTrip.Results);
    }

    [Fact]
    public void 非法JSON_反序列化返回null_绝不抛异常()
    {
        byte[] garbage = [0xDE, 0xAD, 0xBE, 0xEF];

        Assert.Null(ElevatedKillProtocol.DeserializeRequest(garbage));
        Assert.Null(ElevatedKillProtocol.DeserializeResponse(garbage));
    }

    [Fact]
    public void 非kill操作_校验拒绝()
    {
        var request = new ElevatedKillRequest("shell-execute", "op-1",
            [new ElevatedKillTarget(100, 133_528_256_000_000_000L)]);

        string? error = ElevatedKillProtocol.ValidateRequest(request);

        Assert.NotNull(error);
        Assert.Contains("仅执行 kill", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非法PID_校验拒绝(int pid)
    {
        var request = new ElevatedKillRequest(ElevatedKillRequest.KillOperation, "op-1",
            [new ElevatedKillTarget(pid, 133_528_256_000_000_000L)]);

        Assert.NotNull(ElevatedKillProtocol.ValidateRequest(request));
    }

    [Fact]
    public void 预期启动时间缺失_校验拒绝()
    {
        var request = new ElevatedKillRequest(ElevatedKillRequest.KillOperation, "op-1",
            [new ElevatedKillTarget(100, 0)]);

        string? error = ElevatedKillProtocol.ValidateRequest(request);

        Assert.NotNull(error);
        Assert.Contains("启动时间", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 空目标列表_校验拒绝()
    {
        var request = new ElevatedKillRequest(ElevatedKillRequest.KillOperation, "op-1", []);

        Assert.NotNull(ElevatedKillProtocol.ValidateRequest(request));
    }

    [Fact]
    public void 目标数超过上限_校验拒绝()
    {
        var targets = Enumerable.Range(1, ElevatedKillRequest.MaxTargets + 1)
            .Select(p => new ElevatedKillTarget(p, 133_528_256_000_000_000L))
            .ToList();
        var request = new ElevatedKillRequest(ElevatedKillRequest.KillOperation, "op-1", targets);

        string? error = ElevatedKillProtocol.ValidateRequest(request);

        Assert.NotNull(error);
        Assert.Contains("上限", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 合法请求_校验通过()
    {
        Assert.Null(ElevatedKillProtocol.ValidateRequest(SampleRequest(1, 2, 3)));
    }
}

/// <summary>Helper 结果 → 主程序终止结果模型映射。</summary>
public sealed class ElevatedOutcomeMapperTests
{
    [Theory]
    [InlineData(ElevatedKillOutcome.Terminated, ProcessTerminationStatus.Terminated)]
    [InlineData(ElevatedKillOutcome.TimedOut, ProcessTerminationStatus.TimedOut)]
    [InlineData(ElevatedKillOutcome.AlreadyExited, ProcessTerminationStatus.AlreadyExited)]
    [InlineData(ElevatedKillOutcome.IdentityMismatch, ProcessTerminationStatus.IdentityMismatch)]
    [InlineData(ElevatedKillOutcome.AccessDenied, ProcessTerminationStatus.AccessDenied)]
    [InlineData(ElevatedKillOutcome.Failed, ProcessTerminationStatus.Failed)]
    [InlineData(ElevatedKillOutcome.InvalidRequest, ProcessTerminationStatus.Failed)]
    public void 每种Helper结果_映射到语义等价的主程序状态(
        ElevatedKillOutcome outcome, ProcessTerminationStatus expected)
    {
        var identity = new ProcessIdentity(4321, DateTime.FromFileTimeUtc(133_528_256_000_000_000L));
        var helperResult = new ElevatedKillTargetResult(4321, outcome, "detail");

        ProcessTerminationResult mapped = ElevatedOutcomeMapper.ToProcessResult(helperResult, identity, "x.exe");

        Assert.Equal(expected, mapped.Result);
        Assert.Equal(4321, mapped.Pid);
        Assert.Equal(identity, mapped.ExpectedIdentity);
        Assert.False(string.IsNullOrWhiteSpace(mapped.Message));
    }
}
