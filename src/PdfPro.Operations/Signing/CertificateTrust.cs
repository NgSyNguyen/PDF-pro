using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPro.Operations.Signing;

/// <summary>Whether the signer's certificate can be trusted.</summary>
public enum CertificateTrustStatus
{
    /// <summary>It chains up to a trusted root and was valid when checked.</summary>
    Trusted,

    /// <summary>It does not chain up to a root this computer trusts (self-signed, or an authority not installed).</summary>
    UntrustedRoot,

    /// <summary>It (or one above it) was not valid at the time it was checked for (expired or not yet valid).</summary>
    NotTimeValid,

    /// <summary>Its authority revoked it (or one above it).</summary>
    Revoked,

    /// <summary>The chain is broken another way (a bad signature in it, a certificate not allowed to sign…).</summary>
    Invalid,
}

/// <summary>What checking a signer's certificate chain found.</summary>
/// <param name="Status">The verdict.</param>
/// <param name="RevocationChecked">Revocation was checked (online, or from cached lists) for every certificate.</param>
/// <param name="CheckedAt">The time the chain was checked for: the timestamp's, or now.</param>
/// <param name="Chain">The certificates from the signer up, as far as the chain was built.</param>
public sealed record CertificateTrust(CertificateTrustStatus Status, bool RevocationChecked, DateTimeOffset CheckedAt, IReadOnlyList<X509Certificate2> Chain);

/// <summary>How signer certificates are checked.</summary>
/// <param name="CheckRevocation">Ask the authorities whether certificates were revoked (needs the network).</param>
/// <param name="TrustedRoots">Roots to trust instead of the computer's (tests); null for the computer's.</param>
public sealed record TrustOptions(bool CheckRevocation = true, IReadOnlyList<X509Certificate2>? TrustedRoots = null)
{
    public static TrustOptions Default { get; } = new();
}

/// <summary>Checks a signer's certificate chain with <see cref="X509Chain"/>.</summary>
public static class CertificateTrustCheck
{
    /// <summary>How long to wait for revocation lists and OCSP answers.</summary>
    private static readonly TimeSpan RetrievalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Builds the chain of <paramref name="signer"/> (with the certificates the signature carries) for the time
    /// <paramref name="at"/>, and says whether it can be trusted.
    /// </summary>
    public static CertificateTrust Check(X509Certificate2 signer, IEnumerable<X509Certificate2> carried, DateTimeOffset at, TrustOptions options)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(options);
        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.ExtraStore.AddRange(carried.ToArray());
        policy.VerificationTime = at.LocalDateTime;
        policy.RevocationMode = options.CheckRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.UrlRetrievalTimeout = RetrievalTimeout;
        if (options.TrustedRoots is { } roots)
        {
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.AddRange(roots.ToArray());
        }

        try
        {
            chain.Build(signer);
        }
        catch (CryptographicException)
        {
            return new CertificateTrust(CertificateTrustStatus.Invalid, false, at, [signer]);
        }

        var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);

        // Copies: the chain's own certificates go with it.
        var path = chain.ChainElements.Select(e => X509CertificateLoader.LoadCertificate(e.Certificate.RawData)).ToList();
        const X509ChainStatusFlags Unknown = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
        var revocationChecked = options.CheckRevocation && (flags & Unknown) == 0;
        var serious = flags & ~Unknown;
        var status = CertificateTrustStatus.Invalid;
        if ((serious & X509ChainStatusFlags.Revoked) != 0)
        {
            status = CertificateTrustStatus.Revoked;
        }
        else if ((serious & X509ChainStatusFlags.NotSignatureValid) != 0)
        {
            status = CertificateTrustStatus.Invalid;
        }
        else if ((serious & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0)
        {
            status = CertificateTrustStatus.UntrustedRoot;
        }
        else if ((serious & X509ChainStatusFlags.NotTimeValid) != 0)
        {
            status = CertificateTrustStatus.NotTimeValid;
        }
        else if (serious == X509ChainStatusFlags.NoError && path.Count > 0)
        {
            // Built, or only revocation could not be told.
            status = CertificateTrustStatus.Trusted;
        }

        return new CertificateTrust(status, revocationChecked, at, path);
    }
}
