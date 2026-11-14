using PdfPro.Core.Operations;

namespace PdfPro.Operations;

/// <summary>What an export run reads and writes.</summary>
/// <param name="Inputs">The files it reads (one for most operations; several for "Ảnh sang PDF").</param>
/// <param name="Output">The file it writes — or the folder, for the operations that write several files.</param>
/// <param name="Password">The password of a protected input, or null.</param>
/// <param name="Name">What its files are named after (the document's name without extension), for those that name them.</param>
public sealed record ExportCall(IReadOnlyList<string> Inputs, string Output, string? Password = null, string? Name = null)
{
    /// <summary>The one file most operations read.</summary>
    public string Input => Inputs.Count > 0 ? Inputs[0] : throw new InvalidOperationException("An export needs an input file.");

    /// <summary>The name its files are named after: <see cref="Name"/>, or the input's name without extension.</summary>
    public string BaseName => Name ?? Path.GetFileNameWithoutExtension(Input);
}

/// <summary>
/// An operation that looks at a file before it is applied — the scan tools measure blank pages, tilt, dark borders and
/// turned pages (SPEC Phase 10) — and can do so in the worker process (SPEC Phase 13): rendering every page of a scan
/// is where a hostile file would crash PDFium. Its parameters are of <see cref="MeasureType"/>, what it finds of
/// <see cref="MeasurementType"/> (records that travel as JSON).
/// </summary>
public interface IMeasuringOperation
{
    /// <summary>The operation's id ("scan.deskew").</summary>
    string Id { get; }

    /// <summary>The type of the measuring parameters.</summary>
    Type MeasureType { get; }

    /// <summary>The type of what <see cref="MeasureAsync"/> returns.</summary>
    Type MeasurementType { get; }

    /// <summary>Looks at <paramref name="input"/>: what the operation would be applied with.</summary>
    Task<object> MeasureAsync(string input, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// An operation that writes new files from files — compress, convert, repair… (SPEC Phase 9) — and can run in the worker
/// process (SPEC Phase 13), so that a hostile or huge file crashes or exhausts the worker, never the window: what it
/// reads and writes is an <see cref="ExportCall"/>, what it returns is of <see cref="ResultType"/> (a record that travels
/// as JSON, see ParameterSchema.ReportJson).
/// </summary>
public interface IExportOperation
{
    /// <summary>The operation's id ("optimize.compress").</summary>
    string Id { get; }

    /// <summary>The type of what <see cref="ExportAsync"/> returns.</summary>
    Type ResultType { get; }

    /// <summary>Runs the operation: its result (a report, the files written…).</summary>
    Task<object> ExportAsync(ExportCall call, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}
