using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PdfPro.Infrastructure.Instances;

/// <summary>
/// One PDF_Pro per user session. The first launch (primary) creates a named mutex and listens on a named pipe; a
/// later launch (secondary) sends its files to the primary, lets it take the foreground and exits. Both ends use
/// <see cref="PipeOptions.CurrentUserOnly"/>, so other users can neither send files nor impersonate the primary.
/// Messages are a version byte, a length and the NUL-separated paths in UTF-8; anything malformed is dropped.
/// </summary>
public sealed class SingleInstanceChannel : IAsyncDisposable
{
    /// <summary>Upper bound of one message (about a thousand long paths).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    private const byte ProtocolVersion = 1;
    private const byte Acknowledgement = 0x06;
    private const char Separator = '\0';
    private const int MaxConsecutiveFailures = 5;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _pipeName;
    private readonly Mutex? _mutex;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listening;

    private SingleInstanceChannel(string name, Mutex? mutex, ILogger logger)
    {
        _pipeName = name;
        _mutex = mutex;
        _logger = logger;
    }

    /// <summary>True for the first instance of the session: it should listen; otherwise it should send.</summary>
    public bool IsPrimary => _mutex is not null;

    /// <summary>
    /// "PdfPro-{hash}-{session}": distinct per user, data folder and logon session, without the user name (an instance
    /// with its own data folder, such as a UI test run, never hands files to the user's instance).
    /// </summary>
    /// <param name="dataRoot">The instance's data folder.</param>
    public static string DefaultName(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}|{Path.GetFullPath(dataRoot).TrimEnd('\\')}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(user.ToUpperInvariant())))[..16];
        using var process = Process.GetCurrentProcess();
        return $"PdfPro-{hash}-{process.SessionId}";
    }

    /// <summary>Claims the primary role when no other instance holds <paramref name="name"/>.</summary>
    public static SingleInstanceChannel Create(string name, ILogger<SingleInstanceChannel> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(logger);

        // Never owned, only held open: it exists while the primary runs and vanishes with it, even after a crash.
        var mutex = new Mutex(initiallyOwned: false, @"Local\" + name, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceChannel(name, mutex, logger);
        }

        mutex.Dispose();
        return new SingleInstanceChannel(name, mutex: null, logger);
    }

    /// <summary>
    /// Primary only: accepts file lists from later launches until disposed. <paramref name="onFilesReceived"/> runs on
    /// a pool thread; an empty list means "just bring the window to the front".
    /// </summary>
    public void StartListening(Action<IReadOnlyList<string>> onFilesReceived)
    {
        ArgumentNullException.ThrowIfNull(onFilesReceived);
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance listens.");
        }

        if (_listening is not null)
        {
            throw new InvalidOperationException("The channel is already listening.");
        }

        _listening = Task.Run(() => ListenAsync(onFilesReceived, _stop.Token));
    }

    /// <summary>
    /// Secondary: hands <paramref name="files"/> to the primary and allows it to take the foreground. False when
    /// the primary did not answer within <paramref name="timeout"/> (it may be closing or hung).
    /// </summary>
    public async Task<bool> SendAsync(IReadOnlyList<string> files, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var payload = Encode(files, out var dropped);
        if (dropped > 0)
        {
            _logger.LogWarning("{Count} file(s) did not fit in the message to the running instance", dropped);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            NativeMethods.AllowServerToTakeForeground(client);

            var header = new byte[5];
            header[0] = ProtocolVersion;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
            await client.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);

            var answer = new byte[1];
            await client.ReadExactlyAsync(answer, deadline.Token).ConfigureAwait(false);
            return answer[0] == Acknowledgement;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The running instance did not answer within {Timeout}", timeout);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            _logger.LogWarning(ex, "The running instance could not be reached");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listening is not null)
        {
            try
            {
                await _listening.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped while waiting for a connection.
            }
        }

        _stop.Dispose();
        _mutex?.Dispose();
    }

    /// <summary>Encodes as many files as fit in <see cref="MaxMessageBytes"/>; <paramref name="dropped"/> counts the rest.</summary>
    internal static byte[] Encode(IReadOnlyList<string> files, out int dropped)
    {
        var text = new StringBuilder();
        var bytes = 0;
        var kept = 0;
        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file) || file.Contains(Separator, StringComparison.Ordinal))
            {
                continue;
            }

            var size = StrictUtf8.GetByteCount(file) + (kept > 0 ? 1 : 0);
            if (bytes + size > MaxMessageBytes)
            {
                break;
            }

            if (kept > 0)
            {
                text.Append(Separator);
            }

            text.Append(file);
            bytes += size;
            kept++;
        }

        dropped = files.Count(f => !string.IsNullOrEmpty(f) && !f.Contains(Separator, StringComparison.Ordinal)) - kept;
        return StrictUtf8.GetBytes(text.ToString());
    }

    /// <exception cref="InvalidDataException">Not a message of this protocol version, too long or not UTF-8.</exception>
    internal static IReadOnlyList<string> Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The message is not valid UTF-8.", ex);
        }

        return text.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task ListenAsync(Action<IReadOnlyList<string>> onFilesReceived, CancellationToken stopping)
    {
        var failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                // One instance listens at a time, but the limit must allow more: the previous instance lives on until its
                // client closes its end, and a limit of one made the next instance fail as "busy" meanwhile — enough
                // failures in a burst of launches (several files opened at once) stopped the listening for good.
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process holds the name. Retry a few times, then run without forwarding.
                if (++failures >= MaxConsecutiveFailures)
                {
                    _logger.LogError(ex, "The single-instance pipe could not be created; later launches will open their own window");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200 * failures), stopping).ConfigureAwait(false);
                continue;
            }

            await using (server.ConfigureAwait(false))
            {
                try
                {
                    await server.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                    failures = 0;
                    var files = await ReadMessageAsync(server, stopping).ConfigureAwait(false);
                    await server.WriteAsync(new[] { Acknowledgement }, stopping).ConfigureAwait(false);
                    await server.FlushAsync(stopping).ConfigureAwait(false);
                    _logger.LogInformation("Another launch handed over {Count} file(s)", files.Count);
                    onFilesReceived(files);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
                {
                    _logger.LogWarning(ex, "A message from another launch was ignored");
                }
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ReadMessageAsync(PipeStream pipe, CancellationToken stopping)
    {
        // A client that connects and then stalls must not block later launches.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(ReadTimeout);

        var header = new byte[5];
        await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
        if (header[0] != ProtocolVersion)
        {
            throw new InvalidDataException($"Unsupported message version {header[0]}.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length is < 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException($"Invalid message length {length}.");
        }

        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
        return Decode(payload);
    }
}
