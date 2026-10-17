using PdfSharp.Drawing;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// encrypted.pdf: AES-256, open password "test123", printing and copying not permitted.
/// </summary>
internal sealed class EncryptedSample : ISample
{
    public const string UserPassword = "test123";
    public const string OwnerPassword = "chu-so-huu-456";

    public string FileName => "encrypted.pdf";

    public void Generate(string path)
    {
        using var document = SampleDocument.Create("Tài liệu có mật khẩu", "Kiểm thử mở tài liệu mã hóa");
        for (var i = 1; i <= 2; i++)
        {
            var page = SampleDocument.AddA4Page(document);
            using var graphics = XGraphics.FromPdfPage(page);
            var writer = new PageWriter(graphics);
            writer.Heading($"Tài liệu mã hóa AES-256 — trang {i}/2", PageWriter.Heading1);
            writer.Paragraph("Mật khẩu mở tài liệu là test123 (dữ liệu giả, chỉ dùng để kiểm thử). "
                + "Tài liệu không cho phép in và không cho phép sao chép nội dung.");
            writer.Footer($"Trang {i}/2", 842);
        }

        document.SecuritySettings.UserPassword = UserPassword;
        document.SecuritySettings.OwnerPassword = OwnerPassword;
        document.SecuritySettings.PermitPrint = false;
        document.SecuritySettings.PermitFullQualityPrint = false;
        document.SecuritySettings.PermitExtractContent = false;
        document.SecurityHandler.SetEncryptionToV5(encryptMetadata: true);
        document.Save(path);
    }
}
