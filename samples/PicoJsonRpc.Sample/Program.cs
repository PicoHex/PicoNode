using PicoJsonRpc;

// PicoJsonRpc usage sample: a fully self-contained JSON-RPC 2.0 round trip.
//
// Parent mode (default): spawns ITSELF in --child mode via ProcessLifecycle,
// then calls "echo" through the multiplexed JsonRpcClient over the child's
// stdio. Child mode: reads NDJSON frames from stdin and answers requests on
// stdout — a zero-dependency stdio server (no node/python needed), which
// also works when AOT-published (self-spawn via Environment.ProcessPath).

if (args.Length > 0 && args[0] == "--child")
{
    await ChildMainAsync();
    return;
}

await ParentMainAsync();

static async Task ParentMainAsync()
{
    // Spawn our own binary as the subprocess (stdio is the transport).
    var lifecycle = new ProcessLifecycle(
        "self-echo",
        [Environment.ProcessPath ?? throw new InvalidOperationException("no process path"), "--child"]
    );
    await using var client = new JsonRpcClient(lifecycle);

    // One id-routed call (params/result are raw JSON strings).
    var frame = await client.CallAsync(
        "echo",
        paramsJson: """{"message":"hello json-rpc"}""",
        CancellationToken.None
    );

    if (frame.Error is { } err)
    {
        Console.WriteLine($"error {err.Code}: {err.Message}");
        return;
    }

    // The child reflected the params as the result (raw JSON text).
    Console.WriteLine($"echoed: {frame.Result}");
}

static async Task ChildMainAsync()
{
    // Server side: NDJSON text in, responses out. NOTE: NdjsonFramer.ReadAsync
    // is the RESPONSE-side parser (it rejects inbound request frames by
    // design), so the child reads raw lines and decodes via JsonRpcCodec.
    using var output = Console.OpenStandardOutput();
    using var reader = new StreamReader(Console.OpenStandardInput());
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.Length == 0)
            continue;
        var request = JsonRpcCodec.Deserialize<JsonRpcFrame>(line)
            ?? throw new FormatException("null request frame");
        if (request.Method != "echo")
        {
            await output.WriteAsync(
                NdjsonFramer.Encode(
                    JsonRpcFrame.Response(
                        request.Id!,
                        error: new JsonRpcError { Code = -32601, Message = "method not found" }
                    )
                )
            );
            continue;
        }
        // reflect the params back as result
        await output.WriteAsync(
            NdjsonFramer.Encode(JsonRpcFrame.Response(request.Id!, resultJson: request.Params))
        );
        await output.FlushAsync();
    }
}