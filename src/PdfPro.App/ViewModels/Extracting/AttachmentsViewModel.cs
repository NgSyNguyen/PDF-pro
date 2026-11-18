using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Core.Text;
using PdfPro.Operations.Extracting;

namespace PdfPro.App.ViewModels.Extracting;

/// <summary>An attachment as listed: one of the document (<see cref="Info"/>), or a file to attach (<see cref="NewPath"/>).</summary>
public sealed partial class AttachmentItemViewModel : ObservableObject
{
    private AttachmentItemViewModel(AttachmentInfo? info, string? newPath, string name, long? size)
    {
        Info = info;
        NewPath = newPath;
        Name = name;
        Size = size;
        Description = info?.Description ?? string.Empty;
    }

    public AttachmentInfo? Info { get; }

    public string? NewPath { get; }

    public string Name { get; }

    public long? Size { get; }

    public bool IsNew => NewPath is not null;

    /// <summary>What it is said to be; editable for a file being attached.</summary>
    [ObservableProperty]
    public partial string Description { get; set; }

    /// <summary>"12 KB · Tài liệu", "3 KB · Trang 2", "5 KB · Sẽ đính kèm".</summary>
    public string Details
    {
        get
        {
            var size = Size is { } bytes ? ByteSizeFormatter.Format(bytes, CultureInfo.CurrentCulture) : "?";
            var place = IsNew
                ? Strings.Attachments_New
                : Info!.Place == AttachmentPlace.Page ? string.Format(CultureInfo.CurrentCulture, Strings.Attachments_OnPage, Info.Page + 1) : Strings.Attachments_OfDocument;
            return $"{size} · {place}";
        }
    }

    /// <summary>A PDF can be opened in PDF_Pro; other files are only saved (never run).</summary>
    public bool IsPdf => string.Equals(Path.GetExtension(Name), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static AttachmentItemViewModel Existing(AttachmentInfo info) => new(info, null, info.Name, info.Size);

    public static AttachmentItemViewModel Adding(string path) => new(null, path, TextNormalizer.ToNfc(Path.GetFileName(path)), new FileInfo(path).Length);
}

/// <summary>
/// "Tệp đính kèm" (extract.attachments): the files attached to the document — to its list or to a page — saved, a PDF
/// opened in PDF_Pro (other files are never run from here), files attached and attachments removed. Changes are made
/// together as one revision of the document (one undoable step) when confirmed.
/// </summary>
public sealed partial class AttachmentsViewModel : ObservableObject
{
    private readonly string _input;
    private readonly IFileDialogService _files;
    private readonly ILogger _logger;
    private readonly List<string> _removed = [];

    /// <param name="fileName">The document's file name.</param>
    /// <param name="input">The document as it is shown (its own file, or a copy).</param>
    /// <param name="canChange">Attachments may be added and removed (not for a read-only or protected document).</param>
    /// <param name="openFolder">Where a PDF attachment is written to be opened.</param>
    public AttachmentsViewModel(string fileName, string input, bool canChange, string openFolder, IFileDialogService files, ILogger logger)
    {
        FileName = fileName;
        _input = input;
        CanChange = canChange;
        OpenFolder = openFolder;
        _files = files;
        _logger = logger;
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public bool CanChange { get; }

    /// <summary>Where a PDF attachment is written before it opens.</summary>
    public string OpenFolder { get; }

    public ObservableCollection<AttachmentItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(OpenCommand), nameof(RemoveCommand))]
    public partial AttachmentItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>"Tài liệu không có tệp đính kèm."; null when there are some.</summary>
    [ObservableProperty]
    public partial string? EmptyText { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Something was added or removed: "Áp dụng" writes it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool HasChanges { get; private set; }

    /// <summary>The changes confirmed, as attachments.edit parameters.</summary>
    public EditAttachmentsParams? Result { get; private set; }

    /// <summary>A PDF attachment written to open in PDF_Pro once the window has closed.</summary>
    public string? OpenAfterClose { get; private set; }

    /// <summary>Lists the attachments of the document (in the background).</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var listed = await Task.Run(() => Attachments.List(_input));
            Items.Clear();
            foreach (var info in listed)
            {
                Items.Add(AttachmentItemViewModel.Existing(info));
            }

            Selected = Items.FirstOrDefault();
            UpdateEmpty();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The attachments could not be listed: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Attachments_Error_Read;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>"Lưu…": the file written where the user says.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SaveAsync()
    {
        if (Selected is not { } item || _files.PickAttachmentSavePath(item.Name) is not { } path)
        {
            return;
        }

        if (await WriteAsync(item, path))
        {
            StatusText = string.Format(CultureInfo.CurrentCulture, Strings.Attachments_Saved, Path.GetFileName(path));
        }
    }

    private bool HasSelection() => Selected is not null;

    /// <summary>"Mở": a PDF attachment written to a folder of its own and opened in PDF_Pro when the window closes.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (Selected is not { IsPdf: true } item)
        {
            return;
        }

        var path = Path.Combine(OpenFolder, Guid.NewGuid().ToString("N"), Core.Splitting.FileNames.Sanitize(Path.GetFileNameWithoutExtension(item.Name)) + ".pdf");
        if (await WriteAsync(item, path))
        {
            OpenAfterClose = path;
            CloseRequested?.Invoke(this, false);
        }
    }

    private bool CanOpen() => Selected is { IsPdf: true };

    /// <summary>"Đính kèm tệp…": files chosen to be attached (when applied).</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private void Add()
    {
        foreach (var path in _files.PickAttachmentFiles(Strings.Attachments_Add_Title))
        {
            var item = AttachmentItemViewModel.Adding(path);
            if (item.Size > EditAttachmentsOperation.MaxFileBytes)
            {
                ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.Attachments_Error_TooLarge, item.Name);
                continue;
            }

            // A file of the same name replaces the one there (as attachments.edit does).
            foreach (var same in Items.Where(i => string.Equals(i.Name, item.Name, StringComparison.Ordinal) && (i.IsNew || i.Info!.Place == AttachmentPlace.Document)).ToList())
            {
                Drop(same);
            }

            Items.Add(item);
            Selected = item;
        }

        Changed();
    }

    /// <summary>"Xóa": the attachment taken out (when applied).</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (Selected is not { } item)
        {
            return;
        }

        var index = Items.IndexOf(item);
        Drop(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        Changed();
    }

    private bool CanRemove() => CanChange && Selected is not null;

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void Apply()
    {
        var added = Items.Where(i => i.IsNew).Select(i => new AttachmentToAdd(i.NewPath!, string.IsNullOrWhiteSpace(i.Description) ? null : i.Description.Trim())).ToList();
        Result = new EditAttachmentsParams(added.Count == 0 ? null : added, _removed.Count == 0 ? null : [.. _removed]);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, false);

    private void Drop(AttachmentItemViewModel item)
    {
        Items.Remove(item);
        if (item.Info is { } info)
        {
            _removed.Add(info.Key);
        }
    }

    private void Changed()
    {
        HasChanges = _removed.Count > 0 || Items.Any(i => i.IsNew);
        UpdateEmpty();
    }

    private void UpdateEmpty() => EmptyText = Items.Count == 0 ? Strings.Attachments_None : null;

    private async Task<bool> WriteAsync(AttachmentItemViewModel item, string path)
    {
        try
        {
            var data = item.NewPath is { } source ? await File.ReadAllBytesAsync(source) : await Task.Run(() => Attachments.Read(_input, item.Info!.Key));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllBytesAsync(path, data);
            ErrorText = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("An attachment could not be written: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Attachments_Error_Write;
            return false;
        }
    }
}
