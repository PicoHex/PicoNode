
namespace PicoNode.Web.Tests;

public sealed class StreamingMultipartParserCancellationTests
{
    [Test]
    public async Task ParseAsync_cancelled_throws()
    {
        var data =
            "--b\r\nContent-Disposition: form-data; name=\"x\"\r\n\r\ny\r\n--b--\r\n"u8.ToArray();
        using var stream = new MemoryStream(data);
        using var cts = new CancellationTokenSource();

        // Cancel before starting
        await cts.CancelAsync();

        await Assert
            .That(() => StreamingMultipartParser.ParseAsync(stream, "b", ct: cts.Token).AsTask())
            .Throws<OperationCanceledException>();
    }
}
