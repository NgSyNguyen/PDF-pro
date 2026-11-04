using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Services.Security;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Signing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The "Chữ ký" panel: the saved file's signatures, checked while it shows, each with its verdict and reasons.</summary>
public sealed class SignaturesViewModelTests : IDisposable
{
    private static readonly DateTimeOffset SignedAt = new(2026, 10, 29, 9, 30, 0, TimeSpan.FromHours(7));

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly ISignatureVerification _verification = Substitute.For<ISignatureVerification>();
    private readonly X509Certificate2 _signer = SigningCertificates.Signer();
    private readonly DocumentTabFactory _factory;

    public SignaturesViewModelTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            Substitute.For<IMessageService>(),
            Substitute.For<IPageDialogService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance,
            signatureChecks: _verification);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
        _signer.Dispose();
    }

    [Fact]
    public async Task Signatures_AreCheckedOnlyWhileThePanelShows_AndOnceForTheSameFile()
    {
        var (tab, path) = await OpenAsync();
        Returns(Valid());

        await _verification.DidNotReceiveWithAnyArgs().VerifyAsync(default!, Token);
        tab.Signatures.IsActive = true;
        await WhileLoadingAsync(tab.Signatures);
        tab.Signatures.IsActive = false;
        tab.Signatures.IsActive = true;

        await _verification.Received(1).VerifyAsync(path, Arg.Any<CancellationToken>());
        tab.Signatures.Items.ShouldHaveSingleItem();
        await tab.Signatures.RefreshCommand.ExecuteAsync(null);
        await _verification.Received(2).VerifyAsync(path, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AValidSignature_SaysWhoWhenAndWhy()
    {
        var (tab, _) = await OpenAsync();
        Returns(Valid());

        var signatures = await ShowAsync(tab);

        var item = signatures.Items.ShouldHaveSingleItem();
        item.Verdict.ShouldBe(SignatureVerdict.Valid);
        item.VerdictText.ShouldBe(Strings.Signatures_Valid);
        item.SignerName.ShouldBe("Nguyễn Văn A");
        item.Organization.ShouldBe("PDF_Pro Test");
        item.PageLabel.ShouldBe(Format(Strings.Signatures_Page, 2));
        item.Details.ShouldBe(
        [
            Strings.Signatures_Intact,
            Strings.Signatures_Unchanged,
            Format(Strings.Signatures_Trusted, "Nguyễn Văn A"),
            Format(Strings.Signatures_SigningTime, SignedAt.ToLocalTime()),
            Format(Strings.Sign_Line_Reason, "Phê duyệt"),
            Format(Strings.Sign_Line_Location, "Hà Nội"),
            Format(Strings.Signatures_Algorithm, "SHA-256, RSA 2048"),
            Format(Strings.Signatures_Field, "ChuKy1"),
        ]);
        signatures.StatusText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Signatures_Summary, 1, 1, 0, 0));
    }

    [Fact]
    public async Task AnUntrustedCertificate_AndAChangeAfterSigning_AreExplained()
    {
        var (tab, _) = await OpenAsync();
        var untrusted = Valid() with { Trust = new CertificateTrust(CertificateTrustStatus.UntrustedRoot, false, SignedAt, [_signer]) };
        var commented = Valid() with { Index = 1, LaterRevisions = 1, Changes = ChangesAfterSigning.Annotations, Timestamp = SignedAt };
        Returns(untrusted, commented);

        var signatures = await ShowAsync(tab);

        signatures.Items.Select(i => i.Verdict).ShouldBe([SignatureVerdict.Warning, SignatureVerdict.Warning]);
        signatures.Items[0].Details.ShouldContain(Strings.Signatures_UntrustedRoot);
        signatures.Items[1].Details.ShouldContain(Strings.Signatures_LaterAnnotations);
        signatures.Items[1].Details.ShouldContain(Format(Strings.Signatures_Timestamp, SignedAt.ToLocalTime()));
        signatures.Items[1].Details.ShouldNotContain(Format(Strings.Signatures_SigningTime, SignedAt.ToLocalTime()), "a timestamp is the time that counts");
        signatures.StatusText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Signatures_Summary, 2, 0, 2, 0));
    }

    [Fact]
    public async Task ABrokenSignature_IsInvalid_WithoutTrustLines()
    {
        var (tab, _) = await OpenAsync();
        Returns(Valid() with { IsIntact = false, Trust = null, Problem = "changed" });

        var item = (await ShowAsync(tab)).Items.ShouldHaveSingleItem();

        item.Verdict.ShouldBe(SignatureVerdict.Invalid);
        item.Details[0].ShouldBe(Strings.Signatures_Broken);
        item.Details.ShouldNotContain(Strings.Signatures_Unchanged);
        item.Details.ShouldNotContain(Format(Strings.Signatures_Trusted, "Nguyễn Văn A"));
    }

    [Fact]
    public async Task ChoosingASignature_ShowsItsPage()
    {
        var (tab, _) = await OpenAsync();
        tab.Viewer.ViewportSize = new ViewSize(800, 600);
        Returns(Valid());
        var signatures = await ShowAsync(tab);

        signatures.SelectedItem = signatures.Items[0];

        tab.Viewer.CurrentPageIndex.ShouldBe(1);
    }

    [Fact]
    public async Task UnsavedChanges_AreSaidToBeInNoSignature()
    {
        var (tab, _) = await OpenAsync();
        Returns(Valid());
        var signatures = await ShowAsync(tab);
        signatures.ShowsUnsavedNote.ShouldBeFalse();

        tab.RotateRightCommand.Execute(null);

        tab.IsDirty.ShouldBeTrue();
        signatures.ShowsUnsavedNote.ShouldBeTrue();
    }

    [Fact]
    public async Task AFileWithoutSignatures_OrUnreadable_IsSaidSo()
    {
        var (tab, path) = await OpenAsync();
        Returns();

        var signatures = await ShowAsync(tab);
        signatures.StatusText.ShouldBe(Strings.Signatures_None);

        _verification.VerifyAsync(path, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidDataException("bad"));
        await signatures.RefreshCommand.ExecuteAsync(null);
        signatures.StatusText.ShouldBe(Strings.Signatures_Unreadable);
        signatures.Items.ShouldBeEmpty();
    }

    private static string Format(string template, object value) => string.Format(CultureInfo.CurrentCulture, template, value);

    private static async Task WhileLoadingAsync(SignaturesViewModel signatures)
    {
        for (var i = 0; i < 200 && signatures.IsLoading; i++)
        {
            await Task.Delay(10, Token);
        }
    }

    private static async Task<SignaturesViewModel> ShowAsync(DocumentTabViewModel tab)
    {
        tab.Signatures.IsActive = true;
        await WhileLoadingAsync(tab.Signatures);
        return tab.Signatures;
    }

    private void Returns(params SignatureCheck[] checks)
        => _verification.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(checks);

    /// <summary>An intact, trusted signature on page 2 that covers the whole file.</summary>
    private SignatureCheck Valid()
        => new(0, true, true, "adbe.pkcs7.detached", _signer, [_signer], SignedAt, null, "Phê duyệt", null)
        {
            FieldName = "ChuKy1",
            Page = 1,
            Box = new PdfRect(300, 72, 500, 132),
            Location = "Hà Nội",
            DigestAlgorithm = "SHA-256",
            KeyAlgorithm = "RSA 2048",
            Trust = new CertificateTrust(CertificateTrustStatus.Trusted, true, SignedAt, [_signer]),
        };

    private async Task<(DocumentTabViewModel Tab, string Path)> OpenAsync()
    {
        var path = _fixture.CreateFile("đã ký.pdf");
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(3), path));
        return (tab, path);
    }
}
