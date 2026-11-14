using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Ocr;
using PdfPro.Operations.Ocr;

namespace PdfPro.App.ViewModels.Ocr;

/// <summary>What "Nhận dạng chữ (OCR)" was confirmed with.</summary>
/// <param name="Parameters">The run.</param>
/// <param name="SaveText">Also save the recognised text as a .txt file afterwards.</param>
/// <param name="SaveHocr">Also save the pages as one hOCR file afterwards.</param>
public sealed record OcrDialogResult(OcrParams Parameters, bool SaveText, bool SaveHocr);

/// <summary>A language the engine has data for, ticked or not.</summary>
public sealed partial class OcrLanguageOption(string code, string label, bool isChecked) : ObservableObject
{
    public string Code { get; } = code;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    public override string ToString() => Label;
}

/// <summary>
/// "Nhận dạng chữ (OCR)": the languages (those the engine has data for; the main one first), the pages, what to do with
/// pages that already have text, turning and straightening, how many pages are read at once, and what else to save.
/// </summary>
public sealed partial class OcrViewModel : ObservableObject
{
    /// <summary>The order languages are offered in: Vietnamese, English, then the others by code.</summary>
    private static readonly string[] Known = ["vie", "eng"];

    /// <summary>The languages in the order they were ticked (the first one is the main language).</summary>
    private readonly List<string> _order;

    /// <param name="fileName">The document's file name.</param>
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="selected">Pages selected in the organizer, if any (0-based).</param>
    /// <param name="engine">The installed engine.</param>
    /// <param name="languages">The languages of the last run (ticked when the engine has them).</param>
    /// <param name="parallelism">Pages read at once last time; 0 for the default.</param>
    public OcrViewModel(string fileName, int pageCount, IReadOnlyList<int> selected, OcrEngineInfo engine, IReadOnlyList<string> languages, int parallelism)
    {
        ArgumentNullException.ThrowIfNull(engine);
        FileName = fileName;
        Target = new PageTargetViewModel(pageCount, selected);
        Target.Changed += (_, _) => Check();
        var ticked = languages.Where(engine.Languages.Contains).ToList();
        if (ticked.Count == 0)
        {
            ticked.Add(engine.Languages.Contains("vie") ? "vie" : engine.Languages[0]);
        }

        Languages = new ObservableCollection<OcrLanguageOption>(engine.Languages
            .OrderBy(code => Array.IndexOf(Known, code) is var at && at >= 0 ? at : Known.Length)
            .ThenBy(code => code, StringComparer.Ordinal)
            .Select(code => new OcrLanguageOption(code, LanguageName(code), ticked.Contains(code))));
        _order = [.. ticked];
        foreach (var option in Languages)
        {
            option.PropertyChanged += (_, _) => OnLanguageTicked(option);
        }

        MaxParallelism = Math.Max(1, Environment.ProcessorCount);
        Parallelism = Math.Clamp(parallelism > 0 ? parallelism : Math.Max(1, Environment.ProcessorCount - 1), 1, MaxParallelism);
        CanAutoRotate = engine.DetectsOrientation;
        AutoRotate = engine.DetectsOrientation;
        EngineText = string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Engine, engine.Name, engine.Data switch
        {
            OcrDataKind.Best => Strings.Ocr_Data_Best,
            OcrDataKind.Fast => Strings.Ocr_Data_Fast,
            _ => Strings.Ocr_Data_Own,
        });
        Check();
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    /// <summary>"Nhận dạng bằng Tesseract (dữ liệu chính xác nhất)".</summary>
    public string EngineText { get; }

    public ObservableCollection<OcrLanguageOption> Languages { get; }

    public PageTargetViewModel Target { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkip), nameof(IsReplace), nameof(IsAll))]
    public partial ExistingTextMode ExistingText { get; set; } = ExistingTextMode.Skip;

    public bool IsSkip
    {
        get => ExistingText == ExistingTextMode.Skip;
        set => Choose(ExistingTextMode.Skip, value);
    }

    public bool IsReplace
    {
        get => ExistingText == ExistingTextMode.Replace;
        set => Choose(ExistingTextMode.Replace, value);
    }

    public bool IsAll
    {
        get => ExistingText == ExistingTextMode.All;
        set => Choose(ExistingTextMode.All, value);
    }

    /// <summary>The engine can tell how pages are turned (it has the orientation data).</summary>
    public bool CanAutoRotate { get; }

    [ObservableProperty]
    public partial bool AutoRotate { get; set; }

    [ObservableProperty]
    public partial bool Deskew { get; set; } = true;

    [ObservableProperty]
    public partial bool StraightenPages { get; set; }

    public int MaxParallelism { get; }

    [ObservableProperty]
    public partial double? Parallelism { get; set; }

    [ObservableProperty]
    public partial bool SaveText { get; set; }

    [ObservableProperty]
    public partial bool SaveHocr { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; private set; }

    public OcrDialogResult? Result { get; private set; }

    /// <summary>The languages, the main one first, in the order they were ticked.</summary>
    public IReadOnlyList<string> ChosenLanguages => [.. _order.Where(code => Languages.Any(l => l.Code == code && l.IsChecked))];

    /// <summary>"Tiếng Việt", "Tiếng Anh", or the code itself.</summary>
    public static string LanguageName(string code)
        => Strings.ResourceManager.GetString("Ocr_Language_" + code, Strings.Culture) is { Length: > 0 } name ? name : code;

    /// <summary>The confirmed choice, or null with <paramref name="error"/>.</summary>
    public OcrDialogResult? Choice(out string? error)
    {
        error = null;
        var languages = ChosenLanguages;
        if (languages.Count == 0)
        {
            error = Strings.Ocr_Error_NoLanguage;
            return null;
        }

        if (Target.Pages(out var pageError) is null)
        {
            error = pageError ?? Strings.Stamp_Error_Pages;
            return null;
        }

        if (Parallelism is not { } workers || !double.IsFinite(workers) || workers < 1 || workers > MaxParallelism)
        {
            error = string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Error_Parallelism, MaxParallelism);
            return null;
        }

        var pages = Target.Indices.Count == Target.PageCount ? null : Target.Indices;
        var parameters = new OcrParams(
            languages,
            ExistingText,
            pages,
            (int)Math.Round(workers),
            AutoRotate: AutoRotate && CanAutoRotate,
            Deskew: Deskew,
            StraightenPages: StraightenPages,
            Hocr: SaveHocr);
        return new OcrDialogResult(parameters, SaveText, SaveHocr);
    }

    partial void OnParallelismChanged(double? value) => Check();

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Choice(out var error) is { } choice)
        {
            Result = choice;
            CloseRequested?.Invoke(this, true);
        }
        else
        {
            ErrorText = error;
        }
    }

    private bool CanApply() => ErrorText is null;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private void OnLanguageTicked(OcrLanguageOption option)
    {
        _order.Remove(option.Code);
        if (option.IsChecked)
        {
            _order.Add(option.Code);
        }

        Check();
    }

    private void Choose(ExistingTextMode mode, bool chosen)
    {
        if (chosen)
        {
            ExistingText = mode;
        }
    }

    private void Check() => ErrorText = Choice(out var error) is null ? error : null;
}
