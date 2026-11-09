using System.Globalization;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Converting;
using PdfPro.App.ViewModels.Optimizing;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Text;
using PdfPro.Operations.Optimizing;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Tối ưu cho web", "Sửa tệp hỏng" and "Nén".</summary>
public sealed partial class MainWindowViewModelTests
{
    private readonly IExternalToolRunner _tools = Substitute.For<IExternalToolRunner>();

    [Fact]
    public async Task Repair_ABrokenFile_IsReadBack_Explained_AndOpened()
    {
        var input = _fixture.CreateFile("hỏng.pdf", File.ReadAllBytes(Samples.Broken));
        var output = Path.Combine(Path.GetDirectoryName(input)!, Format(Strings.Repair_Name, "hỏng") + ".pdf");
        _fileDialogs.PickPdfFile(Strings.Repair_Pick_Title).Returns(input);
        _fileDialogs.PickSavePath(Format(Strings.Repair_Name, "hỏng") + ".pdf", Path.GetDirectoryName(input)).Returns(output);
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(3), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);

        await viewModel.RepairCommand.ExecuteAsync(null);

        using (var repaired = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            repaired.PageCount.ShouldBe(3);
        }

        await _messages.Received(1).ShowInfoAsync(Strings.Repair_Done_Title, Format(Strings.Repair_Done_Message, Strings.Repair_Method_CrossReference, 3));
        viewModel.ActiveDocument.ShouldNotBeNull().FilePath.ShouldBe(output, "the repaired file opens");
    }

    [Fact]
    public async Task Repair_AFileNothingReads_SaysWhatWasTried()
    {
        var input = _fixture.CreateFile("rác.pdf", "%PDF-1.7\nnot a document\n"u8.ToArray());
        var output = Path.Combine(Path.GetDirectoryName(input)!, "ra.pdf");
        _fileDialogs.PickPdfFile(Strings.Repair_Pick_Title).Returns(input);
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(output);
        var viewModel = Create(isDebugBuild: false);

        await viewModel.RepairCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(
            Strings.Repair_Error_Title,
            Arg.Is<string>(m => m.StartsWith(Strings.Repair_Error_Message, StringComparison.Ordinal)
                && m.Contains(Format(Strings.Repair_Error_Line, Strings.Repair_Method_Qpdf, Strings.Repair_Failure_Missing), StringComparison.Ordinal)));
        File.Exists(output).ShouldBeFalse();
        viewModel.Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Linearize_WritesAWebCopy_OfTheDocumentsOwnFile()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        var output = Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.Linearize_Name, "a") + ".pdf");
        _fileDialogs.PickSavePath(Format(Strings.Linearize_Name, "a") + ".pdf", Path.GetDirectoryName(path)).Returns(output);
        ExternalToolRequest? ran = null;
        _tools.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            ran = call.Arg<ExternalToolRequest>();
            File.Copy(ran.Arguments[^2], ran.Arguments[^1], overwrite: true);
            return new ExternalToolResult("qpdf", 0, string.Empty, string.Empty, TimeSpan.Zero, false);
        });

        await viewModel.LinearizeCommand.ExecuteAsync(null);

        ran.ShouldNotBeNull().Arguments.ShouldContain("--linearize");
        ran.Arguments[^2].ShouldBe(path, "nothing was unsaved: the document's own file is laid out");
        File.Exists(output).ShouldBeTrue();
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Linearize_Done, Path.GetFileName(output)));
    }

    [Fact]
    public async Task Linearize_WithoutQpdf_SaysHowToGetIt()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(Path.Combine(Path.GetDirectoryName(path)!, "web.pdf"));
        _tools.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ExternalToolNotFoundException("qpdf"));

        await viewModel.LinearizeCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Linearize_Error_Title, Strings.Optimize_Error_NoQpdf);
    }

    [Fact]
    public async Task Compress_WritesASmallerCopy_SaysHowMuch_AndOffersToOpenIt()
    {
        var (viewModel, path) = await CreateWithPhotoAsync();
        var original = File.ReadAllBytes(path);
        var output = Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.Compress_Name, "ảnh") + ".pdf");
        _fileDialogs.PickSavePath(Format(Strings.Compress_Name, "ảnh") + ".pdf", Path.GetDirectoryName(path)).Returns(output);
        CompressViewModel? shown = null;
        _pageDialogs.ShowCompress(Arg.Any<CompressViewModel>()).Returns(call =>
        {
            shown = call.Arg<CompressViewModel>();
            shown.Preset = CompressionPreset.Screen;
            shown.ApplyCommand.Execute(null);
            return true;
        });
        _messages.AskAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>()).Returns(DialogChoice.Primary);

        await viewModel.CompressCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull().OriginalSize.ShouldBe(new FileInfo(path).Length, "nothing was unsaved: the document's own file is compressed");
        var (before, after) = (new FileInfo(path).Length, new FileInfo(output).Length);
        after.ShouldBeLessThan(before / 4);
        var culture = CultureInfo.CurrentCulture;
        var message = Format(
            Strings.Compress_Done_Message,
            Path.GetFileName(output),
            ByteSizeFormatter.Format(before, culture),
            ByteSizeFormatter.Format(after, culture),
            (1 - ((double)after / before)).ToString("P0", culture));
        await _messages.Received(1).AskAsync(
            Strings.Compress_Done_Title,
            Arg.Is<string>(m => m.StartsWith(message, StringComparison.Ordinal)),
            Strings.Compress_Open,
            Arg.Is<string?>(p => p == null),
            Strings.Common_Close);
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Compress_Done, Path.GetFileName(output)));
        viewModel.ActiveDocument.ShouldNotBeNull().FilePath.ShouldBe(output, "the user chose to open the compressed copy");
        File.ReadAllBytes(path).ShouldBe(original, "the document's own file is not touched");
    }

    [Fact]
    public async Task Compress_CancelledInTheDialog_WritesNothing()
    {
        var (viewModel, path) = await CreateWithPhotoAsync();
        _pageDialogs.ShowCompress(Arg.Any<CompressViewModel>()).Returns(false);

        await viewModel.CompressCommand.ExecuteAsync(null);

        _fileDialogs.DidNotReceive().PickSavePath(Arg.Any<string>(), Arg.Any<string?>());
        File.Exists(Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.Compress_Name, "ảnh") + ".pdf")).ShouldBeFalse();
    }

    [Fact]
    public async Task Compress_ToTheDocumentsOwnFile_IsRefused()
    {
        var (viewModel, path) = await CreateWithPhotoAsync();
        var original = File.ReadAllBytes(path);
        _pageDialogs.ShowCompress(Arg.Any<CompressViewModel>()).Returns(call =>
        {
            call.Arg<CompressViewModel>().ApplyCommand.Execute(null);
            return true;
        });
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(path);

        await viewModel.CompressCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Compress_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(path)));
        File.ReadAllBytes(path).ShouldBe(original);
    }

    [Fact]
    public async Task Grayscale_WritesAGreyCopy_SaysWhatItIs_AndOffersToOpenIt()
    {
        var (viewModel, path) = await CreateWithPhotoAsync();
        var original = File.ReadAllBytes(path);
        var output = Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.Grayscale_Name, "ảnh") + ".pdf");
        _fileDialogs.PickSavePath(Format(Strings.Grayscale_Name, "ảnh") + ".pdf", Path.GetDirectoryName(path)).Returns(output);
        _pageDialogs.ShowGrayscale(Arg.Any<GrayscaleViewModel>()).Returns(call =>
        {
            call.Arg<GrayscaleViewModel>().ApplyCommand.Execute(null);
            return true;
        });
        _messages.AskAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>()).Returns(DialogChoice.Cancel);

        await viewModel.GrayscaleCommand.ExecuteAsync(null);

        using (var grey = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            grey.PageCount.ShouldBe(1);
        }

        var culture = CultureInfo.CurrentCulture;
        var message = Format(
            Strings.Grayscale_Done_Message,
            Path.GetFileName(output),
            1,
            ByteSizeFormatter.Format(original.Length, culture),
            ByteSizeFormatter.Format(new FileInfo(output).Length, culture));
        await _messages.Received(1).AskAsync(Strings.Grayscale_Done_Title, message, Strings.Compress_Open, Arg.Is<string?>(p => p == null), Strings.Common_Close);
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Grayscale_Done, Path.GetFileName(output)));
        viewModel.Documents.Count().ShouldBe(1, "the user chose not to open the copy");
        File.ReadAllBytes(path).ShouldBe(original, "the document's own file is not touched");
    }

    [Fact]
    public async Task Grayscale_AsPictures_SaysTheTextIsGone()
    {
        var (viewModel, path) = await CreateWithPhotoAsync();
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(Path.Combine(Path.GetDirectoryName(path)!, "xám.pdf"));
        _pageDialogs.ShowGrayscale(Arg.Any<GrayscaleViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<GrayscaleViewModel>();
            dialog.IsRaster = true;
            dialog.DpiText = "72";
            dialog.ApplyCommand.Execute(null);
            return true;
        });

        await viewModel.GrayscaleCommand.ExecuteAsync(null);

        await _messages.Received(1).AskAsync(
            Strings.Grayscale_Done_Title,
            Arg.Is<string>(m => m.EndsWith(Strings.Grayscale_Done_AllPictures, StringComparison.Ordinal)),
            Strings.Compress_Open,
            Arg.Is<string?>(p => p == null),
            Strings.Common_Close);
    }

    [Fact]
    public async Task ToImages_WritesThePages_IntoTheChosenFolder_AndOpensIt()
    {
        var (viewModel, path) = await CreateWithPhotoAsync();
        var folder = Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.ToImages_Folder_Default, "ảnh"));
        ToImagesViewModel? shown = null;
        _pageDialogs.ShowToImages(Arg.Any<ToImagesViewModel>()).Returns(call =>
        {
            shown = call.Arg<ToImagesViewModel>();
            shown.IsJpeg = true;
            shown.DpiText = "72";
            shown.ApplyCommand.Execute(null);
            return true;
        });

        await viewModel.ToImagesCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull().OutputFolder.ShouldBe(folder, "the folder proposed is next to the document");
        Directory.GetFiles(folder).Select(Path.GetFileName).ShouldBe(["ảnh-001.jpg"]);
        _shell.Received(1).OpenFolder(folder);
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.ToImages_Done, folder));
    }

    [Fact]
    public async Task ToImages_CancelledInTheDialog_WritesNothing()
    {
        var (viewModel, path) = await CreateWithPhotoAsync();
        _pageDialogs.ShowToImages(Arg.Any<ToImagesViewModel>()).Returns(false);

        await viewModel.ToImagesCommand.ExecuteAsync(null);

        Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.ToImages_Folder_Default, "ảnh"))).ShouldBeFalse();
        _shell.DidNotReceive().OpenFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task FromImages_MakesAPdf_OfThePicturesInOrder_AndOpensIt()
    {
        var folder = Directory.CreateDirectory(_fixture.CreateFolder("ảnh chụp")).FullName;
        string[] pictures = [Jpeg(folder, "trang 2.jpg", 200), Jpeg(folder, "trang 1.jpg", 100)];
        var output = Path.Combine(folder, "hồ sơ.pdf");
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(2), call.ArgAt<string>(0)));
        _pageDialogs.ShowFromImages(Arg.Any<FromImagesViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<FromImagesViewModel>();
            dialog.AddPaths(pictures);
            dialog.OutputPath = output;
            dialog.RunCommand.Execute(null);
            return true;
        });
        var viewModel = Create(isDebugBuild: false);

        await viewModel.FromImagesCommand.ExecuteAsync(null);

        using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            pdf.PageCount.ShouldBe(2);
            pdf.Pages[0].Width.Point.ShouldBe(100 * 72 / 96.0, 0.01, "trang 1 comes first (natural order)");
            pdf.Pages[1].Width.Point.ShouldBe(200 * 72 / 96.0, 0.01);
        }

        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.FromImages_Done, "hồ sơ.pdf"));
        viewModel.ActiveDocument.ShouldNotBeNull().FilePath.ShouldBe(output, "the PDF opens when done");
    }

    [Fact]
    public async Task FromImages_APictureThatCannotBeRead_IsNamed()
    {
        var folder = Directory.CreateDirectory(_fixture.CreateFolder("ảnh hỏng")).FullName;
        var bad = Path.Combine(folder, "hỏng.jpg");
        File.WriteAllBytes(bad, [0xFF, 0xD8, 0, 0]);
        var output = Path.Combine(folder, "x.pdf");
        _pageDialogs.ShowFromImages(Arg.Any<FromImagesViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<FromImagesViewModel>();
            dialog.AddPaths([Jpeg(folder, "tốt.jpg", 50), bad]);
            dialog.OutputPath = output;
            dialog.RunCommand.Execute(null);
            return true;
        });
        var viewModel = Create(isDebugBuild: false);

        await viewModel.FromImagesCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.FromImages_Error_Title, Format(Strings.FromImages_Error_Unreadable, "hỏng.jpg"));
        File.Exists(output).ShouldBeFalse();
    }

    /// <summary>A grey JPEG of <paramref name="width"/> × 50 pixels, without a resolution (96 DPI is used).</summary>
    private static string Jpeg(string folder, string name, int width)
    {
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
            width, 50, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, new byte[width * 50], width)));
        var path = Path.Combine(folder, name);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    /// <summary>"ảnh.pdf" (a page with a 400 DPI photograph) open and saved; qpdf is not installed.</summary>
    private async Task<(MainWindowViewModel ViewModel, string Path)> CreateWithPhotoAsync()
    {
        var path = _fixture.CreateFile("ảnh.pdf", PhotoPdf.Create());
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(1), call.ArgAt<string>(0)));
        _tools.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ExternalToolNotFoundException("qpdf"));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        return (viewModel, path);
    }
}
