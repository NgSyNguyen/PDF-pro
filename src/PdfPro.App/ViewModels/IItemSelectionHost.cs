namespace PdfPro.App.ViewModels;

/// <summary>Items a view model wants selected, and the one to scroll to and focus.</summary>
/// <param name="Items">The items to select (all others are deselected).</param>
/// <param name="FocusItem">The item to bring into view and focus, or null.</param>
public sealed record ItemSelectionRequest(IReadOnlyList<object> Items, object? FocusItem);

/// <summary>
/// A view model whose multiple selection a list shows and edits: the list reports what the user selects, and the view
/// model asks the list to select items itself (after an edit, from a page range box).
/// </summary>
public interface IItemSelectionHost
{
    /// <summary>Raised when the view model selects items itself.</summary>
    event EventHandler<ItemSelectionRequest>? SelectionRequested;

    /// <summary>The selection a list shows when it starts showing this view model (switching tabs, new items).</summary>
    ItemSelectionRequest CurrentSelection { get; }

    /// <summary>The user changed the selection in the list.</summary>
    void OnViewSelectionChanged(IReadOnlyList<object> selectedItems);
}
