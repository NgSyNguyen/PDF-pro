using System.Globalization;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

public sealed class DocumentPropertiesViewModelTests : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly PdfSize A4 = new(595, 842);

    private readonly TempDirectory _temp = new();
    private readonly CapturingLogger<DocumentPropertiesViewModel> _logger = new();
    private readonly string _file;

    public DocumentPropertiesViewModelTests()
    {
        _file = _temp.Combine("hop-dong.pdf");
        File.WriteAllBytes(_file, new byte[3 * 1024 * 1024 / 2]); // 1.5 MB
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void BeforeLoading_ShowsWhatIsKnownAndPlaceholders()
    {
        var document = FakePdfDocument.WithPages(12);
        var viewModel = Create(document);

        viewModel.FileName.ShouldBe("hop-dong.pdf");
        Value(viewModel, Strings.DocProps_PageCount).ShouldBe(12.ToString("N0", CultureInfo.CurrentCulture));
        Value(viewModel, Strings.DocProps_PdfVersion).ShouldBe("1.7");
        Value(viewModel, Strings.DocProps_FileName).ShouldBe("hop-dong.pdf");
        Value(viewModel, Strings.DocProps_Location).ShouldBe(_temp.Path);
        Value(viewModel, Strings.DocProps_DocTitle).ShouldBe(Strings.DocProps_Loading);
        Value(viewModel, Strings.DocProps_FileSize).ShouldBe(Strings.DocProps_Loading);
        viewModel.Sections.Select(s => s.Header).ShouldBe(
        [
            Strings.DocProps_SectionDescription,
            Strings.DocProps_SectionFile,
            Strings.DocProps_SectionDocument,
            Strings.DocProps_SectionSecurity,
        ]);
    }

    [Fact]
    public async Task Load_FillsTheInfoDictionaryAndFileSize()
    {
        var created = new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.FromHours(7));
        var document = FakePdfDocument.WithPages(3);
        document.Metadata = new PdfDocumentMetadata(
            "Hợp đồng\r\nmua bán", "Nguyễn Văn A", null, "  ", "Writer", "PDFsharp", created, null);
        var viewModel = Create(document);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await viewModel.LoadAsync();

        changed.ShouldContain(nameof(DocumentPropertiesViewModel.Sections));
        Value(viewModel, Strings.DocProps_DocTitle).ShouldBe("Hợp đồng mua bán");
        Value(viewModel, Strings.DocProps_Author).ShouldBe("Nguyễn Văn A");
        Value(viewModel, Strings.DocProps_Subject).ShouldBe(Strings.DocProps_NotSet);
        Value(viewModel, Strings.DocProps_Keywords).ShouldBe(Strings.DocProps_NotSet);
        Value(viewModel, Strings.DocProps_Creator).ShouldBe("Writer");
        Value(viewModel, Strings.DocProps_Producer).ShouldBe("PDFsharp");
        Value(viewModel, Strings.DocProps_Created).ShouldBe(created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        Value(viewModel, Strings.DocProps_Modified).ShouldBe(Strings.DocProps_NotSet);
        Value(viewModel, Strings.DocProps_FileSize).ShouldBe(DocumentPropertiesViewModel.FormatFileSize(1_572_864, CultureInfo.CurrentCulture));
    }

    [Fact]
    public async Task Load_Failure_StillShowsTheRest()
    {
        var document = FakePdfDocument.WithPages(2);
        document.MetadataError = new ObjectDisposedException("document");
        var viewModel = Create(document);

        await viewModel.LoadAsync();

        Value(viewModel, Strings.DocProps_DocTitle).ShouldBe(Strings.DocProps_ReadFailed);
        Value(viewModel, Strings.DocProps_Created).ShouldBe(Strings.DocProps_ReadFailed);
        Value(viewModel, Strings.DocProps_PageCount).ShouldBe(2.ToString("N0", CultureInfo.CurrentCulture));
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Load_MissingFile_SaysTheSizeCouldNotBeRead()
    {
        var viewModel = new DocumentPropertiesViewModel(FakePdfDocument.WithPages(1), _temp.Combine("gone.pdf"), _logger);

        await viewModel.LoadAsync();

        Value(viewModel, Strings.DocProps_FileSize).ShouldBe(Strings.DocProps_ReadFailed);
    }

    [Fact]
    public void PlainDocument_HasNoSecurityFeatures()
    {
        var viewModel = Create(FakePdfDocument.WithPages(1));

        Value(viewModel, Strings.DocProps_Encryption).ShouldBe(Strings.DocProps_No);
        Value(viewModel, Strings.DocProps_Signatures).ShouldBe(Strings.DocProps_No);
        Value(viewModel, Strings.DocProps_Form).ShouldBe(Strings.DocProps_No);
        Value(viewModel, Strings.DocProps_JavaScript).ShouldBe(Strings.DocProps_No);
    }

    [Fact]
    public void SecurityFeatures_AreDescribed()
    {
        var document = FakePdfDocument.WithPages(1);

        // Revision 6 (AES-256) with only the print bit (bit 3) granted; two signatures, an AcroForm and JavaScript.
        document.Features = new PdfDocumentFeatures("2.0", 6, 1 << 2, 2, PdfFormType.AcroForm, true);
        var viewModel = Create(document);

        Value(viewModel, Strings.DocProps_PdfVersion).ShouldBe("2.0");
        Value(viewModel, Strings.DocProps_Encryption).ShouldBe(
            string.Format(CultureInfo.CurrentCulture, Strings.DocProps_EncryptedDetails, Strings.DocProps_Allowed, Strings.DocProps_NotAllowed));
        Value(viewModel, Strings.DocProps_Signatures).ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.DocProps_SignatureCount, 2));
        Value(viewModel, Strings.DocProps_Form).ShouldBe(Strings.DocProps_FormAcroForm);
        Value(viewModel, Strings.DocProps_JavaScript).ShouldBe(Strings.DocProps_Yes);
    }

    [Theory]
    [InlineData(PdfFormType.XfaFull)]
    [InlineData(PdfFormType.XfaForeground)]
    public void XfaForms_AreNamed(PdfFormType formType)
    {
        var document = FakePdfDocument.WithPages(1);
        document.Features = FakePdfDocument.PlainFeatures with { FormType = formType };

        Value(Create(document), Strings.DocProps_Form).ShouldBe(Strings.DocProps_FormXfa);
    }

    [Fact]
    public void PaperSize_NamesStandardFormats()
    {
        DocumentPropertiesViewModel.DescribePaperSize([A4, A4], Invariant)
            .ShouldBe(string.Format(Invariant, Strings.DocProps_PaperSizeNamed, "A4", "210", "297"));
        DocumentPropertiesViewModel.DescribePaperSize([new PdfSize(792, 612)], Invariant)
            .ShouldBe(string.Format(Invariant, Strings.DocProps_PaperSizeLandscape, "Letter", "279", "216"));
    }

    [Fact]
    public void PaperSize_CustomAndMixedSizes()
    {
        DocumentPropertiesViewModel.DescribePaperSize([new PdfSize(500, 700)], Invariant)
            .ShouldBe(string.Format(Invariant, Strings.DocProps_PaperSizeCustom, "176", "247"));

        var first = string.Format(Invariant, Strings.DocProps_PaperSizeNamed, "A4", "210", "297");
        DocumentPropertiesViewModel.DescribePaperSize([A4, new PdfSize(842, 595)], Invariant)
            .ShouldBe(string.Format(Invariant, Strings.DocProps_PaperSizeMixed, first));
        DocumentPropertiesViewModel.DescribePaperSize([A4, new PdfSize(596, 843)], Invariant)
            .ShouldBe(first, "sizes within the tolerance count as the same paper");
        DocumentPropertiesViewModel.DescribePaperSize([], Invariant).ShouldBe(Strings.DocProps_NotSet);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1,023 B")]
    [InlineData(1024, "1 KB|1,024")]
    [InlineData(203_052, "198 KB|203,052")]
    [InlineData(1_572_864, "1.5 MB|1,572,864")]
    [InlineData(3_192_222, "3 MB|3,192,222")]
    [InlineData(5_368_709_120, "5 GB|5,368,709,120")]
    public void FileSize_IsRoundedWithTheExactByteCount(long bytes, string expected)
    {
        var parts = expected.Split('|');
        var text = DocumentPropertiesViewModel.FormatFileSize(bytes, Invariant);

        text.ShouldBe(parts.Length == 1
            ? string.Format(Invariant, Strings.DocProps_FileSizeBytes, parts[0][..^2])
            : string.Format(Invariant, Strings.DocProps_FileSizeValue, parts[0], parts[1]));
    }

    private static string Value(DocumentPropertiesViewModel viewModel, string label)
        => viewModel.Sections.SelectMany(s => s.Rows).Single(r => r.Label == label).Value;

    private DocumentPropertiesViewModel Create(FakePdfDocument document) => new(document, _file, _logger);
}
