using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.App.Resources;

namespace PdfPro.App.ViewModels.Splitting;

/// <summary>
/// One file of the split preview: its first page's image (loaded while the list shows the row), its pages, and its
/// name, which the user can change.
/// </summary>
public sealed partial class SplitRowViewModel : ObservableObject, IRealizable
{
    private readonly SplitViewModel _owner;
    private CancellationTokenSource? _loading;
    private bool _updating;

    internal SplitRowViewModel(SplitViewModel owner, int firstPage)
    {
        _owner = owner;
        FirstPage = firstPage;
        PagesText = string.Empty;
        FileName = string.Empty;
    }

    /// <summary>0-based first page.</summary>
    public int FirstPage { get; }

    /// <summary>1-based file number.</summary>
    [ObservableProperty]
    public partial int Index { get; private set; }

    /// <summary>"9-14" (1-based).</summary>
    [ObservableProperty]
    public partial string PagesText { get; private set; }

    [ObservableProperty]
    public partial int PageCount { get; private set; }

    /// <summary>The file name without extension; the user may type another.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalNameText))]
    public partial string FileName { get; set; }

    /// <summary>The name the file will really get ("… (2)" when the name is taken).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalNameText))]
    public partial string FinalName { get; private set; } = string.Empty;

    /// <summary>"Đã có tệp này — tệp mới sẽ là X (2).pdf", or null when the name is free.</summary>
    public string? FinalNameText
        => string.Equals(FinalName, Sanitized(FileName), StringComparison.Ordinal) || FinalName.Length == 0
            ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.Split_Name_Conflict, FinalName + ".pdf");

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; internal set; }

    public string AutomationName => string.Format(CultureInfo.CurrentCulture, Strings.Split_Row_AutomationName, Index, PagesText);

    public void Realize(double dpiScale)
    {
        if (Thumbnail is not null || _loading is not null)
        {
            return;
        }

        _loading = new CancellationTokenSource();
        _owner.LoadThumbnail(this, _loading.Token);
    }

    public void Release()
    {
        _loading?.Cancel();
        _loading = null;
        Thumbnail = null;
    }

    internal void Update(int index, string pagesText, int pageCount, string fileName, string finalName)
    {
        _updating = true;
        try
        {
            Index = index;
            PagesText = pagesText;
            PageCount = pageCount;
            FileName = fileName;
            FinalName = finalName;
            OnPropertyChanged(nameof(AutomationName));
        }
        finally
        {
            _updating = false;
        }
    }

    partial void OnFileNameChanged(string value)
    {
        if (!_updating)
        {
            _owner.OnRowNameEdited(this);
        }
    }

    private static string Sanitized(string name) => Core.Splitting.FileNames.Sanitize(name);
}
