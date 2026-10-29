using PdfPro.App.ViewModels.Security;

namespace PdfPro.App.ViewModels;

/// <summary>"Ký số" of a document tab: placing a visible signature on a page.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>The box of a visible signature, placed with the viewer's mouse.</summary>
    public SignaturePlacementViewModel SignaturePlacement { get; }

    /// <summary>
    /// Lets the user drag the box of a visible signature on a page (or click for one of the usual size). The viewer's
    /// mouse is taken like by any mode: the page editor closes, the organizer gives way and other tools turn off. Null
    /// when the user gave up (Esc, another mode, the tab closing).
    /// </summary>
    public Task<SignatureBox?> PlaceSignatureAsync()
    {
        if (Editor.IsActive)
        {
            Editor.IsActive = false;
        }

        IsOrganizing = false;
        Annotations.StopTool();
        ContentEditor.IsActive = false;
        FormDesigner.IsActive = false;
        Redaction.IsActive = false;
        TextSelection.Clear();
        return SignaturePlacement.PlaceAsync();
    }

    private void OnSignaturePlacementPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SignaturePlacementViewModel.IsActive))
        {
            UpdateFormsActive();
        }
    }
}
