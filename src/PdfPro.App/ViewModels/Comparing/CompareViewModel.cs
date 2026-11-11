using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Comparing;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Operations.Comparing;
using PdfPro.Operations.Security;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Comparing;

/// <summary>How the pages of a pair are shown.</summary>
public enum CompareViewMode
{
    /// <summary>The old page on the left, the new one on the right.</summary>
    SideBySide,

    /// <summary>The new page over the old one, see-through.</summary>
    Overlay,

    /// <summary>The old page left of a line, the new one right of it.</summary>
    Slider,
}

/// <summary>What a highlight shows: words added (green), deleted (red), modified (yellow), or a region that looks different (purple).</summary>
public enum CompareMarkKind
{
    Added,
    Deleted,
    Modified,
    Visual,
}

/// <summary>A document to compare: its file, the name shown, and a password it opens with (never shown).</summary>
public sealed record ComparedDocument(string Path, string Name, string? Password = null)
{
    public override string ToString() => $"ComparedDocument {{ {Name}, password: {(Password is null ? "no" : "yes")} }}";
}

/// <summary>A highlight on a shown page, in device-independent pixels of the page at 100 %.</summary>
public sealed partial class CompareMark(Rect bounds, CompareMarkKind kind, int? change) : ObservableObject
{
    public double X => bounds.X;

    public double Y => bounds.Y;

    public double Width => bounds.Width;

    public double Height => bounds.Height;

    public Rect Bounds => bounds;

    public CompareMarkKind Kind => kind;

    /// <summary>The index of the change it belongs to, or null for a region that looks different.</summary>
    public int? Change => change;

    /// <summary>It belongs to the change chosen in the list.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One side of the pair shown: a page with its highlights, or a note that this side has no such page.</summary>
public sealed partial class ComparePage : ObservableObject
{
    public ComparePage(int pageIndex, double width, double height, IReadOnlyList<CompareMark> marks)
    {
        PageIndex = pageIndex;
        Width = width;
        Height = height;
        Marks = marks;
    }

    private ComparePage(string missingText)
    {
        MissingText = missingText;
        Marks = [];
    }

    /// <summary>The page (0-based), null when this side has none.</summary>
    public int? PageIndex { get; }

    /// <summary>Width at 100 %, in device-independent pixels.</summary>
    public double Width { get; }

    public double Height { get; }

    public IReadOnlyList<CompareMark> Marks { get; }

    /// <summary>"Bản cũ không có trang này — trang được thêm."; null for a page.</summary>
    public string? MissingText { get; }

    public bool IsMissing => MissingText is not null;

    /// <summary>The rendered page, once PDFium has drawn it.</summary>
    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    public static ComparePage Missing(string text) => new(text);
}

/// <summary>A change as listed: its number, kind, pages and words.</summary>
public sealed record CompareChangeItem(int Index, DocumentChange Change, string KindText, string PagesText)
{
    public CompareMarkKind Kind => CompareViewModel.KindOf(Change.Kind);

    public string Number => (Index + 1).ToString(CultureInfo.CurrentCulture);

    public string OldText => Change.OldText;

    public string NewText => Change.NewText;

    public bool HasOldText => Change.OldText.Length > 0;

    public bool HasNewText => Change.NewText.Length > 0;
}

/// <summary>Where to bring into view on each side (device-independent pixels of the page at 100 %), null for nothing.</summary>
public sealed class CompareRevealEventArgs(Rect? old, Rect? @new) : EventArgs
{
    public Rect? Old => old;

    public Rect? New => @new;
}

/// <summary>
/// "So sánh tài liệu" (compare.documents): two documents chosen (the open one first), compared in the background — text
/// word by word with pages paired by content, and the pairs as pictures — then shown pair by pair: side by side (scrolled
/// and zoomed together, or not), the new page over the old one, or with a slider between them; words added are green,
/// deleted red, modified yellow, regions that look different purple. Choosing a change in the list shows its pair and
/// brings its words into view on both sides. The comparison can be written as a PDF report.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    /// <summary>Device-independent pixels per point at 100 %.</summary>
    public const double DipPerPoint = 96.0 / 72;

    /// <summary>Pixels per point the pages are drawn at (144 DPI): sharp up to 150 %.</summary>
    public const double RenderScale = 2;

    public const double MinZoom = 0.25;

    public const double MaxZoom = 4;

