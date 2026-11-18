using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Operations.Converting;

namespace PdfPro.App.ViewModels.Converting;

/// <summary>What "PDF/A" was confirmed with.</summary>
/// <param name="Parameters">The level.</param>
/// <param name="Validate">Check the copy with veraPDF afterwards.</param>
public sealed record PdfADialogResult(ToPdfAParams Parameters, bool Validate);

/// <summary>
/// "PDF/A" (convert.toPdfA): PDF/A-2b (recommended) or PDF/A-1b with what each costs spelled out, whether to check the
/// copy with veraPDF (offered when it is installed), and a warning when the document is signed.
/// </summary>
public sealed partial class PdfAViewModel : ObservableObject
{
    /// <param name="fileName">The document's name.</param>
    /// <param name="pageCount">Its pages.</param>
    /// <param name="isSigned">Whether it carries digital signatures (the copy will not).</param>
    /// <param name="veraPdf">The veraPDF installation found, or null.</param>
    public PdfAViewModel(string fileName, int pageCount, bool isSigned, VeraPdfInstallation? veraPdf)
    {
        FileName = fileName;
        PageCount = pageCount;
        IsSigned = isSigned;
        CanValidate = veraPdf is { Java: not null };
        Validate = CanValidate;
        VeraPdfText = veraPdf switch
        {
            { Java: not null } => string.Format(CultureInfo.CurrentCulture, Strings.PdfA_VeraPdf_Found, veraPdf.Folder),
            not null => Strings.PdfA_VeraPdf_NoJava,
            null => Strings.PdfA_VeraPdf_Missing,
        };
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public int PageCount { get; }

    /// <summary>"12 trang".</summary>
    public string PagesText => string.Format(CultureInfo.CurrentCulture, Strings.Grayscale_Pages, PageCount);

    /// <summary>The document carries digital signatures: the copy will not carry valid ones.</summary>
    public bool IsSigned { get; }

    /// <summary>veraPDF and Java are installed.</summary>
    public bool CanValidate { get; }

    /// <summary>Where veraPDF is, or what is missing to run it.</summary>
    public string VeraPdfText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsA2B), nameof(IsA1B))]
    public partial PdfALevel Level { get; set; } = PdfALevel.A2B;

    public bool IsA2B
    {
        get => Level == PdfALevel.A2B;
        set
        {
            if (value)
            {
                Level = PdfALevel.A2B;
            }
        }
    }

    public bool IsA1B
    {
        get => Level == PdfALevel.A1B;
        set
        {
            if (value)
            {
                Level = PdfALevel.A1B;
            }
        }
    }

    /// <summary>Check the copy with veraPDF afterwards.</summary>
    [ObservableProperty]
    public partial bool Validate { get; set; }

    public PdfADialogResult? Result { get; private set; }

    [RelayCommand]
    private void Apply()
    {
        Result = new PdfADialogResult(new ToPdfAParams(Level), Validate && CanValidate);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
