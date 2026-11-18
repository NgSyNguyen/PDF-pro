using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Updates;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>Updates (SPEC Phase 13): checked when asked or on start, downloaded when the user says so, installed on exit.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task ANewerVersion_IsDownloadedWhenTheUserSaysSo_AndInstalledOnExit()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CanUpdate.Returns(true);
        var found = new AvailableUpdate("1.0.1", new object());
        updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(found);
        _messages.AskAsync(Strings.Update_Title, Arg.Any<string>(), Strings.Update_Download, Arg.Is<string?>(p => p == null), Strings.Update_Later).Returns(DialogChoice.Primary);
        var viewModel = Create(isDebugBuild: false, updates: updates);

        await viewModel.CheckForUpdatesAsync(manual: false);

        await updates.Received(1).DownloadAsync(found, Arg.Any<IProgress<int>>(), Arg.Any<CancellationToken>());
        updates.Received(1).InstallOnExit(found);
    }

    [Fact]
    public async Task Later_DownloadsNothing()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CanUpdate.Returns(true);
        updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(new AvailableUpdate("1.0.1", new object()));
        _messages.AskAsync(Strings.Update_Title, Arg.Any<string>(), Strings.Update_Download, Arg.Is<string?>(p => p == null), Strings.Update_Later).Returns(DialogChoice.Cancel);
        var viewModel = Create(isDebugBuild: false, updates: updates);

        await viewModel.CheckForUpdatesAsync(manual: true);

        await updates.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!, TestContext.Current.CancellationToken);
        updates.DidNotReceiveWithAnyArgs().InstallOnExit(default!);
    }

    [Fact]
    public async Task TheCheckOnStart_FollowsTheSetting_AndStaysSilent()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CanUpdate.Returns(true);
        updates.CheckAsync(Arg.Any<CancellationToken>()).Returns((AvailableUpdate?)null);
        var viewModel = Create(isDebugBuild: false, updates: updates);

        await viewModel.CheckForUpdatesAsync(manual: false);
        await _settings.UpdateAsync(s => s with { CheckForUpdates = false }, TestContext.Current.CancellationToken);
        await viewModel.CheckForUpdatesAsync(manual: false);

        await updates.Received(1).CheckAsync(Arg.Any<CancellationToken>());
        await _messages.DidNotReceiveWithAnyArgs().ShowInfoAsync(default!, default!);
    }

    [Fact]
    public async Task AskedForByTheUser_EveryOutcomeIsSaid()
    {
        var updates = Substitute.For<IUpdateService>();
        var viewModel = Create(isDebugBuild: false, updates: updates);

        await viewModel.CheckForUpdatesAsync(manual: true);
        await _messages.Received(1).ShowInfoAsync(Strings.Update_Title, Strings.Update_NotInstalled);

        updates.CanUpdate.Returns(true);
        updates.CheckAsync(Arg.Any<CancellationToken>()).Returns((AvailableUpdate?)null);
        await viewModel.CheckForUpdatesAsync(manual: true);
        await _messages.Received(1).ShowInfoAsync(Strings.Update_Title, string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Update_UpToDate, "1.0.0"));

        updates.CheckAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new UpdateCheckException(new System.Net.Http.HttpRequestException()));
        await viewModel.CheckForUpdatesAsync(manual: true);
        await _messages.Received(1).ShowErrorAsync(Strings.Update_Title, Strings.Update_Error_Check);
    }

    [Theory]
    [InlineData(UpdateChannels.Stable, System.Runtime.InteropServices.Architecture.X64, "win")]
    [InlineData(UpdateChannels.Stable, System.Runtime.InteropServices.Architecture.Arm64, "win-arm64")]
    [InlineData(UpdateChannels.Beta, System.Runtime.InteropServices.Architecture.X64, "beta")]
    [InlineData(UpdateChannels.Beta, System.Runtime.InteropServices.Architecture.Arm64, "beta-arm64")]
    public void TheVelopackChannel_FollowsTheChoiceAndTheProcessor(string choice, System.Runtime.InteropServices.Architecture architecture, string channel)
        => UpdateChannels.Of(choice, architecture).ShouldBe(channel);

    [Theory]
    [InlineData(null, "1.0.0-beta.2+d68e79e", UpdateChannels.Beta)]
    [InlineData(null, "1.0.0-beta.1", UpdateChannels.Beta)]
    [InlineData(null, "1.0.0", UpdateChannels.Stable)]
    [InlineData(null, "1.2.3+build-7", UpdateChannels.Stable)]
    [InlineData("", "1.0.0-beta.1", UpdateChannels.Beta)]
    [InlineData(UpdateChannels.Stable, "1.0.0-beta.2", UpdateChannels.Stable)]
    [InlineData(UpdateChannels.Beta, "1.0.0", UpdateChannels.Beta)]
    public void WithoutAChoice_ACopyFollowsTheChannelOfItsOwnVersion(string? choice, string version, string expected)
        => UpdateChannels.Resolve(choice, version).ShouldBe(expected);
}
