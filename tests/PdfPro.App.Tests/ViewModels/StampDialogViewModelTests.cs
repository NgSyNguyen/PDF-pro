using System.Globalization;
using System.IO;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Editing;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Stamping;
using PdfPro.Core.Text;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Stamping;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The stamp dialogs (watermark, background, header and footer, page numbers, removing stamps) and their commands.</summary>
public sealed class StampDialogViewModelTests : IDisposable
{
    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly ManualDebouncerFactory _debouncers = new();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly IPageDialogService _pageDialogs = Substitute.For<IPageDialogService>();
    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();
    private readonly IDocumentSaver _saver = Substitute.For<IDocumentSaver>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task Watermark_StartsAsALightDraftText_OnEveryPage()
    {
        await using var dialog = Watermark(5);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.ErrorText.ShouldBeNull();
        dialog.ApplyCommand.Execute(null);

        closed.ShouldBe(true);
        var choice = dialog.Result.ShouldNotBeNull();
        choice.Operation.ShouldBeOfType<WatermarkOperation>();
        choice.Description.ShouldBe(Strings.History_Watermark);
        var parameters = choice.Parameters.ShouldBeOfType<WatermarkParams>();
        parameters.Text.ShouldBe(Strings.Watermark_DefaultText);
        parameters.ImagePath.ShouldBeNull();
        parameters.Opacity.ShouldBe(0.3);
        parameters.Rotation.ShouldBe(45);
        parameters.Anchor.ShouldBe(StampAnchor.Center);
        parameters.Style.ShouldNotBeNull().FontFamily.ShouldBe(FontFamilies.Default);
        parameters.Pages.ShouldBe("1-5");
    }

    [Fact]
    public async Task Watermark_NeedsATextOrAPicture()
    {
        var picture = _fixture.CreateFile("logo.png");
        _fileDialogs.PickImageFile(Arg.Any<string>()).Returns(picture);
        await using var dialog = Watermark(3);

        dialog.Text = "  ";
        dialog.ErrorText.ShouldBe(Strings.Watermark_Error_Text);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();

        dialog.UsesPicture = true;
        dialog.ErrorText.ShouldBe(Strings.Watermark_Error_Picture);
        dialog.PictureName.ShouldBe(Strings.Watermark_NoPicture);

        dialog.PickPictureCommand.Execute(null);
        dialog.ErrorText.ShouldBeNull();
        dialog.PictureName.ShouldBe("logo.png");
        dialog.PictureScalePercent = 25;
        dialog.ApplyCommand.Execute(null);

        var parameters = dialog.Result.ShouldNotBeNull().Parameters.ShouldBeOfType<WatermarkParams>();
        parameters.ImagePath.ShouldBe(picture);
        parameters.Text.ShouldBeNull();
        parameters.ImageScale.ShouldBe(0.25);
    }

    [Fact]
    public async Task APageRangeOutsideTheDocument_IsRefused()
    {
        await using var dialog = Watermark(3);

        dialog.Target.IsRange = true;
        dialog.Target.RangeText = "2-9";

        dialog.ErrorText.ShouldNotBeNull();
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();

        dialog.Target.RangeText = "2-3";
        dialog.ErrorText.ShouldBeNull();
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldNotBeNull().Parameters.ShouldBeOfType<WatermarkParams>().Pages.ShouldBe("2-3");
    }

