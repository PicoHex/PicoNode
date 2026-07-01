namespace PicoNode.Agent.Tests.PicoAgentReloadTests;

/// <summary>
/// Agent.RunAsync accepted a CancellationToken parameter but only propagated it
/// into ListenAsync. The subsequent wait on TaskCompletionSource never observed
/// the token, so callers who pass a token expecting shutdown-on-cancel (e.g.
/// hosted services, integration tests, harnesses) would hang until Ctrl+C or
/// process exit.
///
/// Fix contract: cancelling <paramref name="ct"/> after ListenAsync succeeds
/// must cause RunAsync to complete gracefully and StopAsync to be invoked.
/// </summary>
public sealed class AgentRunAsyncCancellationTests
{
    [Test]
    public async Task RunAsync_CompletesWhenCancellationTokenFires()
    {
        var config = new AgentConfig
        {
            Providers = new()
            {
                ["test"] = new ProviderEntry
                {
                    ApiKey = "sk-test",
                    ApiFormat = "openai",
                    BaseUrl = "https://api.test/v1",
                },
            },
            Model = "gpt-4",
        };
        await using var agent = await global::PicoAgent.Agent.CreateAsync(config);

        using var cts = new CancellationTokenSource();
        // Pick a free port ourselves — some test hosts reject bind(port=0)
        // through the pipeline options in ways raw sockets don't.
        var port = GetFreePort();
        var runTask = agent.RunAsync($"http://127.0.0.1:{port}", cts.Token);

        // Give ListenAsync a moment to bind the port before cancelling.
        await Task.Delay(200);
        cts.Cancel();

        // Must return within a short window; before the fix this hangs forever.
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(3)));
        await Assert.That(completed == runTask).IsTrue();
        await runTask;
    }

    private static int GetFreePort()
    {
        var s = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp
        );
        try
        {
            s.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            return ((System.Net.IPEndPoint)s.LocalEndPoint!).Port;
        }
        finally
        {
            s.Close();
        }
    }
}
