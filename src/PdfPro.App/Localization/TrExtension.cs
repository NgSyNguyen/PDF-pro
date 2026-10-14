using System.Windows.Markup;
using PdfPro.App.Resources;

namespace PdfPro.App.Localization;

/// <summary>
/// Looks up a UI string in Strings.resx for the current UI culture: <c>Text="{loc:Tr Ribbon_Tab_File}"</c>.
/// A missing key renders as "[Key]" so it is visible during testing (a unit test also checks every key).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Get(Key);

    internal static string Get(string key) => Strings.ResourceManager.GetString(key, Strings.Culture) ?? $"[{key}]";
}
