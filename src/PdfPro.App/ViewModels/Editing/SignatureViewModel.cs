using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Editing;
using PdfPro.Core.Overlay;

namespace PdfPro.App.ViewModels.Editing;

/// <summary>How the signature is made.</summary>
public enum SignatureMode
{
    Draw,
    Type,
    Picture,
}

/// <summary>A signature of the library, with its picture.</summary>
public sealed record SavedSignatureViewModel(string Id, BitmapSource Picture);

/// <summary>
/// "Chữ ký tay": a signature drawn with the mouse or a pen (strokes smoothed), a name written in a handwriting font, or
/// a photo of a signature on paper (the paper made transparent) — or one saved earlier. It is a picture, NOT a digital
/// signature (Phase 8). The chosen signature is kept, encrypted for the Windows account, when asked. The result is a
/// PNG with a transparent background.
/// </summary>
public sealed partial class SignatureViewModel : ObservableObject
{
    private readonly SignatureLibrary? _library;
    private readonly IFileDialogService _files;
    private readonly ILogger _logger;
    private byte[]? _pictureFile;
    private byte[]? _current;

    public SignatureViewModel(SignatureLibrary? library, IFileDialogService files, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(files);
        _library = library;
        _files = files;
        _logger = logger;
        Strokes.StrokesChanged += (_, _) => Refresh();
        InkAttributes = Attributes();
        foreach (var entry in library?.List() ?? [])
        {
            if (library!.Load(entry.Id) is { } png)
            {
                Saved.Add(new SavedSignatureViewModel(entry.Id, SignatureRenderer.Decode(png)));
            }
        }

        Refresh();
    }

    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<string> Fonts => SignatureRenderer.HandwritingFonts;

    /// <summary>Ink colours: black, blue, red.</summary>
    public IReadOnlyList<ColorSwatch> InkColors { get; } = [new("#000000"), new("#1D4ED8"), new("#DC2626")];

    /// <summary>The strokes drawn so far (the ink canvas draws into it).</summary>
    public StrokeCollection Strokes { get; } = [];

    /// <summary>Signatures saved earlier, newest first.</summary>
    public ObservableCollection<SavedSignatureViewModel> Saved { get; } = [];

    public bool HasSaved => Saved.Count > 0;

    [ObservableProperty]
    public partial SignatureMode Mode { get; set; }

    [ObservableProperty]
    public partial DrawingAttributes InkAttributes { get; private set; }

    [ObservableProperty]
    public partial string InkColor { get; set; } = "#1D4ED8";

    /// <summary>Pen width in device-independent pixels.</summary>
    [ObservableProperty]
    public partial double InkWidth { get; set; } = 2.5;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Font { get; set; } = SignatureRenderer.HandwritingFonts[0];

    /// <summary>Paper lighter than this (0–255) becomes transparent.</summary>
    [ObservableProperty]
    public partial double PaperThreshold { get; set; } = 225;

    /// <summary>Keep the new signature in the library.</summary>
    [ObservableProperty]
    public partial bool SaveToLibrary { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseCommand))]
    public partial BitmapSource? Preview { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>The signature chosen: a PNG with a transparent background.</summary>
    public byte[]? Result { get; private set; }

    partial void OnModeChanged(SignatureMode value) => Refresh();

    partial void OnInkColorChanged(string value)
    {
        InkAttributes = Attributes();
        foreach (var stroke in Strokes)
        {
            stroke.DrawingAttributes = InkAttributes.Clone();
        }

        Refresh();
    }

    partial void OnInkWidthChanged(double value)
    {
        InkAttributes = Attributes();
        Refresh();
    }

    partial void OnNameChanged(string value) => Refresh();

    partial void OnFontChanged(string value) => Refresh();

    partial void OnPaperThresholdChanged(double value) => Refresh();

    [RelayCommand]
    private void SetInkColor(string hex) => InkColor = hex;

    [RelayCommand]
    private void Clear()
    {
        Strokes.Clear();
        Name = string.Empty;
        _pictureFile = null;
        Refresh();
    }

    [RelayCommand]
    private void PickPicture()
    {
        if (_files.PickImageFile(Strings.Signature_PickPicture_Title) is not { } path)
        {
            return;
        }

        try
        {
            _pictureFile = File.ReadAllBytes(path);
            Mode = SignatureMode.Picture;
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A signature picture could not be read: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Signature_Error_Picture;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Use()
    {
        if (_current is null)
        {
            return;
        }

        if (SaveToLibrary && _library is not null)
        {
            try
            {
                _library.Save(_current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // The signature is still used; only keeping it failed.
                _logger.LogWarning("A signature could not be saved to the library: {ErrorType}", ex.GetType().Name);
            }
        }

        Result = _current;
        CloseRequested?.Invoke(this, true);
    }

    private bool CanUse() => Preview is not null;

    /// <summary>Uses a saved signature as it is.</summary>
    [RelayCommand]
    private void UseSaved(SavedSignatureViewModel? signature)
    {
        if (signature is not null && _library?.Load(signature.Id) is { } png)
        {
            Result = png;
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void DeleteSaved(SavedSignatureViewModel? signature)
    {
        if (signature is null)
        {
            return;
        }

        _library?.Delete(signature.Id);
        Saved.Remove(signature);
        OnPropertyChanged(nameof(HasSaved));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private DrawingAttributes Attributes() => new()
    {
        Color = RgbColor.TryParse(InkColor, out var color) ? Color.FromRgb(color.Value.R, color.Value.G, color.Value.B) : Colors.Black,
        Width = InkWidth,
        Height = InkWidth,
        FitToCurve = true,
        IgnorePressure = false,
    };

    /// <summary>Makes the signature of the current mode and its preview.</summary>
    private void Refresh()
    {
        ErrorText = null;
        try
        {
            _current = Mode switch
            {
                SignatureMode.Draw => SignatureRenderer.FromStrokes(Strokes),
                SignatureMode.Type => SignatureRenderer.FromText(Name, Font, InkAttributes.Color),
                _ => _pictureFile is null ? null : SignatureRenderer.FromPicture(_pictureFile, (byte)Math.Clamp(PaperThreshold, 100, 254)),
            };
        }
        catch (NotSupportedException)
        {
            _current = null;
            ErrorText = Strings.Signature_Error_Picture;
        }

        Preview = _current is null ? null : SignatureRenderer.Decode(_current);
    }
}
