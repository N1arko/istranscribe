namespace IsTranscribe.Application.Recovery;

public sealed record RecoveryLaunchDecision(
    bool RequiresAttention,
    string Summary,
    string? Detail = null)
{
    public static RecoveryLaunchDecision None { get; } = new(false, "No recovery action is required.");
}

public interface IRecoveryCoordinator
{
    ValueTask<RecoveryLaunchDecision> EvaluateAsync(CancellationToken cancellationToken);
}

public sealed class NullRecoveryCoordinator : IRecoveryCoordinator
{
    public ValueTask<RecoveryLaunchDecision> EvaluateAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecoveryLaunchDecision.None);
}
