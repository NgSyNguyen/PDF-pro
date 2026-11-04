using PdfPro.App.Services.Editing;
using PdfPro.App.Services.Forms;
using PdfPro.Operations.Signing;

namespace PdfPro.App.Services.Security;

/// <summary>
/// What signing documents needs: certificates, a time-stamping client, the document's form (its empty signature fields)
/// and the saved handwritten signatures.
/// </summary>
public sealed record SigningServices(ICertificateStore Certificates, ITimestampClient Timestamps, IFormSource Forms, SignatureLibrary? Signatures = null);
