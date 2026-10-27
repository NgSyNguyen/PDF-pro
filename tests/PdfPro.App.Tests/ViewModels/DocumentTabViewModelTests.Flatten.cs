using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels;
using PdfPro.Operations.Annotating;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Làm phẳng": the comments drawn into the pages, once confirmed, by the window.</summary>
public sealed partial class DocumentTabViewModelTests
{
    [Fact]
    public async Task Flatten_WholeDocument_AsksTheWindowToRunIt()
    {
        await using var tab = await CreateAsync(3);
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        _messages.AskAsync(Strings.Flatten_Title, Strings.Flatten_Message, Strings.Flatten_All, Arg.Any<string?>(), Strings.Common_Cancel)
            .Returns(DialogChoice.Primary);

        await tab.FlattenAnnotationsCommand.ExecuteAsync(null);

        request.ShouldNotBeNull();
        request.Operation.ShouldBeOfType<FlattenAnnotationsOperation>();
        request.Parameters.ShouldBe(new FlattenAnnotationsParams());
        request.Description.ShouldBe(Strings.History_FlattenAnnotations);
        tab.IsDirty.ShouldBeFalse("the window applies it");
    }

    [Fact]
    public async Task Flatten_CanBeLimitedToTheCurrentPage_OrToThePagesSelectedInTheOrganizer()
    {
        await using var tab = await CreateAsync(3);
        var requests = new List<ContentChangeRequest>();
        tab.ContentChangeRequested += (_, r) => requests.Add(r);
        _messages.AskAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>()).Returns(DialogChoice.Secondary);
        tab.Viewer.NavigateTo(1);

        await tab.FlattenAnnotationsCommand.ExecuteAsync(null);
        tab.ToggleOrganizeModeCommand.Execute(null);
        tab.Organizer!.Select([0, 1, 2]);
        await tab.FlattenAnnotationsCommand.ExecuteAsync(null);

        await _messages.Received(1).AskAsync(Strings.Flatten_Title, Strings.Flatten_Message, Strings.Flatten_All, Format(Strings.Flatten_OnlyPage, 2), Strings.Common_Cancel);
        await _messages.Received(1).AskAsync(Strings.Flatten_Title, Strings.Flatten_Message, Strings.Flatten_All, Format(Strings.Flatten_OnlySelected, 3), Strings.Common_Cancel);
        requests.Select(r => r.Parameters).ShouldBe([new FlattenAnnotationsParams("2"), new FlattenAnnotationsParams("1-3")]);
        requests.Select(r => r.Description).ShouldBe([Format(Strings.History_FlattenAnnotationsOn, 1), Format(Strings.History_FlattenAnnotationsOn, 3)]);
    }

    [Fact]
    public async Task Flatten_Cancelled_AsksNothing()
    {
        await using var tab = await CreateAsync(2);
        var requested = false;
        tab.ContentChangeRequested += (_, _) => requested = true;
        _messages.AskAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>()).Returns(DialogChoice.Cancel);

        await tab.FlattenAnnotationsCommand.ExecuteAsync(null);

        requested.ShouldBeFalse();
    }
}
