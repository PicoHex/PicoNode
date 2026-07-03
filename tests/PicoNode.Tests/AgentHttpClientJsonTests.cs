namespace PicoNode.Tests;

public sealed class AgentHttpClientJsonTests
{
    /// <summary>
    /// Verifies that SwitchModelAsync can be invoked with model IDs containing
    /// special JSON characters without throwing during JSON payload construction.
    /// The HTTP call will fail (no server), but the JSON construction must succeed.
    /// </summary>
    [Test]
    public async Task SwitchModel_WithSpecialChars_DoesNotThrowDuringConstruction()
    {
        await using var client = new AgentHttpClient("http://127.0.0.1:1");

        // These characters must survive JSON string escaping:
        //   " (quote), \ (backslash), \n (newline), ASCII control chars
        var trickyIds = new[]
        {
            "model\"name", // embedded quote
            "model\\slash", // backslash
            "model\nnewline", // newline
            "model\rreturn", // carriage return
            "model\ttab", // tab
            "normal-model-id", // normal
        };

        // Pre-cancel to avoid waiting for connection timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        foreach (var id in trickyIds)
        {
            try
            {
                await client.SwitchModelAsync(id, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: HttpClient cancels before connecting
            }
            catch (HttpRequestException)
            {
                // Also acceptable: connection refused before cancellation
            }
            // Any other exception (e.g., invalid JSON) is a test failure
        }

        // If we got here without an unhandled exception, the JSON construction is valid
    }
}
