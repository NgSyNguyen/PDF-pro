using PdfPro.App.Services.Forms;

namespace PdfPro.App.Tests.TestSupport;

/// <summary>Hands out a fixed form for every file (none by default) and counts the reads.</summary>
internal sealed class FakeFormSource : IFormSource
{
    public FormSnapshot Form { get; set; } = FormSnapshot.None;

    public List<string> Reads { get; } = [];

    public Task<FormSnapshot> ReadAsync(string path, CancellationToken cancellationToken)
    {
        Reads.Add(path);
        return Task.FromResult(Form);
    }
}
