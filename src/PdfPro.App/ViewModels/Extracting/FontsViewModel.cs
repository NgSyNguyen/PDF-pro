using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Extracting;

namespace PdfPro.App.ViewModels.Extracting;

/// <summary>A font as listed: its name, type, whether it is embedded, its encoding and pages.</summary>
public sealed record FontItem(FontEntry Font)
{
    public string Name => Font.Name;

    public string Type => Font.Type;

    /// <summary>"Nhúng đầy đủ", "Nhúng một phần", "Chưa nhúng" (with "phông chuẩn" for the 14 standard ones).</summary>
    public string EmbeddingText => Font.Embedding switch
    {
        FontEmbedding.Embedded => Strings.Fonts_Embedded,
        FontEmbedding.Subset => Strings.Fonts_Subset,
        _ when Font.IsStandard => Strings.Fonts_NotEmbeddedStandard,
        _ => Strings.Fonts_NotEmbedded,
    };

    public bool IsNotEmbedded => Font.Embedding == FontEmbedding.NotEmbedded;

    public string Encoding => Font.Encoding ?? "—";

    /// <summary>"1-3, 5".</summary>
    public string PagesText => PageRanges.Format([.. Font.Pages.Select(p => p - 1)]);

    /// <summary>"Không" when its text cannot be copied or searched reliably.</summary>
    public string UnicodeText => Font.HasUnicode ? Strings.Fonts_Yes : Strings.Fonts_No;
}

/// <summary>
/// "Phông chữ" (extract.fonts): the fonts of the document — those of its pages, forms, comments and Type 3 glyphs — with
/// whether each is embedded whole, as a subset, or not at all (a risk when printing or sending the file), its type,
/// encoding and pages; fonts not embedded first.
/// </summary>
public sealed partial class FontsViewModel(string fileName, string input, ILogger logger) : ObservableObject
{
    public event EventHandler<bool>? CloseRequested;

    public string FileName => fileName;

    [ObservableProperty]
    public partial IReadOnlyList<FontItem> Fonts { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>"8 phông: 2 nhúng đầy đủ, 5 nhúng một phần, 1 chưa nhúng.".</summary>
    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    /// <summary>The advice shown when a font is not embedded.</summary>
    [ObservableProperty]
    public partial bool HasNotEmbedded { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>Lists the fonts (in the background).</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            var fonts = await Task.Run(() => FontInventory.List(input, cancellationToken: cancellationToken), cancellationToken);
            Fonts = [.. fonts.Select(f => new FontItem(f))];
            HasNotEmbedded = fonts.Any(f => f.Embedding == FontEmbedding.NotEmbedded);
            SummaryText = fonts.Count == 0
                ? Strings.Fonts_None
                : string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Fonts_Summary,
                    fonts.Count,
                    fonts.Count(f => f.Embedding == FontEmbedding.Embedded),
                    fonts.Count(f => f.Embedding == FontEmbedding.Subset),
                    fonts.Count(f => f.Embedding == FontEmbedding.NotEmbedded));
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The fonts could not be listed: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Fonts_Error_Read;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, false);
}
