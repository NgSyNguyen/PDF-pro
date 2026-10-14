using System.Reflection;

namespace PdfPro.Operations.Tests;

/// <summary>
/// Guards the one-way dependency rule: App/Cli → Operations/Ocr/Infrastructure → Pdfium → Core.
/// Only references that the compiler actually emitted are checked, i.e. real usages.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] UiAssemblies = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms"];

    public static TheoryData<string, string[]> AllowedProjectReferences => new()
    {
        { "PdfPro.Core", [] },
        { "PdfPro.Pdfium", ["PdfPro.Core"] },
        { "PdfPro.Operations", ["PdfPro.Core", "PdfPro.Pdfium"] },
        { "PdfPro.Ocr", ["PdfPro.Core", "PdfPro.Pdfium"] },
    };

    [Theory]
    [MemberData(nameof(AllowedProjectReferences))]
    public void Assembly_ReferencesOnlyLowerLayers(string assemblyName, string[] allowed)
    {
        var projectReferences = ReferencedNames(assemblyName).Where(n => n.StartsWith("PdfPro.", StringComparison.Ordinal));

        projectReferences.ShouldAllBe(name => allowed.Contains(name));
    }

    [Theory]
    [InlineData("PdfPro.Core")]
    [InlineData("PdfPro.Pdfium")]
    [InlineData("PdfPro.Operations")]
    [InlineData("PdfPro.Ocr")]
    public void Assembly_DoesNotReferenceUiFrameworks(string assemblyName)
        => ReferencedNames(assemblyName).ShouldNotContain(name => UiAssemblies.Contains(name));

    [Fact]
    public void Core_ReferencesOnlyTheBaseClassLibrary()
        => ReferencedNames("PdfPro.Core").ShouldAllBe(name => name.StartsWith("System.", StringComparison.Ordinal));

    private static IEnumerable<string> ReferencedNames(string assemblyName)
        => Assembly.Load(assemblyName).GetReferencedAssemblies().Select(a => a.Name!);
}
