using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Imaging;
using PdfPro.Core.Ocr;

namespace PdfPro.Ocr;

/// <summary>Where Tesseract is (null: look for it) and which data it reads.</summary>
public sealed record TesseractSettings(string? ProgramPath = null, OcrDataKind Data = OcrDataKind.Best);

/// <summary>
/// Tesseract 5 (Apache-2.0), run as a separate program through <see cref="IExternalToolRunner"/> — never a shell,
/// arguments one by one, a time limit per page, cancellable. A page is written as a grey PNG (with its resolution) into
/// a folder of its own, recognised in one run into a PDF holding only its text (<c>textonly_pdf</c>), TSV (words, boxes
/// and confidences), plain text and, when asked, hOCR; the folder is removed afterwards. Each run uses one thread
/// (OMP_THREAD_LIMIT=1): pages are recognised in parallel by the caller.
/// </summary>
public sealed partial class TesseractEngine(IExternalToolRunner runner, ITesseractLocator locator, Func<TesseractSettings> settings) : IOcrEngine
{
    public const string Name = "Tesseract";

    public OcrEngineInfo? Describe()
    {
        var installation = Find();
        return installation is null ? null : new OcrEngineInfo(Name, installation.Program, installation.Data, installation.Languages, installation.HasOsd);
    }

    public async Task<OcrPageResult> RecognizeAsync(OcrImage image, OcrOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Languages.Count == 0)
        {
            throw new ArgumentException("At least one language is needed.", nameof(options));
        }

        if (options.Languages.FirstOrDefault(l => !LanguageCode().IsMatch(l)) is { } wrong)
        {
            throw new ArgumentException($"'{wrong}' is not a language code.", nameof(options));
        }

