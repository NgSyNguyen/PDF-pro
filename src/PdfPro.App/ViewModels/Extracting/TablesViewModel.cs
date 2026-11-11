using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Extracting;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Tables;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Security;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Extracting;

/// <summary>The document whose tables are taken: its name, the file read (its own, or a copy of what it shows), its pages and where to save.</summary>
public sealed record TablesSource(string FileName, string Input, int PageCount, int CurrentPage, string SaveFolder);

/// <summary>A table found, as listed: its number, page, size and how it was read, whether it is kept, and its cells for the grid.</summary>
public sealed partial class TableItemViewModel : ObservableObject
{
    public TableItemViewModel(ExtractedTable table, int number)
    {
        Table = table;
        Number = number;
        Preview = Grid(table);
    }

    public ExtractedTable Table { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial int Number { get; set; }

    /// <summary>The table is written when exporting.</summary>
    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    /// <summary>"Bảng 2 · trang 3".</summary>
    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.Tables_Item, Number, Table.Page + 1);

    /// <summary>"7 dòng × 4 cột · có đường kẻ".</summary>
    public string Details => string.Format(
        CultureInfo.CurrentCulture,
        Strings.Tables_ItemSize,
        Table.RowCount,
        Table.ColumnCount,
        Table.Method == TableMethod.Lattice ? Strings.Tables_Method_Lattice : Strings.Tables_Method_Stream);

    /// <summary>The cells under Excel's column letters, for the preview grid.</summary>
    public DataView Preview { get; }

    private static DataView Grid(ExtractedTable table)
    {
        var data = new DataTable { Locale = CultureInfo.InvariantCulture };
        for (var c = 0; c < table.ColumnCount; c++)
        {
            data.Columns.Add(XlsxWriter.ColumnName(c), typeof(string));
        }

        foreach (var row in table.Rows)
        {
            data.Rows.Add([.. Enumerable.Range(0, table.ColumnCount).Select(c => (object)(c < row.Count ? row[c] : string.Empty))]);
        }

        return data.DefaultView;
    }
}

/// <summary>A table's place on the page shown, in device-independent pixels at 100 %.</summary>
public sealed partial class TableBox(Rect bounds, TableItemViewModel item) : ObservableObject
{
    public double X => bounds.X;

    public double Y => bounds.Y;

    public double Width => bounds.Width;

    public double Height => bounds.Height;

    public TableItemViewModel Item => item;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A choice with its label (format, way of reading numbers, separator, method).</summary>
public sealed record TableOption<T>(T Value, string Label);

/// <summary>
/// "Trích bảng" (extract.tables): the tables of the document found in the background (ruled ones by their lines, the
/// others by their columns), listed with a grid of their cells and shown boxed on their page. A box drawn on a page is
/// read as a table (the way chosen: automatic, by its lines, by its columns), replacing the table it covers; a table can
/// be read again another way, left out or removed. The tables kept are written as an Excel workbook (a sheet each,
/// numbers as numbers) or CSV files (UTF-8 with a BOM, the separator of the computer's region by default).
/// </summary>
public sealed partial class TablesViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    public const double DipPerPoint = 96.0 / 72;

    /// <summary>Pixels per point the page is drawn at (120 DPI).</summary>
    public const double RenderScale = 120.0 / 72;

    private readonly TablesSource _source;
    private readonly ExtractingServices _services;
    private readonly IFileDialogService _files;
    private readonly IShellLauncher _shell;
    private readonly ILogger _logger;
    private IPdfDocument? _document;
    private PageGeometry? _geometry;
    private CancellationTokenSource? _showing;
    private Task? _closing;
    private string? _writtenFolder;

