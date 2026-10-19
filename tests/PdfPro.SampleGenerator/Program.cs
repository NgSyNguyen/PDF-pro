using System.Globalization;
using System.Security.Cryptography;
using PdfPro.SampleGenerator;
using PdfPro.SampleGenerator.Samples;
using PdfSharp.Fonts;

// Usage: PdfPro.SampleGenerator [outputDirectory]   (default: <repository>/samples)
var outputDirectory = args.Length > 0 ? Path.GetFullPath(args[0]) : RepositoryPaths.FindSamplesDirectory();
Directory.CreateDirectory(outputDirectory);

// Arial / Times New Roman ship with Windows and cover Vietnamese, like a document exported from Word.
GlobalFontSettings.UseWindowsFontsUnderWindows = true;

ISample[] samples =
[
    new BasicViSample(),
    new BigSample(),
    new EncryptedSample(),
    new BrokenSample(),
    new RotatedCropBoxSample(),
    new LinksAnnotsSample(),
];

Console.WriteLine($"Writing samples to {outputDirectory}");
foreach (var sample in samples)
{
    var path = Path.Combine(outputDirectory, sample.FileName);
    sample.Generate(path);
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..12];
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"  {sample.FileName,-22} {new FileInfo(path).Length,10:N0} bytes  sha256 {hash}…"));
}
