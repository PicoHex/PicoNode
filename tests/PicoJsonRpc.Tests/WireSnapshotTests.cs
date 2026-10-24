using System.Security.Cryptography;

namespace PicoJsonRpc.Tests;

/// <summary>
/// The GUARD, not more examples (T7.7). <see cref="WireConformanceTests"/> pins the
/// shape as inline assertions, which documents it but cannot answer the release
/// question: <em>did the bytes a peer reads move?</em> <c>scripts/release.ps1</c>
/// derives the version by diffing the public API against <c>api/*.public.txt</c>,
/// and <c>api-surface</c> only sees assemblies — so a pure wire change (identical
/// C# surface, different JSON) would ship as a <c>y</c> bump with nothing in the
/// version, the tag or the API baseline to announce it. That is exactly how T7.4
/// went out: the shape changed, the public API did not.
/// <para>
/// So the emitted wire is pinned to a FILE, byte for byte:
/// <c>tests/PicoJsonRpc.Tests/Fixtures/wire-v1.ndjson</c> — the concatenated
/// <see cref="NdjsonFramer.Encode"/> output of the corpus below, NDJSON terminators
/// included. Any change to the encoder, the member-presence rules or the payload
/// gate moves at least one byte, and this test fails naming the frame that moved
/// and showing golden vs emitted. There is no way to change the wire without
/// touching the snapshot in the same commit, and no way to touch the snapshot
/// without a diff a reviewer sees in a diff of the fixture.
/// </para>
/// <para>
/// <b>The version half of the guard</b> lives in the release helper: it prints the
/// snapshot's sha256 and compares the file against the last release tag, and when
/// the hash moved it says the release commit must carry a <c>wire-change:</c> line
/// (and that <c>PicoAgent.Binding</c> ships in lockstep). It warns rather than
/// blocks — a release script that refuses to run is a release script people work
/// around; the failing test above is what actually blocks a silent change. The
/// hash this test prints in its failure message is the same hash the release dry
/// run prints, so the two halves name the same thing. That half is asserted in
/// <see cref="WireReleaseScriptTests"/>, by RUNNING the script against a throwaway
/// repository and comparing the lines it prints — not by matching text in its
/// source, which the block's own header comment made useless (T7.7 fix round 1).
/// </para>
/// <para>
/// <b>What this file does NOT cover:</b> the 12 frames the corpus names are not
/// "every frame the encoder emits". The quoted-string <c>params</c> fallback, the
/// unpaired-surrogate substitution, multi-line normalisation and a <c>result</c> of
/// <c>null</c> are pinned by <see cref="WireConformanceTests"/> instead. A shape no
/// corpus frame reaches is unpinned here; adding one is a line in the corpus plus a
/// line in the fixture.
/// </para>
/// <para>
/// The corpus extends the eight frame kinds T7.4 pinned inline with the shapes it
/// did not cover: <c>error.data</c> as a JSON array, an empty <c>params</c> object,
/// a notification with no <c>params</c> member at all, and a request with no
/// <c>params</c>. The batch rejection is not hand-built — it is the frame the
/// framer's READER actually offers for a batch line, so the snapshot pins that
/// constructor too.
/// </para>
/// </summary>
public sealed class WireSnapshotTests
{
    /// <summary>The snapshot as the repository holds it — one relative path, so the
    /// fixture, the failure message and the release helper all name the same file.</summary>
    internal const string SnapshotRelativePath = "tests/PicoJsonRpc.Tests/Fixtures/wire-v1.ndjson";

    /// <summary>The file in the TEST output directory (copied by the
    /// <c>Fixtures/**</c> rule in the csproj).</summary>
    private static string SnapshotPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", NodeFixture.WireSnapshot);

