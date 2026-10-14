using PdfPro.Infrastructure.Settings;

namespace PdfPro.Infrastructure.Tests;

public sealed class ArchitectureTests
{
    private static readonly string[] UiAssemblies = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms"];

    [Fact]
    public void Infrastructure_DoesNotReferenceUiFrameworks()
    {
        var references = typeof(AppSettings).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        references.ShouldNotContain(name => UiAssemblies.Contains(name));
    }

    [Fact]
    public void Infrastructure_DependsOnlyOnCoreAmongProjectAssemblies()
    {
        var projectReferences = typeof(AppSettings).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("PdfPro.", StringComparison.Ordinal));

        projectReferences.ShouldAllBe(name => name == "PdfPro.Core");
    }
}
