using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.Core.ExternalTools;
using PdfPro.Operations.Converting;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// veraPDF, when installed: Java run with veraPDF's jars and command-line class (no launcher script, no shell), the
/// level asked, its XML report read. veraPDF itself is played by the tool runner.
/// </summary>
public sealed class VeraPdfTests
{
    private const string Folder = @"C:\Users\lan\verapdf";
    private const string Java = @"C:\Program Files\Java\jdk-21\bin\java.exe";

    private const string NotCompliant = """
        <?xml version="1.0" encoding="utf-8"?>
        <report>
          <buildInformation><releaseDetails id="core" version="1.30.1" buildDate="2026-10-01T00:00:00Z"/></buildInformation>
          <jobs>
            <job>
              <item size="1234"><name>C:\docs\tờ trình.pdf</name></item>
              <validationReport jobEndStatus="normal" profileName="PDF/A-2B validation profile" statement="PDF file is not compliant with Validation Profile requirements." isCompliant="false">
                <details passedRules="140" failedRules="2" passedChecks="2000" failedChecks="3">
                  <rule specification="ISO 19005-2:2011" clause="6.2.11.4.1" testNumber="1" status="failed" failedChecks="2" tags="font">
                    <description>The font programs for all fonts used for rendering within a conforming file shall be embedded within that file, as defined in ISO 32000-1:2008, 9.9</description>
                    <object>PDFont</object>
                    <test>fontFile_size == 1</test>
                    <check status="failed"><context>root/document[0]/pages[0]/contentStream[0]/operators[3]/font[0](Helvetica)</context><errorMessage>The font program is not embedded</errorMessage></check>
                  </rule>
                  <rule specification="ISO 19005-2:2011" clause="6.6.2.1" testNumber="1" status="failed" failedChecks="1">
                    <description>The Catalog dictionary of a conforming file shall contain the Metadata key</description>
                    <object>CosDocument</object>
                    <test>containsMetadata == true</test>
                  </rule>
                  <rule specification="ISO 19005-2:2011" clause="6.1.2" testNumber="1" status="passed" passedChecks="1">
                    <description>The file header shall begin at byte zero</description>
                  </rule>
                </details>
              </validationReport>
              <duration start="1" finish="2">00:00:00.512</duration>
            </job>
          </jobs>
        </report>
        """;

    private const string Compliant = """
        <report><jobs><job><validationReport jobEndStatus="normal" profileName="PDF/A-1B validation profile" isCompliant="true">
        <details passedRules="120" failedRules="0" passedChecks="900" failedChecks="0"/></validationReport></job></jobs></report>
        """;

    private readonly IExternalToolRunner _runner = Substitute.For<IExternalToolRunner>();
    private readonly IVeraPdfLocator _locator = Substitute.For<IVeraPdfLocator>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void AReport_GivesTheVerdict_AndTheRulesFailed()
    {
        var validation = VeraPdfValidator.Parse("veraPDF says hello\n" + NotCompliant).ShouldNotBeNull();

        validation.IsCompliant.ShouldBeFalse();
        validation.Profile.ShouldBe("PDF/A-2B validation profile");
        validation.Failures.ShouldBe(
        [
            new PdfARuleFailure("ISO 19005-2:2011", "6.2.11.4.1", 1, "The font programs for all fonts used for rendering within a conforming file shall be embedded within that file, as defined in ISO 32000-1:2008, 9.9", 2),
            new PdfARuleFailure("ISO 19005-2:2011", "6.6.2.1", 1, "The Catalog dictionary of a conforming file shall contain the Metadata key", 1),
        ]);
        VeraPdfValidator.Parse(Compliant).ShouldBe(new PdfAValidation(true, "PDF/A-1B validation profile", []), new ValidationComparer());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Exception in thread main")]
    [InlineData("<report><jobs/></report>")]
    [InlineData("<report><unclosed>")]
    [InlineData("<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><r>&x;</r>")]
    public void SomethingElse_IsNoReport(string output) => VeraPdfValidator.Parse(output).ShouldBeNull();

    [Theory]
    [InlineData(PdfALevel.A2B, "2b")]
    [InlineData(PdfALevel.A1B, "1b")]
    public async Task VeraPdf_RunsOnJava_WithItsJars_TheLevelAsked_AndAnXmlReport(PdfALevel level, string flavour)
    {
        _locator.Find(@"D:\veraPDF").Returns(new VeraPdfInstallation(Folder, Java));
        ExternalToolRequest? ran = null;
        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            ran = call.Arg<ExternalToolRequest>();
            return new ExternalToolResult("java", 1, NotCompliant, string.Empty, TimeSpan.Zero, false);
        });

        var validation = await new VeraPdfValidator(_runner, _locator).ValidateAsync(@"C:\docs\tờ trình.pdf", level, @"D:\veraPDF", Token);

