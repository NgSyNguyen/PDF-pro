using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PdfPro.App.Controls;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;

namespace PdfPro.App.Tests.Viewer;

/// <summary>Runs the behaviour in a real (off-screen, never activated) window so Loaded and Unloaded are genuine.</summary>
public sealed class RealizationTests
{
    [Fact]
    public void LoadedElement_RealizesItsItem()
    {
        var item = new FakeItem();

        Sta.Run(() => WithWindow((window, image) =>
        {
            image.DataContext = item;
            window.Content = image;
            DoEvents();
        }));

        item.Realized.ShouldBe(1);
        item.Released.ShouldBe(0);
    }

    [Fact]
    public void RecycledContainer_ReleasesTheItemItShowed()
    {
        var item = new FakeItem();

        Sta.Run(() => WithWindow((window, image) =>
        {
            image.DataContext = item;
            window.Content = image;
            DoEvents();

            // A virtualizing panel switches the DataContext to {DisconnectedItem} (without DataContextChanged)
            // and then removes the container from the tree.
            image.DataContext = DisconnectedItem();
            window.Content = null;
            DoEvents();
            Realization.GetRealizedItem(image).ShouldBeNull();
        }));

        item.Released.ShouldBe(1);
    }

    [Fact]
    public void ReusedContainer_ReleasesTheOldItemAndRealizesTheNewOne()
    {
        var first = new FakeItem();
        var second = new FakeItem();

        Sta.Run(() => WithWindow((window, image) =>
        {
            image.DataContext = first;
            window.Content = image;
            DoEvents();

            image.DataContext = second;
            DoEvents();
        }));

        first.Released.ShouldBe(1);
        second.Realized.ShouldBe(1);
        second.Released.ShouldBe(0);
    }

    [Fact]
    public void DisablingTheBehaviour_ReleasesTheItem()
    {
        var item = new FakeItem();

        Sta.Run(() => WithWindow((window, image) =>
        {
            image.DataContext = item;
            window.Content = image;
            DoEvents();

            Realization.SetIsEnabled(image, false);
        }));

        item.Released.ShouldBe(1);
    }

    private static object DisconnectedItem()
        => typeof(BindingExpressionBase).GetField("DisconnectedItem", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static void WithWindow(Action<Window, Image> test)
    {
        var window = new Window
        {
            Width = 50,
            Height = 50,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        var image = new Image();
        Realization.SetIsEnabled(image, true);
        window.Show();
        try
        {
            test(window, image);
        }
        finally
        {
            window.Close();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// <summary>Lets the dispatcher raise pending Loaded/Unloaded events.</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private sealed class FakeItem : IRealizable
    {
        public int Realized { get; private set; }

        public int Released { get; private set; }

        public void Realize(double dpiScale) => Realized++;

        public void Release() => Released++;
    }
}
