using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Termination;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>TerminationRequest 的范围授权默认值（P6.4）。</summary>
public sealed class TerminationRequestTests
{
    [Fact]
    public void 请求_ScopeConsent默认Default_绝不自动获得弱组授权()
    {
        var request = new TerminationRequest(
            "App", [new ProcessIdentity(100, DateTimeOffset.UtcNow.UtcDateTime)], DateTimeOffset.UtcNow);

        Assert.Equal(TerminationScopeConsent.Default, request.ScopeConsent);
    }
}
