using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Operations.Stamping;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Documents;

/// <summary>Reading files for Bates numbering: page counts, and the files it cannot number explained.</summary>
public sealed class BatesServiceTests
{
    private const string FilePath = @"C:\hồ sơ\tờ trình.pdf";

    private readonly IPdfDocumentLoader _loader = Substitute.For<IPdfDocumentLoader>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly BatesService _service;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public BatesServiceTests()
        => _service = new BatesService(new BatesOperation(), _loader, _messages, NullLogger<BatesService>.Instance);

    [Fact]
    public async Task AFile_IsReadWithItsPageCount()
    {
        var document = FakePdfDocument.WithPages(7);
        _loader.LoadAsync(FilePath, null, Arg.Any<CancellationToken>()).Returns(document);

        var file = await _service.AddAsync(FilePath, "tờ trình (đang mở)", Token);

        file.ShouldBe(new BatesFile(FilePath, "tờ trình (đang mở)", 7));
        document.IsDisposed.ShouldBeTrue("the file is not kept open");
        await _messages.DidNotReceiveWithAnyArgs().ShowErrorAsync(default!, default!);
    }

    [Fact]
    public async Task AProtectedFile_IsRefusedWithAnExplanation()
    {
        var document = FakePdfDocument.WithPages(2);
        document.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 6 };
        _loader.LoadAsync(FilePath, null, Arg.Any<CancellationToken>()).Returns(document);

        (await _service.AddAsync(FilePath, cancellationToken: Token)).ShouldBeNull();

        await _messages.Received(1).ShowErrorAsync(Strings.Bates_Error_AddTitle, Format(Strings.Bates_Error_Encrypted, "tờ trình.pdf"));
        document.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task AFileThatNeedsAPassword_IsRefusedWithoutAsking()
    {
        _loader.LoadAsync(FilePath, null, Arg.Any<CancellationToken>()).ThrowsAsync(new PdfDocumentException(PdfOpenFailure.PasswordRequired, "password"));

        (await _service.AddAsync(FilePath, cancellationToken: Token)).ShouldBeNull();

        await _messages.Received(1).ShowErrorAsync(Strings.Bates_Error_AddTitle, Format(Strings.Bates_Error_Encrypted, "tờ trình.pdf"));
    }

    [Fact]
    public async Task ADamagedFile_IsExplainedLikeWhenOpening()
    {
        _loader.LoadAsync(FilePath, null, Arg.Any<CancellationToken>()).ThrowsAsync(new PdfDocumentException(PdfOpenFailure.Corrupted, "broken"));

        (await _service.AddAsync(FilePath, cancellationToken: Token)).ShouldBeNull();

        await _messages.Received(1).ShowErrorAsync(Strings.Bates_Error_AddTitle, Format(DocumentOpener.MessageFor(PdfOpenFailure.Corrupted), "tờ trình.pdf"));
    }

    [Fact]
    public async Task AFileThatCannotBeRead_IsExplained()
    {
        _loader.LoadAsync(FilePath, null, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("locked"));

        (await _service.AddAsync(FilePath, cancellationToken: Token)).ShouldBeNull();

        await _messages.Received(1).ShowErrorAsync(Strings.Bates_Error_AddTitle, Format(Strings.Open_Error_ReadFailed, "tờ trình.pdf"));
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
