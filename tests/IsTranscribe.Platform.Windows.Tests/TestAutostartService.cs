using IsTranscribe.Core.Platform;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Registry-free test boundary for FEAT-013 launch-at-login behavior.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </remarks>
internal sealed class TestAutostartService(bool enabled = false) : IAutostartService
{
    public bool IsEnabled { get; private set; } = enabled;

    public bool FailNextSet { get; set; }

    public bool MutateBeforeFailure { get; set; }

    public int SetCallCount { get; private set; }

    public ValueTask<AutostartRegistrationState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AutostartRegistrationState(IsEnabled));
    }

    public ValueTask<AutostartRegistrationState> SetEnabledAsync(
        bool requestedEnabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetCallCount++;
        if (FailNextSet)
        {
            FailNextSet = false;
            if (MutateBeforeFailure)
            {
                IsEnabled = requestedEnabled;
            }

            return ValueTask.FromException<AutostartRegistrationState>(
                new InvalidOperationException("Injected autostart failure."));
        }

        IsEnabled = requestedEnabled;
        return ValueTask.FromResult(new AutostartRegistrationState(IsEnabled));
    }
}
