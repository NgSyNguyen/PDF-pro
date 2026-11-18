using System.Text;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Text;

/// <summary>Text of TCVN3 and VNI fonts turned back into Vietnamese.</summary>
public sealed class LegacyVietnameseTests
{
    private static readonly LegacyFont Tcvn3 = new(LegacyVietnameseEncoding.Tcvn3, false);
    private static readonly LegacyFont Tcvn3Capitals = new(LegacyVietnameseEncoding.Tcvn3, true);
    private static readonly LegacyFont Vni = new(LegacyVietnameseEncoding.Vni, false);

    [Theory]
    [InlineData("Céng hoµ x· héi chñ nghÜa ViÖt Nam", "Cộng hoà xã hội chủ nghĩa Việt Nam")]
    [InlineData("§éc lËp - Tù do - H¹nh phóc", "Độc lập - Tự do - Hạnh phúc")]
    [InlineData("Uû ban nh©n d©n x· MÉu Thö", "Uỷ ban nhân dân xã Mẫu Thử")]
    [InlineData("Ng­êi ký: NguyÔn V¨n ¡n, §µ N½ng", "Người ký: Nguyễn Văn Ăn, Đà Nẵng")]
    public void Tcvn3Text_BecomesVietnamese(string garbled, string vietnamese)
        => LegacyVietnamese.Convert(garbled, Tcvn3).ShouldBe(vietnamese.Normalize(NormalizationForm.FormC));

    [Fact]
    public void TheCapitalFontsOfTcvn3_GiveCapitals()
        => LegacyVietnamese.Convert("céng hoµ x· héi", Tcvn3Capitals).ShouldBe("CỘNG HOÀ XÃ HỘI");

    [Theory]
    [InlineData("Coäng hoøa xaõ hoäi chuû nghóa Vieät Nam", "Cộng hòa xã hội chủ nghĩa Việt Nam")]
    [InlineData("Ñoäc laäp - Töï do - Haïnh phuùc", "Độc lập - Tự do - Hạnh phúc")]
    [InlineData("COÄNG HOØA XAÕ HOÄI CHUÛ NGHÓA VIEÄT NAM", "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM")]
    [InlineData("Ngöôøi daân taêng thôøi gian yù kieán ñeà nghò", "Người dân tăng thời gian ý kiến đề nghị")]
    [InlineData("Ñaø Naüng, Huaán, Thaúng, Kyø", "Đà Nẵng, Huấn, Thẳng, Kỳ")]
    public void VniText_BecomesVietnamese(string garbled, string vietnamese)
        => LegacyVietnamese.Convert(garbled, Vni).ShouldBe(vietnamese.Normalize(NormalizationForm.FormC));

    [Fact]
    public void EveryCharacterKeepsItsPlace_AVniMarkBecomingACombiningMark()
    {
        string[] drawn = ["V", "i", "e", "ä", "t"];

        var converted = LegacyVietnamese.Convert(drawn, Vni);

        converted.Count.ShouldBe(5, "one output per character: their boxes still line up");
        converted[3].ShouldBe("̣̂");
        string.Concat(converted).Normalize(NormalizationForm.FormC).ShouldBe("Việt");
    }

    [Theory]
    [InlineData(".VnTime", LegacyVietnameseEncoding.Tcvn3, false)]
    [InlineData("ABCDEF+.VnTime", LegacyVietnameseEncoding.Tcvn3, false)]
    [InlineData(".VnTimeH,Bold", LegacyVietnameseEncoding.Tcvn3, true)]
    [InlineData(".VnArial NarrowH", LegacyVietnameseEncoding.Tcvn3, true)]
    [InlineData("VnTime", LegacyVietnameseEncoding.Tcvn3, false)]
    [InlineData("VNI-Times", LegacyVietnameseEncoding.Vni, false)]
    [InlineData("QWERTY+VNI-Helve,Bold", LegacyVietnameseEncoding.Vni, false)]
    [InlineData("TimesNewRomanPSMT", LegacyVietnameseEncoding.None, false)]
    [InlineData("Vnexpress", LegacyVietnameseEncoding.None, false)]
    [InlineData(null, LegacyVietnameseEncoding.None, false)]
    public void TheEncoding_IsKnownByTheFontsName(string? name, LegacyVietnameseEncoding encoding, bool capitals)
        => LegacyVietnamese.Detect(name).ShouldBe(new LegacyFont(encoding, capitals));

    [Theory]
    [InlineData(".VnTime", "Times New Roman")]
    [InlineData(".VnArialH", "Arial")]
    [InlineData("VNI-Helve", "Arial")]
    [InlineData("VNI-Couri", "Courier New")]
    [InlineData("Arial", null)]
    public void ALegacyFont_HasAUnicodeLookAlike(string name, string? unicode)
        => LegacyVietnamese.UnicodeFont(name).ShouldBe(unicode);

    [Fact]
    public void LettersOfSeveralFonts_AreConvertedFontByFont()
    {
        var letters = "Céng".Select(c => (c.ToString(), (string?)".VnTime"))
            .Concat(" ".Select(c => (c.ToString(), (string?)"Arial")))
            .Concat("hoøa".Select(c => (c.ToString(), (string?)"VNI-Times")))
            .Concat(" é".Select(c => (c.ToString(), (string?)"Arial")));

        LegacyVietnamese.Convert(letters).ShouldBe("Cộng hòa é", "the last é is a Unicode font's");
    }

    [Fact]
    public void UnicodeText_IsLeftAsItIs()
        => LegacyVietnamese.Convert("Cộng hòa", LegacyFont.None).ShouldBe("Cộng hòa");
}
