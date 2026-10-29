using PdfPro.Core.Security;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Materializing;

/// <summary>PDFsharp names itself as /Creator and /Producer when a document does not; PdfWriting takes that back.</summary>
public sealed class PdfWritingTests(PdfiumFixture pdfium)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADocumentWithoutCreatorOrProducer_IsWrittenWithout(bool seekable)
    {
        using var document = new PdfDocument();
        document.AddPage();
        document.Info.Title = "Báo cáo (bản 2)";

        var bytes = Write(document, seekable);

        using var read = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        read.Info.Elements.ContainsKey("/Creator").ShouldBeFalse();
        read.Info.Elements.ContainsKey("/Producer").ShouldBeFalse();
        read.Info.Title.ShouldBe("Báo cáo (bản 2)", "the other entries stay");
        read.PageCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PdfsharpsOwnXmp_IsNotLeft_AndNothingNamesIt(bool seekable)
    {
        using var document = new PdfDocument();
        document.AddPage();

        var bytes = Write(document, seekable);

        System.Text.Encoding.Latin1.GetString(bytes).ShouldNotContain("PDFsharp", Case.Insensitive, "neither in the information nor in an XMP packet");
        using var read = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        read.Internals.Catalog.Elements.ContainsKey("/Metadata").ShouldBeFalse();
    }

    [Fact]
    public void TheDocumentsOwnXmp_IsKept()
    {
        using var document = new PdfDocument();
        document.AddPage();
        var xmp = new PdfDictionary(document);
        xmp.Elements.SetName("/Type", "/Metadata");
        xmp.Elements.SetName("/Subtype", "/XML");
        xmp.CreateStream(System.Text.Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'>Tài liệu gốc</x:xmpmeta>"));
        document.Internals.AddObject(xmp);
        document.Internals.Catalog.Elements["/Metadata"] = xmp.Reference!;

        var bytes = Write(document, seekable: true);

        using var read = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        var catalog = read.Internals.Catalog;
        System.Text.Encoding.UTF8.GetString(catalog.Elements.GetDictionary("/Metadata")!.Stream.UnfilteredValue).ShouldContain("Tài liệu gốc");
        catalog.Elements.Keys.ShouldNotContain("/PdfProOwnMetadata");
        System.Text.Encoding.Latin1.GetString(bytes).ShouldNotContain("xmp:CreatorTool");
        document.Internals.Catalog.Elements.ContainsKey("/PdfProOwnMetadata").ShouldBeFalse("the document in memory is left as it was");
    }

    [Fact]
    public async Task TheBlankedFile_IsStillReadByPdfium()
    {
        using var document = new PdfDocument();
        document.AddPage();
        var bytes = Write(document, seekable: true);
        using var temp = new TempDirectory();
        var path = temp.Combine("blank.pdf");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        await using var opened = await PdfInspector.OpenAsync(pdfium.Gate, path);

        opened.PageCount.ShouldBe(1);
        (await opened.GetMetadataAsync(TestContext.Current.CancellationToken)).Producer.ShouldBeNullOrEmpty();
    }

    [Fact]
    public void ACreatorAndProducerTheDocumentHad_AreKept()
    {
        using var document = new PdfDocument();
        document.AddPage();
        document.Info.Creator = "Microsoft Word";
        document.Info.Elements.SetString("/Producer", "Microsoft: Print To PDF");

        var bytes = Write(document, seekable: true);

        using var read = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        read.Info.Creator.ShouldBe("Microsoft Word");
        read.Info.Elements.ContainsKey("/Producer").ShouldBeTrue();
    }

    [Fact]
    public void AnEncryptedDocument_IsBlankedToo_AndStillOpens()
    {
        using var document = new PdfDocument();
        document.AddPage();
        EncryptionWriter.Protect(document, new DocumentSecurity("mo", "chu"));

        var bytes = Write(document, seekable: true);

        using var read = PdfReader.Open(new MemoryStream(bytes), "chu", PdfDocumentOpenMode.Import);
        read.Info.Elements.ContainsKey("/Producer").ShouldBeFalse();
        read.Internals.Catalog.Elements.ContainsKey("/Metadata").ShouldBeFalse();
        read.PageCount.ShouldBe(1);
    }

    private static byte[] Write(PdfDocument document, bool seekable)
    {
        using var output = new MemoryStream();
        PdfWriting.Save(document, seekable ? output : new ForwardOnlyStream(output));
        return output.ToArray();
    }

    /// <summary>A stream that can only be written forwards, as a network or pipe would be.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
