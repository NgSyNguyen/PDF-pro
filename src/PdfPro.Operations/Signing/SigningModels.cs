using PdfPro.Core.Geometry;

namespace PdfPro.Operations.Signing;

/// <summary>A visible signature: where it shows and what it shows.</summary>
/// <param name="Page">The page (1-based) of a new signature field; ignored when an existing field is signed.</param>
/// <param name="Rect">Its box in the page's PDF space; ignored when an existing field is signed.</param>
/// <param name="Lines">The text shown ("Ký bởi: …", the date, the reason…), already in the user's language.</param>
/// <param name="Image">A picture of a handwritten signature (PNG or JPEG), shown left of the text, or null.</param>
public sealed record SignatureAppearance(int Page, PdfRect Rect, IReadOnlyList<string> Lines, byte[]? Image = null);

/// <summary>A digital signature to add.</summary>
/// <param name="FieldName">
/// The signature field: an empty one of the document with that name is signed, otherwise a new one is made.
/// </param>
/// <param name="Appearance">How it shows on its page, or null for an invisible signature.</param>
/// <param name="Reason">Why the document is signed (/Reason), or null.</param>
/// <param name="Location">Where it is signed (/Location), or null.</param>
/// <param name="ContactInfo">How to reach the signer (/ContactInfo), or null.</param>
/// <param name="TimestampServer">An RFC 3161 time-stamping authority to timestamp the signature, or null.</param>
/// <param name="Certification">
/// Certifies the document (DocMDP, ISO 32000-1 12.8.2.2) allowing, after it: 1 no change, 2 filling in forms and signing,
/// 3 also comments; null for an ordinary (approval) signature. Only the first signature of a document can certify it.
/// </param>
/// <param name="Pades">
/// A PAdES signature (ETSI EN 319 142, SubFilter ETSI.CAdES.detached with the signing certificate as a signed attribute)
/// instead of adbe.pkcs7.detached; with a timestamp it is PAdES-B-T.
/// </param>
public sealed record SignRequest(
    string FieldName,
    SignatureAppearance? Appearance = null,
    string? Reason = null,
    string? Location = null,
    string? ContactInfo = null,
    Uri? TimestampServer = null,
    int? Certification = null,
    bool Pades = false)
{
    public override string ToString() => $"SignRequest {{ Field = {FieldName}, Visible = {Appearance is not null}, Timestamp = {TimestampServer is not null}, Certification = {Certification}, Pades = {Pades} }}";
}

/// <summary>Why a document could not be signed.</summary>
public enum SigningFailure
{
    /// <summary>The file cannot be read or updated.</summary>
    Unreadable,

    /// <summary>The document is encrypted: remove its password first.</summary>
    Encrypted,

    /// <summary>The certificate has no private key (or it cannot be used).</summary>
    NoPrivateKey,

    /// <summary>The field of that name is already signed.</summary>
    FieldAlreadySigned,

    /// <summary>A field of that name exists and is not a signature field.</summary>
    NotASignatureField,

    /// <summary>The page of the signature does not exist.</summary>
    NoSuchPage,

    /// <summary>The time-stamping authority could not be reached or gave no valid timestamp.</summary>
    Timestamp,

    /// <summary>The document is certified and its certification allows no change, not even another signature.</summary>
    Certified,

    /// <summary>A certification was asked for a document already signed: only its first signature can certify it.</summary>
    AlreadySigned,
}

/// <summary>A document could not be signed; nothing was written.</summary>
public sealed class SigningException(SigningFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public SigningFailure Failure { get; } = failure;
}
