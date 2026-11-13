using System.Text.RegularExpressions;
using PdfPro.App.Tests.TestSupport;

namespace PdfPro.App.Tests.Views;

/// <summary>
/// A <c>{StaticResource Key}</c> whose key is defined nowhere only fails when its view is loaded — the watermark dialog
/// crashed on opening for that reason (InverseBool). Every key the App's XAML uses must be defined in some XAML
/// (x:Key) or added by a code-behind (<c>Resources.Add("Key", …)</c>).
/// </summary>
public sealed partial class XamlResourceTests
{
    [Fact]
    public void EveryStaticResource_IsDefinedSomewhere()
    {
        var app = Path.Combine(Samples.Repository, "src", "PdfPro.App");
        var xaml = Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories).Where(InSources).ToList();
        var codeBehind = Directory.GetFiles(app, "*.xaml.cs", SearchOption.AllDirectories).Where(InSources);
        xaml.Count.ShouldBeGreaterThan(20, "the views are found");

        var defined = xaml.SelectMany(f => DefinedKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Concat(codeBehind.SelectMany(f => AddedKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)))
            .ToHashSet(StringComparer.Ordinal);

        var missing = xaml
            .SelectMany(f => UsedKey().Matches(File.ReadAllText(f)).Select(m => (Key: m.Groups[1].Value, File: Path.GetFileName(f))))
            .Where(use => !defined.Contains(use.Key))
            .Select(use => $"{use.File}: {use.Key}")
            .Distinct()
            .ToList();

        missing.ShouldBeEmpty("these resources are used but never defined");
    }

    /// <summary>
    /// A progress bar's Value binds two-way by default (RangeBase), which throws as the view loads when the property has
    /// no public setter — the batch window crashed on opening for that reason. Nobody drags a progress bar: always OneWay.
    /// </summary>
    [Fact]
    public void ProgressBars_BindTheirValueOneWay()
    {
        var app = Path.Combine(Samples.Repository, "src", "PdfPro.App");
        var twoWay = Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories).Where(InSources)
            .SelectMany(f => ProgressBar().Matches(File.ReadAllText(f))
                .Select(m => BoundValue().Match(m.Value))
                .Where(v => v.Success && !v.Groups[1].Value.Contains("Mode=OneWay", StringComparison.Ordinal))
                .Select(v => $"{Path.GetFileName(f)}: {v.Value}"))
            .ToList();

        twoWay.ShouldBeEmpty("a progress bar's Value must be bound with Mode=OneWay");
    }

    [GeneratedRegex(@"<(?:ui:)?ProgressBar\b[^>]*>", RegexOptions.Singleline, 1000)]
    private static partial Regex ProgressBar();

    [GeneratedRegex(@"\bValue=""\{Binding ([^}]*)\}""", RegexOptions.None, 1000)]
    private static partial Regex BoundValue();

    private static bool InSources(string path)
        => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [GeneratedRegex("x:Key=\"([^\"]+)\"", RegexOptions.None, 1000)]
    private static partial Regex DefinedKey();

    [GeneratedRegex(@"Resources\.Add\(\s*""([^""]+)""", RegexOptions.None, 1000)]
    private static partial Regex AddedKey();

    /// <summary>Simple keys only: <c>{x:Static …}</c> keys and dotted names are not looked up here.</summary>
    [GeneratedRegex(@"\{StaticResource\s+([A-Za-z0-9_]+)\s*\}", RegexOptions.None, 1000)]
    private static partial Regex UsedKey();
}
