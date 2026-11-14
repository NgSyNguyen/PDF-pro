using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfPro.Cli.Resources;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Signing;

namespace PdfPro.Cli;

/// <summary>A certificate to sign with, from a .pfx file; loaded once, held in memory only.</summary>
/// <param name="Path">The .pfx file.</param>
/// <param name="Password">Its password, or null.</param>
/// <param name="Visible">Where a visible signature goes, or null for an invisible one.</param>
public sealed record SigningCertificate(string Path, string? Password, SignaturePage? Visible)
{
    public override string ToString() => $"SigningCertificate {{ {System.IO.Path.GetFileName(Path)}, Visible = {Visible} }}";
}

/// <summary>A certificate that could not be loaded (wrong password, not a .pfx, no private key).</summary>
public sealed class CertificateLoadException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// What a workflow never keeps, given on the command line: JSON values merged into the steps (passwords) and a
/// certificate for the signing steps. Nothing of it is written anywhere.
/// </summary>
public sealed class CliSecrets(IReadOnlyDictionary<string, JsonObject> values, SigningCertificate? certificate) : IWorkflowSecrets, IDisposable
{
    private readonly WorkflowSecretValues _values = new(values);
    private readonly Lock _lock = new();
    private X509Certificate2? _loaded;

    /// <summary>The values of a secrets file: {"security.encrypt": {"security": {"ownerPassword": "…"}}, "3": {…}}.</summary>
    /// <exception cref="FormatException">The file is not such an object.</exception>
    public static IReadOnlyDictionary<string, JsonObject> Read(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException(CliStrings.Error_SecretsFile);
            return root.ToDictionary(p => p.Key, p => p.Value as JsonObject ?? throw new FormatException(CliStrings.Error_SecretsFile), StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new FormatException(CliStrings.Error_SecretsFile, ex);
        }
    }

    public async ValueTask<object?> CompleteAsync(WorkflowSecretRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Operation.Id != SignOperation.OperationId)
        {
            return await _values.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (certificate is null)
        {
            return null;
        }

        var stored = request.Stored;
        var signing = stored["request"]?.Deserialize<SignRequest>(ParameterSchema.Json) ?? new SignRequest(string.Empty);
        var loaded = Certificate();
        if (string.IsNullOrWhiteSpace(signing.FieldName))
        {
            signing = signing with { FieldName = "Signature" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) };
        }

        if (certificate.Visible is { } page && signing.Appearance is null)
        {
            var (number, rect) = SignatureCorner.Place(await File.ReadAllBytesAsync(request.Input, cancellationToken).ConfigureAwait(false), page);
            var lines = new[]
            {
                string.Format(CultureInfo.CurrentCulture, CliStrings.Sign_SignedBy, loaded.GetNameInfo(X509NameType.SimpleName, forIssuer: false)),
                string.Format(CultureInfo.CurrentCulture, CliStrings.Sign_Date, DateTime.Now.ToString("g", CultureInfo.CurrentCulture)),
            };
            signing = signing with { Appearance = new SignatureAppearance(number, rect, lines) };
        }

        var overwrite = stored["overwrite"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        return new SignParams(signing, loaded, overwrite);
    }

    public void Dispose() => _loaded?.Dispose();

    public override string ToString() => $"CliSecrets {{ {values.Count} step(s), certificate: {certificate is not null} }}";

    private X509Certificate2 Certificate()
    {
        lock (_lock)
        {
            if (_loaded is not null)
            {
                return _loaded;
            }

            try
            {
                var loaded = X509CertificateLoader.LoadPkcs12FromFile(certificate!.Path, certificate.Password, X509KeyStorageFlags.EphemeralKeySet);
                if (!loaded.HasPrivateKey)
                {
                    loaded.Dispose();
                    throw new CertificateLoadException(CliStrings.Error_CertificateNoKey);
                }

                return _loaded = loaded;
            }
            catch (CryptographicException ex)
            {
                throw new CertificateLoadException(CliStrings.Error_Certificate, ex);
            }
            catch (IOException ex)
            {
                throw new CertificateLoadException(CliStrings.Error_Certificate, ex);
            }
        }
    }
}
