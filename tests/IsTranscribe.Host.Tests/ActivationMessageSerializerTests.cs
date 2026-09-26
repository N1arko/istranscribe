using IsTranscribe.Host.Bootstrap;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class ActivationMessageSerializerTests
{
    [Fact]
    public void ActivationPayloadRoundTrips()
    {
        var message = new ActivationMessage("open-or-focus-app", DateTimeOffset.Parse("2026-04-05T12:00:00Z"));

        var payload = ActivationMessageSerializer.Serialize(message);
        var roundTrip = ActivationMessageSerializer.Deserialize(payload);

        Assert.Equal(message, roundTrip);
    }
}
