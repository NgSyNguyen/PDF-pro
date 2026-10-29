using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Annotating;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Annotations;
using PdfPro.Operations.Annotating;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Làm phẳng": the dialog, then the window draws the comments into the pages.</summary>
public sealed partial class DocumentTabViewModelTests
{
    [Fact]
    public async Task Flatten_WholeDocument_AsksTheWindowToRunIt()
    {
        await using var tab = await CreateAsync(3);
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        _pageDialogs.ShowFlatten(Arg.Any<FlattenViewModel>()).Returns(call =>
        {
            call.Arg<FlattenViewModel>().ApplyCommand.Execute(null);
            return true;
        });

        tab.FlattenAnnotationsCommand.Execute(null);

        request.ShouldNotBeNull();
        request.Operation.ShouldBeOfType<FlattenAnnotationsOperation>();
        request.Parameters.ShouldBe(new FlattenAnnotationsParams());
        request.Description.ShouldBe(Strings.History_FlattenAnnotations);
        tab.IsDirty.ShouldBeFalse("the window applies it");
    }

    [Fact]
    public async Task Flatten_CanBeLimitedToTheCurrentPage_AndToSomeKinds()
    {
        await using var tab = await CreateAsync(3);
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        tab.Viewer.NavigateTo(1);
        _pageDialogs.ShowFlatten(Arg.Any<FlattenViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<FlattenViewModel>();
            dialog.Target.Target.ShouldBe(PageTarget.All, "the whole document unless asked");
            dialog.Target.IsSelected = true;
            dialog.Notes = false;
            dialog.Stamps = false;
            dialog.ApplyCommand.Execute(null);
            return true;
        });

        tab.FlattenAnnotationsCommand.Execute(null);

        request.ShouldNotBeNull();
        request.Parameters.ShouldBe(new FlattenAnnotationsParams("2", [AnnotationGroup.Markup, AnnotationGroup.Drawings]));
        request.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_FlattenAnnotationsOn, 1));
    }

    [Fact]
    public async Task Flatten_Cancelled_AsksNothing()
    {
        await using var tab = await CreateAsync(2);
        var requested = false;
        tab.ContentChangeRequested += (_, _) => requested = true;
        _pageDialogs.ShowFlatten(Arg.Any<FlattenViewModel>()).Returns(false);

        tab.FlattenAnnotationsCommand.Execute(null);

        requested.ShouldBeFalse();
    }

    [Fact]
    public void FlattenDialog_NeedsAKind_AndAValidRange()
    {
        var dialog = new FlattenViewModel(5, [2]);
        var closed = new List<bool>();
        dialog.CloseRequested += (_, accepted) => closed.Add(accepted);

        dialog.Notes = dialog.Markup = dialog.Drawings = dialog.Stamps = false;
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse("nothing would be flattened");
        dialog.Stamps = true;
        dialog.Target.IsRange = true;
        dialog.Target.RangeText = "7";
        dialog.ApplyCommand.Execute(null);
        dialog.Error.ShouldNotBeNull();
        dialog.Result.ShouldBeNull();

        dialog.Target.IsOdd = true;
        dialog.Error.ShouldBeNull("cleared when the pages change");
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldBe(new FlattenAnnotationsParams("odd", [AnnotationGroup.Stamps]));
        dialog.CancelCommand.Execute(null);
        closed.ShouldBe([true, false]);
    }
}
