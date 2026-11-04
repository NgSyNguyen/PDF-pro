using System.Text;
using PdfPro.Operations.Security;

namespace PdfPro.Operations.Tests.Security;

public sealed class ContentSectionsTests
{
    private static string Strip(string content, string[]? hiddenProperties = null, string[]? hiddenXObjects = null)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        var found = ContentSections.Find(bytes, p => (hiddenProperties ?? ["L1"]).Contains(p), x => (hiddenXObjects ?? []).Contains(x));
        return Encoding.Latin1.GetString(ContentSections.Remove(bytes, found.Spans));
    }

    [Fact]
    public void AHiddenLayer_Goes_AndEveryOtherByteStays()
        => Strip("q 1 0 0 1 0 0 cm\n/OC /L1 BDC 0 0 m 10 10 l S EMC\n/OC /L2 BDC (Hien) Tj EMC\nQ")
            .ShouldBe("q 1 0 0 1 0 0 cm\n \n/OC /L2 BDC (Hien) Tj EMC\nQ");

    [Fact]
    public void NestedMarkedContent_GoesWithItsHiddenLayer()
        => Strip("/OC /L1 BDC /Span <</ActualText (x)>> BDC (a) Tj EMC /P BMC (b) Tj EMC EMC (c) Tj")
            .ShouldBe("  (c) Tj");

    [Fact]
    public void ALayerWhoseNameIsEscaped_IsRecognised()
        => Strip("/OC /L#31 BDC (a) Tj EMC (b) Tj").ShouldBe("  (b) Tj");

    [Fact]
    public void StringsAndInlineImages_DoNotConfuseTheReader()
    {
        // ")" and "EMC" inside a string, binary data with "EMC" in an inline image.
        const string content = "/OC /L1 BDC (a \\) EMC ( b ) EMC) Tj BI /W 2 /H 1 /BPC 8 /CS /G ID \u0001EMCÿ EI EMC (kept) Tj";

        Strip(content).ShouldBe("  (kept) Tj");
    }

    [Fact]
    public void AHiddenXObject_IsNoLongerPainted()
        => Strip("q 100 0 0 100 0 0 cm /Im1 Do Q q /Fm2 Do Q", hiddenXObjects: ["Im1"]).ShouldBe("q 100 0 0 100 0 0 cm   Q q /Fm2 Do Q");

    [Fact]
    public void ASectionThatWouldUnbalanceTheState_IsKept()
    {
        var bytes = Encoding.Latin1.GetBytes("/OC /L1 BDC q 0 0 m EMC (x) Tj Q");

        var found = ContentSections.Find(bytes, p => p == "L1", _ => false);

        found.Spans.ShouldBeEmpty();
        found.Unbalanced.ShouldBe(1);
    }

    [Fact]
    public void ASectionNeverClosed_RunsToTheEnd()
        => Strip("(a) Tj /OC /L1 BDC (b) Tj").ShouldBe("(a) Tj  ");

    [Fact]
    public void CommentsAreSkipped()
        => Strip("% /OC /L1 BDC\n(a) Tj").ShouldBe("% /OC /L1 BDC\n(a) Tj");
}
