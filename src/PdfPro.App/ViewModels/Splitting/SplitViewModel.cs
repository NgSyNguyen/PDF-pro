using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Splitting;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Splitting;

/// <summary>A split mode as the dialog lists it.</summary>
public sealed record SplitModeOption(SplitMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What the user chose in the split dialog.</summary>
public enum SplitDialogAction
{
    /// <summary>Write the files.</summary>
    Split,

    /// <summary>Turn the computed files into first-page marks, to check them in the organizer before splitting.</summary>
    ConvertToMarks,
}

/// <summary>The confirmed split dialog.</summary>
/// <param name="Segments">The files, with the names the user typed.</param>
/// <param name="Marks">First-page marks to give the document (split by marks, or turning a split into marks); null
/// when the marks stay as they are.</param>
public sealed record SplitDialogResult(
    SplitDialogAction Action,
    SplitParams Parameters,
    IReadOnlyList<SplitSegment> Segments,
    string OutputFolder,
    bool OpenFolder,
    IReadOnlyList<SplitMarkEntry>? Marks);

/// <summary>
/// "Tách" (SPEC "Tách — 7 chế độ"): the mode and its settings, a live preview of the files (first page, pages, page
/// count, file name the user can change), the naming pattern, the folder and whether to replace files of the same
/// name. What a mode needs to read (blank pages, text, bookmarks, sizes) is read once, in the background, with
/// progress and a stop button; the preview then follows every change at once. Must be used on the UI thread.
/// </summary>
public sealed partial class SplitViewModel : ObservableObject, IDisposable
{
    private readonly SplitDocument _document;
    private readonly SplitOperation _operation;
    private readonly SplitAnalysis _analysis;
    private readonly Func<int, CancellationToken, Task<ImageSource?>> _loadPreview;
    private readonly IFileDialogService _fileDialogs;
    private readonly string? _protectedPath;
    private readonly Dictionary<int, string> _customNames = [];
    private readonly Dictionary<int, SplitRowViewModel> _rowsByFirstPage = [];
    private readonly HashSet<SplitMode> _patternModes = [];
    private CancellationTokenSource? _analysisCancellation;
    private SplitParams? _parameters;
    private IReadOnlyList<SplitSegment> _segments = [];
    private bool _namePatternEdited;
    private bool _settingPattern;
    private bool _disposed;

    /// <param name="document">The document as displayed.</param>
    /// <param name="operation">Previews and validates the split.</param>
    /// <param name="defaultFolder">The folder proposed for the files.</param>
    /// <param name="protectedPath">The document's own file, never replaced (or null).</param>
    /// <param name="loadPreview">Loads the image of a page (0-based) for the preview list.</param>
    /// <param name="fileDialogs">Picks another folder.</param>
    public SplitViewModel(
        SplitDocument document,
        SplitOperation operation,
        string defaultFolder,
        string? protectedPath,
        Func<int, CancellationToken, Task<ImageSource?>> loadPreview,
        IFileDialogService fileDialogs)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(loadPreview);
        ArgumentNullException.ThrowIfNull(fileDialogs);
        _document = document;
        _operation = operation;
        _analysis = new SplitAnalysis(document);
        _loadPreview = loadPreview;
        _fileDialogs = fileDialogs;
        _protectedPath = protectedPath;
        Modes =
        [
            new(SplitMode.FirstPages, Strings.Split_Mode_FirstPages),
            new(SplitMode.Ranges, Strings.Split_Mode_Ranges),
            new(SplitMode.EveryN, Strings.Split_Mode_EveryN),
            new(SplitMode.Bookmarks, Strings.Split_Mode_Bookmarks),
            new(SplitMode.Size, Strings.Split_Mode_Size),
            new(SplitMode.BlankSeparators, Strings.Split_Mode_Blank),
            new(SplitMode.Text, Strings.Split_Mode_Text),
        ];

        var plan = document.Plan;
        foreach (var start in plan.GetSplitStarts())
        {
            if (plan[start].SplitMark?.FileName is { } name)
            {
                _customNames[start] = name;
            }
        }

        FirstPagesText = PageRanges.Format(plan.GetSplitStarts());
        RangesText = string.Empty;
        PagesPerFileText = "1";
        MaxMegabytesText = 10.ToString(CultureInfo.CurrentCulture);
        BlankThreshold = BlankPageDetector.DefaultThresholdPercent;
        TextPattern = string.Empty;
        BookmarkLevels = [];
        BookmarkLevel = 1;
        OutputFolder = defaultFolder;
        OpenFolderWhenDone = true;
        Rows = [];
        _namePattern = FileNameTemplate.DefaultPattern;
        SelectedMode = Modes.First(m => m.Mode == (plan.HasSplitMarks ? SplitMode.FirstPages : SplitMode.Ranges));
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<SplitModeOption> Modes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mode), nameof(IsFirstPages), nameof(IsRanges), nameof(IsEveryN), nameof(IsBookmarks), nameof(IsSize), nameof(IsBlank), nameof(IsText), nameof(CanSkipBlankPages), nameof(ShowsBlankThreshold))]
    public partial SplitModeOption SelectedMode { get; set; }

    /// <summary>The chosen mode (the dialog's radio buttons).</summary>
    public SplitMode Mode
    {
        get => SelectedMode.Mode;
        set => SelectedMode = Modes.First(m => m.Mode == value);
    }

    public bool IsFirstPages => Mode == SplitMode.FirstPages;

    public bool IsRanges => Mode == SplitMode.Ranges;

    public bool IsEveryN => Mode == SplitMode.EveryN;

    public bool IsBookmarks => Mode == SplitMode.Bookmarks;

    public bool IsSize => Mode == SplitMode.Size;

    public bool IsBlank => Mode == SplitMode.BlankSeparators;

    public bool IsText => Mode == SplitMode.Text;

    /// <summary>"Bỏ trang trắng" applies to every mode except blank separators (which leave them out anyway).</summary>
    public bool CanSkipBlankPages => Mode != SplitMode.BlankSeparators;

    /// <summary>The blank page sensitivity matters: blank separators, or blank pages left out.</summary>
    public bool ShowsBlankThreshold => IsBlank || (SkipBlankPages && CanSkipBlankPages);

    /// <summary>Split by marks: the first pages ("1, 5, 8"), from the document's marks.</summary>
    [ObservableProperty]
    public partial string FirstPagesText { get; set; }

    [ObservableProperty]
    public partial string RangesText { get; set; }

    [ObservableProperty]
    public partial string PagesPerFileText { get; set; }

    /// <summary>Bookmark levels the document has (1 = top level).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<int> BookmarkLevels { get; private set; }

    [ObservableProperty]
    public partial int BookmarkLevel { get; set; }

    [ObservableProperty]
    public partial string MaxMegabytesText { get; set; }

    /// <summary>Share of dark pixels (%) at or below which a page is blank.</summary>
    [ObservableProperty]
    public partial double BlankThreshold { get; set; }

    /// <summary>"Tìm thấy 4 trang trắng", once blank pages are measured.</summary>
    [ObservableProperty]
    public partial string? BlankPagesText { get; private set; }

    [ObservableProperty]
    public partial string TextPattern { get; set; }

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBlankThreshold))]
    public partial bool SkipBlankPages { get; set; }

    /// <summary>The naming pattern; follows the mode until the user changes it.</summary>
    public string NamePattern
    {
        get => _namePattern;
        set
        {
            if (SetProperty(ref _namePattern, value) && !_settingPattern)
            {
                _namePatternEdited = true;
                Recompute();
            }
        }
    }

    [ObservableProperty]
    public partial string OutputFolder { get; set; }

    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    [ObservableProperty]
    public partial bool OpenFolderWhenDone { get; set; }

    /// <summary>The files the split will write.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SplitRowViewModel> Rows { get; private set; }

    /// <summary>"13 tệp · 230 trang".</summary>
    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    /// <summary>Why nothing can be split with these settings, or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ConvertToMarksCommand))]
    public partial string? ErrorText { get; private set; }

    /// <summary>What the user should know about these files (no text layer, pages over the size limit…), or null.</summary>
    [ObservableProperty]
    public partial string? WarningText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ConvertToMarksCommand), nameof(StopAnalysisCommand))]
    public partial bool IsAnalyzing { get; private set; }

    /// <summary>"Đang đọc tài liệu… 45%".</summary>
    [ObservableProperty]
    public partial string? AnalysisText { get; private set; }

    public SplitDialogResult? Result { get; private set; }

    /// <summary>The background reading in progress, if any (tests wait for it).</summary>
    public Task AnalysisTask { get; private set; } = Task.CompletedTask;

    private string _namePattern;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _analysisCancellation?.Cancel();
        foreach (var row in _rowsByFirstPage.Values)
        {
            row.Release();
        }
    }

    internal void OnRowNameEdited(SplitRowViewModel row)
    {
        var patternName = new SplitNaming(Template(), _document.Name).NameOf(SegmentWithoutCustomName(row.FirstPage), row.Index);
        if (string.IsNullOrWhiteSpace(row.FileName) || string.Equals(row.FileName, patternName, StringComparison.Ordinal))
        {
            _customNames.Remove(row.FirstPage);
        }
        else
        {
            _customNames[row.FirstPage] = row.FileName.Trim();
        }

        _segments = ApplyCustomNames(_segments);
        UpdateNames();
    }

    internal async void LoadThumbnail(SplitRowViewModel row, CancellationToken cancellationToken)
    {
        try
        {
            var image = await _loadPreview(row.FirstPage, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                row.Thumbnail = image;
            }
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or closed.
        }
        catch (PdfDocumentException)
        {
            // The list just shows no image.
        }
    }

    partial void OnSelectedModeChanged(SplitModeOption value)
    {
        if (!_namePatternEdited)
        {
            _settingPattern = true;
            NamePattern = value.Mode switch
            {
                SplitMode.Bookmarks => FileNameTemplate.BookmarkPattern,
                SplitMode.Text => FileNameTemplate.MatchPattern,
                _ => FileNameTemplate.DefaultPattern,
            };
            _settingPattern = false;
        }

        Recompute();
    }

    partial void OnFirstPagesTextChanged(string value) => Recompute();

    partial void OnRangesTextChanged(string value) => Recompute();

    partial void OnPagesPerFileTextChanged(string value) => Recompute();

    partial void OnBookmarkLevelChanged(int value) => Recompute();

    partial void OnMaxMegabytesTextChanged(string value) => Recompute();

    partial void OnBlankThresholdChanged(double value) => Recompute();

    partial void OnTextPatternChanged(string value) => Recompute();

    partial void OnMatchCaseChanged(bool value) => Recompute();

    partial void OnSkipBlankPagesChanged(bool value) => Recompute();

    partial void OnOverwriteChanged(bool value) => UpdateNames();

    partial void OnOutputFolderChanged(string value)
    {
        UpdateNames();
        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Recomputes the files for the current settings; starts reading the document when the mode needs it.</summary>
    private void Recompute()
    {
        if (_disposed || SelectedMode is null)
        {
            return;
        }

        WarningText = null;
        BlankPagesText = null;
        if (!TryBuildParameters(out var parameters, out var error))
        {
            ShowError(error);
            return;
        }

        var validation = _operation.Validate(parameters, new DocumentInfo(_document.Plan.Count));
        if (!validation.IsValid)
        {
            ShowError(Explain(validation.Errors));
            return;
        }

        if (NeedsReading(parameters))
        {
            StartReading(parameters);
            return;
        }

        if (parameters.Mode == SplitMode.Bookmarks && _analysis.MaxBookmarkLevel == 0)
        {
            ShowError(Strings.Split_Error_NoBookmarks);
            return;
        }

        var preview = _operation.Plan(_analysis, parameters);
        if (parameters.NeedsInk)
        {
            BlankPagesText = Format(Strings.Split_Blank_Found, BlankPageDetector.BlankPages(_analysis.DarkPercents!, parameters.BlankThresholdPercent).Count);
        }

        if (preview.Segments.Count == 0)
        {
            ShowError(Strings.Split_Error_Nothing);
            return;
        }

        _parameters = parameters;
        _segments = ApplyCustomNames(preview.Segments);
        ErrorText = null;
        WarningText = Warning(preview);
        ShowRows();
        RunCommand.NotifyCanExecuteChanged();
        ConvertToMarksCommand.NotifyCanExecuteChanged();
    }

    private bool TryBuildParameters(out SplitParams parameters, out string error)
    {
        parameters = new SplitParams(Mode, NamePattern: NamePattern, Overwrite: Overwrite, SkipBlankPages: SkipBlankPages && CanSkipBlankPages, BlankThresholdPercent: Math.Round(BlankThreshold, 3));
        error = string.Empty;
        switch (Mode)
        {
            case SplitMode.FirstPages:
                if (string.IsNullOrWhiteSpace(FirstPagesText))
                {
                    error = Strings.Split_Error_FirstPagesEmpty;
                    return false;
                }

                parameters = parameters with { FirstPages = FirstPagesText };
                return true;
            case SplitMode.Ranges:
                if (string.IsNullOrWhiteSpace(RangesText))
                {
                    error = Strings.Split_Error_RangesEmpty;
                    return false;
                }

                parameters = parameters with { Ranges = RangesText };
                return true;
            case SplitMode.EveryN:
                if (!int.TryParse(PagesPerFileText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var perFile) || perFile < 1 || perFile > 100_000)
                {
                    error = Strings.Split_Error_PagesPerFile;
                    return false;
                }

                parameters = parameters with { PagesPerFile = perFile };
                return true;
            case SplitMode.Bookmarks:
                parameters = parameters with { BookmarkLevel = Math.Max(1, BookmarkLevel) };
                return true;
            case SplitMode.Size:
                if (!TryParseNumber(MaxMegabytesText, out var megabytes) || megabytes < 0.01 || megabytes > 100_000)
                {
                    error = Strings.Split_Error_Size;
                    return false;
                }

                parameters = parameters with { MaxMegabytes = megabytes };
                return true;
            case SplitMode.Text:
                if (string.IsNullOrWhiteSpace(TextPattern))
                {
                    error = Strings.Split_Error_PatternEmpty;
                    return false;
                }

                parameters = parameters with { TextPattern = TextPattern, MatchCase = MatchCase };
                return true;
            default:
                return true;
        }
    }

    private bool NeedsReading(SplitParams parameters)
        => (parameters.NeedsInk && _analysis.DarkPercents is null)
            || (parameters.Mode == SplitMode.Text && _analysis.Texts is null)
            || (parameters.Mode == SplitMode.Bookmarks && _analysis.Bookmarks is null)
            || (parameters.Mode == SplitMode.Size && _analysis.Sizes is null);

    /// <summary>Reads what the settings need in the background, then recomputes (one reading at a time).</summary>
    private void StartReading(SplitParams parameters)
    {
        ShowRowsCleared();
        if (IsAnalyzing)
        {
            return; // the running reading recomputes when it ends, and reads more if still needed
        }

        IsAnalyzing = true;
        AnalysisText = Format(Strings.Split_Analyzing, 0);
        _analysisCancellation = new CancellationTokenSource();
        var token = _analysisCancellation.Token;
        var progress = new Progress<OperationProgress>(p => AnalysisText = Format(Strings.Split_Analyzing, (int)Math.Round(p.Fraction * 100)));
        AnalysisTask = ReadAsync(parameters, progress, token);
    }

    private async Task ReadAsync(SplitParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        string? failure = null;
        try
        {
            await _analysis.PrepareAsync(parameters, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            failure = Strings.Split_Error_Stopped;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever went wrong while reading for the preview, the dialog stays usable and says so.
            failure = Strings.Split_Error_ReadFailed;
        }

        IsAnalyzing = false;
        AnalysisText = null;
        if (_disposed)
        {
            return;
        }

        if (failure is not null)
        {
            ShowError(failure);
            return;
        }

        BookmarkLevels = [.. Enumerable.Range(1, Math.Max(1, _analysis.MaxBookmarkLevel))];
        Recompute();
    }

    [RelayCommand(CanExecute = nameof(IsAnalyzing))]
    private void StopAnalysis() => _analysisCancellation?.Cancel();

    [RelayCommand]
    private void PickFolder()
    {
        if (_fileDialogs.PickFolder(Strings.Split_Folder_Pick_Title, OutputFolder) is { } folder)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (_parameters is not { } parameters)
        {
            return;
        }

        IReadOnlyList<SplitMarkEntry>? marks = parameters.Mode == SplitMode.FirstPages ? MarksOf(_segments) : null;
        Result = new SplitDialogResult(SplitDialogAction.Split, parameters, _segments, Path.GetFullPath(OutputFolder.Trim()), OpenFolderWhenDone, marks);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanRun()
        => !IsAnalyzing && ErrorText is null && _segments.Count > 0 && _parameters is not null && IsUsableFolder(OutputFolder);

    /// <summary>"Chuyển thành dấu": the computed files become first-page marks, to check in the organizer.</summary>
    [RelayCommand(CanExecute = nameof(CanConvertToMarks))]
    private void ConvertToMarks()
    {
        if (_parameters is not { } parameters)
        {
            return;
        }

        Result = new SplitDialogResult(SplitDialogAction.ConvertToMarks, parameters, _segments, OutputFolder, OpenFolderWhenDone, MarksOf(_segments));
        CloseRequested?.Invoke(this, true);
    }

    private bool CanConvertToMarks() => !IsAnalyzing && ErrorText is null && _segments.Count > 0 && Mode != SplitMode.FirstPages;

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, false);
    }

    /// <summary>Marks for the first page of every file (page 1 only when it carries a name), with the typed names.</summary>
    private static IReadOnlyList<SplitMarkEntry> MarksOf(IReadOnlyList<SplitSegment> segments)
        => [.. segments
            .Where(s => s.First > 0 || s.FileName is not null)
            .DistinctBy(s => s.First)
            .OrderBy(s => s.First)
            .Select(s => new SplitMarkEntry(s.First + 1, s.FileName))];

    private IReadOnlyList<SplitSegment> ApplyCustomNames(IReadOnlyList<SplitSegment> segments)
        => [.. segments.Select(s => new SplitSegment(s.Pages, s.Title, _customNames.GetValueOrDefault(s.First) ?? (Mode == SplitMode.FirstPages ? null : s.FileName)))];

    private SplitSegment SegmentWithoutCustomName(int firstPage)
        => _segments.First(s => s.First == firstPage) is var s ? new SplitSegment(s.Pages, s.Title) : throw new InvalidOperationException();

    private void ShowRows()
    {
        var names = Names();
        var rows = new SplitRowViewModel[_segments.Count];
        var keep = new HashSet<int>();
        for (var i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            keep.Add(segment.First);
            if (!_rowsByFirstPage.TryGetValue(segment.First, out var row))
            {
                row = new SplitRowViewModel(this, segment.First);
                _rowsByFirstPage[segment.First] = row;
            }

            row.Update(i + 1, PageRanges.Format(segment.Pages), segment.PageCount, names.Base[i], names.Final[i]);
            rows[i] = row;
        }

        foreach (var gone in _rowsByFirstPage.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _rowsByFirstPage[gone].Release();
            _rowsByFirstPage.Remove(gone);
        }

        Rows = rows;
        SummaryText = Format(Strings.Split_Summary_Files, _segments.Count, _segments.Sum(s => s.PageCount));
    }

    private void ShowRowsCleared()
    {
        _parameters = null;
        _segments = [];
        Rows = [];
        SummaryText = null;
        ErrorText = null;
        RunCommand.NotifyCanExecuteChanged();
        ConvertToMarksCommand.NotifyCanExecuteChanged();
    }

    private void ShowError(string message)
    {
        ShowRowsCleared();
        ErrorText = message;
    }

    private void UpdateNames()
    {
        if (_segments.Count == 0 || !FileNameTemplate.TryParse(NamePattern, out _, out _, out _))
        {
            return;
        }

        var names = Names();
        for (var i = 0; i < _segments.Count && i < Rows.Count; i++)
        {
            Rows[i].Update(i + 1, Rows[i].PagesText, Rows[i].PageCount, names.Base[i], names.Final[i]);
        }
    }

    /// <summary>Each file's name as the pattern or the user gives it, and as it will be written (made unique).</summary>
    private (string[] Base, IReadOnlyList<string> Final) Names()
    {
        var naming = new SplitNaming(Template(), _document.Name);
        var names = _segments.Select((s, i) => naming.NameOf(s, i + 1)).ToArray();
        var folder = IsUsableFolder(OutputFolder) ? Path.GetFullPath(OutputFolder.Trim()) : null;
        var final = FileNames.MakeUnique(names, name =>
        {
            if (folder is null)
            {
                return false;
            }

            var path = Path.Combine(folder, name + ".pdf");
            return (!Overwrite && File.Exists(path)) || (_protectedPath is not null && string.Equals(Path.GetFullPath(_protectedPath), path, StringComparison.OrdinalIgnoreCase));
        });
        return (names, final);
    }

    private FileNameTemplate Template()
        => FileNameTemplate.TryParse(NamePattern, out var template, out _, out _) ? template : FileNameTemplate.Default;

    private string? Warning(SplitPreview preview)
    {
        var warnings = new List<string>();
        if (preview.NoText)
        {
            warnings.Add(Strings.Split_Text_NoText);
        }

        if (preview.OversizedFiles.Count > 0)
        {
            warnings.Add(Format(Strings.Split_Warning_Oversize, preview.OversizedFiles.Count));
        }

        if (preview.SlowPages.Count > 0)
        {
            warnings.Add(Format(Strings.Split_Text_Slow, preview.SlowPages.Count));
        }

        return warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
    }

    private static string Explain(IReadOnlyList<OperationValidationError> errors)
    {
        var member = errors.FirstOrDefault()?.MemberName;
        return member switch
        {
            nameof(SplitParams.Ranges) => Strings.Split_Error_Ranges,
            nameof(SplitParams.FirstPages) => Strings.Split_Error_FirstPages,
            nameof(SplitParams.TextPattern) => Strings.Split_Error_Pattern,
            nameof(SplitParams.NamePattern) => Strings.Split_Error_NamePattern,
            _ => Strings.Edit_Error_Invalid,
        };
    }

    private static bool IsUsableFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            return Path.IsPathFullyQualified(folder.Trim()) && folder.Trim().IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParseNumber(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
