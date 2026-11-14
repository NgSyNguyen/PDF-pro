using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Stamping;
using PdfPro.Core.Text;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Số Bates": the files, the running numbers they get, the label and the output folder.</summary>
public sealed class BatesViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IBatesService _bates = Substitute.For<IBatesService>();
    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void WithoutFiles_NothingCanBeNumbered_ButTheLabelShows()
    {
        var dialog = Create();

        dialog.ErrorText.ShouldBe(Strings.Bates_Error_NoFiles);
        dialog.RunCommand.CanExecute(null).ShouldBeFalse();
        dialog.SampleText.ShouldBe("000001");
        dialog.SummaryText.ShouldBeNull();
        dialog.OutputFolder.ShouldBeEmpty();
    }

    [Fact]
    public async Task Files_GetRunningNumbers_AndAFolderNextToTheFirst()
    {
        var dialog = Create();
        var a = AddFile("a.pdf", 2);
        var b = AddFile("b.pdf", 3);
        var c = AddFile("c.pdf", 1);

        await dialog.AddPathsAsync([a.Path, b.Path, c.Path]);
        dialog.Prefix = "HS-";
        dialog.Digits = 4;
        dialog.StartNumber = 98;

        dialog.Items.Select(i => i.NumbersText).ShouldBe(["HS-0098 – HS-0099", "HS-0100 – HS-0102", "HS-0103 – HS-0103"]);
        dialog.SampleText.ShouldBe("HS-0098");
        dialog.SummaryText.ShouldBe(Format(Strings.Bates_Summary, 3, 6, "HS-0098", "HS-0103"));
        dialog.OutputFolder.ShouldBe(_temp.Combine(Strings.Bates_DefaultFolder));
        dialog.WarningText.ShouldBeNull();
        dialog.ErrorText.ShouldBeNull();
    }

    [Fact]
    public async Task ReorderingTheFiles_RenumbersThem()
    {
        var dialog = Create();
        await dialog.AddPathsAsync([AddFile("hồ sơ 10.pdf", 1).Path, AddFile("hồ sơ 2.pdf", 2).Path, AddFile("hồ sơ 1.pdf", 1).Path]);

        dialog.SortByNameCommand.Execute(null);
        dialog.Items.Select(i => i.FileName).ShouldBe(["hồ sơ 1.pdf", "hồ sơ 2.pdf", "hồ sơ 10.pdf"], "natural order, as Explorer sorts");
        dialog.Items[2].NumbersText.ShouldBe("000004 – 000004");

        dialog.MoveUpCommand.Execute(dialog.Items[2]);
        dialog.Items[1].FileName.ShouldBe("hồ sơ 10.pdf");
        dialog.Items[1].NumbersText.ShouldBe("000002 – 000002");

        dialog.RemoveCommand.Execute(dialog.Items[0]);
        dialog.Items.Select(i => i.NumbersText).ShouldBe(["000001 – 000001", "000002 – 000003"]);
    }

    [Fact]
    public async Task AFileTheServiceRefuses_IsLeftOut()
    {
        var dialog = Create();
        var good = AddFile("a.pdf", 2);
        _bates.AddAsync(_temp.Combine("mật.pdf"), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((BatesFile?)null);

        await dialog.AddPathsAsync([_temp.Combine("mật.pdf"), good.Path]);

        dialog.Items.ShouldHaveSingleItem().File.ShouldBe(good);
    }

    [Fact]
    public async Task NumbersLongerThanTheDigits_AreAWarningOnly()
    {
        var dialog = Create();
        await dialog.AddPathsAsync([AddFile("a.pdf", 5).Path]);

        dialog.Digits = 2;
        dialog.StartNumber = 98;

        dialog.WarningText.ShouldBe(Format(Strings.Bates_Warning_Digits, "102", 2));
        dialog.ErrorText.ShouldBeNull();
        dialog.RunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Theory]
    [InlineData("HS\t", "")]
    [InlineData("", "123456789012345678901234567890123456789012345678901")]
    public async Task ALongOrControlAffix_IsRefused(string prefix, string suffix)
    {
        var dialog = Create();
        await dialog.AddPathsAsync([AddFile("a.pdf", 1).Path]);

        dialog.Prefix = prefix;
        dialog.Suffix = suffix;

        dialog.ErrorText.ShouldBe(Format(Strings.Bates_Error_Affix, 50));
        dialog.RunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task TheColourAndTheFolder_MustBeUsable()
    {
        var dialog = Create();
        await dialog.AddPathsAsync([AddFile("a.pdf", 1).Path]);

        dialog.Color = "đỏ";
        dialog.ErrorText.ShouldBe(Strings.Stamp_Error_Color);
        dialog.SetColorCommand.Execute("#DC2626");
        dialog.ErrorText.ShouldBeNull();

        dialog.OutputFolder = "kết quả";
        dialog.ErrorText.ShouldBe(Strings.Bates_Error_Output, "a folder is needed, not a relative name");
        dialog.OutputFolder = " ";
        dialog.ErrorText.ShouldBe(Strings.Bates_Error_Output);
    }

    [Fact]
    public async Task ReplacingCopies_CannotGoIntoAFolderOfTheFiles()
    {
        var dialog = Create();
        await dialog.AddPathsAsync([AddFile("a.pdf", 1).Path]);

        dialog.OutputFolder = _temp.Path;
        dialog.ErrorText.ShouldBeNull("without replacing, copies get \" (2)\"");

        dialog.Overwrite = true;
        dialog.ErrorText.ShouldBe(Strings.Bates_Error_OutputIsInput);
    }

    [Fact]
    public async Task PickFolder_UsesTheChosenFolder()
    {
        var dialog = Create();
        await dialog.AddPathsAsync([AddFile("a.pdf", 1).Path]);
        var chosen = _temp.Combine("đánh số");
        _fileDialogs.PickFolder(Strings.Bates_PickFolder_Title, _temp.Combine(Strings.Bates_DefaultFolder)).Returns(chosen);

        dialog.PickFolderCommand.Execute(null);

        dialog.OutputFolder.ShouldBe(chosen);
    }

    [Fact]
    public async Task Run_GivesTheFilesInOrder_AndTheLabel()
    {
        var dialog = Create();
        var a = AddFile("a.pdf", 2);
        var b = AddFile("b.pdf", 3);
        await dialog.AddPathsAsync([a.Path, b.Path]);
        dialog.Prefix = "HS-";
        dialog.Suffix = "-A";
        dialog.Digits = 5;
        dialog.StartNumber = 120;
        dialog.Anchor = StampAnchor.TopRight;
        dialog.FontSize = 12;
        dialog.Bold = false;
        dialog.SetColorCommand.Execute("#1D4ED8");
        dialog.Overwrite = true;
        dialog.OpenFolderWhenDone = false;
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.RunCommand.Execute(null);

        closed.ShouldBe(true);
        var result = dialog.Result.ShouldNotBeNull();
        result.Inputs.ShouldBe([a.Path, b.Path]);
        result.OutputFolder.ShouldBe(_temp.Combine(Strings.Bates_DefaultFolder));
        result.OpenFolderWhenDone.ShouldBeFalse();
        var parameters = result.Parameters;
        parameters.Prefix.ShouldBe("HS-");
        parameters.Suffix.ShouldBe("-A");
        parameters.Digits.ShouldBe(5);
        parameters.StartNumber.ShouldBe(120);
        parameters.Anchor.ShouldBe(StampAnchor.TopRight);
        parameters.Overwrite.ShouldBeTrue();
        var style = parameters.Style.ShouldNotBeNull();
        style.FontFamily.ShouldBe(FontFamilies.Default);
        style.FontSize.ShouldBe(12);
        style.Bold.ShouldBeFalse();
        style.Color.ShouldBe(new RgbColor(0x1D, 0x4E, 0xD8));
    }

    [Fact]
    public void Cancel_ClosesWithoutAChoice()
    {
        var dialog = Create();
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.CancelCommand.Execute(null);

        closed.ShouldBe(false);
        dialog.Result.ShouldBeNull();
    }

    [Fact]
    public void TheOpenDocument_CanStartTheList()
    {
        var dialog = new BatesViewModel(_bates, _fileDialogs, [new BatesItemViewModel(new BatesFile(_temp.Combine("hồ sơ.pdf"), "hồ sơ.pdf (đang mở)", 4))]);

        dialog.Items.ShouldHaveSingleItem().NumbersText.ShouldBe("000001 – 000004");
        dialog.OutputFolder.ShouldBe(_temp.Combine(Strings.Bates_DefaultFolder));
        dialog.ErrorText.ShouldBeNull();
    }

    private BatesViewModel Create() => new(_bates, _fileDialogs, []);

    /// <summary>A file the service reads as having <paramref name="pages"/> pages.</summary>
    private BatesFile AddFile(string name, int pages)
    {
        var file = new BatesFile(_temp.Combine(name), name, pages);
        _bates.AddAsync(file.Path, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(file);
        return file;
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
