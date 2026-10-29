using PdfPro.Operations.Optimizing;

namespace PdfPro.App.Services.Optimizing;

/// <summary>The file optimizations the window runs: Fast Web View and repairing damaged files.</summary>
public sealed record OptimizingServices(LinearizeOperation Linearize, RepairOperation Repair);
