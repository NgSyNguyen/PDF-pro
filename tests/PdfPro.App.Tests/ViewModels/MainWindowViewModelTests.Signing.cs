using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Forms;
using PdfPro.App.Services.Security;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Security;
using PdfPro.Operations.Signing;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Ký số": the saved file is signed into a copy, visible or not, with or without a timestamp.</summary>
public sealed partial class MainWindowViewModelTests
{
    private readonly ICertificateStore _certificates = Substitute.For<ICertificateStore>();
    private readonly ITimestampClient _timestamps = Substitute.For<ITimestampClient>();

    [Fact]
    public async Task Sign_InvisibleSignature_SignsTheSavedFile_IntoTheChosenCopy_WhichOpens()
    {
        var (viewModel, path) = await CreateSignableAsync();
        var original = File.ReadAllBytes(path);
        var output = SignedPath(path);
        ChooseInDialog(d => d.SelectedPlacement = d.Placements.Single(p => p.Kind == SignaturePlacementKind.Invisible), useTimestamp: false);

        await viewModel.SignCommand.ExecuteAsync(null);

        File.ReadAllBytes(path).ShouldBe(original, "the document's own file is left as it was");
        var field = SignatureFields(output).ShouldHaveSingleItem();
        field.Name.ShouldBe(Format(Strings.Sign_FieldName, 1));
        field.IsSigned.ShouldBeTrue();
        viewModel.ActiveDocument!.FilePath.ShouldBe(output, "the signed copy opens");
        viewModel.Documents.Count().ShouldBe(2);
        _settings.Current.UseTimestamp.ShouldBeFalse("the choice is remembered");
        await _timestamps.DidNotReceiveWithAnyArgs().RequestAsync(default!, default!, TestToken);
    }

    [Fact]
    public async Task Sign_ADraggedBox_ShowsTheSignatureThere()
    {
        var (viewModel, path) = await CreateSignableAsync();
        var tab = viewModel.ActiveDocument!;
        tab.Viewer.ViewportSize = new ViewSize(1000, 3000);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        ChooseInDialog(d => d.SelectedPlacement = d.Placements.Single(p => p.Kind == SignaturePlacementKind.Draw), useTimestamp: false);

        var signing = viewModel.SignCommand.ExecuteAsync(null);
        tab.SignaturePlacement.IsActive.ShouldBeTrue("the box is placed on the page after the dialog");
        tab.SignaturePlacement.Press(Viewport(tab, 1, new PdfPoint(300, 200))).ShouldBeTrue();
        tab.SignaturePlacement.DragTo(Viewport(tab, 1, new PdfPoint(500, 120)));
        tab.SignaturePlacement.Release();
        await signing;

        var widget = SignatureFields(SignedPath(path)).ShouldHaveSingleItem().Widgets.ShouldHaveSingleItem();
        widget.Page.ShouldBe(1);
        new PdfRect(Math.Round(widget.Rect.Left), Math.Round(widget.Rect.Bottom), Math.Round(widget.Rect.Right), Math.Round(widget.Rect.Top))
            .ShouldBe(new PdfRect(300, 120, 500, 200));
    }

