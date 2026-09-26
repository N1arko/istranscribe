namespace IsTranscribe.Application.Runtime;

/// <summary>
/// Stable product states shared by the runtime and every desktop presentation.
/// </summary>
/// <remarks>
/// @spec spec://common/PROP-006-release-v2-product-canon#product-model.states
/// </remarks>
public enum ApplicationActivityState
{
    Listening,
    Suspected,
    AwaitingConfirmation,
    Recording,
    Processing,
    Ready,
    AttentionRequired,
    Paused
}
