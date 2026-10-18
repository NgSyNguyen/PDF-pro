using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Infrastructure.Instances;
using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests.Instances;

public sealed class SingleInstanceChannelTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // A unique name per test: never talks to a PDF_Pro that happens to run on this machine.
    private readonly string _name = "PdfProTest-" + Guid.NewGuid().ToString("N");
    private readonly List<SingleInstanceChannel> _channels = [];
    private readonly ConcurrentQueue<IReadOnlyList<string>> _received = new();
    private readonly SemaphoreSlim _receivedSignal = new(0);

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _channels)
        {
            await channel.DisposeAsync();
        }

        _receivedSignal.Dispose();
    }

    [Fact]
    public void FirstInstance_IsPrimaryAndTheNextOneIsNot()
    {
        Create().IsPrimary.ShouldBeTrue();
        Create().IsPrimary.ShouldBeFalse();
        Create().IsPrimary.ShouldBeFalse();
    }

    [Fact]
    public async Task PrimaryRole_IsFreedWhenThePrimaryCloses()
    {
        var primary = Create();
        primary.StartListening(_ => { });

        await primary.DisposeAsync();
        _channels.Remove(primary);

        Create().IsPrimary.ShouldBeTrue();
    }

    [Fact]
    public async Task Secondary_HandsItsFilesToThePrimary()
    {
        StartPrimary();
        var secondary = Create();

        var sent = await secondary.SendAsync([@"C:\Tài liệu\hợp đồng.pdf", @"D:\b.pdf"], Timeout, TestContext.Current.CancellationToken);

        sent.ShouldBeTrue();
        (await NextReceivedAsync()).ShouldBe([@"C:\Tài liệu\hợp đồng.pdf", @"D:\b.pdf"]);
    }

    [Fact]
    public async Task LaunchWithoutFiles_SendsAnEmptyList()
    {
        StartPrimary();

        (await Create().SendAsync([], Timeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await NextReceivedAsync()).ShouldBeEmpty("an empty list asks the primary to come to the front");
    }

    [Fact]
    public async Task SimultaneousLaunches_AreAllDelivered()
    {
        // Explorer starts one process per file when several files are opened at once.
        StartPrimary();
        var sends = Enumerable.Range(0, 8)
            .Select(i => Create().SendAsync([$@"C:\docs\{i}.pdf"], Timeout, TestContext.Current.CancellationToken))
            .ToArray();

        (await Task.WhenAll(sends)).ShouldAllBe(sent => sent);
        var files = new List<string>();
        for (var i = 0; i < sends.Length; i++)
        {
            files.AddRange(await NextReceivedAsync());
        }

        files.Order().ShouldBe(Enumerable.Range(0, 8).Select(i => $@"C:\docs\{i}.pdf").Order());
    }

    [Fact]
    public async Task SecondaryStartedBeforeThePrimaryListens_WaitsForIt()
    {
        var primary = Create();
        var sending = Create().SendAsync([@"C:\early.pdf"], Timeout, TestContext.Current.CancellationToken);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        primary.StartListening(Receive);

        (await sending).ShouldBeTrue();
        (await NextReceivedAsync()).ShouldBe([@"C:\early.pdf"]);
    }

    [Fact]
    public async Task PrimaryThatDoesNotListen_TimesOut()
    {
        var logger = new CapturingLogger<SingleInstanceChannel>();
        Create(); // holds the role but never listens (hung or still starting)
        var secondary = SingleInstanceChannel.Create(_name, logger);
        _channels.Add(secondary);

        var sent = await secondary.SendAsync([@"C:\a.pdf"], TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        sent.ShouldBeFalse();
        logger.Entries.ShouldContain(e => e.Message.Contains("did not answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedMessages_AreIgnoredAndTheNextLaunchStillWorks()
    {
        StartPrimary();

        await SendRawAsync([9, 0, 0, 0, 0]);                     // unknown protocol version
        await SendRawAsync(Header(SingleInstanceChannel.MaxMessageBytes + 1)); // too long
        await SendRawAsync([.. Header(2), 0xC3, 0x28]);          // invalid UTF-8

        (await Create().SendAsync([@"C:\ok.pdf"], Timeout, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await NextReceivedAsync()).ShouldBe([@"C:\ok.pdf"]);
        _received.ShouldBeEmpty();
    }

    [Fact]
    public void ListeningAsASecondary_IsRejected()
    {
        Create();

        Should.Throw<InvalidOperationException>(() => Create().StartListening(_ => { }));
    }

    [Fact]
    public void Encode_KeepsOrderAndDropsWhatDoesNotFit()
    {
        var longPath = @"C:\" + new string('x', 400_000) + ".pdf";

        var payload = SingleInstanceChannel.Encode(["a.pdf", longPath, longPath, longPath, "b.pdf"], out var dropped);

        dropped.ShouldBe(2);
        payload.Length.ShouldBeLessThanOrEqualTo(SingleInstanceChannel.MaxMessageBytes);
        SingleInstanceChannel.Decode(payload).ShouldBe(["a.pdf", longPath, longPath]);
    }

    [Fact]
    public void Decode_RoundTripsUnicodeAndSkipsEmptyEntries()
    {
        var payload = Encoding.UTF8.GetBytes("C:\\Tệp ấn phẩm.pdf\0\0D:\\b.pdf");

        SingleInstanceChannel.Decode(payload).ShouldBe(["C:\\Tệp ấn phẩm.pdf", "D:\\b.pdf"]);
        SingleInstanceChannel.Decode([]).ShouldBeEmpty();
        Should.Throw<InvalidDataException>(() => SingleInstanceChannel.Decode([0xFF, 0xFE]));
    }

    [Fact]
    public void DefaultName_DoesNotContainTheUserName()
    {
        var name = SingleInstanceChannel.DefaultName();

        // A hash of the user and the logon session id, nothing readable.
        name.ShouldMatch("^PdfPro-[0-9a-f]{16}-[0-9]+$");
        SingleInstanceChannel.DefaultName().ShouldBe(name);
    }

    private static byte[] Header(int length)
    {
        var header = new byte[5];
        header[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), length);
        return header;
    }

    private SingleInstanceChannel Create()
    {
        var channel = SingleInstanceChannel.Create(_name, NullLogger<SingleInstanceChannel>.Instance);
        _channels.Add(channel);
        return channel;
    }

    private void StartPrimary()
    {
        var primary = Create();
        primary.IsPrimary.ShouldBeTrue();
        primary.StartListening(Receive);
    }

    private void Receive(IReadOnlyList<string> files)
    {
        _received.Enqueue(files);
        _receivedSignal.Release();
    }

    private async Task<IReadOnlyList<string>> NextReceivedAsync()
    {
        (await _receivedSignal.WaitAsync(Timeout, TestContext.Current.CancellationToken)).ShouldBeTrue("the primary should receive a message");
        _received.TryDequeue(out var files).ShouldBeTrue();
        return files!;
    }

    /// <summary>Writes raw bytes like a broken or hostile client and waits until the primary hangs up.</summary>
    private async Task SendRawAsync(byte[] bytes)
    {
        var token = TestContext.Current.CancellationToken;
        await using var client = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(token);
        await client.WriteAsync(bytes, token);
        await client.FlushAsync(token);
        var buffer = new byte[1];
        try
        {
            (await client.ReadAsync(buffer, token)).ShouldBe(0, "a malformed message gets no acknowledgement");
        }
        catch (IOException)
        {
            // The primary closed the pipe first.
        }
    }
}
