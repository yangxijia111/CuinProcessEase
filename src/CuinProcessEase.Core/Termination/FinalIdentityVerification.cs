using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Termination;

/// <summary>单个 targeted identity 的 Final Rescan 验证结论。</summary>
/// <param name="Identity">本次操作曾纳入终止候选的 exact identity。</param>
/// <param name="State">Final Rescan 对该身份的四态判定。</param>
public sealed record FinalIdentityVerification(ProcessIdentity Identity, FinalIdentityState State);
