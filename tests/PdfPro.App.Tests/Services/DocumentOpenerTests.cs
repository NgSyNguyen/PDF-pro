using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Services;

public sealed class DocumentOpenerTests
{
    private const string Path = @"C:\Hồ sơ\Nguyễn Văn A - hợp đồng.pdf";
    private const string FileName = "Nguyễn Văn A - hợp đồng.pdf";

    private readonly IPdfDocumentLoader _loader = Substitute.For<IPdfDocumentLoader>();
    private readonly IPasswordPrompt _passwords = Substitute.For<IPasswordPrompt>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly CapturingLogger<DocumentOpener> _logger = new();
    private readonly DocumentOpener _opener;

    public DocumentOpenerTests() => _opener = new DocumentOpener(_loader, _passwords, _messages, _logger);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PlainDocument_OpensWithoutPrompt()
    {
        var document = FakePdfDocument.WithPages(3);
        _loader.LoadAsync(Path, null, Arg.Any<CancellationToken>()).Returns(document);

        var opened = await _opener.OpenAsync(Path, FileName, cancellationToken: TestToken);

        opened.ShouldNotBeNull().Document.ShouldBeSameAs(document);
        opened.Password.ShouldBeNull();
        _passwords.DidNotReceiveWithAnyArgs().RequestPassword(default!, default);
    }

    [Fact]
    public async Task ProtectedDocument_AsksForThePasswordAndRetries()
    {
        var document = FakePdfDocument.WithPages(2);
        _loader.LoadAsync(Path, null, Arg.Any<CancellationToken>()).ThrowsAsync(new PdfPasswordRequiredException());
        _loader.LoadAsync(Path, "test123", Arg.Any<CancellationToken>()).Returns(document);
        _passwords.RequestPassword("Nguyễn Văn A - hợp đồng.pdf", false).Returns("test123");

        var opened = await _opener.OpenAsync(Path, FileName, cancellationToken: TestToken);

        opened.ShouldNotBeNull().Document.ShouldBeSameAs(document);
        opened.Password.ShouldBe("test123", "the caller needs it to tell the owner password and to keep the protection");
        opened.ToString().ShouldNotContain("test123");
    }

    [Fact]
    public async Task AGivenPassword_IsTriedFirst_WithoutAsking()
    {
        var document = FakePdfDocument.WithPages(2);
        _loader.LoadAsync(Path, "chu-so-huu", Arg.Any<CancellationToken>()).Returns(document);

        var opened = await _opener.OpenAsync(Path, FileName, "chu-so-huu", TestToken);

        opened.ShouldNotBeNull().Password.ShouldBe("chu-so-huu");
        _passwords.DidNotReceiveWithAnyArgs().RequestPassword(default!, default);
    }

    [Fact]
    public async Task WrongPassword_AsksAgainUntilTheUserCancels()
    {
        _loader.LoadAsync(Path, null, Arg.Any<CancellationToken>()).ThrowsAsync(new PdfPasswordRequiredException());
        _loader.LoadAsync(Path, "sai", Arg.Any<CancellationToken>()).ThrowsAsync(new PdfWrongPasswordException());
        _passwords.RequestPassword(Arg.Any<string>(), false).Returns("sai");
        _passwords.RequestPassword(Arg.Any<string>(), true).Returns((string?)null);

        var opened = await _opener.OpenAsync(Path, FileName, cancellationToken: TestToken);

        opened.ShouldBeNull();
        _passwords.Received(1).RequestPassword(Arg.Any<string>(), true);
        await _messages.DidNotReceiveWithAnyArgs().ShowErrorAsync(default!, default!);
    }

    [Theory]
    [InlineData(PdfOpenFailure.FileNotFound)]
    [InlineData(PdfOpenFailure.AccessDenied)]
    [InlineData(PdfOpenFailure.FileInUse)]
    [InlineData(PdfOpenFailure.FileTooLarge)]
    [InlineData(PdfOpenFailure.ReadFailed)]
    [InlineData(PdfOpenFailure.Corrupted)]
    [InlineData(PdfOpenFailure.UnsupportedSecurity)]
    public async Task Failures_ShowAFriendlyMessageNamingTheFile(PdfOpenFailure failure)
    {
        _loader.LoadAsync(Path, null, Arg.Any<CancellationToken>()).ThrowsAsync(new PdfDocumentException(failure, "x"));

        var opened = await _opener.OpenAsync(Path, FileName, cancellationToken: TestToken);

        opened.ShouldBeNull();
        await _messages.Received(1).ShowErrorAsync(
            Strings.Open_Error_Title,
            Arg.Is<string>(m => m.Contains("Nguyễn Văn A - hợp đồng.pdf") && m != Strings.Open_Error_Unknown));
        DocumentOpener.MessageFor(failure).ShouldNotBe(Strings.Open_Error_Unknown);
    }

    [Fact]
    public async Task UnexpectedError_ShowsTheGenericMessage()
    {
        _loader.LoadAsync(Path, null, Arg.Any<CancellationToken>()).ThrowsAsync(new InsufficientMemoryException());

        (await _opener.OpenAsync(Path, FileName, cancellationToken: TestToken)).ShouldBeNull();

        await _messages.Received(1).ShowErrorAsync(Strings.Open_Error_Title, Arg.Is<string>(m => m.Contains("Nguyễn Văn A")));
    }

    [Fact]
    public async Task Logs_NeverContainThePathFileNameOrPassword()
    {
        _loader.LoadAsync(Path, null, Arg.Any<CancellationToken>()).ThrowsAsync(new PdfPasswordRequiredException());
        _loader.LoadAsync(Path, "mat-khau-bi-mat", Arg.Any<CancellationToken>())
            .ThrowsAsync(new PdfFileAccessException(PdfOpenFailure.ReadFailed, new IOException($"Could not read '{Path}'")));
        _passwords.RequestPassword(Arg.Any<string>(), Arg.Any<bool>()).Returns("mat-khau-bi-mat");

        await _opener.OpenAsync(Path, FileName, cancellationToken: TestToken);

        _logger.Entries.ShouldNotBeEmpty();
        _logger.Entries.ShouldAllBe(e =>
            !e.Message.Contains("Nguyễn") && !e.Message.Contains("mat-khau-bi-mat") && e.Exception == null);
    }
}
