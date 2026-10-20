using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels;

/// <summary>A label and its value in the properties dialog.</summary>
public sealed record PropertyRow(string Label, string Value);

/// <summary>A titled group of rows in the properties dialog.</summary>
public sealed record PropertySection(string Header, IReadOnlyList<PropertyRow> Rows);

/// <summary>
/// Document properties (Ctrl+D): description from the Info dictionary, file facts, PDF version, page count and paper
/// size, and whether the document is encrypted, signed, has a form or JavaScript. Facts known since the document
/// opened show at once; the Info dictionary and the file size are read in the background by <see cref="LoadAsync"/>.
/// </summary>
public sealed partial class DocumentPropertiesViewModel : ObservableObject
{
    private readonly IPdfDocument _document;
    private readonly IReadOnlyList<PdfSize> _pageSizes;
    private readonly string _filePath;
    private readonly ILogger _logger;
    private PdfDocumentMetadata? _metadata;
    private long? _fileSize;
    private bool _loaded;

    /// <param name="document">The document whose structure is described (metadata, version, security).</param>
    /// <param name="pageSizes">The pages as the page plan shows them.</param>
    /// <param name="filePath">The file the document is saved to.</param>
    /// <param name="logger">Logs read failures.</param>
    public DocumentPropertiesViewModel(IPdfDocument document, IReadOnlyList<PdfSize> pageSizes, string filePath, ILogger<DocumentPropertiesViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageSizes);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _document = document;
        _pageSizes = pageSizes;
        _filePath = filePath;
        _logger = logger;
        Sections = BuildSections();
    }

    public string FileName => Path.GetFileName(_filePath);

    [ObservableProperty]
    public partial IReadOnlyList<PropertySection> Sections { get; private set; }

    /// <summary>Reads the Info dictionary and the file size; failures only show as "could not be read".</summary>
    public async Task LoadAsync()
    {
        var size = Task.Run(() => ReadFileSize(_filePath));
        try
        {
            _metadata = await _document.GetMetadataAsync();
        }
        catch (Exception ex)
        {
            // Closed or damaged document: the dialog still shows everything else.
            _logger.LogWarning(ex, "Reading the document information dictionary failed");
        }

        _fileSize = await size;
        _loaded = true;
        Sections = BuildSections();
    }

    /// <summary>"A4 (210 × 297 mm)", "Letter landscape (…)", "200 × 300 mm" or, for mixed sizes, the first page's.</summary>
    internal static string DescribePaperSize(IReadOnlyList<PdfSize> pageSizes, CultureInfo culture)
    {
        if (pageSizes.Count == 0)
        {
            return Strings.DocProps_NotSet;
        }

        var first = pageSizes[0];
        var description = DescribeSize(first, culture);
        var uniform = pageSizes.All(s => Math.Abs(s.Width - first.Width) <= PaperSizes.Tolerance && Math.Abs(s.Height - first.Height) <= PaperSizes.Tolerance);
        return uniform ? description : string.Format(culture, Strings.DocProps_PaperSizeMixed, description);
    }

    /// <summary>"203 KB (203.052 byte)"; sizes under 1 KB only show the byte count.</summary>
    internal static string FormatFileSize(long bytes, CultureInfo culture)
    {
        var exact = bytes.ToString("N0", culture);
        return bytes < 1024
            ? string.Format(culture, Strings.DocProps_FileSizeBytes, exact)
            : string.Format(culture, Strings.DocProps_FileSizeValue, ByteSizeFormatter.Format(bytes, culture), exact);
    }

    private static string DescribeSize(PdfSize size, CultureInfo culture)
    {
        var width = PaperSizes.ToMillimetres(size.Width).ToString("0", culture);
        var height = PaperSizes.ToMillimetres(size.Height).ToString("0", culture);
        return PaperSizes.Identify(size) switch
        {
            { IsLandscape: true } paper => string.Format(culture, Strings.DocProps_PaperSizeLandscape, paper.Name, width, height),
            { } paper => string.Format(culture, Strings.DocProps_PaperSizeNamed, paper.Name, width, height),
            null => string.Format(culture, Strings.DocProps_PaperSizeCustom, width, height),
        };
    }

    private static string YesNo(bool value) => value ? Strings.DocProps_Yes : Strings.DocProps_No;

    private static string Allowed(bool value) => value ? Strings.DocProps_Allowed : Strings.DocProps_NotAllowed;

    private long? ReadFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The file size could not be read");
            return null;
        }
    }

    private IReadOnlyList<PropertySection> BuildSections()
    {
        var culture = CultureInfo.CurrentCulture;
        var features = _document.Features;
        return
        [
            new(Strings.DocProps_SectionDescription,
            [
                new(Strings.DocProps_DocTitle, Info(m => m.Title)),
                new(Strings.DocProps_Author, Info(m => m.Author)),
                new(Strings.DocProps_Subject, Info(m => m.Subject)),
                new(Strings.DocProps_Keywords, Info(m => m.Keywords)),
            ]),
            new(Strings.DocProps_SectionFile,
            [
                new(Strings.DocProps_FileName, FileName),
                new(Strings.DocProps_Location, Path.GetDirectoryName(_filePath) ?? string.Empty),
                new(Strings.DocProps_FileSize, !_loaded ? Strings.DocProps_Loading : _fileSize is { } bytes ? FormatFileSize(bytes, culture) : Strings.DocProps_ReadFailed),
            ]),
            new(Strings.DocProps_SectionDocument,
            [
                new(Strings.DocProps_PdfVersion, string.IsNullOrWhiteSpace(features.FileVersion) ? Strings.DocProps_NotSet : features.FileVersion),
                new(Strings.DocProps_PageCount, _pageSizes.Count.ToString("N0", culture)),
                new(Strings.DocProps_PaperSize, DescribePaperSize(_pageSizes, culture)),
                new(Strings.DocProps_Creator, Info(m => m.Creator)),
                new(Strings.DocProps_Producer, Info(m => m.Producer)),
                new(Strings.DocProps_Created, Date(m => m.CreationDate, culture)),
                new(Strings.DocProps_Modified, Date(m => m.ModificationDate, culture)),
            ]),
            new(Strings.DocProps_SectionSecurity,
            [
                new(Strings.DocProps_Encryption, features.IsEncrypted
                    ? string.Format(culture, Strings.DocProps_EncryptedDetails, Allowed(features.CanPrint), Allowed(features.CanCopyContent))
                    : Strings.DocProps_No),
                new(Strings.DocProps_Signatures, features.HasDigitalSignatures
                    ? string.Format(culture, Strings.DocProps_SignatureCount, features.SignatureCount)
                    : Strings.DocProps_No),
                new(Strings.DocProps_Form, features.FormType switch
                {
                    PdfFormType.AcroForm => Strings.DocProps_FormAcroForm,
                    PdfFormType.XfaFull or PdfFormType.XfaForeground => Strings.DocProps_FormXfa,
                    _ => Strings.DocProps_No,
                }),
                new(Strings.DocProps_JavaScript, YesNo(features.HasJavaScript)),
            ]),
        ];
    }

    private string Info(Func<PdfDocumentMetadata, string?> field)
    {
        if (!_loaded)
        {
            return Strings.DocProps_Loading;
        }

        if (_metadata is null)
        {
            return Strings.DocProps_ReadFailed;
        }

        var value = TextNormalizer.ToSingleLine(field(_metadata));
        return value.Length > 0 ? value : Strings.DocProps_NotSet;
    }

    private string Date(Func<PdfDocumentMetadata, DateTimeOffset?> field, CultureInfo culture)
    {
        if (!_loaded)
        {
            return Strings.DocProps_Loading;
        }

        if (_metadata is null)
        {
            return Strings.DocProps_ReadFailed;
        }

        return field(_metadata) is { } date ? date.ToLocalTime().ToString("g", culture) : Strings.DocProps_NotSet;
    }
}