    [Fact]
    public async Task Sign_GivingUpTheBox_SignsNothing()
    {
        var (viewModel, path) = await CreateSignableAsync();
        var tab = viewModel.ActiveDocument!;
        ChooseInDialog(d => d.SelectedPlacement = d.Placements.Single(p => p.Kind == SignaturePlacementKind.Draw), useTimestamp: false);

        var signing = viewModel.SignCommand.ExecuteAsync(null);
        tab.SignaturePlacement.Cancel();
        await signing;

        _fileDialogs.DidNotReceiveWithAnyArgs().PickSavePath(default!, default);
        File.Exists(SignedPath(path)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(DialogChoice.Primary, 1)]
    [InlineData(DialogChoice.Cancel, 0)]
    public async Task Sign_UnsavedChanges_AreSavedFirst_OrNothingHappens(DialogChoice choice, int saves)
    {
        var (viewModel, _) = await CreateDirtyAsync();
        _messages.AskAsync(Strings.Sign_SaveFirst_Title, Format(Strings.Sign_SaveFirst_Message, "a.pdf"), Strings.Sign_SaveFirst_Save, null, Strings.Common_Cancel).Returns(choice);
        SaverMarksSaved();
        _pageDialogs.ShowSign(Arg.Any<SignDialogViewModel>()).Returns(false);

        await viewModel.SignCommand.ExecuteAsync(null);

        await _saver.ReceivedWithAnyArgs(saves).SaveAsync(default!, default!, default!, TestToken);
        _pageDialogs.Received(saves).ShowSign(Arg.Any<SignDialogViewModel>());
    }

    [Fact]
    public async Task Sign_AProtectedDocument_IsRefused()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        viewModel.ActiveDocument!.Apply(TestOperations.Catalog().Get<SetProtectionOperation>(), new SetProtectionParams(Unknown)).ShouldBeTrue();

        await viewModel.SignCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Sign_Error_Title, Strings.Sign_Error_Encrypted);
        _pageDialogs.DidNotReceiveWithAnyArgs().ShowSign(default!);
    }

    [Fact]
    public async Task Sign_EmptySignatureFields_AreOffered()
    {
        var (viewModel, _) = await CreateSignableAsync();
        _forms.Form = new FormSnapshot(
            FormType.AcroForm,
            [
                new FormField("Người duyệt", FormFieldKind.Signature, null, [], 0, [], []),
                new FormField("Đã ký", FormFieldKind.Signature, FormField.Signed, [], 0, [], []),
                new FormField("Họ tên", FormFieldKind.Text, null, [], 0, [], []),
            ]);
        SignDialogViewModel? shown = null;
        _pageDialogs.ShowSign(Arg.Any<SignDialogViewModel>()).Returns(call =>
        {
            shown = call.Arg<SignDialogViewModel>();
            return false;
        });

        await viewModel.SignCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull().Placements.Where(p => p.Kind == SignaturePlacementKind.Field).Select(p => p.FieldName).ShouldBe(["Người duyệt"]);
    }

    [Theory]
    [InlineData(DialogChoice.Primary)]
    [InlineData(DialogChoice.Cancel)]
    public async Task Sign_WhenNoTimestampCanBeHad_TheUserMaySignWithoutOne(DialogChoice choice)
    {
        var (viewModel, path) = await CreateSignableAsync();
        _timestamps.RequestAsync(Arg.Any<Uri>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SigningException(SigningFailure.Timestamp, "unreachable"));
        _messages.AskAsync(Strings.Sign_NoTimestamp_Title, Strings.Sign_NoTimestamp_Message, Strings.Sign_NoTimestamp_Sign, null, Strings.Common_Cancel).Returns(choice);
        ChooseInDialog(d => d.SelectedPlacement = d.Placements.Single(p => p.Kind == SignaturePlacementKind.Invisible), useTimestamp: true);

        await viewModel.SignCommand.ExecuteAsync(null);

        await _timestamps.ReceivedWithAnyArgs(1).RequestAsync(default!, default!, TestToken);
        File.Exists(SignedPath(path)).ShouldBe(choice == DialogChoice.Primary);
        await _messages.DidNotReceive().ShowErrorAsync(Strings.Sign_Error_Title, Arg.Any<string>());
    }

    [Fact]
    public async Task Sign_ACopyOpenInAnotherTab_IsNotWritten()
    {
        var (viewModel, path) = await CreateSignableAsync();
        var other = _fixture.CreateFile("khác.pdf");
        await viewModel.OpenFilesAsync([other]);
        viewModel.SelectedTab = viewModel.Documents.First();
        ChooseInDialog(d => d.SelectedPlacement = d.Placements.Single(p => p.Kind == SignaturePlacementKind.Invisible), useTimestamp: false);
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(other);

        await viewModel.SignCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Sign_Error_Title, Format(Strings.Save_AlreadyOpen, "khác.pdf"));
        File.ReadAllBytes(other).Length.ShouldBe(4, "the open file is left alone");
        File.Exists(SignedPath(path)).ShouldBeFalse();
    }

    [Fact]
    public async Task Verify_ShowsTheSignaturesPanel_WhichChecksWhileItShows()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var tab = viewModel.ActiveDocument!;

        Command(viewModel, "security.verify").Command.Execute(null);

        viewModel.Sidebar.SelectedPanel.ShouldBe(SidebarPanel.Signatures);
        tab.Signatures.IsActive.ShouldBeTrue();
        viewModel.Sidebar.SelectedPanel = SidebarPanel.Thumbnails;
        tab.Signatures.IsActive.ShouldBeFalse();
    }

    /// <summary>An open document whose file is a real PDF (six A4 pages), with a signing certificate in the store.</summary>
    private async Task<(MainWindowViewModel ViewModel, string Path)> CreateSignableAsync()
    {
        var certificate = SigningCertificates.Signer();
        _certificates.SigningCertificates().Returns([new CertificateChoice(certificate.Thumbprint, "Nguyễn Văn A", "PDF_Pro Test", certificate.NotAfter)]);
        _certificates.Open(certificate.Thumbprint).Returns(_ => SigningCertificates.Signer());
        var path = _fixture.CreateFile("hợp đồng.pdf", File.ReadAllBytes(Samples.Basic));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        _fileDialogs.PickSavePath(Format(Strings.Sign_SignedName, "hợp đồng") + ".pdf", Path.GetDirectoryName(path)).Returns(SignedPath(path));
        return (viewModel, path);
    }

    private static string SignedPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.Sign_SignedName, "hợp đồng") + ".pdf");

    /// <summary>The dialog gets <paramref name="choose"/>, then "Ký".</summary>
    private void ChooseInDialog(Action<SignDialogViewModel> choose, bool useTimestamp)
        => _pageDialogs.ShowSign(Arg.Any<SignDialogViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<SignDialogViewModel>();
            choose(dialog);
            dialog.UseTimestamp = useTimestamp;
            dialog.SignCommand.Execute(null);
            return dialog.Result is not null;
        });

    private static List<FormField> SignatureFields(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return [.. FormReader.Read(document).Where(f => f.Kind == FormFieldKind.Signature)];
    }

    private static ViewPoint Viewport(DocumentTabViewModel tab, int page, PdfPoint point)
    {
        var layout = tab.Viewer.Layout;
        var bounds = layout.GetPageBounds(page);
        var mapper = new PageCoordinateMapper(tab.Editor.GeometryOf(tab.Workspace.Pages.Pages[page].Key).ShouldNotBeNull(), layout.Scale);
        var shown = mapper.PdfToScreen(point);
        return new ViewPoint(bounds.X + shown.X - tab.Viewer.ScrollOffset.X, bounds.Y + shown.Y - tab.Viewer.ScrollOffset.Y);
    }
}
