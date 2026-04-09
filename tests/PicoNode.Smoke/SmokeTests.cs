using PicoNode.Http;

namespace PicoNode.Smoke;

public sealed class SmokeTests
{
    [Test]
    public async Task RunTcpSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = new TcpEchoHandler(),
                DrainTimeout = TimeSpan.FromSeconds(2),
            }
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var payload = new byte[] { 1, 2, 3, 4 };
        await stream.WriteAsync(payload);

        var buffer = new byte[payload.Length];
        await ReadExactAsync(stream, buffer);
        await AssertPayloadEqualAsync(buffer, payload);
    }

    [Test]
    public async Task RunHttpGetSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = CreateHttpNode(
            port,
            static (request, _) =>
                ValueTask.FromResult(
                    request.Method == "GET" && request.Target == "/hello"
                        ? CreateTextResponse(200, "OK", "hello")
                        : CreateTextResponse(404, "Not Found", "missing")
                )
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(stream, "GET /hello HTTP/1.1\r\nHost: localhost\r\n\r\n");
        var response = await ReadHttpResponseAsync(stream);

        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 200 OK");
        await Assert.That(response.Headers["Content-Length"]).IsEqualTo("5");
        await Assert.That(Encoding.ASCII.GetString(response.Body)).IsEqualTo("hello");
    }

    [Test]
    public async Task RunHttpPostWithContentLengthSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = CreateHttpNode(
            port,
            static (request, _) =>
            {
                if (request.Method == "POST" && request.Target == "/submit")
                {
                    return ValueTask.FromResult(
                        CreateTextResponse(
                            200,
                            "OK",
                            $"posted:{Encoding.ASCII.GetString(request.Body.Span)}"
                        )
                    );
                }

                return ValueTask.FromResult(CreateTextResponse(404, "Not Found", "missing"));
            }
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(
            stream,
            "POST /submit HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\n\r\nhello"
        );

        var response = await ReadHttpResponseAsync(stream);

        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 200 OK");
        await Assert.That(response.Headers["Content-Length"]).IsEqualTo("12");
        await Assert.That(Encoding.ASCII.GetString(response.Body)).IsEqualTo("posted:hello");
    }

    [Test]
    public async Task RunHttpSequentialReuseSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);
        var requests = new ConcurrentQueue<string>();

        await using var node = CreateHttpNode(
            port,
            (request, _) =>
            {
                requests.Enqueue($"{request.Method} {request.Target}");

                return ValueTask.FromResult(
                    (request.Method, request.Target) switch
                    {
                        ("GET", "/first") => CreateTextResponse(200, "OK", "first"),
                        ("POST", "/second")
                            => CreateTextResponse(
                                200,
                                "OK",
                                $"second:{Encoding.ASCII.GetString(request.Body.Span)}"
                            ),
                        _ => CreateTextResponse(404, "Not Found", "missing"),
                    }
                );
            }
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(stream, "GET /first HTTP/1.1\r\nHost: localhost\r\n\r\n");
        var firstResponse = await ReadHttpResponseAsync(stream);

        await SendHttpRequestAsync(
            stream,
            "POST /second HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\n\r\nagain"
        );

        var secondResponse = await ReadHttpResponseAsync(stream);

        await Assert.That(firstResponse.StatusLine).IsEqualTo("HTTP/1.1 200 OK");
        await Assert.That(Encoding.ASCII.GetString(firstResponse.Body)).IsEqualTo("first");
        await Assert.That(secondResponse.StatusLine).IsEqualTo("HTTP/1.1 200 OK");
        await Assert.That(Encoding.ASCII.GetString(secondResponse.Body)).IsEqualTo("second:again");
        await Assert.That(requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RunHttpUnsupportedFramingSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);
        var requestHandlerCalled = false;

        await using var node = CreateHttpNode(
            port,
            (_, _) =>
            {
                requestHandlerCalled = true;
                return ValueTask.FromResult(CreateTextResponse(200, "OK", "unexpected"));
            }
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(
            stream,
            "POST /submit HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"
        );

        var response = await ReadHttpResponseAsync(stream);
        var closed = await ReadToEndAsync(stream);

        await Assert.That(requestHandlerCalled).IsFalse();
        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 501 Not Implemented");
        await Assert.That(response.Headers["Connection"]).IsEqualTo("close");
        await Assert.That(response.Headers["Content-Length"]).IsEqualTo("0");
        await Assert.That(response.Body.Length).IsEqualTo(0);
        await Assert.That(closed).IsTrue();
    }

    [Test]
    public async Task RunHttpHandlerFailureSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = CreateHttpNode(
            port,
            static (_, _) => throw new InvalidOperationException("boom")
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(stream, "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");

        var response = await ReadHttpResponseAsync(stream);
        var closed = await ReadToEndAsync(stream);

        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 500 Internal Server Error");
        await Assert.That(response.Headers["Connection"]).IsEqualTo("close");
        await Assert.That(response.Headers["Content-Length"]).IsEqualTo("0");
        await Assert.That(closed).IsTrue();
    }

    [Test]
    public async Task RunHttpRouterMethodNotAllowedSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = CreateHttpNode(
            port,
            new HttpRouter(
                new HttpRouterOptions
                {
                    Routes =
                    [
                        HttpRoute.MapPost(
                            "/echo",
                            static (_, _) => ValueTask.FromResult(CreateTextResponse(200, "OK", "echo"))
                        ),
                    ],
                }
            ).HandleAsync
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(stream, "GET /echo HTTP/1.1\r\nHost: localhost\r\n\r\n");

        var response = await ReadHttpResponseAsync(stream);

        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 405 Method Not Allowed");
        await Assert.That(response.Headers["Allow"]).IsEqualTo("POST");
        await Assert.That(response.Headers["Content-Length"]).IsEqualTo("0");
    }

    [Test]
    public async Task RunHttpRouterFallbackSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = CreateHttpNode(
            port,
            new HttpRouter(
                new HttpRouterOptions
                {
                    Routes =
                    [
                        HttpRoute.MapGet(
                            "/hello",
                            static (_, _) => ValueTask.FromResult(CreateTextResponse(200, "OK", "hello"))
                        ),
                    ],
                    FallbackHandler = static (_, _) =>
                        ValueTask.FromResult(
                            CreateTextResponse(404, "Not Found", "router-missing")
                        ),
                }
            ).HandleAsync
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(stream, "GET /missing HTTP/1.1\r\nHost: localhost\r\n\r\n");

        var response = await ReadHttpResponseAsync(stream);

        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 404 Not Found");
        await Assert.That(Encoding.ASCII.GetString(response.Body)).IsEqualTo("router-missing");
    }

    [Test]
    public async Task RunHttpMissingHostSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = CreateHttpNode(
            port,
            new HttpRouter(
                new HttpRouterOptions
                {
                    Routes =
                    [
                        HttpRoute.MapGet(
                            "/hello",
                            static (_, _) => ValueTask.FromResult(CreateTextResponse(200, "OK", "hello"))
                        ),
                    ],
                }
            ).HandleAsync
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(stream, "GET /hello HTTP/1.1\r\nAccept: text/plain\r\n\r\n");

        var response = await ReadHttpResponseAsync(stream);
        var closed = await ReadToEndAsync(stream);

        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 400 Bad Request");
        await Assert.That(response.Headers["Connection"]).IsEqualTo("close");
        await Assert.That(response.Headers["Content-Length"]).IsEqualTo("0");
        await Assert.That(closed).IsTrue();
    }

    [Test]
    public async Task RunHttpRouterPutDeleteSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = CreateHttpNode(
            port,
            new HttpRouter(
                new HttpRouterOptions
                {
                    Routes =
                    [
                        HttpRoute.MapPut(
                            "/resource",
                            static (_, _) => ValueTask.FromResult(CreateTextResponse(200, "OK", "put-ok"))
                        ),
                        HttpRoute.MapDelete(
                            "/resource",
                            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" })
                        ),
                    ],
                }
            ).HandleAsync
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(
            stream,
            "PUT /resource HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\n\r\n"
        );
        var putResponse = await ReadHttpResponseAsync(stream);

        await SendHttpRequestAsync(stream, "DELETE /resource HTTP/1.1\r\nHost: localhost\r\n\r\n");
        var deleteResponse = await ReadHttpResponseAsync(stream);

        await Assert.That(putResponse.StatusLine).IsEqualTo("HTTP/1.1 200 OK");
        await Assert.That(Encoding.ASCII.GetString(putResponse.Body)).IsEqualTo("put-ok");
        await Assert.That(deleteResponse.StatusLine).IsEqualTo("HTTP/1.1 204 No Content");
        await Assert.That(deleteResponse.Headers["Content-Length"]).IsEqualTo("0");
    }

    [Test]
    public async Task RunHttpInvalidRequestTargetSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = CreateHttpNode(
            port,
            new HttpRouter(
                new HttpRouterOptions
                {
                    Routes =
                    [
                        HttpRoute.MapGet(
                            "/hello",
                            static (_, _) => ValueTask.FromResult(CreateTextResponse(200, "OK", "hello"))
                        ),
                    ],
                }
            ).HandleAsync
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(stream, "GET foo HTTP/1.1\r\nHost: localhost\r\n\r\n");

        var response = await ReadHttpResponseAsync(stream);
        var closed = await ReadToEndAsync(stream);

        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 400 Bad Request");
        await Assert.That(response.Headers["Connection"]).IsEqualTo("close");
        await Assert.That(response.Headers["Content-Length"]).IsEqualTo("0");
        await Assert.That(closed).IsTrue();
    }

    [Test]
    public async Task RunHttpInvalidHostFormatSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = CreateHttpNode(
            port,
            new HttpRouter(
                new HttpRouterOptions
                {
                    Routes =
                    [
                        HttpRoute.MapGet(
                            "/hello",
                            static (_, _) => ValueTask.FromResult(CreateTextResponse(200, "OK", "hello"))
                        ),
                    ],
                }
            ).HandleAsync
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        await SendHttpRequestAsync(stream, "GET /hello HTTP/1.1\r\nHost: http://localhost\r\n\r\n");

        var response = await ReadHttpResponseAsync(stream);
        var closed = await ReadToEndAsync(stream);

        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 400 Bad Request");
        await Assert.That(response.Headers["Connection"]).IsEqualTo("close");
        await Assert.That(response.Headers["Content-Length"]).IsEqualTo("0");
        await Assert.That(closed).IsTrue();
    }

    [Test]
    public async Task RunUdpSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = new UdpEchoHandler(),
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        var payload = new byte[] { 9, 8, 7, 6 };
        await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
        var result = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await AssertPayloadEqualAsync(result.Buffer, payload);
    }

    [Test]
    public async Task StopAsync_waits_for_connection_close_completion()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);
        var handler = new BlockingCloseHandler();

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = handler,
                DrainTimeout = TimeSpan.FromSeconds(5),
            }
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await handler.Connected.WaitAsync(TimeSpan.FromSeconds(5));

        Task? stopTask = null;
        try
        {
            stopTask = node.StopAsync();
            await handler.CloseStarted.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert
                .That(await CompletesWithinAsync(stopTask, TimeSpan.FromMilliseconds(200)))
                .IsFalse();
        }
        finally
        {
            handler.AllowClose();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
                await handler.CloseCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Test]
    public async Task StopAsync_rejects_new_connections_while_stopping()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);
        var handler = new BlockingCloseHandler();

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = handler,
                DrainTimeout = TimeSpan.FromSeconds(5),
            }
        );

        await node.StartAsync();

        using var firstClient = new TcpClient();
        await firstClient.ConnectAsync(IPAddress.Loopback, port);
        await handler.Connected.WaitAsync(TimeSpan.FromSeconds(5));

        Task? stopTask = null;
        try
        {
            stopTask = node.StopAsync();
            await handler.CloseStarted.WaitAsync(TimeSpan.FromSeconds(5));

            var secondConnectionAccepted = await AttemptLateTcpConnectionAsync(port);
            await Assert.That(secondConnectionAccepted).IsFalse();
            await Assert.That(handler.ConnectionCount).IsEqualTo(1);
        }
        finally
        {
            handler.AllowClose();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
                await handler.CloseCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Test]
    public async Task StopAsync_waits_for_inflight_udp_handler_completion()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);
        var handler = new BlockingUdpHandler();

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = handler,
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        await client.SendAsync(new byte[] { 1, 2, 3 }, 3, new IPEndPoint(IPAddress.Loopback, port));
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));

        Task? stopTask = null;
        try
        {
            stopTask = node.StopAsync();
            await Assert
                .That(await CompletesWithinAsync(stopTask, TimeSpan.FromMilliseconds(200)))
                .IsFalse();
        }
        finally
        {
            handler.Release();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
                await handler.Completed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Test]
    public async Task Udp_handler_fault_reports_fault_and_subsequent_datagram_still_processes()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);
        var faults = new ConcurrentQueue<NodeFaultCode>();

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = new ThrowOnceThenEchoUdpHandler(),
                FaultHandler = fault => faults.Enqueue(fault.Code),
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        var remote = new IPEndPoint(IPAddress.Loopback, port);

        await client.SendAsync(new byte[] { 0x10 }, 1, remote);
        await Task.Delay(200);

        await client.SendAsync(new byte[] { 0x20, 0x21 }, 2, remote);
        var echoed = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(faults.Contains(NodeFaultCode.DatagramHandlerFailed)).IsTrue();
        await AssertPayloadEqualAsync(echoed.Buffer, new byte[] { 0x20, 0x21 });
    }

    [Test]
    public async Task Udp_drop_newest_reports_fault_when_dispatch_queue_is_saturated()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);
        var handler = new BlockingUdpHandler();
        var faults = new ConcurrentQueue<NodeFaultCode>();

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = handler,
                DispatchWorkerCount = 1,
                DatagramQueueCapacity = 1,
                QueueOverflowMode = UdpOverflowMode.DropNewest,
                FaultHandler = fault => faults.Enqueue(fault.Code),
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        var remote = new IPEndPoint(IPAddress.Loopback, port);

        await client.SendAsync(new byte[] { 1 }, 1, remote);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 32; i++)
        {
            await client.SendAsync(new byte[] { (byte)(i + 2) }, 1, remote);
        }

        await EventuallyAsync(
            () => Task.FromResult(faults.Contains(NodeFaultCode.DatagramDropped)),
            TimeSpan.FromSeconds(5)
        );

        handler.Release();
        await handler.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(faults.Contains(NodeFaultCode.DatagramDropped)).IsTrue();
    }

    [Test]
    public async Task Udp_stop_cancellation_leaves_state_as_stopping_until_drain_finishes()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);
        var handler = new BlockingUdpHandler();

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = handler,
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        await client.SendAsync(new byte[] { 0x33 }, 1, new IPEndPoint(IPAddress.Loopback, port));
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert
            .That(async () => await node.StopAsync(stopCts.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(node.State).IsEqualTo(NodeState.Stopping);

        handler.Release();
        await node.StopAsync();
        await handler.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    [Test]
    public async Task RunTlsTcpEchoSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);
        using var cert = CreateSelfSignedCertificate();

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = new TcpEchoHandler(),
                DrainTimeout = TimeSpan.FromSeconds(2),
                SslOptions = new SslServerAuthenticationOptions { ServerCertificate = cert, },
            }
        );

        await node.StartAsync();

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port);
        await using var sslStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            static (_, _, _, _) => true
        );
        await sslStream.AuthenticateAsClientAsync("localhost");

        var payload = new byte[] { 10, 20, 30, 40 };
        await sslStream.WriteAsync(payload);
        await sslStream.FlushAsync();

        var buffer = new byte[payload.Length];
        await ReadExactAsync(sslStream, buffer);
        await AssertPayloadEqualAsync(buffer, payload);
    }

    [Test]
    public async Task RunTlsHttpGetSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);
        using var cert = CreateSelfSignedCertificate();

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = new HttpConnectionHandler(
                    new HttpConnectionHandlerOptions
                    {
                        RequestHandler = static (request, _) =>
                            ValueTask.FromResult(
                                request.Method == "GET" && request.Target == "/secure"
                                    ? CreateTextResponse(200, "OK", "secure-hello")
                                    : CreateTextResponse(404, "Not Found", "missing")
                            ),
                    }
                ),
                DrainTimeout = TimeSpan.FromSeconds(2),
                SslOptions = new SslServerAuthenticationOptions { ServerCertificate = cert, },
            }
        );

        await node.StartAsync();

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port);
        await using var sslStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            static (_, _, _, _) => true
        );
        await sslStream.AuthenticateAsClientAsync("localhost");

        var requestBytes = Encoding
            .ASCII
            .GetBytes("GET /secure HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await sslStream.WriteAsync(requestBytes);
        await sslStream.FlushAsync();

        var response = await ReadHttpResponseAsync(sslStream);
        await Assert.That(response.StatusLine).IsEqualTo("HTTP/1.1 200 OK");
        await Assert.That(Encoding.ASCII.GetString(response.Body)).IsEqualTo("secure-hello");
    }

    [Test]
    public async Task TlsHandshakeFailure_reports_fault_and_does_not_crash()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);
        using var cert = CreateSelfSignedCertificate();
        var faults = new ConcurrentBag<NodeFault>();

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = new TcpEchoHandler(),
                DrainTimeout = TimeSpan.FromSeconds(2),
                FaultHandler = faults.Add,
                SslOptions = new SslServerAuthenticationOptions { ServerCertificate = cert, },
            }
        );

        await node.StartAsync();

        // Connect with raw TCP (no TLS) — handshake will fail
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var rawStream = client.GetStream();
        await rawStream.WriteAsync(Encoding.ASCII.GetBytes("not-tls-data\r\n"));
        client.Close();

        await EventuallyAsync(
            () => Task.FromResult(faults.Any(f => f.Code == NodeFaultCode.TlsFailed)),
            TimeSpan.FromSeconds(5)
        );

        await Assert.That(faults.Any(f => f.Code == NodeFaultCode.TlsFailed)).IsTrue();
        await Assert.That(node.State).IsEqualTo(NodeState.Running);
    }

    private static int GetAvailablePort(SocketType socketType, ProtocolType protocolType)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, socketType, protocolType);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        if (socket.LocalEndPoint is not IPEndPoint localEndPoint)
        {
            throw new InvalidOperationException("Socket endpoint should be available after bind.");
        }

        return localEndPoint.Port;
    }

    private static TcpNode CreateHttpNode(int port, HttpRequestHandler requestHandler) =>
        new(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = new HttpConnectionHandler(
                    new HttpConnectionHandlerOptions { RequestHandler = requestHandler, }
                ),
                DrainTimeout = TimeSpan.FromSeconds(2),
            }
        );

    private static HttpResponse CreateTextResponse(
        int statusCode,
        string reasonPhrase,
        string body
    ) =>
        new()
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Headers =
            [
                new KeyValuePair<string, string>("Content-Type", "text/plain"),
                new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff"),
            ],
            Body = Encoding.ASCII.GetBytes(body),
        };

    private static Task SendHttpRequestAsync(NetworkStream stream, string request) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(request)).AsTask();

    private static async Task<(
        string StatusLine,
        Dictionary<string, string> Headers,
        byte[] Body
    )> ReadHttpResponseAsync(NetworkStream stream)
    {
        using var buffer = new MemoryStream();

        while (true)
        {
            var chunk = new byte[256];
            var read = await stream
                .ReadAsync(chunk.AsMemory())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (read == 0)
            {
                throw new InvalidOperationException(
                    "Connection closed while reading HTTP response."
                );
            }

            buffer.Write(chunk, 0, read);

            var responseBytes = buffer.ToArray();
            var headerLength = FindHeaderLength(responseBytes);
            if (headerLength < 0)
            {
                continue;
            }

            var headerText = Encoding.ASCII.GetString(responseBytes, 0, headerLength);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var index = 1; index < lines.Length; index++)
            {
                var colonIndex = lines[index].IndexOf(':');
                if (colonIndex <= 0)
                {
                    throw new InvalidOperationException("Invalid HTTP response header.");
                }

                headers.Add(lines[index][..colonIndex], lines[index][(colonIndex + 1)..].Trim());
            }

            var contentLength = headers.TryGetValue("Content-Length", out var contentLengthValue)
                ? int.Parse(contentLengthValue, CultureInfo.InvariantCulture)
                : 0;
            var body = new byte[contentLength];
            var bufferedBodyCount = responseBytes.Length - (headerLength + 4);
            var copiedBodyCount = Math.Min(bufferedBodyCount, contentLength);

            if (copiedBodyCount > 0)
            {
                Array.Copy(responseBytes, headerLength + 4, body, 0, copiedBodyCount);
            }

            if (copiedBodyCount < contentLength)
            {
                await ReadExactAsync(
                    stream,
                    body.AsMemory(copiedBodyCount, contentLength - copiedBodyCount)
                );
            }

            return (lines[0], headers, body);
        }
    }

    private static int FindHeaderLength(byte[] buffer)
    {
        for (var index = 0; index <= buffer.Length - 4; index++)
        {
            if (
                buffer[index] == '\r'
                && buffer[index + 1] == '\n'
                && buffer[index + 2] == '\r'
                && buffer[index + 3] == '\n'
            )
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task<bool> ReadToEndAsync(NetworkStream stream)
    {
        var buffer = new byte[1];
        var read = await stream
            .ReadAsync(buffer.AsMemory())
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        return read == 0;
    }

    private static Task ReadExactAsync(NetworkStream stream, byte[] buffer) =>
        ReadExactAsync(stream, buffer.AsMemory());

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer[offset..])
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (read == 0)
            {
                throw new InvalidOperationException("Connection closed while reading.");
            }

            offset += read;
        }
    }

    private static async Task AssertPayloadEqualAsync(byte[] actual, byte[] expected)
    {
        await Assert.That(actual.Length).IsEqualTo(expected.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            await Assert.That(actual[i]).IsEqualTo(expected[i]);
        }
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private static async Task EventuallyAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null
    )
    {
        var deadline = DateTime.UtcNow + timeout;
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(25);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(interval);
        }

        throw new TimeoutException("Condition was not met before the timeout elapsed.");
    }

    private static async Task<bool> AttemptLateTcpConnectionAsync(int port)
    {
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (SocketException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }

        try
        {
            using var stream = client.GetStream();
            await stream
                .WriteAsync(new byte[] { 0x42 }.AsMemory())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));

            var buffer = new byte[1];
            var read = await stream
                .ReadAsync(buffer.AsMemory())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));
            return read > 0;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer[offset..])
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (read == 0)
            {
                throw new InvalidOperationException("Connection closed while reading.");
            }

            offset += read;
        }
    }

    private static async Task<(
        string StatusLine,
        Dictionary<string, string> Headers,
        byte[] Body
    )> ReadHttpResponseAsync(Stream stream)
    {
        using var buffer = new MemoryStream();

        while (true)
        {
            var chunk = new byte[256];
            var read = await stream
                .ReadAsync(chunk.AsMemory())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (read == 0)
            {
                throw new InvalidOperationException(
                    "Connection closed while reading HTTP response."
                );
            }

            buffer.Write(chunk, 0, read);

            var responseBytes = buffer.ToArray();
            var headerLength = FindHeaderLength(responseBytes);
            if (headerLength < 0)
            {
                continue;
            }

            var headerText = Encoding.ASCII.GetString(responseBytes, 0, headerLength);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var index = 1; index < lines.Length; index++)
            {
                var colonIndex = lines[index].IndexOf(':');
                if (colonIndex <= 0)
                {
                    throw new InvalidOperationException("Invalid HTTP response header.");
                }

                headers.Add(lines[index][..colonIndex], lines[index][(colonIndex + 1)..].Trim());
            }

            var contentLength = headers.TryGetValue("Content-Length", out var contentLengthValue)
                ? int.Parse(contentLengthValue, CultureInfo.InvariantCulture)
                : 0;
            var body = new byte[contentLength];
            var bufferedBodyCount = responseBytes.Length - (headerLength + 4);
            var copiedBodyCount = Math.Min(bufferedBodyCount, contentLength);

            if (copiedBodyCount > 0)
            {
                Array.Copy(responseBytes, headerLength + 4, body, 0, copiedBodyCount);
            }

            if (copiedBodyCount < contentLength)
            {
                await ReadExactAsync(
                    stream,
                    body.AsMemory(copiedBodyCount, contentLength - copiedBodyCount)
                );
            }

            return (lines[0], headers, body);
        }
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1)
        );
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }
}

