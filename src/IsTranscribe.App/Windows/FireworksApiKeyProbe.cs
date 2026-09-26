using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using IsTranscribe.App.Configuration;

namespace IsTranscribe.App.Windows;

internal static class FireworksApiKeyProbe
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly Uri ProbeUri = new("https://api.fireworks.ai/v1/accounts/fireworks/models?pageSize=1");

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.api-key
    public static async Task<WindowOperationResult> TestAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return WindowOperationResult.Fail("Enter the API key first.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ProbeUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        try
        {
            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return WindowOperationResult.Ok("Fireworks API responded successfully. The key looks valid.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return WindowOperationResult.Fail("Fireworks rejected the key. Check that it is correct and active.");
            }

            return WindowOperationResult.Fail($"Fireworks API returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (TaskCanceledException)
        {
            return WindowOperationResult.Fail("Fireworks API did not respond in time.");
        }
        catch (HttpRequestException exception)
        {
            return WindowOperationResult.Fail($"Could not reach Fireworks API: {exception.Message}");
        }
    }
}