    /// <summary>
    /// The guard. Byte-for-byte: the concatenation of every corpus frame's
    /// <see cref="NdjsonFramer.Encode"/> output must equal the snapshot file.
    /// <para>
    /// It fails with a diff rather than with TUnit's default "strings differ" dump
    /// of two 800-byte blobs, because the useful question is <em>which frame
    /// changed</em> — the corpus is named, so the message can say
    /// <c>frame 9 'error-data-array'</c> and print that line's golden and emitted
    /// text side by side.
    /// </para>
    /// </summary>
    [Test]
    public async Task Wire_snapshot_is_unchanged()
    {
        var corpus = await CorpusAsync();
        var emitted = new List<byte>(1024);
        foreach (var (_, frame) in corpus)
            emitted.AddRange(NdjsonFramer.Encode(frame));

        var golden = ReadSnapshot();
        if (!emitted.ToArray().AsSpan().SequenceEqual(golden))
            Assert.Fail(Explain(emitted.ToArray(), golden, corpus));

        // Non-vacuity: an empty snapshot would make the comparison above trivially
        // true for a corpus that emits nothing.
        await Assert.That(golden.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// The snapshot's OWN bytes are part of the contract. NDJSON terminates every
    /// line with a single <c>\n</c>, and the fixture is stored with
    /// <c>-text</c> in <c>.gitattributes</c> precisely so a checkout cannot rewrite
    /// those terminators: with <c>core.autocrlf=true</c> (this repository's setting)
    /// a text-normalised checkout would hand the test CRLF on Windows and LF on
    /// Linux, and the guard would then be comparing two different wires on two
    /// platforms. Untreated = the file is byte-identical wherever it is read.
    /// </summary>
    [Test]
    public async Task Snapshot_is_lf_terminated_and_has_no_carriage_return()
    {
        var golden = ReadSnapshot();

        await Assert
            .That(Array.IndexOf(golden, (byte)'\r'))
            .IsEqualTo(-1)
            .Because(
                "the snapshot must be stored LF-only: .gitattributes marks *.ndjson as "
                    + "-text so no checkout rewrites it; a CR here means the file was edited "
                    + "on a CRLF worktree and the wire it pins is not the wire we emit"
            );

        await Assert
            .That(golden[^1])
            .IsEqualTo((byte)'\n')
            .Because("NDJSON terminates every line, the last one included");
    }

    /// <summary>
    /// The snapshot is a FIXED POINT of the codec: any golden line our reader
    /// accepts must re-encode to itself. That is the property a peer relies on when
    /// it echoes a frame back, and it catches an asymmetry between
    /// <see cref="JsonRpcWire.ReadFrame"/> and <see cref="JsonRpcWire.WriteFrame"/>
    /// that a writer-only snapshot cannot see — e.g. a member the reader drops and
    /// the writer then re-emits differently.
    /// <para>
    /// A line carrying BOTH <c>id</c> and <c>method</c> is a child REQUEST, which the
    /// FRAMER rejects on purpose (the union rule: a peer must not send us one), so
    /// those lines are asserted to throw there instead — the request frames in the
    /// corpus exist to pin what WE emit, not what we accept. Which is which is derived
    /// from the corpus (the reader's own view), not hand-listed, so adding a frame to
    /// the snapshot cannot quietly skip this check.
    /// </para>
    /// </summary>
    [Test]
    public async Task Every_golden_line_round_trips_through_the_codec()
    {
        var corpus = await CorpusAsync();
        var golden = Lines(ReadSnapshot());
        await Assert
            .That(golden.Count)
            .IsEqualTo(corpus.Count)
            .Because("the snapshot must hold exactly one line per corpus frame, in order");

        var roundTripped = 0;
        var rejected = 0;
        for (var i = 0; i < corpus.Count; i++)
        {
            var line = golden[i];
            var asFrame = JsonRpcWire.ReadFrame(Encoding.UTF8.GetBytes(line));
            if (asFrame.Id is not null && asFrame.Method is not null)
            {
                rejected++;
                await Assert
                    .That(async () =>
                        await NdjsonFramer
                            .ReadAsync(LineStream(line), CancellationToken.None)
                            .ToListAsync()
                    )
                    .Throws<FormatException>();
                continue;
            }

            roundTripped++;
            var reread = await NdjsonFramer
                .ReadAsync(LineStream(line), CancellationToken.None)
                .ToListAsync();
            await Assert.That(reread).Count().IsEqualTo(1);
            await Assert
                .That(Encoding.UTF8.GetString(NdjsonFramer.Encode(reread[0])).TrimEnd('\n'))
                .IsEqualTo(line)
                .Because($"frame {i} '{corpus[i].Name}' must re-encode to its own snapshot line");
        }

        // neither branch may be vacuous: the corpus is supposed to hold both kinds
        await Assert.That(roundTripped).IsGreaterThan(0);
        await Assert.That(rejected).IsGreaterThan(0);
    }

    // -- the corpus ----------------------------------------------------------

    /// <summary>
    /// Every shape the framer emits, named. The names are what the failure message
    /// uses, so a rename is a readability change and a reorder is a snapshot change.
    /// </summary>
    private static async Task<IReadOnlyList<(string Name, JsonRpcFrame Frame)>> CorpusAsync()
    {
        // The batch rejection is READ, not constructed: the offered -32600 frame is
        // the framer's own answer to an unsupported batch line (id null, per JSON-RPC
        // 2.0 §5), and only the reader path proves that shape.
        using var batchLine = new MemoryStream(
            "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"a\"}]\n"u8.ToArray()
        );
        var batchFrames = await NdjsonFramer.ReadAsync(batchLine, CancellationToken.None)
            .ToListAsync();

        return
        [
            // the eight shapes T7.4 pins inline, in the same order
            ("request", JsonRpcFrame.Request("req-1", "stream", "{\"messages\":[]}")),
            ("notification", JsonRpcFrame.Notification("chunk", "{\"text\":\"a\"}")),
            ("response-ok", JsonRpcFrame.Response("req-1", "{\"ok\":true}")),
            (
                "response-error",
                JsonRpcFrame.Response(
                    "req-2",
                    error: new JsonRpcError
                    {
                        Code = -32601,
                        Message = "no such method",
                        Data = "{\"method\":\"nope\"}",
                    }
                )
            ),
            (
                "stream-chunk",
                JsonRpcFrame.Notification("stream", "{\"_streamId\":\"s-1\",\"text\":\"b\"}")
            ),
            ("cancel", JsonRpcFrame.Notification("cancel", "{\"id\":\"req-1\"}")),
            ("numeric-id", JsonRpcFrame.Response("7", "\"ok\"")),
            ("batch-reject", batchFrames.Single()),
            // ...and the shapes T7.4 did NOT pin
            (
                "error-data-array",
                JsonRpcFrame.Response(
                    "req-3",
                    error: new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid params",
                        // error.data is the DEEPEST payload position; it takes ANY
                        // JSON value, and an array is the kind nothing else covers
                        Data = "[\"a\",1]"
                    }
                )
            ),
            ("empty-params", JsonRpcFrame.Notification("tick", "{}")),
            ("notification-without-params", JsonRpcFrame.Notification("ping", null)),
            ("request-without-params", JsonRpcFrame.Request("req-4", "ping")),
        ];
    }

    // -- reading and explaining ----------------------------------------------

    private static byte[] ReadSnapshot()
    {
        var path = SnapshotPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"the golden wire snapshot was not found at '{path}'. It lives at "
                    + $"'{SnapshotRelativePath}' and reaches the output directory through the "
                    + "csproj's Fixtures/** copy rule. A missing snapshot is NOT a skip: the "
                    + "wire would then be pinned by nothing and a format change could ship as "
                    + "a y bump.",
                path
            );
        }
        return File.ReadAllBytes(path);
    }

    /// <summary>The text as NDJSON lines — the trailing terminator's empty element
    /// dropped, line content otherwise untouched (a stray CR must stay visible in
    /// the diff rather than being silently trimmed).</summary>
    private static List<string> Lines(byte[] bytes)
    {
        var lines = Encoding.UTF8.GetString(bytes).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The failure text. It answers three questions in order: <em>which</em> frame
    /// moved (<c>frame 9 'error-data-array'</c>), <em>what</em> moved (golden vs
    /// emitted, line for line), and <em>what to do</em> (update the snapshot in the
    /// same commit and write a <c>wire-change:</c> line). Both sha256 digests are
    /// printed because that is the number the release dry run prints.
    /// </summary>
    private static MemoryStream LineStream(string line) =>
        new(Encoding.UTF8.GetBytes(line + "\n"));

    private static string Explain(
        byte[] emitted,
        byte[] golden,
        IReadOnlyList<(string Name, JsonRpcFrame Frame)> corpus
    )
    {
        var goldenLines = Lines(golden);
        var emittedLines = Lines(emitted);
        var changes = new List<string>();
        var differing = 0;

        for (var i = 0; i < Math.Max(goldenLines.Count, emittedLines.Count); i++)
        {
            var name = i < corpus.Count ? corpus[i].Name : "nothing (the corpus ends here)";
            var expected = i < goldenLines.Count ? goldenLines[i] : null;
            var actual = i < emittedLines.Count ? emittedLines[i] : null;
            if (expected == actual)
                continue;

            differing++;
            if (changes.Count >= 8)
                continue; // counted, not printed — the tail is in the file itself
            changes.Add(
                $"  frame {i} '{name}' (line {i + 1}): "
                    + (expected is null ? "the snapshot has an EXTRA line"
                        : actual is null ? "the corpus no longer emits this line"
                        : "differs")
                    + "\n"
                    + $"    golden : {expected ?? "<absent>"}\n"
                    + $"    emitted: {actual ?? "<absent>"}"
            );
        }

        var firstByte = 0;
        while (
            firstByte < Math.Min(golden.Length, emitted.Length)
            && golden[firstByte] == emitted[firstByte]
        )
            firstByte++;

        return $"""
            the wire shape moved: the emitted frames no longer match the golden snapshot.

              snapshot : {SnapshotRelativePath}
                         {golden.Length} bytes, sha256 {Hash(golden)}
              emitted  : {corpus.Count} frames
                         {emitted.Length} bytes, sha256 {Hash(emitted)}
              {differing} of {Math.Max(goldenLines.Count, emittedLines.Count)} lines differ, first differing byte at offset {firstByte}

            {string.Join("\n", changes)}
            {(changes.Count < differing ? $"  ... and {differing - changes.Count} more differing line(s)\n" : "")}
            api-surface cannot see these bytes: the public C# surface is unchanged, which is
            why the wire has its own snapshot. If the new bytes are INTENDED, regenerate
            {SnapshotRelativePath} from THIS test's emission in the SAME commit, and put a
            `wire-change:` line in the commit message — the release helper prints this file's
            sha256 for exactly that reason, and a consumer pinned to the old shape
            (PicoAgent.Binding) must ship in lockstep.
            """;
    }

}
