using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.ViewModels;

/// <summary>"Chạy thao tác nặng trong tiến trình riêng" (SPEC Phase 13): on or off, and the worker's memory limit.</summary>
public sealed partial class SettingsViewModel
{
    public double MinWorkerMemory => AppSettings.MinWorkerMemoryMegabytes;

    public double MaxWorkerMemory => AppSettings.MaxWorkerMemoryMegabytes;

    [ObservableProperty]
    public partial bool IsolateOperations { get; set; }

    [ObservableProperty]
    public partial double? WorkerMemoryMegabytes { get; set; }

    private void InitializeWorker(AppSettings current)
    {
        IsolateOperations = current.IsolateOperations;
        WorkerMemoryMegabytes = current.WorkerMemoryMegabytes;
    }

    partial void OnIsolateOperationsChanged(bool value)
    {
        if (_isInitialized)
        {
            PendingSave = SaveAsync(s => s with { IsolateOperations = value });
        }
    }

    partial void OnWorkerMemoryMegabytesChanged(double? value)
    {
        if (!_isInitialized || value is not { } megabytes || !double.IsFinite(megabytes))
        {
            return;
        }

        var clamped = (int)Math.Clamp(Math.Round(megabytes), AppSettings.MinWorkerMemoryMegabytes, AppSettings.MaxWorkerMemoryMegabytes);
        if (clamped != megabytes)
        {
            WorkerMemoryMegabytes = clamped; // saves through the nested change
            return;
        }

        PendingSave = SaveAsync(s => s with { WorkerMemoryMegabytes = clamped });
    }
}