    [Fact]
    public async Task Preview_ShowsTheCurrentPageStamped_OrAsItIsWhenItIsLeftOut()
    {
        var preview = new RecordingPreview();
        var dialog = Watermark(5, currentPage: 1, preview: preview);
        preview.Renders.ShouldBeEmpty("the preview waits until typing pauses");

        _debouncers.FireAll();

        var (operation, parameters) = preview.Renders.Single();
        operation.ShouldBeOfType<WatermarkOperation>();
        parameters.ShouldBeOfType<WatermarkParams>().Pages.ShouldBeNull("the preview file holds the current page only");
        dialog.PreviewLabel.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Stamp_PreviewOf, 2));

        dialog.Target.IsOdd = true;
        _debouncers.FireAll();

        preview.Renders[^1].Operation.ShouldBeNull("page 2 is not stamped, so it is shown as it is");
        preview.Renders[^1].Parameters.ShouldBeNull();

        await dialog.DisposeAsync();
        preview.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Background_IsAColourOrAPicture()
    {
        var picture = _fixture.CreateFile("paper.jpg");
        _fileDialogs.PickImageFile(Arg.Any<string>()).Returns(picture);
        await using var dialog = new BackgroundDialogViewModel(Context(2), new BackgroundOperation(), _fileDialogs, null, _debouncers);

        dialog.Color = "vàng";
        dialog.ErrorText.ShouldBe(Strings.Stamp_Error_Color);

        dialog.SetColorCommand.Execute("#16A34A");
        dialog.ErrorText.ShouldBeNull();
        dialog.ApplyCommand.Execute(null);
        var colour = dialog.Result.ShouldNotBeNull().Parameters.ShouldBeOfType<BackgroundParams>();
        colour.Color.ShouldBe(new RgbColor(0x16, 0xA3, 0x4A));
        colour.ImagePath.ShouldBeNull();
        colour.Fit.ShouldBe(BackgroundFit.Stretch);
        dialog.Result.Description.ShouldBe(Strings.History_Background);

        dialog.PickPictureCommand.Execute(null);
        dialog.KeepProportions = true;
        dialog.OpacityPercent = 40;
        dialog.ApplyCommand.Execute(null);
        var photo = dialog.Result.ShouldNotBeNull().Parameters.ShouldBeOfType<BackgroundParams>();
        photo.Color.ShouldBeNull();
        photo.ImagePath.ShouldBe(picture);
        photo.Fit.ShouldBe(BackgroundFit.Fit);
        photo.Opacity.ShouldBe(0.4);
    }

    [Fact]
    public async Task HeaderFooter_WritesTheDocumentName_WithMarginsInMillimetres()
    {
        await using var dialog = new HeaderFooterDialogViewModel(Context(4, fileName: "Hồ sơ"), new HeaderFooterOperation(), null, _debouncers);

        dialog.BottomCenter.ShouldBe(Strings.HeaderFooter_DefaultFooter);
        dialog.TopLeft = "{filename}";
        dialog.ApplyCommand.Execute(null);

        var choice = dialog.Result.ShouldNotBeNull();
        choice.Description.ShouldBe(Strings.History_HeaderFooter);
        var parameters = choice.Parameters.ShouldBeOfType<HeaderFooterParams>();
        parameters.TopLeft.ShouldBe("{filename}");
        parameters.TopCenter.ShouldBeNull("blank places are left out");
        parameters.FileName.ShouldBe("Hồ sơ", "not the name of the session's working file");
        parameters.MarginX.ShouldBe(36, 0.01);
        parameters.MarginY.ShouldBe(24.09, 0.01);
        parameters.Pages.ShouldBe("1-4");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{author}")]
    public async Task HeaderFooter_NeedsText_WithKnownFields(string footer)
    {
        await using var dialog = new HeaderFooterDialogViewModel(Context(2), new HeaderFooterOperation(), null, _debouncers);

        dialog.BottomCenter = footer;

        dialog.ErrorText.ShouldBe(Strings.HeaderFooter_Error_Text);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task HeaderFooterPreview_WritesOutWhatThePreviewedPageGets()
    {
        var preview = new RecordingPreview();
        await using var dialog = new HeaderFooterDialogViewModel(Context(3, currentPage: 1, fileName: "Hồ sơ", title: "Tờ trình"), new HeaderFooterOperation(), preview, _debouncers);
        dialog.TopLeft = "{filename} – {title}";
        dialog.TopRight = "{{nháp}}";
        dialog.SkipFirstPage = true;
        dialog.NumberStyle = PageNumberStyle.UpperRoman;

        _debouncers.FireAll();

        var (operation, parameters) = preview.Renders.Single();
        operation.ShouldBeOfType<HeaderFooterOperation>();
        var page = parameters.ShouldBeOfType<HeaderFooterParams>();
        page.TopLeft.ShouldBe("Hồ sơ – Tờ trình");
        page.TopRight.ShouldBe("{{nháp}}", "written out, then kept as text in the one-page file");
        page.BottomCenter.ShouldBe(StampTemplate.Escape(Render(Strings.HeaderFooter_DefaultFooter, "I", "II")), "the cover is skipped, so page 2 is the first numbered");
        page.SkipFirstPage.ShouldBeFalse();
        page.Pages.ShouldBeNull();
    }

    [Fact]
    public async Task HeaderFooterPreview_OfASkippedCover_ShowsThePageAsItIs()
    {
        var preview = new RecordingPreview();
        await using var dialog = new HeaderFooterDialogViewModel(Context(3, currentPage: 0), new HeaderFooterOperation(), preview, _debouncers);

        dialog.SkipFirstPage = true;
        _debouncers.FireAll();

        preview.Renders.Single().Operation.ShouldBeNull();
    }

    [Fact]
    public async Task PageNumbers_FromAChosenPage_InTheChosenStyle()
    {
        var preview = new RecordingPreview();
        await using var dialog = new PageNumbersDialogViewModel(Context(5, currentPage: 3, fileName: "Hồ sơ"), new PageNumbersOperation(), preview, _debouncers);

        dialog.Pattern = Strings.PageNumbers_Pattern_PageOf;
        dialog.FirstPage = 2;
        dialog.NumberStyle = PageNumberStyle.UpperRoman;
        dialog.Anchor = StampAnchor.BottomRight;
        _debouncers.FireAll();

        var page = preview.Renders.Single().Parameters.ShouldBeOfType<PageNumbersParams>();
        page.Pattern.ShouldBe(StampTemplate.Escape(Render(Strings.PageNumbers_Pattern_PageOf, "III", "IV")), "page 4 is the third numbered of four");
        page.FirstPage.ShouldBe(1);
        page.Pages.ShouldBeNull();

        dialog.ApplyCommand.Execute(null);
        var choice = dialog.Result.ShouldNotBeNull();
        choice.Description.ShouldBe(Strings.History_PageNumbers);
        var parameters = choice.Parameters.ShouldBeOfType<PageNumbersParams>();
        parameters.Pattern.ShouldBe(Strings.PageNumbers_Pattern_PageOf);
        parameters.FirstPage.ShouldBe(2);
        parameters.Style.ShouldBe(PageNumberStyle.UpperRoman);
        parameters.Anchor.ShouldBe(StampAnchor.BottomRight);
        parameters.FileName.ShouldBe("Hồ sơ");
        dialog.Anchors.Select(a => a.Value).ShouldNotContain(StampAnchor.Center, "numbers go at the top or the bottom");
    }

    [Fact]
    public async Task PageNumbers_RefuseAFirstPageAfterTheLast_AndAnEmptyPattern()
    {
        var preview = new RecordingPreview();
        await using var dialog = new PageNumbersDialogViewModel(Context(5, currentPage: 0), new PageNumbersOperation(), preview, _debouncers);
        dialog.FirstPage = 2;
        _debouncers.FireAll();
        preview.Renders[^1].Operation.ShouldBeNull("page 1 gets no number");

        dialog.FirstPage = 9;
        dialog.ErrorText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.PageNumbers_Error_FirstPage, 5));

        dialog.FirstPage = 1;
        dialog.Pattern = " ";
        dialog.ErrorText.ShouldBe(Strings.HeaderFooter_Error_Text);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveStamps_ListsTheKindsFound_AndTakesOffTheCheckedOnes()
    {
        var found = new Dictionary<StampKind, int> { [StampKind.PageNumbers] = 4, [StampKind.Watermark] = 6 };
        await using var dialog = new RemoveStampsDialogViewModel(Context(6), found, new RemoveStampsOperation(), null, _debouncers);

        dialog.Kinds.Select(k => k.Kind).ShouldBe([StampKind.Watermark, StampKind.PageNumbers]);
        dialog.Kinds[0].Label.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.RemoveStamps_Watermark, 6));
        dialog.Kinds.ShouldAllBe(k => k.IsChecked);

        dialog.Kinds[0].IsChecked = false;
        dialog.Kinds[1].IsChecked = false;
        dialog.ErrorText.ShouldBe(Strings.RemoveStamps_Error_NoKind);

        dialog.Kinds[1].IsChecked = true;
        dialog.ApplyCommand.Execute(null);

        var choice = dialog.Result.ShouldNotBeNull();
        choice.Description.ShouldBe(Strings.History_RemoveStamps);
        var parameters = choice.Parameters.ShouldBeOfType<RemoveStampsParams>();
        parameters.Kinds.ShouldBe([StampKind.PageNumbers]);
        parameters.Pages.ShouldBe("1-6");
    }

    [Fact]
    public async Task Cancel_ClosesWithoutAChoice()
    {
        await using var dialog = Watermark(2);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.CancelCommand.Execute(null);

        closed.ShouldBe(false);
        dialog.Result.ShouldBeNull();
    }

    [Fact]
    public async Task StampCommands_AreOffWithoutTheStampServices()
    {
        await using var without = await OpenAsync(2, withStamps: false);
        await using var with = await OpenAsync(2);

        StampCommands(without).ShouldAllBe(c => !c.CanExecute(null));
        StampCommands(with).ShouldAllBe(c => c.CanExecute(null));
    }

    [Fact]
    public async Task StampCommands_WaitForObjectsInTheEditor()
    {
        await using var tab = await OpenAsync(2);
        tab.AddShapeCommand.Execute(null);
        var key = tab.Workspace.Pages.Pages[0].Key;
        var geometry = (await tab.Editor.GetGeometryAsync(key)).ShouldNotBeNull();
        var center = new PageCoordinateMapper(geometry, 1).ScreenToPdf(new ViewPoint(100, 100));

        tab.Editor.Add(key, new ShapeOverlay(0, new OverlayBox(center, new PdfSize(40, 20)), ShapeKind.Rectangle, new StrokeStyle(RgbColor.Red, 2)));

        StampCommands(tab).ShouldAllBe(c => !c.CanExecute(null), "objects wait in the editor");
    }

    [Fact]
    public async Task Watermark_Applied_AsksTheWindowForOneContentChange_OnTheCurrentPagePreview()
    {
        await using var tab = await OpenAsync(3);
        tab.Viewer.NavigateTo(2);
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        StampDialogViewModel? shown = null;
        _pageDialogs.ShowStamp(Arg.Any<StampDialogViewModel>()).Returns(call =>
        {
            shown = call.Arg<StampDialogViewModel>();
            shown.ApplyCommand.Execute(null);
            return true;
        });

        await tab.StampWatermarkCommand.ExecuteAsync(null);

        shown.ShouldBeOfType<WatermarkDialogViewModel>().CurrentPage.ShouldBe(2);
        request.ShouldNotBeNull().Operation.ShouldBeOfType<WatermarkOperation>();
        request.Parameters.ShouldBeOfType<WatermarkParams>().Pages.ShouldBe("1-3");
        request.Description.ShouldBe(Strings.History_Watermark);
        tab.IsDirty.ShouldBeFalse("the window applies it");
    }

    [Fact]
    public async Task PageNumbers_InTheOrganizer_StartWithTheSelectedPages_AndTheDocumentName()
    {
        await using var tab = await OpenAsync(5);
        tab.ToggleOrganizeModeCommand.Execute(null);
        tab.Organizer!.Select([3, 1]);
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        _pageDialogs.ShowStamp(Arg.Any<StampDialogViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<StampDialogViewModel>();
            dialog.Target.IsSelected.ShouldBeTrue();
            dialog.CurrentPage.ShouldBe(1, "the first selected page is previewed");
            dialog.ApplyCommand.Execute(null);
            return true;
        });

        await tab.StampPageNumbersCommand.ExecuteAsync(null);

        var parameters = request.ShouldNotBeNull().Parameters.ShouldBeOfType<PageNumbersParams>();
        parameters.Pages.ShouldBe("2, 4");
        parameters.FileName.ShouldBe("a");
    }

    [Fact]
    public async Task AStampDialogCancelled_AsksNothing()
    {
        await using var tab = await OpenAsync(2);
        var requested = false;
        tab.ContentChangeRequested += (_, _) => requested = true;
        _pageDialogs.ShowStamp(Arg.Any<StampDialogViewModel>()).Returns(false);

        await tab.StampHeaderFooterCommand.ExecuteAsync(null);
        await tab.StampBackgroundCommand.ExecuteAsync(null);

        _pageDialogs.Received(2).ShowStamp(Arg.Any<StampDialogViewModel>());
        requested.ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveStamps_OfADocumentWithout_SaysSo()
    {
        await using var tab = await OpenAsync(2);
        ExportWrites(await PdfAsync("plain", 2, stamped: false));

        await tab.RemoveStampsCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowInfoAsync(Strings.RemoveStamps_Title, Strings.RemoveStamps_None);
        _pageDialogs.DidNotReceive().ShowStamp(Arg.Any<StampDialogViewModel>());
        Directory.GetFiles(tab.Workspace.WorkDirectory, "stamps-*").ShouldBeEmpty("the scanned copy is removed");
    }

    [Fact]
    public async Task RemoveStamps_OffersTheKindsTheDocumentAsShownCarries()
    {
        await using var tab = await OpenAsync(2);
        ExportWrites(await PdfAsync("stamped", 2, stamped: true));
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        _pageDialogs.ShowStamp(Arg.Any<StampDialogViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<StampDialogViewModel>().ShouldBeOfType<RemoveStampsDialogViewModel>();
            dialog.Kinds.Single().Label.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.RemoveStamps_Background, 2));
            dialog.ApplyCommand.Execute(null);
            return true;
        });

        await tab.RemoveStampsCommand.ExecuteAsync(null);

        request.ShouldNotBeNull().Operation.ShouldBeOfType<RemoveStampsOperation>();
        request.Parameters.ShouldBeOfType<RemoveStampsParams>().Kinds.ShouldBe([StampKind.Background]);
        await _saver.Received(1).ExportAsync(tab.Workspace, Arg.Is<IReadOnlyList<int>>(p => p.Count == 2 && p[0] == 0 && p[1] == 1), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        Directory.GetFiles(tab.Workspace.WorkDirectory, "stamps-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveStamps_WhenThePagesCannotBeWritten_ExplainsIt()
    {
        await using var tab = await OpenAsync(2);
        _saver.ExportAsync(Arg.Any<DocumentWorkspace>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DocumentSaveException(SaveFailure.SourceUnreadable));

        await tab.RemoveStampsCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Stamp_Error_Title, Strings.Stamp_Error_Scan);
        _pageDialogs.DidNotReceive().ShowStamp(Arg.Any<StampDialogViewModel>());
    }

    private static string Render(string pattern, string page, string pages)
        => StampTemplate.Parse(pattern).Render(new StampValues(page, pages, DateTime.Now, string.Empty, null));

    private static StampContext Context(int pageCount, int currentPage = 0, string fileName = "a", string? title = null, IReadOnlyList<int>? selected = null)
        => new(pageCount, fileName, title, currentPage, selected ?? []);

    private static IEnumerable<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand> StampCommands(DocumentTabViewModel tab)
        => [tab.StampWatermarkCommand, tab.StampBackgroundCommand, tab.StampHeaderFooterCommand, tab.StampPageNumbersCommand, tab.RemoveStampsCommand];

    private WatermarkDialogViewModel Watermark(int pageCount, int currentPage = 0, IStampPreview? preview = null)
        => new(Context(pageCount, currentPage), new WatermarkOperation(), _fileDialogs, preview, _debouncers);

    private async Task<DocumentTabViewModel> OpenAsync(int pages, bool withStamps = true)
    {
        var factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            _debouncers,
            TestOperations.Catalog(),
            _messages,
            _pageDialogs,
            _fileDialogs,
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance,
            saver: withStamps ? _saver : null);
        var tab = factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(pages)));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        return tab;
    }

    /// <summary>"Writing the pages" copies <paramref name="pdf"/> to the requested place.</summary>
    private void ExportWrites(string pdf)
        => _saver.ExportAsync(Arg.Any<DocumentWorkspace>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                File.Copy(pdf, call.ArgAt<string>(2));
                return Task.CompletedTask;
            });

    /// <summary>A real PDF of empty pages, with a PDF_Pro background on each when <paramref name="stamped"/>.</summary>
    private async Task<string> PdfAsync(string name, int pages, bool stamped)
    {
        var plain = _fixture.CreateFile($"{name}-plain.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            for (var i = 0; i < pages; i++)
            {
                document.AddPage();
            }

            document.Save(plain);
        }

        if (!stamped)
        {
            return plain;
        }

        var path = _fixture.CreateFile($"{name}.pdf");
        var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (output.ConfigureAwait(false))
        {
            await new BackgroundOperation().ApplyToFileAsync(plain, output, new BackgroundParams(RgbColor.Gray), new Progress<OperationProgress>(), Token);
        }

        return path;
    }

    /// <summary>Records what each preview showed: the operation and parameters, or nothing for the page as it is.</summary>
    private sealed class RecordingPreview : IStampPreview
    {
        public List<(IContentOperation? Operation, object? Parameters)> Renders { get; } = [];

        public bool IsDisposed { get; private set; }

        public Task<ImageSource?> RenderAsync(IContentOperation? operation, object? parameters, double width, CancellationToken cancellationToken)
        {
            Renders.Add((operation, parameters));
            return Task.FromResult<ImageSource?>(null);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
