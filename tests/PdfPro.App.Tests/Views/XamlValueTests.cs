using System.Reflection;
using System.Xml.Linq;
using PdfPro.App.Tests.TestSupport;

namespace PdfPro.App.Tests.Views;

/// <summary>
/// The markup compiler accepts XAML that WPF then cannot load: a view with a misspelt enum value, symbol, static
/// member, type or setter property crashes when it opens, which no view model test sees (the watermark dialog did,
/// for a missing resource — <see cref="XamlResourceTests"/>). <see cref="XamlValueChecker"/> reads them all.
/// </summary>
public sealed class XamlValueTests
{
    [Fact]
    public void EveryValueOfTheAppsXaml_IsOneWpfCanLoad()
    {
        var checker = new XamlValueChecker(typeof(PdfPro.App.App).Assembly, name => Assembly.Load(new AssemblyName(name)));
        var app = Path.Combine(Samples.Repository, "src", "PdfPro.App");
        var files = Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories).Where(InSources).ToList();
        files.Count.ShouldBeGreaterThan(20, "the views are found");

        files.SelectMany(checker.Check).ToList().ShouldBeEmpty("these values would crash their view when it is loaded");
    }

    [Fact]
    public void TheChecker_FindsWhatWouldCrashAView_AndNothingElse()
    {
        var checker = new XamlValueChecker(typeof(PdfPro.App.App).Assembly, name => Assembly.Load(new AssemblyName(name)));
        var document = XDocument.Parse(FaultyXaml, LoadOptions.SetLineInfo);

        var problems = checker.Check(document, "faulty.xaml");

        problems.ShouldSatisfyAllConditions(
            () => problems.Count.ShouldBe(13, string.Join(Environment.NewLine, problems)),
            () => problems.ShouldContain(p => p.Contains("\"Primarry\" is not a member of ControlAppearance", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("Property=\"Colour\"", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("IsMouseOver=\"Yes\"", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("\"Diagonal\" is not a member of Orientation", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("\"Sometimes\" is not a member of ScrollBarVisibility", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("\"NoSuchSymbol24\" is not a member of SymbolRegular", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("Filled=\"Maybe\"", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("\"TwoWayy\" is not a member of BindingMode", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("{x:Static views:MainWindow.NoSuchMember} does not exist", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("\"Sideways\" is not a member of RelativeSourceMode", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("{x:Type views:NoSuchView} does not exist", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("Grid.Rown", StringComparison.Ordinal)),
            () => problems.ShouldContain(p => p.Contains("<NoSuchControl>", StringComparison.Ordinal)),
            () => problems.ShouldNotContain(p => p.Contains("faulty.xaml(27)", StringComparison.Ordinal)),
            () => problems.ShouldNotContain(p => p.Contains("faulty.xaml(28)", StringComparison.Ordinal)));
    }

    private static bool InSources(string path)
        => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// Thirteen faults, one per kind the checker reads (lines 13–26), then two correct lines (27, 28) — designer-only
    /// attributes and keys with a converter of their own are not faults.
    /// </summary>
    private const string FaultyXaml = """
        <UserControl
          x:Class="PdfPro.App.Views.Faulty"
          xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
          xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
          xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
          xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
          xmlns:views="clr-namespace:PdfPro.App.Views"
          mc:Ignorable="d"
          d:Whatever="{d:DesignInstance Type=views:Nothing}">
          <UserControl.Resources>
            <Style x:Key="S" TargetType="{x:Type ui:Button}">
              <Setter Property="Appearance" Value="Primarry" />
              <Setter Property="Colour" Value="Red" />
              <Style.Triggers>
                <Trigger Property="IsMouseOver" Value="Yes" />
              </Style.Triggers>
            </Style>
          </UserControl.Resources>
          <StackPanel Orientation="Diagonal" ScrollViewer.VerticalScrollBarVisibility="Sometimes">
            <ui:SymbolIcon Symbol="NoSuchSymbol24" />
            <ui:SymbolIcon Symbol="Add24" Filled="Maybe" />
            <TextBlock Text="{Binding Name, Mode=TwoWayy}" Visibility="{x:Static views:MainWindow.NoSuchMember}" />
            <TextBlock Tag="{RelativeSource Sideways}" DataContext="{x:Type views:NoSuchView}" />
            <Border Grid.Rown="1" />
            <NoSuchControl />
            <TextBlock Text="fine" Visibility="Collapsed" Tag="{Binding RelativeSource={RelativeSource AncestorType={x:Type Window}}, Mode=OneWay}" />
            <TextBlock><TextBlock.InputBindings><KeyBinding Key="F4" Modifiers="Control+Shift" /></TextBlock.InputBindings></TextBlock>
          </StackPanel>
        </UserControl>
        """;
}
