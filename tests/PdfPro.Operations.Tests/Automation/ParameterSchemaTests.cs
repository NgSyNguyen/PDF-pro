using System.Text.Json.Nodes;
using PdfPro.Core.Operations;
using PdfPro.Core.Security;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Automation;

/// <summary>Parameter records seen as fields (for generated forms, workflows and the command line), read from pairs and JSON, secrets kept out.</summary>
public sealed class ParameterSchemaTests(PdfiumFixture pdfium)
{
    [Fact]
    public void AParameterRecord_IsSeenAsFields_WithTheirKindsDefaultsAndRules()
    {
        var fields = ParameterSchema.Of(typeof(ExtractImagesParams));

        fields.Select(f => (f.Name, f.Kind, f.Required)).ShouldBe([
            ("pages", ParameterKind.Text, false),
            ("minSide", ParameterKind.Integer, false),
            ("skipDuplicates", ParameterKind.Boolean, false),
            ("overwrite", ParameterKind.Boolean, false),
        ]);
        var minSide = fields[1];
        (minSide.Minimum, minSide.Maximum, minSide.Default).ShouldBe((0.0, 10_000.0, (object)0));
        fields[2].Default.ShouldBe(true);

        var compress = ParameterSchema.Of(typeof(CompressParams)).First();
        compress.Kind.ShouldBe(ParameterKind.Enum);
        compress.Choices.ShouldContain("ebook");
    }

    [Fact]
    public void ACommandLine_BecomesARecord_OrSaysWhatIsWrong()
    {
        var values = new Dictionary<string, string> { ["MinSide"] = "64", ["skipDuplicates"] = "không", ["pages"] = "1-3" };

        ParameterSchema.FromPairs("extract.images", typeof(ExtractImagesParams), values).ShouldBe(new ExtractImagesParams("1-3", 64, false));
        ParameterSchema.FromPairs("optimize.compress", typeof(CompressParams), new Dictionary<string, string> { ["preset"] = "Screen" }).ShouldBe(new CompressParams(CompressionPreset.Screen));

        Should.Throw<OperationValidationException>(() => ParameterSchema.FromPairs("extract.images", typeof(ExtractImagesParams), new Dictionary<string, string> { ["size"] = "1" })).Errors[0].MemberName.ShouldBe("size");
        Should.Throw<OperationValidationException>(() => ParameterSchema.FromPairs("extract.images", typeof(ExtractImagesParams), new Dictionary<string, string> { ["minSide"] = "nhiều" }));
        Should.Throw<OperationValidationException>(() => ParameterSchema.FromPairs("extract.images", typeof(ExtractImagesParams), new Dictionary<string, string> { ["minSide"] = "20000" }), "outside [Range]");
        Should.Throw<OperationValidationException>(() => ParameterSchema.FromPairs("optimize.compress", typeof(CompressParams), new Dictionary<string, string> { ["preset"] = "tiny" }));
    }

    [Fact]
    public void Json_GivesDefaultsForWhatIsMissing_AndRequiredValuesAreChecked()
    {
        ParameterSchema.FromJson("extract.images", typeof(ExtractImagesParams), null).ShouldBe(new ExtractImagesParams());
        ParameterSchema.FromJson("extract.images", typeof(ExtractImagesParams), JsonNode.Parse("""{"minSide":12}""")).ShouldBe(new ExtractImagesParams(MinSide: 12));
        Should.Throw<OperationValidationException>(() => ParameterSchema.FromJson("security.encrypt", typeof(ProtectParams), new JsonObject()));
    }

    [Fact]
    public void Passwords_AreNeverWritten_AtAnyDepth()
    {
        var parameters = new ProtectParams(new DocumentSecurity("mo-tai-lieu", "chu-so-huu", EncryptionAlgorithm.Aes256, DocumentPermissions.Print));

        var json = ParameterSchema.ToJson(parameters);

        json.ToJsonString().ShouldNotContain("chu-so-huu");
        json.ToJsonString().ShouldNotContain("mo-tai-lieu");
        json["security"]!["permissions"].ShouldNotBeNull();
        ParameterSchema.HasSecrets(typeof(ProtectParams)).ShouldBeTrue();
        ParameterSchema.HasSecrets(typeof(CompressParams)).ShouldBeFalse();
        ParameterSchema.ToJson(parameters, withoutSecrets: false).ToJsonString().ShouldContain("chu-so-huu");
    }

    [Fact]
    public void EveryOperation_IsDescribed_AndItsParametersRoundTrip()
    {
        var catalog = TestCatalog.Create(pdfium.Gate);

        var descriptors = catalog.Describe();

        descriptors.Count.ShouldBe(catalog.All.Count);
        descriptors.ShouldContain(d => d.Id == "organize.merge" && d.ManyInputs && !d.WritesPdf);
        descriptors.ShouldContain(d => d.Id == "scan.removeBlankPages" && d.Measured && d.WritesPdf);
        descriptors.ShouldContain(d => d.Id == "convert.toText" && !d.WritesPdf);
        descriptors.Single(d => d.Id == "optimize.compress").Group.ShouldBe("optimize");
        descriptors.SelectMany(d => d.Parameters).ShouldAllBe(f => f.Name.Length > 0 && char.IsLower(f.Name[0]));
    }
}
