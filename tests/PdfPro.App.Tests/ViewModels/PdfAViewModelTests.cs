using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Operations.Converting;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"PDF/A": the level, veraPDF when it is there, the warning for signed documents.</summary>
public sealed class PdfAViewModelTests
{
    [Fact]
    public void ItStarts_WithPdfA2b_CheckedByVeraPdf_WhenVeraPdfAndJavaAreThere()
    {
        var dialog = new PdfAViewModel("hồ sơ.pdf", 6, isSigned: false, new VeraPdfInstallation(@"C:\Users\lan\verapdf", @"C:\Java\bin\java.exe"));

        dialog.IsA2B.ShouldBeTrue();
        dialog.CanValidate.ShouldBeTrue();
        dialog.Validate.ShouldBeTrue();
        dialog.IsSigned.ShouldBeFalse();
        dialog.VeraPdfText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.PdfA_VeraPdf_Found, @"C:\Users\lan\verapdf"));
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldBe(new PdfADialogResult(new ToPdfAParams(), Validate: true));
    }

    [Fact]
    public void PdfA1b_CanBeChosen_AndWithoutVeraPdf_NothingIsChecked()
    {
        var dialog = new PdfAViewModel("hồ sơ.pdf", 6, isSigned: true, veraPdf: null);
        dialog.IsSigned.ShouldBeTrue("the dialog warns that the copy loses the signatures");
        dialog.CanValidate.ShouldBeFalse();
        dialog.VeraPdfText.ShouldBe(Strings.PdfA_VeraPdf_Missing);

        dialog.IsA1B = true;
        dialog.Validate = true;
        dialog.ApplyCommand.Execute(null);

        dialog.IsA2B.ShouldBeFalse();
        dialog.Result.ShouldBe(new PdfADialogResult(new ToPdfAParams(PdfALevel.A1B), Validate: false));
    }

    [Fact]
    public void VeraPdfWithoutJava_IsSaid_AndCancelling_ChoosesNothing()
    {
        var dialog = new PdfAViewModel("hồ sơ.pdf", 6, isSigned: false, new VeraPdfInstallation(@"C:\verapdf", null));

        dialog.CanValidate.ShouldBeFalse();
        dialog.VeraPdfText.ShouldBe(Strings.PdfA_VeraPdf_NoJava);
        dialog.CancelCommand.Execute(null);
        dialog.Result.ShouldBeNull();
    }
}
