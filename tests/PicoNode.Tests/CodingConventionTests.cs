namespace PicoNode.Tests;

/// <summary>
/// Meta-tests that enforce coding conventions, documentation quality,
/// and source-level invariants across the PicoNode codebase.
/// </summary>
public sealed class CodingConventionTests
{
    private static string SrcPath =>
        Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src")
        );

    [Test]
    public async Task OnReceivedAsync_doc_comment_has_correct_grammar()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Network", "PicoNode.Abs", "ITcpConnectionHandler.cs")
        );
        await Assert.That(content).DoesNotContain("a earlier");
    }

    [Test]
    public async Task NodeFaultCode_enum_values_have_XML_doc()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Network", "PicoNode.Abs", "NodeFaultCode.cs")
        );
        await Assert
            .That(content)
            .Contains("/// <summary>Node failed to bind or start listening.</summary>");
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Node failed to drain in-flight work during shutdown.</summary>"
            );
        await Assert
            .That(content)
            .Contains("/// <summary>Socket accept threw an unrecoverable exception.</summary>");
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Connection rejected by config policy (e.g. max connections limit).</summary>"
            );
        await Assert
            .That(content)
            .Contains("/// <summary>Socket receive operation failed.</summary>");
        await Assert.That(content).Contains("/// <summary>Socket send operation failed.</summary>");
        await Assert
            .That(content)
            .Contains(
                "/// <summary>User-provided connection handler or close-sequence callback threw an unhandled exception.</summary>"
            );
        await Assert.That(content).Contains("/// <summary>UDP socket receive failed.</summary>");
        // DatagramDropped covers TWO causes: the drop-newest overflow, and the datagram that lands in
        // the window between its receive and StopAsync closing the queue. The sentence has to name
        // both - a summary that still said "channel overflow" only would describe half of what the
        // node reports with this code.
        var dropped = DocBlockAbove(content, "DatagramDropped,");
        await Assert.That(dropped).Contains("UDP datagram dropped instead of delivered");
        await Assert.That(dropped).Contains("UdpOverflowMode.DropNewest");
        await Assert.That(dropped).Contains("shutdown had already closed it");
        await Assert
            .That(content)
            .Contains("/// <summary>UDP datagram handler threw an unhandled exception.</summary>");
        await Assert
            .That(content)
            .Contains("/// <summary>TLS handshake or authentication failed.</summary>");
    }

    [Test]
    public async Task TcpCloseReason_enum_values_have_XML_doc()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Network", "PicoNode.Abs", "TcpCloseReason.cs")
        );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Local side initiated close via <c>Close()</c> or shutdown.</summary>"
            );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Remote peer closed the connection (FIN or RST received).</summary>"
            );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Connection exceeded the configured idle timeout without activity.</summary>"
            );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>User-provided connection handler threw an unhandled exception.</summary>"
            );
        await Assert
            .That(content)
            .Contains("/// <summary>Socket receive operation failed irrecoverably.</summary>");
        await Assert
            .That(content)
            .Contains("/// <summary>Socket send operation failed irrecoverably.</summary>");
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Node is stopping; connection closed as part of graceful shutdown drain.</summary>"
            );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Connection rejected during accept (e.g. max connections reached).</summary>"
            );
    }

    [Test]
    public async Task NodeState_enum_values_have_XML_doc()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Network", "PicoNode.Abs", "NodeState.cs")
        );
        await Assert
            .That(content)
            .Contains("/// <summary>Node constructed but not yet started.</summary>");
        await Assert
            .That(content)
            .Contains("/// <summary>Node is binding and starting accept/receive loops.</summary>");
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Node is accepting connections or datagrams normally.</summary>"
            );
        await Assert
            .That(content)
            .Contains("/// <summary>Node is draining in-flight work before stopping.</summary>");
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Node has completed shutdown; all resources released.</summary>"
            );
        await Assert
            .That(content)
            .Contains("/// <summary>Node has been disposed; cannot be restarted.</summary>");
    }

    [Test]
    public async Task UdpOverflowMode_enum_values_have_XML_doc()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Network", "PicoNode.Abs", "UdpOverflowMode.cs")
        );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>When the dispatch queue is full, drop the newest datagram.</summary>"
            );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>When the dispatch queue is full, block the receive loop until space is available.</summary>"
            );
    }

    [Test]
    public async Task ITcpConnectionContext_all_members_have_XML_doc()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Network", "PicoNode.Abs", "ITcpConnectionContext.cs")
        );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Unique connection identifier within this transport node.</summary>"
            );
        await Assert
            .That(content)
            .Contains("/// <summary>Remote endpoint of the connected peer.</summary>");
        await Assert
            .That(content)
            .Contains("/// <summary>UTC timestamp when the connection was accepted.</summary>");
        await Assert
            .That(content)
            .Contains(
                "/// <summary>UTC timestamp of the last read or write activity on this connection.</summary>"
            );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Arbitrary user state object. Use to attach protocol-level state (e.g. HTTP/1.1 parser state).</summary>"
            );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Sends data to the connected peer. The buffer is consumed asynchronously.</summary>"
            );
        await Assert
            .That(content)
            .Contains(
                "/// <summary>Initiates a graceful close of the connection. Fire-and-forget; the actual close is asynchronous.</summary>"
            );
    }

    [Test]
    public async Task ITcpConnectionContext_RemoteEndPoint_is_EndPoint()
    {
        var prop = typeof(ITcpConnectionContext).GetProperty(
            nameof(ITcpConnectionContext.RemoteEndPoint)
        );
        await Assert.That(prop!.PropertyType).IsEqualTo(typeof(System.Net.EndPoint));
    }

    [Test]
    public async Task IUdpDatagramContext_RemoteEndPoint_is_EndPoint()
    {
        var prop = typeof(IUdpDatagramContext).GetProperty(
            nameof(IUdpDatagramContext.RemoteEndPoint)
        );
        await Assert.That(prop!.PropertyType).IsEqualTo(typeof(System.Net.EndPoint));
    }

    [Test]
    public async Task HttpHeaderNames_resolves_from_PicoNode_Http_namespace()
    {
        // After migration, HttpHeaderNames should be in PicoNode.Http namespace
        var type = typeof(PicoNode.Http.HttpHeaderNames);
        await Assert.That(type).IsNotNull();
    }

    [Test]
    public async Task MultipartFormDataParser_class_documents_Body_vs_BodyStream_strategy()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Web", "PicoNode.Web", "MultipartFormDataParser.cs")
        );
        // Must have a summary comment on the class that explains Body vs BodyStream
        await Assert.That(content).Contains("/// <summary>");
        await Assert.That(content).Contains("Body");
        await Assert.That(content).Contains("BodyStream");
        // The summary must be before the class declaration, not inside a method
        var classIndex = content.IndexOf("public static class MultipartFormDataParser");
        var summaryIndex = content.IndexOf("/// <summary>");
        await Assert.That(summaryIndex).IsLessThan(classIndex);
    }

    [Test]
    public async Task RouteTable_class_documents_shared_role()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Http", "PicoNode.Http", "Internal", "RouteTable.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
        await Assert.That(content).Contains("RouteTable");
    }

    [Test]
    public async Task HttpRouter_class_documents_exact_path_matching()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Http", "PicoNode.Http", "HttpRouter.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
        await Assert.That(content).Contains("Exact");
    }

    [Test]
    public async Task RadixTree_class_documents_parameterized_routing()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Web", "PicoNode.Web", "Internal", "RadixTree.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
        await Assert.That(content).Contains("parameterized");
    }

    [Test]
    public async Task WebRouter_class_documents_exact_and_parameterized()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Web", "PicoNode.Web", "WebRouter.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
        await Assert.That(content).Contains("RouteTable");
        await Assert.That(content).Contains("RadixTree");
    }

    [Test]
    public async Task WebResults_class_has_XML_doc_explaining_relationship_to_Results()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Web", "PicoNode.Web", "WebResults.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
    }

    [Test]
    public async Task EffectiveAcceptFaultBackoff_docs_match_what_the_clamp_does()
    {
        // The summary claimed the pause was "clamped into [floor, max]", which a 0.5 ms value
        // contradicts: it passes through unchanged. Only a NON-POSITIVE value is replaced by the floor,
        // and only the upper end is a cap. The behaviour is pinned in TcpNodeBranchTests; this pins the
        // words, because a doc line that misdescribes the clamp is how the next reader is misled.
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Network", "PicoNode", "TcpNode.cs")
        );
        var doc = DocBlockAbove(
            content,
            "internal static TimeSpan EffectiveAcceptFaultBackoff(TimeSpan configured)"
        );

        await Assert.That(doc).IsNotEmpty();
        await Assert.That(doc).Contains("REPLACED");
        await Assert.That(doc).Contains("capped");
        await Assert.That(doc).Contains("unchanged");
        await Assert.That(doc).DoesNotContain("clamped into");
    }

    [Test]
    public async Task WebServerOptions_AcceptFaultBackoff_documents_the_one_millisecond_floor()
    {
        // PicoWeb inherits the TCP accept-loop clamp through WebServerOptionsExtensions, so a
        // WebServerOptions.AcceptFaultBackoff of zero now means "pause 1 ms", not "pause not at all".
        // The public doc has to say so: it is the only place a PicoWeb host reads the option.
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Web", "PicoWeb", "WebServerOptions.cs")
        );
        var doc = DocBlockAbove(content, "public TimeSpan AcceptFaultBackoff { get; init; }");

        await Assert.That(doc).IsNotEmpty();
        await Assert.That(doc).Contains("1 ms");
        await Assert.That(doc).Contains("zero");
    }

    [Test]
    public async Task The_timer_limit_cap_is_expressed_once()
    {
        // Single-homing the CONSTANT (DrainBudget.MaxTimerDuration) did not single-home the EXPRESSION:
        // `value < MaxTimerDuration ? value : MaxTimerDuration` had been written out in
        // DrainBudget.Effective, TcpNode.EffectiveAcceptFaultBackoff,
        // UdpNode.EffectiveReceiveFaultBackoff and TcpConnectionLifecycle.HandlerLoopTimeoutFor, so one
        // timer-limit rule had four homes and could drift in three of them. DrainBudget.Cap is now the
        // only place the cap is expressed; every other source line has to go through it.
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SrcPath, "*.cs", SearchOption.AllDirectories))
        {
            if (
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
            )
            {
                continue;
            }

            var name = Path.GetFileName(file);
            if (string.Equals(name, "DrainBudget.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = await File.ReadAllLinesAsync(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                // XML doc comments may still cref the constant: that documents the limit, it is not a
                // second copy of the rule.
                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains("MaxTimerDuration", StringComparison.Ordinal))
                {
                    offenders.Add($"{name}:{i + 1}");
                }
            }
        }

        await Assert
            .That(offenders.Count)
            .IsEqualTo(0)
            .Because(
                $"the cap is expressed once, in DrainBudget.Cap(...): {string.Join(", ", offenders)}"
            );

        var budget = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Network", "PicoNode", "DrainBudget.cs")
        );
        await Assert.That(budget).Contains("internal static TimeSpan Cap(");

        // ...and the readers consume it, so removing the duplication cannot be done by dropping the cap
        // instead of sharing it.
        foreach (var reader in new[] { "TcpNode.cs", "UdpNode.cs" })
        {
            var source = await File.ReadAllTextAsync(
                Path.Combine(SrcPath, "Network", "PicoNode", reader)
            );
            await Assert.That(source).Contains("DrainBudget.Cap(");
        }
    }

    /// <summary>
    /// The <c>///</c> block immediately above a declaration, joined into one string, so a doc
    /// assertion cannot be satisfied by wording that lives somewhere else in the file.
    /// </summary>
    private static string DocBlockAbove(string content, string declaration)
    {
        var index = content.IndexOf(declaration, StringComparison.Ordinal);
        if (index < 0)
        {
            return string.Empty;
        }

        var before = content[..index].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var doc = new List<string>();
        for (var i = before.Length - 2; i >= 0; i--)
        {
            var line = before[i].Trim();
            if (!line.StartsWith("///", StringComparison.Ordinal))
            {
                break;
            }

            doc.Insert(0, line);
        }

        return string.Join('\n', doc);
    }

    [Test]
    public async Task Analyzer_pack_paths_use_Configuration_variable_not_hardcoded_Release()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "Web", "PicoWeb", "PicoWeb.csproj")
        );
        // Hardcoded 'Release' in analyzer path would break Debug builds
        await Assert.That(content).DoesNotContain("bin\\Release\\netstandard2.0");
    }
}