    public TablesViewModel(TablesSource source, ExtractingServices services, IFileDialogService files, IShellLauncher shell, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentOutOfRangeException.ThrowIfLessThan(source.PageCount, 1);
        _source = source;
        _services = services;
        _files = files;
        _shell = shell;
        _logger = logger;
        ShownPage = Math.Clamp(source.CurrentPage, 0, source.PageCount - 1);
        Separator = TextInfoSeparator();
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName => _source.FileName;

    public int PageCount => _source.PageCount;

    /// <summary>Pages to search ("1-3, 8"); empty for all.</summary>
    [ObservableProperty]
    public partial string PagesText { get; set; } = string.Empty;

    public ObservableCollection<TableItemViewModel> Tables { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RereadCommand), nameof(RemoveCommand))]
    public partial TableItemViewModel? SelectedTable { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(ExportCommand), nameof(ReadAreaCommand), nameof(RereadCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>"Đang tìm bảng: trang 3/12…".</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    /// <summary>"Tìm thấy 2 bảng." or why nothing was found.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>The page shown (0-based).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int ShownPage { get; private set; }

    /// <summary>"Trang 3/12".</summary>
    public string PageText => string.Format(CultureInfo.CurrentCulture, Strings.Tables_Page, ShownPage + 1, PageCount);

    [ObservableProperty]
    public partial ImageSource? PageImage { get; private set; }

    /// <summary>The page's width at 100 %, in device-independent pixels.</summary>
    [ObservableProperty]
    public partial double PageWidth { get; private set; }

    [ObservableProperty]
    public partial double PageHeight { get; private set; }

    /// <summary>The tables of the page shown.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TableBox> Boxes { get; private set; } = [];

    [ObservableProperty]
    public partial double Zoom { get; set; } = 0.75;

    public IReadOnlyList<TableOption<TableMethod>> Methods { get; } =
    [
        new(TableMethod.Auto, Strings.Tables_Method_Auto),
        new(TableMethod.Lattice, Strings.Tables_Method_Lattice),
        new(TableMethod.Stream, Strings.Tables_Method_Stream),
    ];

    /// <summary>How a box drawn on the page, or a table read again, is read.</summary>
    [ObservableProperty]
    public partial TableMethod Method { get; set; }

    public IReadOnlyList<TableOption<TableFileFormat>> Formats { get; } =
    [
        new(TableFileFormat.Xlsx, Strings.Tables_Format_Xlsx),
        new(TableFileFormat.Csv, Strings.Tables_Format_Csv),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCsv))]
    public partial TableFileFormat Format { get; set; }

    public bool IsCsv => Format == TableFileFormat.Csv;

    public IReadOnlyList<TableOption<NumberReading>> NumberReadings { get; } =
    [
        new(NumberReading.DotGrouping, Strings.Tables_Numbers_Dot),
        new(NumberReading.CommaGrouping, Strings.Tables_Numbers_Comma),
        new(NumberReading.Text, Strings.Tables_Numbers_Text),
    ];

    /// <summary>Excel: how numbers are written in the document (Vietnamese "1.250.000" by default).</summary>
    [ObservableProperty]
    public partial NumberReading Numbers { get; set; } = NumberReading.DotGrouping;

    public IReadOnlyList<TableOption<CsvSeparator>> Separators { get; } =
    [
        new(CsvSeparator.Comma, Strings.Tables_Separator_Comma),
        new(CsvSeparator.Semicolon, Strings.Tables_Separator_Semicolon),
        new(CsvSeparator.Tab, Strings.Tables_Separator_Tab),
    ];

    [ObservableProperty]
    public partial CsvSeparator Separator { get; set; }

    /// <summary>"Đã ghi 2 bảng vào “bảng.xlsx”.".</summary>
    [ObservableProperty]
    public partial string? ReportText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    public partial bool HasWritten { get; private set; }

    /// <summary>The page being shown (drawn); tests wait on it.</summary>
    public Task Showing { get; private set; } = Task.CompletedTask;

    public void Dispose() => _ = Closing();

    public async ValueTask DisposeAsync() => await Closing();

    partial void OnSelectedTableChanged(TableItemViewModel? value)
    {
        if (value is null)
        {
            Select();
            return;
        }

        if (value.Table.Page != ShownPage || PageImage is null)
        {
            Showing = ShowPageAsync(value.Table.Page);
        }
        else
        {
            Select();
        }
    }

    /// <summary>"Tìm bảng": the tables of the pages asked (all by default), the first one shown.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle), IncludeCancelCommand = true)]
    private async Task FindAsync(CancellationToken cancellationToken)
    {
        if (!PageRanges.TryParse(string.IsNullOrWhiteSpace(PagesText) ? "1-" + PageCount.ToString(CultureInfo.InvariantCulture) : PagesText, PageCount, out var pages, out _) || pages.Count == 0)
        {
            ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.Tables_Error_Pages, PageCount);
            return;
        }

        ErrorText = null;
        StatusText = null;
        IsBusy = true;
        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                if (IsBusy)
                {
                    ProgressText = string.Format(CultureInfo.CurrentCulture, Strings.Tables_Progress, Math.Min(p.Completed + 1, p.Total), p.Total);
                }
            });
            var found = await TableExtractor.FindAsync(_source.Input, null, pages, progress, cancellationToken);
            Tables.Clear();
            foreach (var table in found)
            {
                Tables.Add(new TableItemViewModel(table, Tables.Count + 1));
            }

            _logger.LogInformation("Tables were looked for on {Pages} pages: {Count} found", pages.Count, found.Count);
            StatusText = found.Count == 0 ? Strings.Tables_NoneFound : string.Format(CultureInfo.CurrentCulture, Strings.Tables_Found, found.Count);
            ExportCommand.NotifyCanExecuteChanged();
            if (Tables.Count > 0)
            {
                SelectedTable = Tables[0];
            }
            else
            {
                Showing = ShowPageAsync(ShownPage);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user or by closing the window.
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or DocumentPasswordException)
        {
            _logger.LogWarning("Tables could not be looked for: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Tables_Error_Read;
        }
        finally
        {
            IsBusy = false;
            ProgressText = null;
        }
    }

    private bool IsIdle() => !IsBusy && _closing is null;

    /// <summary>A box drawn on the page shown (device-independent pixels at 100 %) read as a table, replacing the table it covers.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ReadAreaAsync(Rect area)
    {
        if (_geometry is not { } geometry || area.Width < 4 || area.Height < 4)
        {
            return;
        }

        var box = new PageCoordinateMapper(geometry, DipPerPoint).ScreenRectToPdf(new ViewRect(area.X, area.Y, area.Width, area.Height));
        var covered = Tables.FirstOrDefault(t => t.Table.Page == ShownPage && Overlaps(t.Table.Box, box));
        await ReadAsync(new TableRegion(ShownPage, box, Method), covered);
    }

    /// <summary>"Đọc lại": the table chosen read again the way chosen.</summary>
    [RelayCommand(CanExecute = nameof(CanReread))]
    private Task RereadAsync()
        => SelectedTable is { } item ? ReadAsync(new TableRegion(item.Table.Page, item.Table.Box, Method), item) : Task.CompletedTask;

    private bool CanReread() => SelectedTable is not null && IsIdle();

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (SelectedTable is not { } item)
        {
            return;
        }

        var index = Tables.IndexOf(item);
        Tables.Remove(item);
        Renumber();
        SelectedTable = Tables.Count == 0 ? null : Tables[Math.Min(index, Tables.Count - 1)];
        ExportCommand.NotifyCanExecuteChanged();
        if (Tables.Count == 0)
        {
            Showing = ShowPageAsync(ShownPage);
        }
    }

    private bool CanRemove() => SelectedTable is not null;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPage() => Showing = ShowPageAsync(ShownPage - 1);

    private bool CanGoBack() => ShownPage > 0;

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void NextPage() => Showing = ShowPageAsync(ShownPage + 1);

    private bool CanGoOn() => ShownPage < PageCount - 1;

    /// <summary>"Xuất…": the tables kept written where the user says.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var tables = Tables.Where(t => t.IsIncluded).Select(t => t.Table).ToList();
        if (tables.Count == 0)
        {
            ErrorText = Strings.Tables_Error_NoneChosen;
            return;
        }

        var csv = Format == TableFileFormat.Csv;
        if (_files.PickTableSavePath(Path.GetFileNameWithoutExtension(_source.FileName), _source.SaveFolder, csv) is not { } path)
        {
            return;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var parameters = new ExtractTablesParams(
            Format,
            Separator: Separator,
            Numbers: Numbers,
            SheetName: Strings.Tables_SheetName,
            Overwrite: !csv || tables.Count == 1);
        IsBusy = true;
        try
        {
            var written = await _services.Tables.WriteTablesAsync(tables, folder, Path.GetFileNameWithoutExtension(path), parameters, CancellationToken.None);
            _writtenFolder = folder;
            HasWritten = true;
            ErrorText = null;
            ReportText = written.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, Strings.Tables_Written, tables.Count, Path.GetFileName(written[0]))
                : string.Format(CultureInfo.CurrentCulture, Strings.Tables_WrittenFiles, written.Count, Path.GetFileName(written[0]), Path.GetFileName(written[^1]));
            _logger.LogInformation("Tables were written: {Count} as {Format} in {Files} files", tables.Count, Format, written.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Tables could not be written: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Tables_Error_Write;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExport() => Tables.Count > 0 && IsIdle();

    [RelayCommand(CanExecute = nameof(HasWritten))]
    private void OpenFolder()
    {
        if (_writtenFolder is { } folder)
        {
            _shell.OpenFolder(folder);
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, HasWritten);

    /// <summary>Reads one region; the table found replaces <paramref name="replaced"/> or joins its page's tables.</summary>
    private async Task ReadAsync(TableRegion region, TableItemViewModel? replaced)
    {
        ErrorText = null;
        IsBusy = true;
        try
        {
            var read = await TableExtractor.ReadAsync(_source.Input, null, [region], null, CancellationToken.None);
            if (read.Count == 0)
            {
                StatusText = Strings.Tables_NoTableInArea;
                return;
            }

            var item = new TableItemViewModel(read[0], 0) { IsIncluded = replaced?.IsIncluded ?? true };
            if (replaced is not null && Tables.IndexOf(replaced) is var index and >= 0)
            {
                Tables[index] = item;
            }
            else
            {
                var after = Tables.Select((t, i) => (t, i)).LastOrDefault(x => x.t.Table.Page < region.Page || (x.t.Table.Page == region.Page && x.t.Table.Box.Top >= read[0].Box.Top));
                Tables.Insert(after.t is null ? 0 : after.i + 1, item);
            }

            Renumber();
            StatusText = string.Format(CultureInfo.CurrentCulture, Strings.Tables_Read, item.Table.RowCount, item.Table.ColumnCount);
            ExportCommand.NotifyCanExecuteChanged();
            SelectedTable = item;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or DocumentPasswordException)
        {
            _logger.LogWarning("An area could not be read as a table: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Tables_Error_Read;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows a page: measured and its tables boxed first, then drawn.</summary>
    private async Task ShowPageAsync(int page)
    {
        if (page < 0 || page >= PageCount || _closing is not null)
        {
            return;
        }

        _showing?.Cancel();
        var showing = new CancellationTokenSource();
        _showing = showing;
        var token = showing.Token;
        try
        {
            _document ??= await _services.Loader.LoadAsync(_source.Input, null, token);
            var geometry = await _document.GetPageGeometryAsync(page, token);
            token.ThrowIfCancellationRequested();
            if (page != ShownPage || PageImage is null)
            {
                PageImage = null;
            }

            ShownPage = page;
            _geometry = geometry;
            PageWidth = geometry.DisplaySize.Width * DipPerPoint;
            PageHeight = geometry.DisplaySize.Height * DipPerPoint;
            Select();
            var image = await _services.Renderer.RenderAsync(_document, page, RenderScale, null, PdfiumPriority.High, token);
            token.ThrowIfCancellationRequested();
            PageImage = image.Image;
        }
        catch (OperationCanceledException)
        {
            // Another page was asked for, or the window closed.
        }
        catch (PdfDocumentException ex)
        {
            _logger.LogWarning("A page could not be shown for table extraction: {Failure}", ex.Failure);
        }
    }

    /// <summary>The boxes of the tables of the page shown, the chosen one marked.</summary>
    private void Select()
    {
        if (_geometry is not { } geometry)
        {
            Boxes = [];
            return;
        }

        var mapper = new PageCoordinateMapper(geometry, DipPerPoint);
        Boxes = [.. Tables
            .Where(t => t.Table.Page == ShownPage)
            .Select(t => (Item: t, Rect: mapper.PdfRectToScreen(t.Table.Box)))
            .Select(t => new TableBox(new Rect(t.Rect.X, t.Rect.Y, t.Rect.Width, t.Rect.Height), t.Item) { IsSelected = ReferenceEquals(t.Item, SelectedTable) })];
    }

    private void Renumber()
    {
        for (var i = 0; i < Tables.Count; i++)
        {
            Tables[i].Number = i + 1;
        }

        Select();
    }

    private Task Closing()
    {
        if (_closing is null)
        {
            if (FindCommand.IsRunning)
            {
                FindCommand.Cancel();
            }

            _showing?.Cancel();
            _closing = CloseDocumentAsync();
        }

        return _closing;
    }

    /// <summary>Waits for a search stopped by closing (it may still be reading the file), then closes the document shown.</summary>
    private async Task CloseDocumentAsync()
    {
        if (FindCommand.ExecutionTask is { } search)
        {
            try
            {
                await search;
            }
            catch (OperationCanceledException)
            {
                // Stopped by closing.
            }
        }

        _showing?.Cancel();
        if (_document is { } document)
        {
            _document = null;
            try
            {
                await document.DisposeAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or PdfDocumentException)
            {
                _logger.LogDebug("The document of the table extraction did not close cleanly: {ErrorType}", ex.GetType().Name);
            }
        }
    }

    private static bool Overlaps(PdfRect a, PdfRect b)
        => Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left) && Math.Min(a.Top, b.Top) > Math.Max(a.Bottom, b.Bottom);

    /// <summary>The CSV separator Excel expects on this computer (its list separator).</summary>
    private static CsvSeparator TextInfoSeparator() => CultureInfo.CurrentCulture.TextInfo.ListSeparator switch
    {
        ";" => CsvSeparator.Semicolon,
        "\t" => CsvSeparator.Tab,
        _ => CsvSeparator.Comma,
    };
}