        validation.Failures.Count.ShouldBe(2, "exit code 1 is a file that is not compliant, still a report");
        ran.ShouldNotBeNull().FileName.ShouldBe(Java);
        var arguments = ran.Arguments.ToList();
        arguments[arguments.IndexOf("-classpath") + 1].ShouldBe(Path.Combine(Folder, "etc") + Path.PathSeparator + Path.Combine(Folder, "bin", "*"));
        arguments.ShouldContain("org.verapdf.apps.GreenfieldCliWrapper");
        arguments[arguments.IndexOf("--flavour") + 1].ShouldBe(flavour);
        arguments[arguments.IndexOf("--format") + 1].ShouldBe("xml");
        arguments[^1].ShouldBe(@"C:\docs\tờ trình.pdf");
        ran.WorkingDirectory.ShouldBe(Folder);
    }

    [Fact]
    public async Task WhatIsMissing_IsSaid()
    {
        var validator = new VeraPdfValidator(_runner, _locator);
        _locator.Find(Arg.Any<string?>()).Returns((VeraPdfInstallation?)null);
        (await Should.ThrowAsync<VeraPdfNotFoundException>(() => validator.ValidateAsync("a.pdf", PdfALevel.A2B, null, Token))).Missing.ShouldBe(VeraPdfMissing.Program);

        _locator.Find(Arg.Any<string?>()).Returns(new VeraPdfInstallation(Folder, null));
        (await Should.ThrowAsync<VeraPdfNotFoundException>(() => validator.ValidateAsync("a.pdf", PdfALevel.A2B, null, Token))).Missing.ShouldBe(VeraPdfMissing.Java);

        _locator.Find(Arg.Any<string?>()).Returns(new VeraPdfInstallation(Folder, Java));
        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ExternalToolNotFoundException("java"));
        (await Should.ThrowAsync<VeraPdfNotFoundException>(() => validator.ValidateAsync("a.pdf", PdfALevel.A2B, null, Token))).Missing.ShouldBe(VeraPdfMissing.Java);

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ExternalToolTimeoutException("java", TimeSpan.FromMinutes(10), string.Empty));
        await Should.ThrowAsync<VeraPdfFailedException>(() => validator.ValidateAsync("a.pdf", PdfALevel.A2B, null, Token));

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(new ExternalToolResult("java", 7, string.Empty, "Error: could not find main class", TimeSpan.Zero, false));
        await Should.ThrowAsync<VeraPdfFailedException>(() => validator.ValidateAsync("a.pdf", PdfALevel.A2B, null, Token));
    }

    [Theory]
    [InlineData(null, new[] { @"C:\Users\lan\verapdf\bin" }, @"C:\Users\lan\verapdf")]
    [InlineData(null, new[] { @"C:\Program Files\veraPDF\bin" }, @"C:\Program Files\veraPDF")]
    [InlineData(@"D:\Tools\veraPDF\verapdf.bat", new[] { @"D:\Tools\veraPDF\bin" }, @"D:\Tools\veraPDF")]
    [InlineData(@"D:\Tools\veraPDF\bin", new[] { @"D:\Tools\veraPDF\bin" }, @"D:\Tools\veraPDF")]
    [InlineData(@"D:\Tools\veraPDF", new[] { @"D:\Tools\veraPDF\bin" }, @"D:\Tools\veraPDF")]
    [InlineData(@"D:\Khác", new[] { @"C:\Users\lan\verapdf\bin" }, null)]
    public void OnWindows_VeraPdf_IsFoundWhereItsInstallerPutsIt(string? preferred, string[] withJars, string? expected)
    {
        var jars = withJars.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var locator = new VeraPdfLocator(
            path => path.Replace('/', '\\') == Java,
            folder => jars.Contains(folder.Replace('/', '\\')),
            variable => variable switch { "USERPROFILE" => @"C:\Users\lan", "ProgramFiles" => @"C:\Program Files", "JAVA_HOME" => @"C:\Program Files\Java\jdk-21", _ => null },
            windows: true);

        var found = locator.Find(preferred);

        found?.Folder.Replace('/', '\\').ShouldBe(expected);
        if (expected is not null)
        {
            found!.Java!.Replace('/', '\\').ShouldBe(Java);
        }
    }

    [Fact]
    public async Task AnInstalledVeraPdf_ValidatesAConvertedFile()
    {
        Assert.SkipUnless(new VeraPdfLocator().Find() is { Java: not null }, "veraPDF and Java are not installed");
        using var temp = new TestSupport.TempDirectory();
        var output = temp.Combine("pdfa.pdf");
        using var gate = new PdfPro.Pdfium.PdfiumGate(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfPro.Pdfium.PdfiumGate>.Instance);
        await new ToPdfAOperation(gate).ConvertAsync(TestSupport.Samples.VanBanVi, null, output, new ToPdfAParams(), Token);
        var runner = new PdfPro.Infrastructure.ExternalTools.ExternalToolRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfPro.Infrastructure.ExternalTools.ExternalToolRunner>.Instance);

        var validation = await new VeraPdfValidator(runner, new VeraPdfLocator()).ValidateAsync(output, PdfALevel.A2B, null, Token);

        validation.Failures.ShouldBeEmpty();
        validation.IsCompliant.ShouldBeTrue();
    }

    private sealed class ValidationComparer : IEqualityComparer<PdfAValidation>
    {
        public bool Equals(PdfAValidation? x, PdfAValidation? y)
            => x is not null && y is not null && x.IsCompliant == y.IsCompliant && x.Profile == y.Profile && x.Failures.SequenceEqual(y.Failures);

        public int GetHashCode(PdfAValidation obj) => obj.Profile.GetHashCode(StringComparison.Ordinal);
    }
}
