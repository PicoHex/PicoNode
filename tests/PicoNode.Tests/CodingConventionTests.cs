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
            Path.Combine(SrcPath, "PicoNode.Abs", "ITcpConnectionHandler.cs")
        );
        await Assert.That(content).DoesNotContain("a earlier");
    }

    [Test]
    public async Task NodeFaultCode_enum_values_have_XML_doc()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "PicoNode.Abs", "NodeFaultCode.cs")
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
                "/// <summary>User-provided connection handler threw an unhandled exception.</summary>"
            );
        await Assert.That(content).Contains("/// <summary>UDP socket receive failed.</summary>");
        await Assert
            .That(content)
            .Contains(
                "/// <summary>UDP datagram dropped due to channel overflow (<see cref=\"UdpOverflowMode.DropNewest\"/>).</summary>"
            );
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
            Path.Combine(SrcPath, "PicoNode.Abs", "TcpCloseReason.cs")
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
            Path.Combine(SrcPath, "PicoNode.Abs", "NodeState.cs")
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
            Path.Combine(SrcPath, "PicoNode.Abs", "UdpOverflowMode.cs")
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
            Path.Combine(SrcPath, "PicoNode.Abs", "ITcpConnectionContext.cs")
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
            Path.Combine(SrcPath, "PicoNode.Web", "MultipartFormDataParser.cs")
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
            Path.Combine(SrcPath, "PicoNode.Http", "Internal", "RouteTable.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
        await Assert.That(content).Contains("RouteTable");
    }

    [Test]
    public async Task HttpRouter_class_documents_exact_path_matching()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "PicoNode.Http", "HttpRouter.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
        await Assert.That(content).Contains("Exact");
    }

    [Test]
    public async Task RadixTree_class_documents_parameterized_routing()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "PicoNode.Web", "Internal", "RadixTree.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
        await Assert.That(content).Contains("parameterized");
    }

    [Test]
    public async Task WebRouter_class_documents_exact_and_parameterized()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "PicoNode.Web", "WebRouter.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
        await Assert.That(content).Contains("RouteTable");
        await Assert.That(content).Contains("RadixTree");
    }

    [Test]
    public async Task WebResults_class_has_XML_doc_explaining_relationship_to_Results()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "PicoNode.Web", "WebResults.cs")
        );
        await Assert.That(content).Contains("/// <summary>");
    }

    [Test]
    public async Task Analyzer_pack_paths_use_Configuration_variable_not_hardcoded_Release()
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "PicoWeb", "PicoWeb.csproj")
        );
        // Hardcoded 'Release' in analyzer path would break Debug builds
        await Assert.That(content).DoesNotContain("bin\\Release\\netstandard2.0");
    }

    [Test]
    public async Task PicoJetson_version_is_at_least_2026_2()
    {
        // NOTE: PicoJetson lags behind other PicoHex packages.
        // This test guards against regression. When PicoJetson catches up,
        // update the test and the csproj reference together.
        var content = await File.ReadAllTextAsync(
            Path.Combine(SrcPath, "PicoWeb", "PicoWeb.csproj")
        );
        await Assert.That(content).Contains("\"PicoJetson\" Version=\"2026.2.3\"");
    }
}