    private readonly CompareServices _services;
    private readonly Services.IFileDialogService _files;
    private readonly ILogger _logger;
    private ComparedDocument? _oldFile;
    private ComparedDocument? _newFile;
    private IPdfDocument? _oldDocument;
    private IPdfDocument? _newDocument;
    private ComparisonResult? _result;
    private CancellationTokenSource? _showing;
    private Task? _closing;

    public CompareViewModel(CompareServices services, Services.IFileDialogService files, ILogger logger, ComparedDocument? oldFile = null, ComparedDocument? newFile = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(files);
        _services = services;
        _files = files;
        _logger = logger;
        _oldFile = oldFile;
        _newFile = newFile;
    }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Asks the view to scroll the highlights of the chosen change into view.</summary>
    public event EventHandler<CompareRevealEventArgs>? RevealRequested;

    public string OldName => _oldFile?.Name ?? Strings.Compare_NoFile;

    public string NewName => _newFile?.Name ?? Strings.Compare_NoFile;

    public string? OldPath => _oldFile?.Path;

    public string? NewPath => _newFile?.Path;

    /// <summary>Headers and footers repeated on most pages are left out ("Trang 3/12").</summary>
    [ObservableProperty]
    public partial bool IgnoreRunningLines { get; set; } = true;

    [ObservableProperty]
    public partial bool IgnoreCase { get; set; }

