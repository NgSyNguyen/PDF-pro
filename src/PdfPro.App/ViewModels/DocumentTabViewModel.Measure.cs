using CommunityToolkit.Mvvm.Input;
using PdfPro.App.ViewModels.Viewer;

namespace PdfPro.App.ViewModels;

/// <summary>"Đo" (distances, lengths and areas) of a document tab.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>"Đo": the measuring tool of the viewer.</summary>
    public MeasureViewModel Measure { get; }

    /// <summary>The measuring tool is on (the ribbon button shows it pressed).</summary>
    public bool IsMeasuring => Measure.IsActive;

    /// <summary>
    /// "Đo": turns the measuring tool on or off. It takes the viewer's mouse like the other modes, which turn off; any
    /// of them turning on turns it off (<see cref="UpdateFormsActive"/>). Works on documents only read too.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleMeasure))]
    private void ToggleMeasure()
    {
        if (Measure.IsActive)
        {
            Measure.IsActive = false;
            return;
        }

        if (Editor.IsActive)
        {
            Editor.IsActive = false;
        }

        IsOrganizing = false;
        Annotations.StopTool();
        ContentEditor.IsActive = false;
        FormDesigner.IsActive = false;
        Redaction.IsActive = false;
        SignaturePlacement.Cancel();
        TextSelection.Clear();
        Measure.IsActive = true;
    }

    private bool CanToggleMeasure() => Measure.IsActive || Viewer.PageCount > 0;

    private void OnMeasurePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MeasureViewModel.IsActive))
        {
            UpdateFormsActive();
            OnPropertyChanged(nameof(IsMeasuring));
        }
    }
}
