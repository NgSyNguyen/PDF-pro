using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Editing;
using PdfPro.App.Services.Security;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Security;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Ký số": the certificate, the placement, what a visible signature shows and the timestamp server.</summary>
public sealed class SignDialogViewModelTests : IDisposable
{
    private const string Server = "http://timestamp.example.test";

    private readonly ICertificateStore _store = Substitute.For<ICertificateStore>();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = SigningCertificates.Signer();
    private readonly CertificateChoice _choice;

    public SignDialogViewModelTests()
    {
        _choice = new CertificateChoice(_certificate.Thumbprint, "Nguyễn Văn A", "PDF_Pro Test", _certificate.NotAfter);
        _store.SigningCertificates().Returns([_choice]);
        _store.Open(_certificate.Thumbprint).Returns(_ => SigningCertificates.Signer());
    }

    public void Dispose() => _certificate.Dispose();

    [Fact]
    public void TheStore_IsUsedFirst_WithItsFirstCertificate()
    {
        var dialog = Create();

        dialog.UseStore.ShouldBeTrue();
        dialog.UseFile.ShouldBeFalse();
        dialog.SelectedCertificate.ShouldNotBeNull().Certificate.ShouldBe(_choice);
        dialog.SelectedCertificate.Label.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Sign_Certificate_Item, "Nguyễn Văn A", "PDF_Pro Test", _certificate.NotAfter));
        dialog.HasNoStoreCertificates.ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyStore_LeadsToAFile()
    {
        _store.SigningCertificates().Returns([]);

        var dialog = Create();

        dialog.UseStore.ShouldBeFalse();
        dialog.HasNoStoreCertificates.ShouldBeTrue();
        dialog.SignCommand.Execute(null);
        dialog.Error.ShouldBe(Strings.Sign_Error_PfxFile);
        dialog.Result.ShouldBeNull();
    }

    [Fact]
    public void Signing_WithTheStore_GivesItsCertificate_AndCloses()
    {
        var dialog = Create();
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.SignCommand.Execute(null);

        closed.ShouldBe(true);
        var result = dialog.Result.ShouldNotBeNull();
        result.Certificate.HasPrivateKey.ShouldBeTrue();
        result.SignerName.ShouldBe("Nguyễn Văn A");
        result.TimestampServer.ShouldBe(new Uri(Server));
        result.Certificate.Dispose();
    }

    [Fact]
    public void ACertificateGoneFromTheStore_IsExplained()
    {
        _store.Open(Arg.Any<string>()).Returns((System.Security.Cryptography.X509Certificates.X509Certificate2?)null);
        var dialog = Create();

        dialog.SignCommand.Execute(null);

        dialog.Error.ShouldBe(Strings.Sign_Error_NoCertificate);
        dialog.HasError.ShouldBeTrue();
        dialog.Result.ShouldBeNull();
    }

    [Theory]
    [InlineData(CertificateFileProblem.WrongPassword)]
    [InlineData(CertificateFileProblem.Unreadable)]
    [InlineData(CertificateFileProblem.NoPrivateKey)]
    public void AFileThatCannotBeUsed_IsExplained(CertificateFileProblem problem)
    {
        _store.LoadFile(@"C:\keys\me.pfx", "sai").Returns(_ => throw new CertificateFileException(problem));
        var dialog = Create();
        _files.PickCertificateFile().Returns(@"C:\keys\me.pfx");
        dialog.BrowseCommand.Execute(null);
        dialog.SetPfxPassword("sai");

        dialog.SignCommand.Execute(null);

        dialog.UseFile.ShouldBeTrue("choosing a file uses it");
        dialog.Error.ShouldBe(problem switch
        {
            CertificateFileProblem.WrongPassword => Strings.Sign_Error_PfxPassword,
            CertificateFileProblem.NoPrivateKey => Strings.Sign_Error_NoPrivateKey,
            _ => Strings.Sign_Error_PfxUnreadable,
        });
        dialog.Result.ShouldBeNull();
    }

    [Fact]
    public void AFile_IsOpenedWithThePasswordTyped_WhichIsForgottenAfterwards()
    {
        _store.LoadFile(@"C:\keys\me.pfx", "mật khẩu").Returns(_ => SigningCertificates.Signer("Trần Thị B"));
        var dialog = Create();
        dialog.UseFile = true;
        dialog.PfxPath = @"C:\keys\me.pfx";
        dialog.SetPfxPassword("mật khẩu");

        dialog.SignCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.SignerName.ShouldBe("Trần Thị B");
        result.Certificate.Dispose();
        _store.Received(1).LoadFile(@"C:\keys\me.pfx", "mật khẩu");
        dialog.SignCommand.Execute(null);
        _store.Received(1).LoadFile(@"C:\keys\me.pfx", string.Empty);
    }

    [Fact]
    public void EmptySignatureFields_AreOfferedFirst_ThenABoxOrNothing()
    {
        var dialog = Create(fields: ["Người duyệt", "Giám đốc"]);

        dialog.Placements.Select(p => p.Kind).ShouldBe([SignaturePlacementKind.Field, SignaturePlacementKind.Field, SignaturePlacementKind.Draw, SignaturePlacementKind.Invisible]);
        dialog.Placements[0].Label.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Sign_Placement_Field, "Người duyệt"));
        dialog.SelectedPlacement.FieldName.ShouldBe("Người duyệt");
        Create().SelectedPlacement.Kind.ShouldBe(SignaturePlacementKind.Draw, "without fields a box is drawn");
    }

    [Fact]
    public void TheChosenField_AndTheAppearance_AreInTheResult()
    {
        var dialog = Create(fields: ["Người duyệt", "Giám đốc"], images: [new SignatureEntry("a1", DateTime.UtcNow)]);
        dialog.SelectedPlacement = dialog.Placements[1];
        dialog.ShowDate = false;
        dialog.Reason = "  Phê duyệt hợp đồng  ";
        dialog.Location = "Hà Nội";
        dialog.Contact = " ";

        dialog.SignCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.Placement.ShouldBe(SignaturePlacementKind.Field);
        result.FieldName.ShouldBe("Giám đốc");
        result.Image.ShouldBe([1, 2, 3], "the saved signature is shown by default");
        result.Reason.ShouldBe("Phê duyệt hợp đồng");
        result.Location.ShouldBe("Hà Nội");
        result.Contact.ShouldBeNull();
        result.Lines(DateTimeOffset.Now).ShouldBe(
        [
            string.Format(CultureInfo.CurrentCulture, Strings.Sign_Line_SignedBy, "Nguyễn Văn A"),
            string.Format(CultureInfo.CurrentCulture, Strings.Sign_Line_Reason, "Phê duyệt hợp đồng"),
            string.Format(CultureInfo.CurrentCulture, Strings.Sign_Line_Location, "Hà Nội"),
        ]);
        result.Certificate.Dispose();
    }

    [Fact]
    public void AnInvisibleSignature_ShowsNoPicture()
    {
        var dialog = Create(images: [new SignatureEntry("a1", DateTime.UtcNow)]);
        dialog.SelectedPlacement = dialog.Placements.Single(p => p.Kind == SignaturePlacementKind.Invisible);

        dialog.IsVisible.ShouldBeFalse();
        dialog.SignCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.Image.ShouldBeNull();
        result.Certificate.Dispose();
    }

    [Fact]
    public void ASignatureThatShowsNothingElse_ShowsTheName()
    {
        var dialog = Create();
        dialog.ShowName = false;
        dialog.ShowDate = false;

        dialog.SignCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.Lines(DateTimeOffset.Now).ShouldBe([string.Format(CultureInfo.CurrentCulture, Strings.Sign_Line_SignedBy, "Nguyễn Văn A")]);
        result.Certificate.Dispose();
    }

    [Theory]
    [InlineData("ftp://tsa.example")]
    [InlineData("tsa.example")]
    [InlineData("")]
    public void ATimestampServer_MustBeAWebAddress_WhenUsed(string server)
    {
        var dialog = Create();
        dialog.TimestampServer = server;

        dialog.SignCommand.Execute(null);

        dialog.Error.ShouldBe(Strings.Sign_Error_TimestampServer);
        dialog.UseTimestamp = false;
        dialog.SignCommand.Execute(null);
        var result = dialog.Result.ShouldNotBeNull();
        result.TimestampServer.ShouldBeNull();
        result.Certificate.Dispose();
    }

    [Fact]
    public void Cancel_ClosesWithoutAResult()
    {
        var dialog = Create();
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.CancelCommand.Execute(null);

        closed.ShouldBe(false);
        dialog.Result.ShouldBeNull();
        _store.DidNotReceive().Open(Arg.Any<string>());
    }

    private SignDialogViewModel Create(IReadOnlyList<string>? fields = null, IReadOnlyList<SignatureEntry>? images = null)
        => new(_store, _files, fields ?? [], images ?? [], id => id == "a1" ? [1, 2, 3] : null, Server, useTimestamp: true);
}
