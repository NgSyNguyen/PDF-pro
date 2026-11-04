using System.Net.Http.Headers;

namespace PdfPro.Operations.Signing;

/// <summary>Asks an RFC 3161 time-stamping authority for a timestamp.</summary>
public interface ITimestampClient
{
    /// <summary>Sends a DER-encoded TimeStampReq and returns the DER-encoded TimeStampResp.</summary>
    /// <exception cref="SigningException">The authority could not be reached or refused.</exception>
    Task<byte[]> RequestAsync(Uri server, byte[] request, CancellationToken cancellationToken);
}

/// <summary>RFC 3161 over HTTP (POST application/timestamp-query).</summary>
public sealed class HttpTimestampClient(HttpClient http) : ITimestampClient
{
    /// <summary>A timestamp response larger than this is refused.</summary>
    private const int MaxResponseBytes = 1 << 20;

    public async Task<byte[]> RequestAsync(Uri server, byte[] request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(request);
        if (server.Scheme is not ("http" or "https"))
        {
            throw new SigningException(SigningFailure.Timestamp, "The time-stamping authority must be an http or https address.");
        }

        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        try
        {
            using var response = await http.PostAsync(server, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SigningException(SigningFailure.Timestamp, $"The time-stamping authority answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new SigningException(SigningFailure.Timestamp, "The time-stamping authority answered too much.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length > MaxResponseBytes
                ? throw new SigningException(SigningFailure.Timestamp, "The time-stamping authority answered too much.")
                : bytes;
        }
        catch (HttpRequestException ex)
        {
            throw new SigningException(SigningFailure.Timestamp, "The time-stamping authority cannot be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SigningException(SigningFailure.Timestamp, "The time-stamping authority did not answer in time.", ex);
        }
    }
}
