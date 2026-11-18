using PdfPro.Core.Operations;
using PdfPro.Core.Splitting;

namespace PdfPro.Operations.Optimizing;

/// <summary>Writing optimized copies: through a temporary file of the output's folder, then moved into place.</summary>
internal static class OptimizedFiles
{
    /// <summary>
    /// Runs <paramref name="write"/> into a temporary file next to <paramref name="output"/>, then replaces the output
    /// with it; a failure leaves the output as it was.
    /// </summary>
    public static async Task<T> WriteAsync<T>(string output, Func<string, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output)) ?? throw new ArgumentException("The output has no folder.", nameof(output));
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var result = await write(temporary, cancellationToken).ConfigureAwait(false);
            if (File.Exists(output))
            {
                File.Replace(temporary, output, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, output);
            }

            return result;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>The output of each input in the output folder, under its own name (made unique unless overwriting).</summary>
    /// <exception cref="InvalidOperationException">An output would be an input.</exception>
    public static IReadOnlyList<string> Outputs(OperationContext context, bool overwrite)
    {
        var inputs = context.InputPaths.Select(Path.GetFullPath).ToList();
        var names = FileNames.MakeUnique([.. inputs.Select(p => Path.GetFileNameWithoutExtension(p))], name => !overwrite && File.Exists(Path.Combine(context.OutputDirectory, name + ".pdf")));
        var outputs = names.Select(n => Path.GetFullPath(Path.Combine(context.OutputDirectory, n + ".pdf"))).ToList();
        if (outputs.Any(o => inputs.Contains(o, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("An output would overwrite its input; choose another output folder.");
        }

        return outputs;
    }
}
