using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.RegularExpressions;
using PdfPro.Operations.Signing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Signing;

/// <summary>
/// The kinds of signature beside the approval one: a certification (DocMDP) that only the first signature can be and
/// that says what may change after it, and a PAdES signature (ETSI.CAdES.detached) signing for its certificate, with a
/// timestamp PAdES-B-T.
/// </summary>
public sealed partial class SignatureKindsTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly DateTimeOffset SigningTime = DateTimeOffset.Now;

    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = TestCertificates.Signer();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _certificate.Dispose();

    [Fact]
    public async Task ACertification_IsTheFirstSignature_AndSaysWhatMayChangeAfterIt()
    {
        var certified = await PdfSigner.SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), new SignRequest("ChungNhan", Certification: DocMdp.FormsAndSigning), _certificate, null, SigningTime, Token);

        using (var pdf = PdfReader.Open(new MemoryStream(certified), PdfDocumentOpenMode.Import))
        {
            DocMdp.PermissionOf(pdf).ShouldBe(DocMdp.FormsAndSigning);
        }

        var permission = await pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, certified);
                return document.GetFeatures().CertificationPermission;
            },
            PdfiumPriority.Normal,
            Token);
        permission.ShouldBe(DocMdp.FormsAndSigning, "PDFium reads the certification too");
        (await SignatureVerifier.VerifyAsync(pdfium.Gate, certified, Token)).ShouldHaveSingleItem().IsIntact.ShouldBeTrue();

        (await Should.ThrowAsync<SigningException>(() => PdfSigner.SignAsync(certified, new SignRequest("ChungNhan2", Certification: DocMdp.Annotations), _certificate, null, SigningTime, Token)))
            .Failure.ShouldBe(SigningFailure.AlreadySigned, "only the first signature certifies");
        var approved = await PdfSigner.SignAsync(certified, new SignRequest("Duyet"), _certificate, null, SigningTime, Token);
        (await SignatureVerifier.VerifyAsync(pdfium.Gate, approved, Token)).ShouldAllBe(c => c.IsIntact, "a certification allowing signing takes another signature");

        var locked = await PdfSigner.SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), new SignRequest("ChungNhan", Certification: DocMdp.NoChanges), _certificate, null, SigningTime, Token);
        (await Should.ThrowAsync<SigningException>(() => PdfSigner.SignAsync(locked, new SignRequest("Duyet"), _certificate, null, SigningTime, Token)))
            .Failure.ShouldBe(SigningFailure.Certified);
    }

    [Fact]
    public async Task APadesSignature_SignsForItsCertificate_AndWithATimestampIsPadesBT()
    {
        using var tsa = new FakeTimestampAuthority(DateTimeOffset.UtcNow.AddMinutes(-1));

        var signed = await PdfSigner.SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), new SignRequest("ChuKy", TimestampServer: new Uri("http://tsa.example.vn/"), Pades: true), _certificate, tsa, SigningTime, Token);

        var check = (await SignatureVerifier.VerifyAsync(pdfium.Gate, signed, Token)).ShouldHaveSingleItem();
        check.IsIntact.ShouldBeTrue(check.Problem);
        check.SubFilter.ShouldBe("ETSI.CAdES.detached");
        check.Timestamp.ShouldBe(tsa.Time);

        var cms = new SignedCms();
        cms.Decode(Contents(signed));
        var attributes = cms.SignerInfos[0].SignedAttributes;
        attributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).ShouldNotContain("1.2.840.113549.1.9.5", "PAdES puts the signing time in /M, not in the CMS");
        var signingCertificate = attributes.Cast<CryptographicAttributeObject>().Single(a => a.Oid.Value == PdfSigner.SigningCertificateV2Oid);
        CertHash(signingCertificate.Values[0].RawData).ShouldBe(SHA256.HashData(_certificate.RawData));
    }

    /// <summary>The certHash of the first ESSCertIDv2 of a SigningCertificateV2.</summary>
    private static byte[] CertHash(byte[] encoded)
    {
        var reader = new AsnReader(encoded, AsnEncodingRules.DER).ReadSequence().ReadSequence().ReadSequence();
        return reader.ReadOctetString();
    }

    /// <summary>The CMS in the last signature's /Contents (the hole the /ByteRange leaves), without its padding.</summary>
    private static byte[] Contents(byte[] file)
    {
        var text = Encoding.Latin1.GetString(file);
        var match = ByteRange().Matches(text)[^1];
        var start = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var end = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var hex = text[(int)(start + 1)..(int)(end - 1)];
        var bytes = Convert.FromHexString(hex);
        AsnDecoder.ReadEncodedValue(bytes, AsnEncodingRules.BER, out _, out _, out var length);
        return bytes[..length];
    }

    [GeneratedRegex(@"/ByteRange\s*\[\s*0\s+(\d+)\s+(\d+)\s+\d+\s*\]")]
    private static partial Regex ByteRange();
}
