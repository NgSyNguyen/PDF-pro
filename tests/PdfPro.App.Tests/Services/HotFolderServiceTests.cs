using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Services.Automation;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Automation;

namespace PdfPro.App.Tests.Services;

/// <summary>The hot folder of the app: started and stopped as the settings say, files run through the chosen workflow.</summary>
public sealed class HotFolderServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task TheSettings_StartTheHotFolder_WhichTakesTheFilesDroppedIn()
    {
        var watched = Directory.CreateDirectory(Path.Combine(_temp.Path, "vào")).FullName;
        var settings = new FakeSettingsService(AppSettings.Default with { HotFolderEnabled = true, HotFolderPath = watched, HotFolderWorkflow = WorkflowTemplates.Print });
        await using var service = Create(settings);

        await service.StartAsync(TestContext.Current.CancellationToken);
        service.IsRunning.ShouldBeTrue();
        File.Copy(Samples.Basic, Path.Combine(watched, "sổ tay.pdf"));
        var oneDone = string.Format(System.Globalization.CultureInfo.CurrentCulture, PdfPro.App.Resources.Strings.HotFolder_Status_Running, 1, 0);
        for (var i = 0; i < 300 && service.Status != oneDone; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        service.Status.ShouldBe(oneDone);
        File.Exists(Path.Combine(watched, HotFolderNames.Done, "sổ tay.pdf")).ShouldBeTrue();
        File.Exists(Path.Combine(watched, HotFolderNames.Output, "sổ tay.pdf")).ShouldBeTrue();

        await service.ApplyAsync(settings.Current with { HotFolderEnabled = false });
        service.IsRunning.ShouldBeFalse();
        service.Status.ShouldBe(PdfPro.App.Resources.Strings.HotFolder_Status_Off);
    }

    [Fact]
    public async Task AMissingFolderOrWorkflow_IsSaid()
    {
        await using var service = Create(new FakeSettingsService(AppSettings.Default));

        await service.ApplyAsync(AppSettings.Default with { HotFolderEnabled = true, HotFolderPath = Path.Combine(_temp.Path, "không có"), HotFolderWorkflow = WorkflowTemplates.Print });
        service.Status.ShouldBe(PdfPro.App.Resources.Strings.HotFolder_Status_NoFolder);

        await service.ApplyAsync(AppSettings.Default with { HotFolderEnabled = true, HotFolderPath = _temp.Path, HotFolderWorkflow = "không có.pdfflow" });
        service.Status.ShouldBe(PdfPro.App.Resources.Strings.HotFolder_Status_NoWorkflow);
        service.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task TheSettingsWindow_SavesTheHotFolder_AndAppliesIt()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var hotFolder = Substitute.For<IHotFolderService>();
        hotFolder.Status.Returns("…");
        var startup = Substitute.For<IStartupRegistration>();
        var viewModel = new SettingsViewModel(
            settings,
            Substitute.For<PdfPro.App.Services.IAppThemeService>(),
            Substitute.For<PdfPro.App.Services.IAppLifetime>(),
            Substitute.For<PdfPro.App.Localization.ILocalizationService>(),
            new PdfPro.App.Services.Rendering.RenderCache(settings),
            new PdfPro.Infrastructure.Caching.ThumbnailDiskCache(new AppPaths(_temp.Path, _temp.Path), NullLogger<PdfPro.Infrastructure.Caching.ThumbnailDiskCache>.Instance),
            NullLogger<SettingsViewModel>.Instance,
            hotFolder: hotFolder,
            startup: startup,
            workflows: new WorkflowLibrary(Path.Combine(_temp.Path, "workflows")));

        viewModel.HotFolderWorkflows.Select(w => w.Value).ShouldBe([WorkflowTemplates.Digitize, WorkflowTemplates.Publish, WorkflowTemplates.Print]);
        viewModel.HotFolderPath = _temp.Path;
        await viewModel.PendingSave;
        viewModel.HotFolderEnabled = true;
        await viewModel.PendingSave;
        viewModel.StartWithWindows = true;

        settings.Current.HotFolderEnabled.ShouldBeTrue();
        settings.Current.HotFolderPath.ShouldBe(_temp.Path);
        settings.Current.HotFolderWorkflow.ShouldBe(WorkflowTemplates.Digitize);
        await hotFolder.Received().ApplyAsync(Arg.Is<AppSettings>(s => s.HotFolderEnabled));
        startup.Received(1).Set(true);
    }

    private HotFolderService Create(FakeSettingsService settings)
    {
        var catalog = TestOperations.All();
        var automation = new AutomationServices(catalog, new WorkflowRunner(catalog, TestOperations.PdfiumGate), new WorkflowLibrary(Path.Combine(_temp.Path, "workflows")));
        return new HotFolderService(automation, settings, new AppPaths(Path.Combine(_temp.Path, "data"), _temp.Path), NullLogger<HotFolderService>.Instance);
    }
}
