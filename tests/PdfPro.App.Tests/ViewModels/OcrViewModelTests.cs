using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Ocr;
using PdfPro.Core.Ocr;
using PdfPro.Operations.Ocr;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>
/// "Nhận dạng chữ (OCR)": the languages the engine has (Vietnamese first; the last run's ticked; the main one is the
/// one ticked first), the pages, pages with text, turning and straightening, pages read at once, what else to save.
/// </summary>
public sealed class OcrViewModelTests
{
    private static readonly OcrEngineInfo Engine = new("Tesseract", "tesseract.exe", OcrDataKind.Best, ["deu", "eng", "vie"], DetectsOrientation: true);

    [Fact]
    public void Vietnamese_IsOfferedFirst_AndTickedWhenNothingWasChosenBefore()
    {
        var dialog = new OcrViewModel("hồ sơ.pdf", 10, [], Engine, [], 0);

        dialog.Languages.Select(l => l.Code).ShouldBe(["vie", "eng", "deu"]);
        dialog.Languages.Select(l => l.Label).ShouldBe([Strings.Ocr_Language_vie, Strings.Ocr_Language_eng, Strings.Ocr_Language_deu]);
        dialog.ChosenLanguages.ShouldBe(["vie"]);
        dialog.EngineText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Engine, "Tesseract", Strings.Ocr_Data_Best));
        dialog.Parallelism.ShouldBe(Math.Clamp(Environment.ProcessorCount - 1, 1, Environment.ProcessorCount));
        dialog.ErrorText.ShouldBeNull();
    }

    [Fact]
    public void TheLastRunsLanguages_AreTicked_AndTheFirstTickedIsTheMainOne()
    {
        var dialog = new OcrViewModel("a.pdf", 3, [], Engine, ["eng", "fra"], 2);

        dialog.ChosenLanguages.ShouldBe(["eng"], "French has no data");
        dialog.Parallelism.ShouldBe(2);

        dialog.Languages.Single(l => l.Code == "eng").IsChecked = false;
        dialog.Languages.Single(l => l.Code == "vie").IsChecked = true;
        dialog.Languages.Single(l => l.Code == "eng").IsChecked = true;

        dialog.ChosenLanguages.ShouldBe(["vie", "eng"]);
        dialog.Choice(out _).ShouldNotBeNull().Parameters.Languages.ShouldBe(["vie", "eng"]);
    }

    [Fact]
    public void WithoutALanguage_OrWithTooManyPagesAtOnce_NothingCanBeRun()
    {
        var dialog = new OcrViewModel("a.pdf", 3, [], Engine, [], 0);

        dialog.Languages.Single(l => l.Code == "vie").IsChecked = false;
        dialog.ErrorText.ShouldBe(Strings.Ocr_Error_NoLanguage);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();

        dialog.Languages.Single(l => l.Code == "vie").IsChecked = true;
        dialog.Parallelism = dialog.MaxParallelism + 1;
        dialog.ErrorText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Error_Parallelism, dialog.MaxParallelism));
        dialog.Parallelism = null;
        dialog.ErrorText.ShouldNotBeNull();
        dialog.Parallelism = 1;
        dialog.ErrorText.ShouldBeNull();
    }

    [Fact]
    public void TheChoice_GivesTheRun_AndWhatToSave()
    {
        var dialog = new OcrViewModel("a.pdf", 5, [1, 3], Engine, ["vie"], 1)
        {
            IsReplace = true,
            Deskew = false,
            StraightenPages = true,
            SaveText = true,
            SaveHocr = true,
        };
        dialog.Target.IsSelected = true;
        var closed = (bool?)null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.ApplyCommand.Execute(null);

        closed.ShouldBe(true);
        var result = dialog.Result.ShouldNotBeNull();
        (result.SaveText, result.SaveHocr).ShouldBe((true, true));
        var parameters = result.Parameters;
        parameters.Pages.ShouldBe([1, 3]);
        (parameters.ExistingText, parameters.AutoRotate, parameters.Deskew, parameters.StraightenPages, parameters.Hocr, parameters.Parallelism)
            .ShouldBe((ExistingTextMode.Replace, true, false, true, true, 1));
        dialog.Target.IsAll = true;
        dialog.Choice(out _)!.Parameters.Pages.ShouldBeNull("every page");
    }

    [Fact]
    public void WithoutOrientationData_PagesAreNotTurned()
    {
        var dialog = new OcrViewModel("a.pdf", 1, [], Engine with { DetectsOrientation = false, Data = null }, [], 0);

        (dialog.CanAutoRotate, dialog.AutoRotate).ShouldBe((false, false));
        dialog.Choice(out _)!.Parameters.AutoRotate.ShouldBeFalse();
        dialog.EngineText.ShouldContain(Strings.Ocr_Data_Own);
        OcrViewModel.LanguageName("xyz").ShouldBe("xyz");
    }

    [Fact]
    public void TheTimeLeft_IsEstimatedFromWhatIsDone()
    {
        RemainingTime.Estimate(TimeSpan.FromSeconds(30), 0.25).ShouldBe(TimeSpan.FromSeconds(90));
        RemainingTime.Estimate(TimeSpan.FromSeconds(30), 0.01).ShouldBeNull("too early to tell");
        RemainingTime.Estimate(TimeSpan.FromSeconds(30), 1).ShouldBeNull();
        RemainingTime.Describe(TimeSpan.FromSeconds(12)).ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.TimeLeft_Seconds, 15));
        RemainingTime.Describe(TimeSpan.FromSeconds(150)).ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.TimeLeft_Minutes, 3));
        RemainingTime.Describe(TimeSpan.FromSeconds(4500)).ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.TimeLeft_Hours, 1, 15));
    }
}
