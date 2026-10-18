using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PdfPro.Infrastructure.Caching;

/// <summary>
/// Identifies a file's content cheaply for disk caches: SHA-256 over the length and the first and last megabyte.
/// Copies and renames keep the fingerprint; an edit (incremental saves append to the end) changes it.
/// </summary>
public static class DocumentFingerprint
{
    private const int ChunkSize = 1024 * 1024;

    /// <returns>32 lowercase hex characters.</returns>
    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var length = stream.Length;
            var lengthBytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
            hash.AppendData(lengthBytes);

            var buffer = new byte[ChunkSize];
            var head = await ReadChunkAsync(stream, 0, buffer, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, head);
            if (length > ChunkSize)
            {
                var tail = await ReadChunkAsync(stream, Math.Max(ChunkSize, length - ChunkSize), buffer, cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, tail);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset())[..32];
        }
    }

    private static async Task<int> ReadChunkAsync(FileStream stream, long offset, byte[] buffer, CancellationToken cancellationToken)
    {
        stream.Position = offset;
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total;
    }
}