file sealed class TcpEchoHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public Task OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        _ = connection.SendAsync(buffer, cancellationToken);
        return ValueTask.FromResult(buffer.End);
    }
}

file sealed class BlockingCloseHandler : ITcpConnectionHandler
{
    private int _connectionCount;
    private readonly TaskCompletionSource _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closeStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowClose =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closeCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Connected => _connected.Task;

    public Task CloseStarted => _closeStarted.Task;

    public Task CloseCompleted => _closeCompleted.Task;

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _connectionCount);
        _connected.TrySetResult();
        return Task.CompletedTask;
    }

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(buffer.End);

    public async Task OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    )
    {
        _closeStarted.TrySetResult();

        try
        {
            await _allowClose.Task;
        }
        finally
        {
            _closeCompleted.TrySetResult();
        }
    }

    public void AllowClose() => _allowClose.TrySetResult();
}

file sealed class UdpEchoHandler : IUdpDatagramHandler
{
    public Task OnDatagramAsync(
        IUdpDatagramContext context,
        ArraySegment<byte> datagram,
        CancellationToken cancellationToken
    ) => context.SendAsync(datagram, cancellationToken);
}

file sealed class BlockingUdpHandler : IUdpDatagramHandler
{
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public Task Completed => _completed.Task;

    public async Task OnDatagramAsync(
        IUdpDatagramContext context,
        ArraySegment<byte> datagram,
        CancellationToken cancellationToken
    )
    {
        _started.TrySetResult();

        try
        {
            await _release.Task.WaitAsync(cancellationToken);
            await context.SendAsync(datagram, cancellationToken);
        }
        finally
        {
            _completed.TrySetResult();
        }
    }

    public void Release() => _release.TrySetResult();
}

file sealed class ThrowOnceThenEchoUdpHandler : IUdpDatagramHandler
{
    private int _failed;

    public Task OnDatagramAsync(
        IUdpDatagramContext context,
        ArraySegment<byte> datagram,
        CancellationToken cancellationToken
    )
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
        {
            throw new InvalidOperationException("boom");
        }

        return context.SendAsync(datagram, cancellationToken);
    }
}
