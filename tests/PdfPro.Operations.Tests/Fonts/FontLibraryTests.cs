using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Drawing;

namespace PdfPro.Operations.Tests.Fonts;

public sealed class FontLibraryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string, bool, bool> BundledFaces()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var family in FontFamilies.Bundled)
        {
            foreach (var (bold, italic) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                data.Add(family, bold, italic);
            }
        }

        return data;
    }

    [Fact]
    public void BundledFamilies_ComeFirst_WithEveryStyle()
    {
        var library = new FontLibrary([]);

        library.Families.ShouldBe(
        [
            new FontFamilyInfo(FontFamilies.NotoSans, true, true, true),
            new FontFamilyInfo(FontFamilies.NotoSerif, true, true, true),
            new FontFamilyInfo(FontFamilies.BeVietnamPro, true, true, true),
        ]);
    }

    [Theory]
    [MemberData(nameof(BundledFaces))]
    public void EveryBundledFace_WritesEveryVietnameseLetter(string family, bool bold, bool italic)
    {
        var library = new FontLibrary([]);

        var face = library.Metrics(family, bold, italic);

        face.FamilyName.ShouldBe(family);
        (face.IsBold, face.IsItalic).ShouldBe((bold, italic));
        face.HasTrueTypeOutlines.ShouldBeTrue();
        face.CanEmbedSubset.ShouldBeTrue();
        face.Covers(VietnameseText.Letters).ShouldBeTrue();
        library.ResolveTypeface(family.ToUpperInvariant(), bold, italic).ShouldNotBeNull().MustSimulateItalic.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(BundledFaces))]
    public void Widths_MatchWhatPdfSharpMeasures(string family, bool bold, bool italic)
    {
        FontLibrary.Register();
        const string text = "Cộng hòa xã hội chủ nghĩa Việt Nam — Độc lập - Tự do - Hạnh phúc";
        var style = (bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        using var graphics = XGraphics.CreateMeasureContext(new XSize(1000, 1000), XGraphicsUnit.Point, XPageDirection.Downwards, null!);

        var pdfSharp = graphics.MeasureString(text, new XFont(family, 14, style)).Width;
        var ours = TextLayout.Measure(text, FontLibrary.Instance.Metrics(family, bold, italic), 14);

        ours.ShouldBe(pdfSharp, 0.01, "the screen and the PDF must break lines at the same words");
    }

    [Fact]
    public void InstalledFonts_AreThoseThatWriteVietnameseAndMayBeEmbedded()
    {
        var folder = _temp.Combine("fonts");
        Directory.CreateDirectory(folder);
        var assets = Path.Combine(Samples.RepositoryRoot, "assets", "fonts");
        File.Copy(Path.Combine(assets, "Charm", "Charm-Regular.ttf"), Path.Combine(folder, "Charm-Regular.ttf"));
        File.Copy(Path.Combine(assets, "Charm", "Charm-Bold.ttf"), Path.Combine(folder, "Charm-Bold.ttf"));
        File.Copy(Path.Combine(assets, "DancingScript", "DancingScript-Regular.otf"), Path.Combine(folder, "DancingScript-Regular.otf"));
        File.Copy(Path.Combine(assets, "NotoSans", "NotoSans-Regular.ttf"), Path.Combine(folder, "NotoSans-Regular.ttf"));
        File.WriteAllBytes(Path.Combine(folder, "broken.ttf"), [0, 1, 0, 0, 0, 9, 1, 2]);

        var library = new FontLibrary([folder, _temp.Combine("missing")]);

        library.Families.Select(f => f.Name).ShouldBe([FontFamilies.NotoSans, FontFamilies.NotoSerif, FontFamilies.BeVietnamPro, "Charm"], "PostScript outlines and broken files are left out, bundled names stay bundled");
        library.Families[^1].ShouldBe(new FontFamilyInfo("Charm", false, true, false));
        library.Contains("charm").ShouldBeTrue();
        library.FontData("Charm", false, false).Length.ShouldBe((int)new FileInfo(Path.Combine(folder, "Charm-Regular.ttf")).Length);

        // No italic face: the upright face is slanted. Bold exists.
        var italic = library.ResolveTypeface("Charm", bold: true, italic: true).ShouldNotBeNull();
        italic.MustSimulateItalic.ShouldBeTrue();
        italic.MustSimulateBold.ShouldBeFalse();
        library.Metrics("Charm", true, true).IsBold.ShouldBeTrue();
        library.GetFont(italic.FaceName).ShouldNotBeNull();
        library.GetFont("file:C:\\Windows\\win.ini").ShouldBeNull("only faces of the library are served");
        library.ResolveTypeface("Dancing Script", false, false).ShouldBeNull();
        Should.Throw<KeyNotFoundException>(() => library.Metrics("Dancing Script", false, false));
    }

    [Fact]
    public void TheComputersFonts_IncludeArial_WhenItIsInstalled()
    {
        var arial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        Assert.SkipUnless(File.Exists(arial), "Arial is not installed.");

        FontLibrary.Instance.Families.ShouldContain(f => f.Name == "Arial" && !f.IsBundled && f.HasBold && f.HasItalic);
    }

    [Theory]
    [InlineData(0x0000, true)]
    [InlineData(0x0002, false)]
    [InlineData(0x0004, false)]
    [InlineData(0x0008, true)]
    [InlineData(0x000A, true)]
    [InlineData(0x0100, false)]
    [InlineData(0x0208, false)]
    public void EmbeddingPermissions_FollowTheFontLicenseBits(int fsType, bool allowed)
        => TrueTypeFont.AllowsSubsetEmbedding((ushort)fsType).ShouldBe(allowed);

    [Fact]
    public void FilesThatAreNotSingleFonts_AreRefused()
    {
        Should.Throw<InvalidDataException>(() => TrueTypeFont.Load([0x74, 0x74, 0x63, 0x66, 0, 1, 0, 0, 0, 0, 0, 1]));
        Should.Throw<InvalidDataException>(() => TrueTypeFont.Load("not a font at all"u8.ToArray()));
        Should.Throw<InvalidDataException>(() => TrueTypeFont.Load([0, 1, 0, 0, 0, 3]));
    }
}
