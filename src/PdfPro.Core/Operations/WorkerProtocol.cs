using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfPro.Core.Operations;

/// <summary>
/// An operation that keeps a report of each run beside its output (OCR, redaction, sanitizing): the app reads it after
/// the run. A run in the worker process (SPEC Phase 13) brings the report back and gives it to the app's own instance.
/// </summary>
public interface IReportingOperation
{
    /// <summary>The type of the report.</summary>
    Type ReportType { get; }

    /// <summary>The report of the last run made with these very parameters, or null.</summary>
    object? ReportOf(object parameters);

    /// <summary>Remembers a report for these parameters, as if this instance had made the run.</summary>
    void Remember(object parameters, object report);
}

/// <summary>What a message between the app and its worker process carries.</summary>
public enum WorkerMessageKind
{
    /// <summary>App → worker: run an operation.</summary>
    Run,

    /// <summary>App → worker: stop the run <see cref="WorkerMessage.Id"/>.</summary>
    Cancel,

    /// <summary>Worker → app: how far the run is.</summary>
    Progress,

    /// <summary>Worker → app: the run is over (succeeded, failed or cancelled).</summary>
    Result,
}

/// <summary>How a run in the worker ended.</summary>
public enum WorkerOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One message on the worker's pipe (SPEC Phase 13, "PdfPro.Worker"). Parameters and reports are JSON — passwords and
/// document text may be in them: the pipe is the current user's only, and neither side ever logs a message.
/// </summary>
public sealed record WorkerMessage(WorkerMessageKind Kind, int Id)
{
    /// <summary>Run: the operation's id.</summary>
    public string? Operation { get; init; }

    /// <summary>Run: its parameters, as JSON.</summary>
    public string? Parameters { get; init; }

    /// <summary>Run: the file the operation reads.</summary>
    public string? Input { get; init; }

    /// <summary>Run: the file a content operation writes (the file or folder of an export).</summary>
    public string? Output { get; init; }

    /// <summary>Run: an export (compress, convert…) rather than a content operation; its result comes back as the report.</summary>
    public bool Export { get; init; }

    /// <summary>Run: the files an export reads, when it reads several (else <see cref="Input"/>).</summary>
    public IReadOnlyList<string>? Inputs { get; init; }

    /// <summary>Run: the password of an export's protected input (in memory only).</summary>
    public string? Password { get; init; }

    /// <summary>Run: what an export names its files after.</summary>
    public string? Name { get; init; }

    /// <summary>Progress.</summary>
    public int Done { get; init; }

    /// <summary>Progress.</summary>
    public int Total { get; init; }

    /// <summary>Progress: the stage's resource key.</summary>
    public string? Stage { get; init; }

    /// <summary>Result.</summary>
    public WorkerOutcome Outcome { get; init; }

    /// <summary>Result: the report of a <see cref="IReportingOperation"/>, as JSON.</summary>
    public string? Report { get; init; }

    /// <summary>Result: the type of the error (its full name).</summary>
    public string? ErrorType { get; init; }

    /// <summary>Result: what the error says (in memory only).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Result: more about the error, as its type needs (an OCR failure's kind, validation errors as JSON…).</summary>
    public string? ErrorDetail { get; init; }
}

/// <summary>Messages on a stream: a 4-byte little-endian length, then the message as UTF-8 JSON.</summary>
public static class WorkerFraming
{
    /// <summary>A message larger than this is refused (an OCR report of a long document stays well below).</summary>
    public const int MaxMessageBytes = 256 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task WriteAsync(Stream stream, WorkerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next message, or null when the other side closed the stream.</summary>
    /// <exception cref="InvalidDataException">The stream does not hold a message.</exception>
    public static async Task<WorkerMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var head = new byte[4];
        if (!await FillAsync(stream, head, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException("Not a worker message.");
        }

        var body = new byte[length];
        if (!await FillAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The worker message is cut short.");
        }

        try
        {
            return JsonSerializer.Deserialize<WorkerMessage>(body, Options) ?? throw new InvalidDataException("Not a worker message.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Not a worker message.", ex);
        }
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return read == 0 ? false : throw new InvalidDataException("The worker message is cut short.");
            }

            read += count;
        }

        return true;
    }
}

/// <summary>Why a run in the worker process did not come back.</summary>
public enum WorkerFailure
{
    /// <summary>The worker could not be started (its program is missing, or it did not connect in time).</summary>
    NotStarted,

    /// <summary>The worker stopped during the run: the file made it crash, or it went over its memory limit.</summary>
    Crashed,

    /// <summary>The run went over its time limit; the worker was stopped.</summary>
    TimedOut,
}

/// <summary>The worker process failed the run (not the operation); the app's document is unchanged.</summary>
public sealed class WorkerException(WorkerFailure failure, Exception? innerException = null)
    : Exception($"The worker process failed: {failure}.", innerException)
{
    public WorkerFailure Failure { get; } = failure;
}

/// <summary>
/// The operation failed in the worker with an error the app has no type for here: <see cref="ErrorType"/> names it
/// (callers that know the type can build it again from <see cref="Detail"/>).
/// </summary>
public sealed class WorkerOperationException(string errorType, string message, string? detail)
    : Exception(message)
{
    /// <summary>The full name of the error's type in the worker.</summary>
    public string ErrorType { get; } = errorType;

    public string? Detail { get; } = detail;
}
