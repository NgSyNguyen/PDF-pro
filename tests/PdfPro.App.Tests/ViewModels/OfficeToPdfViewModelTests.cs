using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Operations.Converting;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Office sang PDF": the documents, the folder, and what is confirmed.</summary>
public sealed class OfficeToPdfViewModelTests : IDisposable
{
    private const string Soffice = @"C:\Program Files\LibreOffice\program\soffice.exe";

    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ItStarts_Empty_AndCannotRun_ShowingWhichLibreOfficeConverts()
    {
        var dialog = new OfficeToPdfViewModel(_files, Soffice);

        dialog.ErrorText.ShouldBe(Strings.OfficeToPdf_Error_None);
        dialog.RunCommand.CanExecute(null).ShouldBeFalse();
        dialog.OpenWhenDone.ShouldBeTrue();
        dialog.Overwrite.ShouldBeFalse();
        dialog.ConvertersText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.OfficeToPdf_LibreOffice, Soffice));
    }

    [Fact]
    public void WithOfficeApplications_ItSaysWhatConvertsEachKindOfDocument()
    {
        var word = new OfficeApplication(OfficeDocumentFamily.Text, @"C:\Office\WINWORD.EXE", OfficeVendor.Microsoft);
        var excel = new OfficeApplication(OfficeDocumentFamily.Spreadsheet, @"C:\Office\EXCEL.EXE", OfficeVendor.Microsoft);
        var wps = new OfficeApplication(OfficeDocumentFamily.Text, @"C:\Kingsoft\office6\wps.exe", OfficeVendor.Wps);

        var withLibreOffice = new OfficeToPdfViewModel(_files, Soffice, applications: [word, excel]);
        var wpsOnly = new OfficeToPdfViewModel(_files, null, applications: [wps]);

        withLibreOffice.ConvertersText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.OfficeToPdf_ConvertersLine, Strings.OfficeToPdf_App_Word, Strings.OfficeToPdf_App_Excel, Strings.OfficeToPdf_Converter_LibreOffice));
        withLibreOffice.ConvertersToolTip.ShouldContain(Soffice);
        wpsOnly.ConvertersText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.OfficeToPdf_ConvertersLine, Strings.OfficeToPdf_App_Wps, Strings.OfficeToPdf_Converter_Missing, Strings.OfficeToPdf_Converter_Missing));
        OfficeToPdfViewModel.NameOf(new OfficeApplication(OfficeDocumentFamily.Presentation, @"C:\Office\POWERPNT.EXE", OfficeVendor.Microsoft)).ShouldBe(Strings.OfficeToPdf_App_PowerPoint);
        OfficeToPdfViewModel.NameOf(new OfficeApplication(OfficeDocumentFamily.Text, @"C:\x\writer.exe", OfficeVendor.Other)).ShouldBe(Strings.OfficeToPdf_App_Other);
    }

    [Fact]
    public void Documents_AreAddedInNaturalOrder_OthersAndRepeatsLeftOut_AndTheirFolderIsProposed()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("hồ sơ")).FullName;
        var two = File(folder, "tờ trình 2.docx");
        var ten = File(folder, "tờ trình 10.docx");
        var sheet = File(folder, "bảng lương.xlsx");
        var picture = File(folder, "ảnh.jpg");
        var dialog = new OfficeToPdfViewModel(_files, Soffice);

        dialog.AddPaths([ten, picture, two, Path.Combine(folder, "không có.pptx")]);
        dialog.AddPaths([sheet, two]);

        dialog.Items.Select(i => i.FileName).ShouldBe(["tờ trình 2.docx", "tờ trình 10.docx", "bảng lương.xlsx"]);
        dialog.SummaryText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.OfficeToPdf_Summary, 3));
        dialog.OutputFolder.ShouldBe(folder);
        dialog.ErrorText.ShouldBeNull();

        dialog.Overwrite = true;
        dialog.OpenWhenDone = false;
        dialog.RunCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.Files.ShouldBe([two, ten, sheet]);
        result.OutputFolder.ShouldBe(folder);
        result.Overwrite.ShouldBeTrue();
        result.OpenWhenDone.ShouldBeFalse();
    }

    [Fact]
    public void AFolderWithoutAFullPath_IsRefused_AndAPickedOneIsUsed()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("vào")).FullName;
        var dialog = new OfficeToPdfViewModel(_files, Soffice, [File(folder, "thư.rtf")]);

        dialog.OutputFolder = "PDF";

        dialog.ErrorText.ShouldBe(Strings.OfficeToPdf_Error_Folder);
        dialog.RunCommand.CanExecute(null).ShouldBeFalse();
        _files.PickFolder(Strings.OfficeToPdf_Folder_Pick_Title, "PDF").Returns(@"D:\Bản PDF");
        dialog.PickFolderCommand.Execute(null);
        dialog.ErrorText.ShouldBeNull();
        dialog.RunCommand.Execute(null);
        dialog.Result.ShouldNotBeNull().OutputFolder.ShouldBe(@"D:\Bản PDF");
    }

    [Fact]
    public void DocumentsComeFromTheFileDialog_AndCanBeRemoved()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("chọn")).FullName;
        var slides = File(folder, "trình chiếu.pptx");
        _files.PickOfficeFiles(Strings.OfficeToPdf_Pick_Title).Returns([slides]);
        var dialog = new OfficeToPdfViewModel(_files, Soffice);

        dialog.AddFilesCommand.Execute(null);
        dialog.Items.Single().Path.ShouldBe(slides);
        dialog.RemoveCommand.Execute(dialog.Items[0]);

        dialog.Items.ShouldBeEmpty();
        dialog.ErrorText.ShouldBe(Strings.OfficeToPdf_Error_None);
        dialog.CancelCommand.Execute(null);
        dialog.Result.ShouldBeNull();
    }

    private static string File(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        System.IO.File.WriteAllBytes(path, "PK"u8.ToArray());
        return path;
    }
}
