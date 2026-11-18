using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels;
using PdfPro.Infrastructure.Settings;
using Wpf.Ui.Controls;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Tìm lệnh" (Ctrl+K): commands found without tone marks, by group or id, the recent ones first, run when they can.</summary>
public sealed class CommandPaletteViewModelTests
{
    private static readonly PaletteEntry Open = Entry("file.open", "Mở…", "Tệp › Tài liệu", "Ctrl+O");
    private static readonly PaletteEntry Blank = Entry("scan.removeBlankPages", "Xóa trang trắng", "Công cụ › Bản quét");
    private static readonly PaletteEntry Compress = Entry("optimize.compress", "Nén", "Chuyển đổi › Tối ưu");
    private static readonly PaletteEntry Save = new("file.save", "Lưu", "Tệp › Tài liệu", "Ctrl+S", SymbolRegular.Save24, new RelayCommand(() => { }, () => false));

    [Fact]
    public void NothingTyped_ListsTheRecentCommandsFirst()
    {
        var palette = new CommandPaletteViewModel([Open, Blank, Compress, Save], ["optimize.compress", "gone.command"]);

        palette.Results.First().ShouldBe(Compress);
        palette.Results.Count.ShouldBe(4);
        palette.Selected.ShouldBe(Compress);
    }

    [Theory]
    [InlineData("xoa trang trang", "scan.removeBlankPages")]
    [InlineData("xtt", "scan.removeBlankPages")]
    [InlineData("NEN", "optimize.compress")]
    [InlineData("ban quet", "scan.removeBlankPages")]
    [InlineData("file.open", "file.open")]
    public void TypedWords_FindTheCommand(string typed, string id)
    {
        var palette = new CommandPaletteViewModel([Open, Blank, Compress, Save], []);

        palette.Query = typed;

        palette.Results.First().Id.ShouldBe(id);
        palette.Selected!.Id.ShouldBe(id);
    }

    [Fact]
    public void NothingMatching_SaysSo()
    {
        var palette = new CommandPaletteViewModel([Open, Blank], []);

        palette.Query = "zzz";

        palette.HasNoResult.ShouldBeTrue();
        palette.Selected.ShouldBeNull();
    }

    [Fact]
    public void Enter_RunsTheSelected_OnlyWhenItCan()
    {
        var palette = new CommandPaletteViewModel([Save, Open], []);
        bool? closed = null;
        palette.CloseRequested += (_, accepted) => closed = accepted;

        palette.Query = "luu";
        palette.Selected.ShouldBe(Save);
        palette.RunCommand.Execute(null);
        closed.ShouldBeNull("a command that cannot run now is not chosen");

        palette.Query = string.Empty;
        palette.Selected.ShouldBe(Open, "the first command that can run is selected");
        palette.MoveUpCommand.Execute(null);
        palette.Selected.ShouldBe(Save);
        palette.MoveDownCommand.Execute(null);
        palette.Selected.ShouldBe(Open);
        palette.RunCommand.Execute(null);
        (closed, palette.Chosen).ShouldBe((true, Open));
    }

    [Fact]
    public void TheRecentList_PutsTheLastFirst_OnceEach_AndKeepsEight()
    {
        CommandPaletteViewModel.Remember(["a", "b", "c"], "b").ShouldBe(["b", "a", "c"]);
        CommandPaletteViewModel.Remember(null, "x").ShouldBe(["x"]);
        CommandPaletteViewModel.Remember([.. Enumerable.Range(0, 20).Select(i => $"c{i}")], "new").Count.ShouldBe(CommandPaletteViewModel.MaxRecent);
    }

    private static PaletteEntry Entry(string id, string label, string category, string? shortcut = null)
        => new(id, label, category, shortcut, SymbolRegular.Document24, new RelayCommand(() => { }));
}
