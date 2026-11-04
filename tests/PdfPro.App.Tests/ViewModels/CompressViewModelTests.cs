using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services.Optimizing;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Optimizing;
using PdfPro.Core.Text;
using PdfPro.Operations.Optimizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Nén": presets, custom values, and the preview of one page before and after.</summary>
public sealed class CompressViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ICompressPreviews _previews = Substitute.For<ICompressPreviews>();
    private readonly string _input;

    public CompressViewModelTests()
    {
        _input = _temp.Combine("hồ sơ.pdf");
        File.WriteAllBytes(_input, new byte[3000]);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ItStarts_WithEbook_TheSizeNow_AndTheCurrentPage()
    {
        using var dialog = Create(currentPage: 2);

        dialog.Preset.ShouldBe(CompressionPreset.Ebook);
        dialog.IsCustom.ShouldBeFalse();
        dialog.Presets.Select(p => p.Preset).ShouldBe([CompressionPreset.Screen, CompressionPreset.Ebook, CompressionPreset.Print, CompressionPreset.Archive, CompressionPreset.Custom]);
        dialog.OriginalSizeText.ShouldBe(Format(Strings.Compress_OriginalSize, ByteSizeFormatter.Format(3000, CultureInfo.CurrentCulture)));
        dialog.PreviewPageText.ShouldBe("3");
        dialog.Parameters(out var error).ShouldBe(new CompressParams(CompressionPreset.Ebook));
        error.ShouldBeNull();
    }

    [Fact]
    public void Apply_GivesThePreset_AndCloses()
    {
        using var dialog = Create();
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;
        dialog.Preset = CompressionPreset.Screen;

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBe(new CompressParams(CompressionPreset.Screen));
        closed.ShouldBe(true);
    }

    [Theory]
    [InlineData("35", "75", "Compress_Error_Dpi")]
    [InlineData("1201", "75", "Compress_Error_Dpi")]
    [InlineData("150,5", "75", "Compress_Error_Dpi")]
    [InlineData("150", "9", "Compress_Error_Quality")]
    [InlineData("150", "abc", "Compress_Error_Quality")]
    public void CustomValues_OutOfRange_AreExplained_AndBlockCompressing(string dpi, string quality, string message)
    {
        using var dialog = Create();
        dialog.Preset = CompressionPreset.Custom;

        dialog.DpiText = dpi;
        dialog.QualityText = quality;

        dialog.ErrorText.ShouldBe(Strings.ResourceManager.GetString(message, CultureInfo.CurrentUICulture));
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
        dialog.Parameters(out _).ShouldBeNull();
    }

    [Fact]
    public void CustomValues_InRange_AreTheParameters()
    {
        using var dialog = Create();
        dialog.Preset = CompressionPreset.Custom;
        dialog.DpiText = "200";
        dialog.QualityText = "50";

        dialog.IsCustom.ShouldBeTrue();
        dialog.ErrorText.ShouldBeNull();
        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBe(new CompressParams(CompressionPreset.Custom, 200, 50));
    }

    [Fact]
    public async Task Preview_CompressesTheChosenPage_AndShowsBothWithTheirSizes()
    {
        var before = Image();
        var after = Image();
        _previews.RenderAsync(_input, 1, new CompressParams(CompressionPreset.Screen), CompressViewModel.PreviewScale, _temp.Path, Arg.Any<CancellationToken>())
            .Returns(new CompressPreviewImages(before, after, new CompressReport(400_000, 100_000, 2, 3, 0, 1, false, false)));
        using var dialog = Create();
        dialog.Preset = CompressionPreset.Screen;
        dialog.PreviewPageText = "2";

        await dialog.PreviewCommand.ExecuteAsync(null);

        dialog.Before.ShouldBeSameAs(before);
        dialog.After.ShouldBeSameAs(after);
        var culture = CultureInfo.CurrentCulture;
        dialog.PreviewSummary.ShouldBe(
            Format(Strings.Compress_Preview_Sizes, 2, ByteSizeFormatter.Format(400_000, culture), ByteSizeFormatter.Format(100_000, culture), 0.75.ToString("P0", culture))
            + Environment.NewLine + Format(Strings.Compress_Preview_Images, 2, 3));
        dialog.IsPreviewStale.ShouldBeFalse();
        dialog.IsPreviewing.ShouldBeFalse();

        dialog.Preset = CompressionPreset.Print;

        dialog.IsPreviewStale.ShouldBeTrue("the preview no longer shows the chosen preset");
    }

    [Fact]
    public async Task Preview_OfAPageThatWouldNotShrink_SaysItStaysAsItIs()
    {
        _previews.RenderAsync(Arg.Any<string>(), 0, Arg.Any<CompressParams>(), Arg.Any<double>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CompressPreviewImages(Image(), Image(), new CompressReport(5000, 5000, 0, 0, 0, 0, false, true)));
        using var dialog = Create();

        await dialog.PreviewCommand.ExecuteAsync(null);

        dialog.PreviewSummary.ShouldNotBeNull().ShouldStartWith(Format(Strings.Compress_Preview_Kept, 1, ByteSizeFormatter.Format(5000, CultureInfo.CurrentCulture)));
    }

    [Fact]
    public async Task APageOutOfRange_IsNotPreviewed_ButCompressingStaysPossible()
    {
        using var dialog = Create();

        dialog.PreviewPageText = "6";
        await dialog.PreviewCommand.ExecuteAsync(null);

        dialog.PreviewError.ShouldBe(Format(Strings.Compress_Error_Page, 5));
        await _previews.DidNotReceive().RenderAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CompressParams>(), Arg.Any<double>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        dialog.ErrorText.ShouldBeNull();
        dialog.ApplyCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task APreviewThatFails_IsExplained_ButCompressingStaysPossible()
    {
        _previews.RenderAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CompressParams>(), Arg.Any<double>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidDataException("unreadable"));
        using var dialog = Create();

        await dialog.PreviewCommand.ExecuteAsync(null);

        dialog.PreviewError.ShouldBe(Strings.Compress_Error_Preview);
        dialog.Before.ShouldBeNull();
        dialog.ApplyCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void WithoutPreviews_ThePreviewCommandIsOff()
    {
        using var dialog = new CompressViewModel(new CompressSource("hồ sơ.pdf", _input, 5, 0, _temp.Path), previews: null, NullLogger.Instance);

        dialog.PreviewsPages.ShouldBeFalse();
        dialog.PreviewCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task TheRealPreview_RendersBothPages_AndLeavesNoFileBehind()
    {
        var input = _temp.Combine("ảnh.pdf");
        await File.WriteAllBytesAsync(input, PhotoPdf.Create(), TestContext.Current.CancellationToken);
        var work = _temp.Combine("work");
        var previews = new CompressPreviews(new CompressOperation(), new PdfDocumentLoader(TestOperations.PdfiumGate), new PageImageRenderer());

        var images = await previews.RenderAsync(input, 0, new CompressParams(CompressionPreset.Screen), CompressViewModel.PreviewScale, work, TestContext.Current.CancellationToken);

        images.Report.ImagesResampled.ShouldBe(1, "the 400 DPI photograph is brought down to 96 DPI");
        images.Report.CompressedSize.ShouldBeLessThan(images.Report.OriginalSize / 4);
        ((double)((BitmapSource)images.Before).PixelWidth).ShouldBe(1190, 1);
        ((double)((BitmapSource)images.After).PixelWidth).ShouldBe(1190, 1);
        Directory.GetFiles(work).ShouldBeEmpty("the one-page files are removed");
    }

    private CompressViewModel Create(int currentPage = 0)
        => new(new CompressSource("hồ sơ.pdf", _input, 5, currentPage, _temp.Path), _previews, NullLogger.Instance);

    private static ImageSource Image()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return image;
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
