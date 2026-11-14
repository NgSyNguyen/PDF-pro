using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Extracting;
using PdfPro.Operations.Extracting;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Trích ảnh", "Tệp đính kèm" and "Phông chữ" dialogs, on real files.</summary>
public sealed class ExtractingViewModelsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ExtractImages_KeepsImagesOf32PixelsAndMore_AndCheckItsValues()
    {
        var dialog = new ExtractImagesViewModel("hồ sơ.pdf", 6, [], _temp.Combine("ảnh"), _files);

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBe(new ExtractImagesDialogResult(new ExtractImagesParams(null, 32, true, false), _temp.Combine("ảnh"), true));
        dialog.MinSideText = "-1";
        dialog.ErrorText.ShouldBe(Strings.ExtractImages_Error_MinSide);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
        dialog.MinSideText = "0";
        dialog.OutputFolder = "ảnh";
        dialog.ErrorText.ShouldBe(Strings.ToImages_Error_Folder);
    }

    [Fact]
    public async Task Attachments_AreListed_Saved_Added_AndRemoved_InOneChange()
    {
        var note = _temp.Combine("ghi chú.txt");
        File.WriteAllText(note, "nội dung", Encoding.UTF8);
        var input = await WithAttachmentsAsync([note]);
        var extra = _temp.Combine("phụ lục.csv");
        File.WriteAllText(extra, "a,b");
        _files.PickAttachmentFiles(Strings.Attachments_Add_Title).Returns([extra]);
        var saved = _temp.Combine("đã lưu.txt");
        _files.PickAttachmentSavePath("ghi chú.txt").Returns(saved);
        var dialog = new AttachmentsViewModel("hồ sơ.pdf", input, canChange: true, _temp.Combine("mở"), _files, NullLogger.Instance);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        await dialog.LoadAsync();

        var item = dialog.Items.ShouldHaveSingleItem();
        item.Name.ShouldBe("ghi chú.txt");
        item.Details.ShouldEndWith(Strings.Attachments_OfDocument);
        dialog.Selected.ShouldBe(item);
        dialog.OpenCommand.CanExecute(null).ShouldBeFalse("only a PDF opens");
        await dialog.SaveCommand.ExecuteAsync(null);
        File.ReadAllText(saved).ShouldBe("nội dung");
        dialog.StatusText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Attachments_Saved, "đã lưu.txt"));

        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
        dialog.AddCommand.Execute(null);
        dialog.Items.Count.ShouldBe(2);
        dialog.Items[1].IsNew.ShouldBeTrue();
        dialog.Items[1].Description = "Bảng số liệu";
        dialog.Selected = item;
        dialog.RemoveCommand.Execute(null);
        dialog.ApplyCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.Add.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new AttachmentToAdd(extra, "Bảng số liệu"));
        result.Remove.ShouldBe([item.Info!.Key]);
        closed.ShouldBe(true);
    }

    [Fact]
    public async Task APdfAttachment_OpensInTheApp_OnceTheWindowCloses()
    {
        var input = await WithAttachmentsAsync([Samples.Basic]);
        var dialog = new AttachmentsViewModel("hồ sơ.pdf", input, canChange: false, _temp.Combine("mở"), _files, NullLogger.Instance);
        await dialog.LoadAsync();
        dialog.AddCommand.CanExecute(null).ShouldBeFalse("read-only");

        await dialog.OpenCommand.ExecuteAsync(null);

        var opened = dialog.OpenAfterClose.ShouldNotBeNull();
        opened.ShouldStartWith(_temp.Combine("mở"));
        File.ReadAllBytes(opened).ShouldBe(File.ReadAllBytes(Samples.Basic));
    }

    [Fact]
    public async Task ADocumentWithoutAttachments_SaysSo()
    {
        var dialog = new AttachmentsViewModel("hồ sơ.pdf", Samples.Basic, canChange: true, _temp.Path, _files, NullLogger.Instance);

        await dialog.LoadAsync();

        dialog.Items.ShouldBeEmpty();
        dialog.EmptyText.ShouldBe(Strings.Attachments_None);
    }

    [Fact]
    public async Task Fonts_AreListedWithTheirEmbedding()
    {
        var dialog = new FontsViewModel("hồ sơ.pdf", Samples.Basic, NullLogger.Instance);

        await dialog.LoadAsync(TestContext.Current.CancellationToken);

        dialog.ErrorText.ShouldBeNull();
        dialog.Fonts.Count.ShouldBe(8);
        dialog.HasNotEmbedded.ShouldBeFalse();
        dialog.SummaryText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Fonts_Summary, 8, 0, 8, 0));
        dialog.Fonts.ShouldAllBe(f => f.EmbeddingText == Strings.Fonts_Subset);
        dialog.Fonts[0].PagesText.ShouldNotBeNullOrEmpty();
    }

    private async Task<string> WithAttachmentsAsync(IReadOnlyList<string> files)
    {
        var output = _temp.Combine($"có đính kèm {Guid.NewGuid():N}.pdf");
        await using var stream = File.Create(output);
        await new EditAttachmentsOperation().ApplyToFileAsync(Samples.Basic, stream, new EditAttachmentsParams([.. files.Select(f => new AttachmentToAdd(f))]), new Progress<Core.Operations.OperationProgress>(), TestContext.Current.CancellationToken);
        return output;
    }
}
