using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfPro.App.Localization;
using PdfPro.App.Resources;

namespace PdfPro.App.Tests.Localization;

/// <summary>Keeps the Vietnamese and English string tables complete and in sync with the XAML.</summary>
public sealed partial class ResourceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static TheoryData<string> NeutralResxFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "*.resx", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".en.resx", StringComparison.OrdinalIgnoreCase)))
            {
                data.Add(Path.GetRelativePath(RepositoryRoot, file));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NeutralResxFiles))]
    public void EnglishTable_HasExactlyTheVietnameseKeys(string neutralFile)
    {
        var vietnamese = ReadResx(neutralFile);
        var english = ReadResx(Path.ChangeExtension(neutralFile, null) + ".en.resx");

        english.Keys.Except(vietnamese.Keys).ShouldBeEmpty("keys only in English");
        vietnamese.Keys.Except(english.Keys).ShouldBeEmpty("keys missing in English");
    }

    [Theory]
    [MemberData(nameof(NeutralResxFiles))]
    public void Translations_AreNotEmptyAndKeepPlaceholders(string neutralFile)
    {
        var vietnamese = ReadResx(neutralFile);
        var english = ReadResx(Path.ChangeExtension(neutralFile, null) + ".en.resx");

        foreach (var (key, viValue) in vietnamese)
        {
            viValue.ShouldNotBeNullOrWhiteSpace(key);
            english[key].ShouldNotBeNullOrWhiteSpace(key);
            Placeholders(english[key]).ShouldBe(Placeholders(viValue), $"placeholders of {key}");
        }
    }

    [Fact]
    public void EveryKeyUsedInXaml_Exists()
    {
        var keys = ReadResx(Path.Combine("src", "PdfPro.App", "Resources", "Strings.resx")).Keys.ToHashSet();
        var xamlFiles = Directory.GetFiles(Path.Combine(RepositoryRoot, "src", "PdfPro.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var used = xamlFiles
            .SelectMany(f => TrUsage().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups["key"].Value)))
            .ToList();

        used.ShouldNotBeEmpty();
        used.Where(u => !keys.Contains(u.Key)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("vi-VN", "Tệp", "Cài đặt")]
    [InlineData("en-US", "File", "Settings")]
    public void Lookup_ReturnsTheLanguageOfTheCulture(string culture, string fileTab, string settings)
    {
        var info = CultureInfo.GetCultureInfo(culture);

        Strings.ResourceManager.GetString(nameof(Strings.Ribbon_Tab_File), info).ShouldBe(fileTab);
        Strings.ResourceManager.GetString(nameof(Strings.Settings_Title), info).ShouldBe(settings);
    }

    [Fact]
    public void Tr_UnknownKey_IsVisibleInsteadOfBlank()
        => TrExtension.Get("No_Such_Key").ShouldBe("[No_Such_Key]");

    private static Dictionary<string, string> ReadResx(string relativePath)
        => XDocument.Load(Path.Combine(RepositoryRoot, relativePath))
            .Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    private static string[] Placeholders(string value)
        => [.. PlaceholderPattern().Matches(value).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PdfPro.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("PdfPro.slnx not found above the test folder.");
    }

    [GeneratedRegex(@"\{loc:Tr\s+(?<key>[A-Za-z0-9_]+)\s*\}")]
    private static partial Regex TrUsage();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
