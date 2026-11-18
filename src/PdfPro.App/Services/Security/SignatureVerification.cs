using System.IO;
using PdfPro.Operations.Signing;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Security;

/// <summary>Checks the digital signatures of a file (seam for tests).</summary>
public interface ISignatureVerification
{
    /// <summary>The signatures of the file, each checked; empty when it has none.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="InvalidDataException">The file is too large or not a PDF PDFium can read.</exception>
    Task<IReadOnlyList<SignatureCheck>> VerifyAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Reads the file and checks it with <see cref="SignatureVerifier"/>, as this computer trusts certificates.</summary>
public sealed class SignatureVerification(PdfiumGate gate) : ISignatureVerification
{
    /// <summary>A signed file is read whole (its byte ranges are checked against it): larger ones are refused.</summary>
    public const long MaxFileBytes = 1L << 30;

    public async Task<IReadOnlyList<SignatureCheck>> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (new FileInfo(path).Length > MaxFileBytes)
        {
            throw new InvalidDataException("The file is too large to check its signatures.");
        }

        var file = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return await SignatureVerifier.VerifyAsync(gate, file, TrustOptions.Default, cancellationToken).ConfigureAwait(false);
    }
}
