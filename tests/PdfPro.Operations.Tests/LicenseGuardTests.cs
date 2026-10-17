using System.Xml.Linq;

namespace PdfPro.Operations.Tests;

/// <summary>
/// AGENTS.md forbids AGPL/GPL and commercial libraries. These tests read Directory.Packages.props so a forbidden
/// package or version fails the build in CI before it can ship.
/// </summary>
public sealed class LicenseGuardTests
{
    private static readonly string[] ForbiddenPackagePrefixes =
    [
        "itext", "itext7", "itextsharp", "Ghostscript", "MuPDF", "Syncfusion", "Aspose", "Apryse", "PDFTron", "Foxit",
    ];

    private static readonly Dictionary<string, string> Versions = LoadCentralVersions();

    [Fact]
    public void PdfSharp_StaysOnTheLastMitRelease()
    {
        // 6.5.0 and later are AGPL-3.0-only.
        Versions.TryGetValue("PDFsharp", out var version).ShouldBeTrue("PDFsharp must be declared centrally");
        var parsed = Version.Parse(version!.Split('-')[0]);
        parsed.ShouldBeLessThan(new Version(6, 5), "PDFsharp 6.5+ is AGPL; stay on 6.2.x");
        parsed.Major.ShouldBe(6);
        parsed.Minor.ShouldBe(2);
    }

    [Fact]
    public void FluentAssertions_IsNotUsedFromVersion8()
    {
        if (Versions.TryGetValue("FluentAssertions", out var version))
        {
            Version.Parse(version.Split('-')[0]).Major.ShouldBeLessThan(8, "FluentAssertions 8+ is commercial");
        }
    }

    [Fact]
    public void NoForbiddenPackages()
        => Versions.Keys.ShouldNotContain(id => ForbiddenPackagePrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    private static Dictionary<string, string> LoadCentralVersions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Directory.Packages.props not found above the test folder.");
        }

        return XDocument.Load(Path.Combine(directory.FullName, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!, StringComparer.OrdinalIgnoreCase);
    }
}
