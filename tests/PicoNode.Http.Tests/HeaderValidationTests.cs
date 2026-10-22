namespace PicoNode.Http.Tests;

public sealed class HeaderValidationTests
{
    [Test]
    public async Task Rejects_pseudo_after_regular()
    {
        var headers = new List<(string, string)>
        {
            ("content-type", "text/plain"),
            (":method", "GET"),
            (":path", "/x"),
        };
        var result = Http2StreamHandler.ValidateHeadersPublic(headers);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Rejects_duplicate_pseudo()
    {
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":method", "POST"),
            (":path", "/"),
        };
        var result = Http2StreamHandler.ValidateHeadersPublic(headers);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Rejects_connection_specific()
    {
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":path", "/"),
            ("connection", "close"),
        };
        var result = Http2StreamHandler.ValidateHeadersPublic(headers);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Accepts_valid_request()
    {
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":path", "/"),
            (":scheme", "http"),
            ("user-agent", "test"),
        };
        var result = Http2StreamHandler.ValidateHeadersPublic(headers);
        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Method).IsEqualTo("GET");
        await Assert.That(result.Path).IsEqualTo("/");
    }

    // ── T1.4: field-value validation, decoded-list budget, SETTINGS ─────

    [Test]
    public async Task H2_header_value_with_crlf_is_rejected()
    {
        // RFC 9113 §8.2.1: a field value containing CR, LF or NUL — or one with
        // leading/trailing whitespace — MUST be treated as malformed. HTTP/1.1
        // already rejects these (HttpCharacters.IsValidHeaderValue); HTTP/2 must
        // not be the one protocol that lets them through into the request.
        (string Value, string Why)[] poisoned =
        [
            ("a\r\nb", "CR LF"),
            ("a\rb", "bare CR"),
            ("a\nb", "bare LF"),
            ("a\0b", "NUL"),
            (" a", "leading SP"),
            ("a ", "trailing SP"),
            ("\ta", "leading HTAB"),
            ("a\t", "trailing HTAB"),
            ("\0", "lone NUL"),
        ];

        foreach (var (value, why) in poisoned)
        {
            var headers = new List<(string, string)>
            {
                (":method", "GET"),
                (":path", "/"),
                (":scheme", "http"),
                ("x-echo", value),
            };

            var result = Http2StreamHandler.ValidateHeadersPublic(headers);
            await Assert
                .That(result.IsValid)
                .IsFalse()
                .Because($"a field value with {why} is malformed");
        }

        // Pseudo-header values are field values too (RFC 9113 §8.2.1 draws no
        // distinction), and a CRLF in :path reaches routing, logging and any
        // response header an application reflects it into.
        (string Name, string Value)[] poisonedPseudo =
        [
            (":method", "GET\r\n"),
            (":path", "/a\r\nX-Evil: 1"),
            (":scheme", "http\0"),
            (":authority", "example.com\nb"),
        ];

        foreach (var (name, value) in poisonedPseudo)
        {
            var headers = new List<(string, string)> { (":method", "GET"), (":path", "/") };
            headers.RemoveAll(h => h.Item1 == name);
            headers.Add((name, value));

            var result = Http2StreamHandler.ValidateHeadersPublic(headers);
            await Assert
                .That(result.IsValid)
                .IsFalse()
                .Because($"{name} with a control octet is malformed");
        }

        // The same request with an ordinary value stays valid — interior SP/HTAB
        // and observable text are legal field content, so the reject above is
        // about the injected octets only.
        var accepted = new List<(string, string)>
        {
            (":method", "GET"),
            (":path", "/"),
            (":scheme", "http"),
            ("x-echo", "a b\tc"),
        };
        await Assert.That(Http2StreamHandler.ValidateHeadersPublic(accepted).IsValid).IsTrue();
    }

    [Test]
    public async Task H2_header_list_size_is_bounded()
    {
        // 1 200 one-octet index references to static entry 16
        // ("accept-encoding: gzip, deflate"): a 1 200-octet header block that is
        // far below the 16 KiB compressed cap, yet decodes to 1 200 ×
        // (15 + 12 + 32) = 70 800 octets of header list. Before T1.4 nothing
        // bounded the DECODED list, so this was the cheap memory DoS.
        const int repetitions = 1200;
        const int decodedOctetsPerField = 15 + 12 + 32; // name + value + §6.5.2 overhead

        var block = new byte[repetitions];
        Array.Fill(block, (byte)0x90); // 1xxxxxxx, index 16 → indexed header field

        var decodedSize = repetitions * decodedOctetsPerField;
        // Pin the discriminating property: the block fits the compressed cap and
        // the decoded list does not fit the budget.
        await Assert.That(block.Length).IsLessThan(16 * 1024);
        await Assert.That(decodedSize).IsGreaterThan(64 * 1024);

        bool success = true;
        await Assert
            .That(() => success = HpackDecoder.TryDecode(block, out _))
            .ThrowsNothing()
            .Because("an oversized header list must answer false, never throw");
        await Assert.That(success).IsFalse();
    }

    [Test]
    public async Task H2_settings_advertise_max_header_list_size()
    {
        var connection = new SettingsCapturingContext();
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 204 }),
            }
        );

        await handler.OnReceivedAsync(
            connection,
            new ReadOnlySequence<byte>(Http2FrameCodec.ClientPreface.ToArray()),
            CancellationToken.None
        );

        var settings = connection.Sent;
        await Assert.That(settings.Count).IsEqualTo(1);

        // First frame on the wire is the initial SETTINGS frame (RFC 9113 §3.4).
        var frame = settings[0];
        var payloadLength = (frame[0] << 16) | (frame[1] << 8) | frame[2];
        await Assert.That((Http2FrameType)frame[3]).IsEqualTo(Http2FrameType.Settings);
        await Assert.That((frame[4] & (byte)Http2FrameFlags.Ack)).IsEqualTo((byte)0);

        uint? advertised = null;
        var end = Http2FrameCodec.FrameHeaderSize + payloadLength;
        for (var offset = Http2FrameCodec.FrameHeaderSize; offset + 6 <= end; offset += 6)
        {
            var id = (Http2SettingId)(ushort)((frame[offset] << 8) | frame[offset + 1]);
            if (id != Http2SettingId.MaxHeaderListSize)
                continue;

            advertised = (uint)(
                (frame[offset + 2] << 24)
                | (frame[offset + 3] << 16)
                | (frame[offset + 4] << 8)
                | frame[offset + 5]
            );
        }

        // 64 KiB — the budget the decoder enforces on the decoded header list.
        await Assert
            .That(advertised)
            .IsNotNull()
            .Because("SETTINGS must advertise MAX_HEADER_LIST_SIZE");
        await Assert.That(advertised).IsEqualTo((uint?)(64 * 1024));
    }

    /// <summary>Records everything the handler writes, so the SETTINGS frame can be read back.</summary>
    private sealed class SettingsCapturingContext : ITcpConnectionContext
    {
        private readonly List<byte[]> _sent = [];

        public long ConnectionId => 1;

        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc => DateTimeOffset.UnixEpoch;

        public object? UserState { get; set; }

        public CancellationToken RemoteCloseToken => CancellationToken.None;

        public string? NegotiatedProtocol => null;

        public List<byte[]> Sent
        {
            get
            {
                lock (_sent)
                    return [.. _sent];
            }
        }

        public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken ct = default)
        {
            var bytes = buffer.ToArray();
            lock (_sent)
                _sent.Add(bytes);
            return Task.CompletedTask;
        }

        public void Close() { }
    }
}