        var installation = Find() ?? throw new OcrException(OcrFailure.NotInstalled);
        var missing = options.Languages.Where(l => !installation.Languages.Contains(l, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            throw new OcrException(OcrFailure.LanguageMissing, string.Join('+', missing));
        }

        var folder = WorkFolder(options.WorkingFolder);
        try
        {
            var input = Path.Combine(folder, "page.png");
            await File.WriteAllBytesAsync(input, PngCodec.EncodeGray(image.Image, image.Dpi), cancellationToken).ConfigureAwait(false);
            var output = Path.Combine(folder, "page");
            List<string> arguments =
            [
                "--tessdata-dir", installation.DataFolder,
                input, output,
                "--dpi", Dpi(image),
                "-l", string.Join('+', options.Languages),
                "-c", "textonly_pdf=1",
                "-c", "tessedit_create_pdf=1",
                "-c", "tessedit_create_tsv=1",
                "-c", "tessedit_create_txt=1",
            ];
            if (options.Hocr)
            {
                arguments.AddRange(["-c", "tessedit_create_hocr=1"]);
            }

            var result = await RunAsync(installation, folder, arguments, options.PageTimeout, cancellationToken).ConfigureAwait(false);
            string Output(string extension) => output + extension;
            if (!result.Succeeded || !File.Exists(Output(".pdf")) || !File.Exists(Output(".tsv")))
            {
                throw new OcrException(OcrFailure.Failed, Problem(result));
            }

            var text = File.Exists(Output(".txt")) ? await File.ReadAllTextAsync(Output(".txt"), Encoding.UTF8, cancellationToken).ConfigureAwait(false) : string.Empty;
            var hocr = options.Hocr && File.Exists(Output(".hocr")) ? await File.ReadAllTextAsync(Output(".hocr"), Encoding.UTF8, cancellationToken).ConfigureAwait(false) : null;
            return new OcrPageResult(
                await File.ReadAllBytesAsync(Output(".pdf"), cancellationToken).ConfigureAwait(false),
                TesseractOutput.Words(await File.ReadAllTextAsync(Output(".tsv"), Encoding.UTF8, cancellationToken).ConfigureAwait(false)),
                text.Normalize(NormalizationForm.FormC).TrimEnd('\f', '\n', '\r', ' '),
                hocr);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    public async Task<OcrOrientation?> DetectOrientationAsync(OcrImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var installation = Find() ?? throw new OcrException(OcrFailure.NotInstalled);
        if (!installation.HasOsd)
        {
            throw new OcrException(OcrFailure.LanguageMissing, TesseractLocator.Osd);
        }

        var folder = WorkFolder(null);
        try
        {
            var input = Path.Combine(folder, "page.png");
            await File.WriteAllBytesAsync(input, PngCodec.EncodeGray(image.Image, image.Dpi), cancellationToken).ConfigureAwait(false);
            var result = await RunAsync(
                installation,
                folder,
                ["--tessdata-dir", installation.DataFolder, input, "stdout", "--dpi", Dpi(image), "--psm", "0", "-l", TesseractLocator.Osd],
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false);
            if (TesseractOutput.Orientation(result.StandardOutput) is { } orientation)
            {
                return orientation;
            }

            // Too little text to tell is not a failure: the page stays as it is.
            return result.StandardError.Contains("Too few characters", StringComparison.Ordinal) || result.Succeeded
                ? null
                : throw new OcrException(OcrFailure.Failed, Problem(result));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private TesseractInstallation? Find()
    {
        var current = settings();
        return locator.Find(current.ProgramPath, current.Data);
    }

    private async Task<ExternalToolResult> RunAsync(TesseractInstallation installation, string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var request = new ExternalToolRequest(installation.Program, arguments)
        {
            WorkingDirectory = folder,
            Timeout = timeout,
            EnvironmentVariables = new Dictionary<string, string?> { ["OMP_THREAD_LIMIT"] = "1", ["TESSDATA_PREFIX"] = null },
        };
        try
        {
            return await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalToolNotFoundException ex)
        {
            throw new OcrException(OcrFailure.NotInstalled, innerException: ex);
        }
        catch (ExternalToolTimeoutException ex)
        {
            throw new OcrException(OcrFailure.TimedOut, innerException: ex);
        }
    }

    /// <summary>The last lines Tesseract wrote (its messages name no document content).</summary>
    private static string Problem(ExternalToolResult result)
        => $"exit code {result.ExitCode}: " + ExternalToolResult.Tail(result.StandardError.Trim(), 300);

    private static string Dpi(OcrImage image) => Math.Round(image.Dpi).ToString(CultureInfo.InvariantCulture);

    private static string WorkFolder(string? parent)
    {
        var folder = Path.Combine(parent ?? Path.GetTempPath(), "pdfpro-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open (antivirus) is left for the system's temporary-file clean-up.
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]{2,40}$")]
    private static partial Regex LanguageCode();
}

/// <summary>Reads what Tesseract writes.</summary>
public static class TesseractOutput
{
    /// <summary>The words of a TSV report (level 5 with text), NFC, in Tesseract's reading order.</summary>
    public static IReadOnlyList<OcrWord> Words(string tsv)
    {
        ArgumentNullException.ThrowIfNull(tsv);
        var words = new List<OcrWord>();
        foreach (var line in tsv.Split('\n'))
        {
            var columns = line.TrimEnd('\r').Split('\t');
            if (columns.Length < 12 || columns[0] != "5")
            {
                continue;
            }

            var text = string.Join('\t', columns[11..]).Trim();
            if (text.Length == 0
                || !double.TryParse(columns[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) || confidence < 0
                || !int.TryParse(columns[2], CultureInfo.InvariantCulture, out var block)
                || !int.TryParse(columns[3], CultureInfo.InvariantCulture, out var paragraph)
                || !int.TryParse(columns[4], CultureInfo.InvariantCulture, out var lineNumber)
                || !int.TryParse(columns[6], CultureInfo.InvariantCulture, out var left)
                || !int.TryParse(columns[7], CultureInfo.InvariantCulture, out var top)
                || !int.TryParse(columns[8], CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(columns[9], CultureInfo.InvariantCulture, out var height))
            {
                continue;
            }

            words.Add(new OcrWord(text.Normalize(NormalizationForm.FormC), new OcrBox(left, top, width, height), Math.Min(confidence, 100), block, paragraph, lineNumber));
        }

        return words;
    }

    /// <summary>What orientation and script detection (--psm 0) printed, or null when it printed nothing usable.</summary>
    public static OcrOrientation? Orientation(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        int? rotate = null;
        double confidence = 0;
        string? script = null;
        foreach (var line in output.Split('\n'))
        {
            var cut = line.IndexOf(':', StringComparison.Ordinal);
            if (cut < 0)
            {
                continue;
            }

            var key = line[..cut].Trim();
            var value = line[(cut + 1)..].Trim();
            switch (key)
            {
                case "Rotate" when int.TryParse(value, CultureInfo.InvariantCulture, out var degrees) && degrees is 0 or 90 or 180 or 270:
                    rotate = degrees;
                    break;
                case "Orientation confidence" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sure):
                    confidence = sure;
                    break;
                case "Script":
                    script = value.Length > 0 ? value : null;
                    break;
            }
        }

        return rotate is { } turn ? new OcrOrientation(turn, confidence, script) : null;
    }
}
