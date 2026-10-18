namespace PdfPro.App.ViewModels;

/// <summary>
/// An item of a virtualized list whose expensive content (an image) is only loaded while an element shows it.
/// Calls are idempotent: the view may realize an item it already realized, or release one it already released.
/// </summary>
public interface IRealizable
{
    /// <param name="dpiScale">Device pixels per view unit where the item is shown.</param>
    void Realize(double dpiScale);

    void Release();
}
