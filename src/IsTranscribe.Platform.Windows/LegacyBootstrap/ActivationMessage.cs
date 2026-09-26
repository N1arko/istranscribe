using System.Text.Json;

namespace IsTranscribe.Host.Bootstrap;

public sealed record ActivationMessage(
    string Intent,
    DateTimeOffset RequestedAtUtc)
{
    public static ActivationMessage OpenOrFocusApp() =>
        new("open-or-focus-app", DateTimeOffset.UtcNow);
}

public static class ActivationMessageSerializer
{
    public static string Serialize(ActivationMessage message) =>
        JsonSerializer.Serialize(message);

    public static ActivationMessage Deserialize(string payload) =>
        JsonSerializer.Deserialize<ActivationMessage>(payload)
        ?? throw new InvalidOperationException("Activation payload is invalid.");
}