    /// <summary>The paired pages are also compared as pictures.</summary>
    [ObservableProperty]
    public partial bool CompareImages { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(SwapCommand), nameof(PickOldCommand), nameof(PickNewCommand), nameof(ExportReportCommand))]
    public partial bool IsComparing { get; private set; }

    /// <summary>"Đang so sánh hình ảnh: cặp trang 3/12…".</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>The files or options changed since the comparison shown was made.</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand), nameof(NextChangeCommand), nameof(PreviousChangeCommand))]
    public partial bool HasResult { get; private set; }

    /// <summary>"4 thay đổi: 1 thêm, 0 xóa, 3 sửa." and how many pairs look different.</summary>
    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    public ObservableCollection<CompareChangeItem> Changes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextChangeCommand), nameof(PreviousChangeCommand))]
    public partial CompareChangeItem? SelectedChange { get; set; }

    /// <summary>The pair shown (index in the pairs), -1 before a comparison.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairText), nameof(PairPagesText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPairCommand), nameof(NextPairCommand))]
    public partial int PairIndex { get; private set; } = -1;

    public int PairCount => _result?.Text.Pages.Count ?? 0;

    /// <summary>"Cặp trang 3/5".</summary>
    public string? PairText => PairIndex >= 0 ? Format(Strings.Compare_Pair, PairIndex + 1, PairCount) : null;

    /// <summary>"Bản cũ: trang 2 · Bản mới: trang 3".</summary>
    public string? PairPagesText => PairIndex >= 0 && _result is { } result
        ? Format(Strings.Compare_PairPages, PageNumber(result.Text.Pages[PairIndex].OldPage), PageNumber(result.Text.Pages[PairIndex].NewPage))
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SliderClip))]
    public partial ComparePage? OldPage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SliderClip))]
    public partial ComparePage? NewPage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSideBySide), nameof(IsOverlay), nameof(IsSlider), nameof(IsLayered))]
    public partial CompareViewMode ViewMode { get; set; }

    public bool IsSideBySide
    {
        get => ViewMode == CompareViewMode.SideBySide;
        set => SetMode(value, CompareViewMode.SideBySide);
    }

    public bool IsOverlay
    {
        get => ViewMode == CompareViewMode.Overlay;
        set => SetMode(value, CompareViewMode.Overlay);
    }

    public bool IsSlider
    {
        get => ViewMode == CompareViewMode.Slider;
        set => SetMode(value, CompareViewMode.Slider);
    }

    /// <summary>One page over the other (overlay or slider).</summary>
    public bool IsLayered => ViewMode != CompareViewMode.SideBySide;

    /// <summary>Overlay: how opaque the new page is over the old one (0–1).</summary>
    [ObservableProperty]
    public partial double OverlayOpacity { get; set; } = 0.5;

    /// <summary>Slider: where the line is, as a share of the width (0 = all new, 1 = all old).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SliderClip))]
    public partial double SliderPosition { get; set; } = 0.5;

    /// <summary>Slider: the part of the new page shown, right of the line (page pixels at 100 %).</summary>
    public Rect SliderClip => NewPage is { IsMissing: false } page
        ? new Rect(page.Width * SliderPosition, 0, page.Width * (1 - SliderPosition), page.Height)
        : Rect.Empty;

    /// <summary>Both sides scroll and zoom together.</summary>
    [ObservableProperty]
    public partial bool IsSynchronized { get; set; } = true;

    [ObservableProperty]
    public partial double OldZoom { get; set; } = 0.75;

    [ObservableProperty]
    public partial double NewZoom { get; set; } = 0.75;

    /// <summary>"Đã lưu báo cáo: …".</summary>
    [ObservableProperty]
    public partial string? ReportText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportCommand))]
    public partial string? ReportPath { get; private set; }

    /// <summary>The report to open in PDF_Pro once the window has closed, or null.</summary>
    public string? OpenAfterClose { get; private set; }

    /// <summary>The pair being shown (pages measured, then drawn); tests wait on it.</summary>
    public Task Showing { get; private set; } = Task.CompletedTask;

    /// <summary>The comparison shown, or null.</summary>
    public ComparisonResult? Result => _result;

    /// <summary>Stops what runs and closes the documents (in the background).</summary>
    public void Dispose() => _ = Closing();

    /// <summary>Stops what runs and closes the documents, so their files can be deleted.</summary>
    public async ValueTask DisposeAsync() => await Closing();

    internal static CompareMarkKind KindOf(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => CompareMarkKind.Added,
        ChangeKind.Deleted => CompareMarkKind.Deleted,
        _ => CompareMarkKind.Modified,
    };

    /// <summary>The words of the report in the user's language.</summary>
    internal static ComparisonReportLabels ReportLabels() => new(
        Strings.Compare_Report_Title,
        Strings.Compare_Report_OldFile,
        Strings.Compare_Report_NewFile,
        Strings.Compare_Report_Date,
        Strings.Compare_Report_Summary,
        Strings.Compare_Summary_Same,
        Strings.Compare_Kind_Added,
        Strings.Compare_Kind_Deleted,
        Strings.Compare_Kind_Modified,
        Strings.Compare_Report_Pages,
        Strings.Compare_Report_Visual,
        Strings.Compare_Report_Footer);

    partial void OnIgnoreRunningLinesChanged(bool value) => OptionsChanged();

    partial void OnIgnoreCaseChanged(bool value) => OptionsChanged();

    partial void OnCompareImagesChanged(bool value) => OptionsChanged();

    partial void OnOldZoomChanged(double value)
    {
        if (IsSynchronized && NewZoom != value)
        {
            NewZoom = value;
        }
    }

    partial void OnNewZoomChanged(double value)
    {
        if (IsSynchronized && OldZoom != value)
        {
            OldZoom = value;
        }
    }

    partial void OnIsSynchronizedChanged(bool value)
    {
        if (value)
        {
            NewZoom = OldZoom;
        }
    }

    partial void OnSelectedChangeChanged(CompareChangeItem? value)
    {
        if (value is not null && _result is not null)
        {
            Showing = ShowChangeAsync(value);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChooseFiles))]
    private void PickOld()
    {
        if (_files.PickPdfFile(Strings.Compare_PickOld) is { } path)
        {
            _oldFile = new ComparedDocument(path, Path.GetFileName(path));
            FilesChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanChooseFiles))]
    private void PickNew()
    {
        if (_files.PickPdfFile(Strings.Compare_PickNew) is { } path)
        {
            _newFile = new ComparedDocument(path, Path.GetFileName(path));
            FilesChanged();
        }
    }

    /// <summary>"Đổi chỗ": the old version becomes the new one.</summary>
    [RelayCommand(CanExecute = nameof(CanChooseFiles))]
    private void Swap()
    {
        (_oldFile, _newFile) = (_newFile, _oldFile);
        FilesChanged();
    }

    private bool CanChooseFiles() => !IsComparing;

    /// <summary>"So sánh": both documents opened (asking for their passwords), compared, and the first change shown.</summary>
    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_oldFile is not { } oldFile || _newFile is not { } newFile)
        {
            return;
        }

        ErrorText = null;
        IsComparing = true;
        ProgressText = Strings.Compare_Progress_Opening;
        try
        {
            ClearResult();
            await CloseDocumentsAsync();
            if (await OpenAsync(oldFile, cancellationToken) is not { } old)
            {
                return;
            }

            if (!await KeepAsync(old.Document, isOld: true))
            {
                return;
            }

            // The same file twice (a document against itself) is not asked for its password twice.
            var sameFile = string.Equals(Path.GetFullPath(oldFile.Path), Path.GetFullPath(newFile.Path), StringComparison.OrdinalIgnoreCase);
            var newWithPassword = newFile.Password is null && sameFile ? newFile with { Password = old.Password } : newFile;
            if (await OpenAsync(newWithPassword, cancellationToken) is not { } @new || !await KeepAsync(@new.Document, isOld: false))
            {
                return;
            }

            var progress = new Progress<OperationProgress>(p =>
            {
                if (IsComparing)
                {
                    ProgressText = p.StageKey == VisualComparer.Stage
                        ? Format(Strings.Compare_Progress_Visual, Math.Min(p.Completed + 1, Math.Max(1, p.Total)), p.Total)
                        : Strings.Compare_Progress_Text;
                }
            });
            var result = await _services.Comparison.CompareAsync(
                new ComparedFile(oldFile.Path, old.Password),
                new ComparedFile(newFile.Path, @new.Password),
                new CompareSettings(IgnoreRunningLines, IgnoreCase, CompareImages),
                progress,
                cancellationToken);
            _logger.LogInformation(
                "Two documents were compared: {Changes} changes over {Pairs} pairs of pages, {Different} looking different",
                result.Text.Changes.Count,
                result.Text.Pages.Count,
                result.Visual?.Count(v => !v.IsSame));
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user or by closing the window.
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or PdfDocumentException or DocumentPasswordException)
        {
            _logger.LogWarning("Two documents could not be compared: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Compare_Error_Read;
        }
        finally
        {
            IsComparing = false;
            ProgressText = null;
        }
    }

    private bool CanRun() => _oldFile is not null && _newFile is not null && !IsComparing && _closing is null;

    /// <summary>Keeps an opened document to show its pages, unless the window has closed meanwhile.</summary>
    private async Task<bool> KeepAsync(IPdfDocument document, bool isOld)
    {
        if (_closing is not null)
        {
            await document.DisposeAsync();
            return false;
        }

        if (isOld)
        {
            _oldDocument = document;
        }
        else
        {
            _newDocument = document;
        }

        return true;
    }

    private Task Closing()
    {
        if (_closing is null)
        {
            if (RunCommand.IsRunning)
            {
                RunCommand.Cancel();
            }

            _showing?.Cancel();
            _closing = CloseDocumentsAsync();
        }

        return _closing;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPair() => Showing = ShowPairAsync(PairIndex - 1, null);

    private bool CanGoBack() => PairIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void NextPair() => Showing = ShowPairAsync(PairIndex + 1, null);

    private bool CanGoOn() => PairIndex >= 0 && PairIndex < PairCount - 1;

    [RelayCommand(CanExecute = nameof(HasNextChange))]
    private void NextChange() => SelectedChange = Changes[SelectedChange is { } item ? item.Index + 1 : 0];

    private bool HasNextChange() => Changes.Count > 0 && (SelectedChange is not { } item || item.Index < Changes.Count - 1);

    [RelayCommand(CanExecute = nameof(HasPreviousChange))]
    private void PreviousChange() => SelectedChange = Changes[SelectedChange is { } item ? item.Index - 1 : Changes.Count - 1];

    private bool HasPreviousChange() => Changes.Count > 0 && (SelectedChange is not { } item || item.Index > 0);

    /// <summary>"Xuất báo cáo…": the comparison written as a PDF (one line per change, with its pages).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportReportAsync()
    {
        if (_result is not { } result || _oldFile is not { } oldFile || _newFile is not { } newFile)
        {
            return;
        }

        var suggested = Format(Strings.Compare_Report_FileName, Path.GetFileNameWithoutExtension(newFile.Name)) + ".pdf";
        if (_files.PickSavePath(suggested, Path.GetDirectoryName(newFile.Path)) is not { } path)
        {
            return;
        }

        var (oldPages, newPages) = (result.Text.OldPageCount, result.Text.NewPageCount);
        var labels = ReportLabels();
        try
        {
            await Task.Run(() => WriteReport(path, oldFile.Name, oldPages, newFile.Name, newPages, result, labels));
            ReportPath = path;
            ReportText = Format(Strings.Compare_Exported, Path.GetFileName(path));
            ErrorText = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The comparison report could not be written: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Compare_Error_Report;
        }
    }

    private bool CanExport() => HasResult && !IsComparing;

    /// <summary>"Mở báo cáo": closes the window; the report then opens in PDF_Pro.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenReport))]
    private void OpenReport()
    {
        OpenAfterClose = ReportPath;
        CloseRequested?.Invoke(this, true);
    }

    private bool CanOpenReport() => ReportPath is not null;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, false);

    /// <summary>Written next to the chosen file, then moved over it: a file in the way is only replaced by a whole report.</summary>
    private static void WriteReport(string path, string oldName, int oldPages, string newName, int newPages, ComparisonResult result, ComparisonReportLabels labels)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new IOException("The report has no folder.");
        var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                ComparisonReport.Write(stream, oldName, oldPages, newName, newPages, DateTimeOffset.Now, result.Text, result.Visual, labels);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>The document opened by PDFium, asking for its password as long as it is needed and not cancelled; null when it cannot be opened.</summary>
    private async Task<(IPdfDocument Document, string? Password)?> OpenAsync(ComparedDocument file, CancellationToken cancellationToken)
    {
        var password = file.Password;
        var retry = false;
        while (true)
        {
            try
            {
                return (await _services.Loader.LoadAsync(file.Path, password, cancellationToken), password);
            }
            catch (PdfDocumentException ex) when (ex.Failure is PdfOpenFailure.PasswordRequired or PdfOpenFailure.WrongPassword)
            {
                password = _services.Passwords.RequestPassword(file.Name, retry);
                retry = true;
                if (password is null)
                {
                    return null;
                }
            }
            catch (PdfDocumentException ex)
            {
                _logger.LogWarning("A document to compare could not be opened: {Failure}", ex.Failure);
                ErrorText = Format(Strings.Compare_Error_Open, file.Name);
                return null;
            }
        }
    }

    private void ShowResult(ComparisonResult result)
    {
        _result = result;
        var text = result.Text;
        foreach (var (change, index) in text.Changes.Select((c, i) => (c, i)))
        {
            var kind = change.Kind switch
            {
                ChangeKind.Added => Strings.Compare_Kind_Added,
                ChangeKind.Deleted => Strings.Compare_Kind_Deleted,
                _ => Strings.Compare_Kind_Modified,
            };
            Changes.Add(new CompareChangeItem(index, change, kind, Format(Strings.Compare_Change_Pages, change.OldPage + 1, change.NewPage + 1)));
        }

        var summary = text.AreSame
            ? Strings.Compare_Summary_Same
            : Format(
                Strings.Compare_Summary,
                text.Changes.Count,
                text.Changes.Count(c => c.Kind == ChangeKind.Added),
                text.Changes.Count(c => c.Kind == ChangeKind.Deleted),
                text.Changes.Count(c => c.Kind == ChangeKind.Modified));
        if (result.Visual is { } visual)
        {
            var different = visual.Count(v => !v.IsSame);
            summary += " " + (different == 0 ? Strings.Compare_Summary_VisualSame : Format(Strings.Compare_Summary_Visual, different));
        }

        SummaryText = summary;
        IsStale = false;
        HasResult = true;
        OnPropertyChanged(nameof(PairCount));
        NextChangeCommand.NotifyCanExecuteChanged();
        PreviousChangeCommand.NotifyCanExecuteChanged();
        if (Changes.Count > 0)
        {
            SelectedChange = Changes[0];
        }
        else if (text.Pages.Count > 0)
        {
            Showing = ShowPairAsync(0, null);
        }
    }

    private void ClearResult()
    {
        _showing?.Cancel();
        _result = null;
        SelectedChange = null;
        Changes.Clear();
        HasResult = false;
        SummaryText = null;
        OldPage = null;
        NewPage = null;
        PairIndex = -1;
        ReportPath = null;
        ReportText = null;
        OnPropertyChanged(nameof(PairCount));
    }

    private Task ShowChangeAsync(CompareChangeItem item)
    {
        var pair = ComparisonPages.PairOf(_result!.Text, item.Change);
        if (pair < 0)
        {
            return Task.CompletedTask;
        }

        if (pair == PairIndex && OldPage is not null && NewPage is not null)
        {
            Select(item);
            return Task.CompletedTask;
        }

        return ShowPairAsync(pair, item);
    }

    /// <summary>Shows a pair: its pages measured and their highlights placed first (so the chosen change can be brought into view), then drawn.</summary>
    private async Task ShowPairAsync(int index, CompareChangeItem? reveal)
    {
        if (_result is not { } result || index < 0 || index >= result.Text.Pages.Count)
        {
            return;
        }

        _showing?.Cancel();
        var showing = new CancellationTokenSource();
        _showing = showing;
        var token = showing.Token;
        var pair = result.Text.Pages[index];
        var highlights = ComparisonPages.HighlightsOf(result.Text, result.Visual, index);
        try
        {
            var oldPage = await PageAsync(_oldDocument, pair.OldPage, highlights.Old, Strings.Compare_Missing_Old, token);
            var newPage = await PageAsync(_newDocument, pair.NewPage, highlights.New, Strings.Compare_Missing_New, token);
            token.ThrowIfCancellationRequested();
            PairIndex = index;
            OldPage = oldPage;
            NewPage = newPage;
            Select(reveal ?? SelectedChange);
            await Task.WhenAll(DrawAsync(_oldDocument, oldPage, token), DrawAsync(_newDocument, newPage, token));
        }
        catch (OperationCanceledException)
        {
            // Another pair was asked for, or the window closed.
        }
        catch (PdfDocumentException ex)
        {
            _logger.LogWarning("A pair of pages could not be shown: {Failure}", ex.Failure);
        }
    }

    private static async Task<ComparePage> PageAsync(IPdfDocument? document, int? page, IReadOnlyList<PageHighlight> highlights, string missing, CancellationToken cancellationToken)
    {
        if (document is null || page is not { } index)
        {
            return ComparePage.Missing(missing);
        }

        var geometry = await document.GetPageGeometryAsync(index, cancellationToken);
        var mapper = new PageCoordinateMapper(geometry, DipPerPoint);
        var marks = highlights
            .Select(h => (Box: mapper.PdfRectToScreen(h.Box), h.Kind, h.Change))
            .Select(h => new CompareMark(new Rect(h.Box.X, h.Box.Y, h.Box.Width, h.Box.Height), h.Kind is { } kind ? KindOf(kind) : CompareMarkKind.Visual, h.Change))
            .ToList();
        var size = geometry.DisplaySize;
        return new ComparePage(index, size.Width * DipPerPoint, size.Height * DipPerPoint, marks);
    }

    private async Task DrawAsync(IPdfDocument? document, ComparePage page, CancellationToken cancellationToken)
    {
        if (document is null || page.PageIndex is not { } index)
        {
            return;
        }

        var image = await _services.Renderer.RenderAsync(document, index, RenderScale, null, PdfiumPriority.High, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        page.Image = image.Image;
    }

    /// <summary>Marks the change's highlights on the pages shown and asks the view to bring them into view.</summary>
    private void Select(CompareChangeItem? item)
    {
        Rect? old = null;
        Rect? @new = null;
        foreach (var (page, isOld) in new[] { (OldPage, true), (NewPage, false) })
        {
            foreach (var mark in page?.Marks ?? [])
            {
                mark.IsSelected = item is not null && mark.Change == item.Index;
                if (mark.IsSelected)
                {
                    if (isOld)
                    {
                        old = old is { } o ? Rect.Union(o, mark.Bounds) : mark.Bounds;
                    }
                    else
                    {
                        @new = @new is { } n ? Rect.Union(n, mark.Bounds) : mark.Bounds;
                    }
                }
            }
        }

        if (old is not null || @new is not null)
        {
            RevealRequested?.Invoke(this, new CompareRevealEventArgs(old, @new));
        }
    }

    private void SetMode(bool on, CompareViewMode mode)
    {
        if (on)
        {
            ViewMode = mode;
        }
    }

    private void FilesChanged()
    {
        OnPropertyChanged(nameof(OldName));
        OnPropertyChanged(nameof(NewName));
        OnPropertyChanged(nameof(OldPath));
        OnPropertyChanged(nameof(NewPath));
        RunCommand.NotifyCanExecuteChanged();
        OptionsChanged();
    }

    private void OptionsChanged() => IsStale = HasResult;

    private async Task CloseDocumentsAsync()
    {
        var (old, @new) = (_oldDocument, _newDocument);
        (_oldDocument, _newDocument) = (null, null);
        foreach (var document in new[] { old, @new })
        {
            if (document is null)
            {
                continue;
            }

            try
            {
                await document.DisposeAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or PdfDocumentException)
            {
                _logger.LogDebug("A compared document did not close cleanly: {ErrorType}", ex.GetType().Name);
            }
        }
    }

    private static string PageNumber(int? page) => page is { } p ? (p + 1).ToString(CultureInfo.CurrentCulture) : "—";

    private static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
