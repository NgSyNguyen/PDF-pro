using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;

namespace PdfPro.Core.Tests.Forms;

/// <summary>Form data matched to fields, and written to / read from JSON and XFDF.</summary>
public sealed class FormDataTests
{
    private static readonly PdfRect Box = new(0, 0, 100, 20);

    private static readonly FormField Name = new("nguoi.hoTen", FormFieldKind.Text, null, [], FormFieldFlags.None, [], [new FormWidget(0, Box)], MaxLength: 10);
    private static readonly FormField Amount = new("soTien", FormFieldKind.Text, null, [], FormFieldFlags.None, [], [new FormWidget(0, Box)], Format: FormTextFormat.Number(2));
    private static readonly FormField Agree = new("dongY", FormFieldKind.CheckBox, "Off", [], FormFieldFlags.None, [], [new FormWidget(0, Box, "Co")]);
    private static readonly FormField Gender = new("gioiTinh", FormFieldKind.RadioGroup, "Off", [], FormFieldFlags.Radio, [], [new FormWidget(0, Box, "Nam"), new FormWidget(0, Box, "Nu")]);
    private static readonly FormField Province = new("tinh", FormFieldKind.ComboBox, null, [], FormFieldFlags.Combo, [new("HN", "Hà Nội"), new("HCM", "TP. Hồ Chí Minh")], [new FormWidget(0, Box)]);
    private static readonly FormField Hobbies = new("soThich", FormFieldKind.ListBox, null, [], FormFieldFlags.MultiSelect, [new("doc", "Đọc sách"), new("nhac", "Âm nhạc")], [new FormWidget(0, Box)]);
    private static readonly FormField Send = new("gui", FormFieldKind.PushButton, "Gửi", [], FormFieldFlags.PushButton, [], [new FormWidget(0, Box)]);
    private static readonly FormField Locked = new("maSo", FormFieldKind.Text, "001", [], FormFieldFlags.ReadOnly, [], [new FormWidget(0, Box)]);

    private static readonly FormField[] Fields = [Name, Amount, Agree, Gender, Province, Hobbies, Send, Locked];

    [Fact]
    public void Entries_HoldTheFilledFields_ListsAsLists()
    {
        var entries = FormData.Entries(Fields, f => f.Name switch
        {
            "nguoi.hoTen" => new FormValue(f.Name, "An"),
            "soThich" => new FormValue(f.Name, "doc", ["doc", "nhac"]),
            _ => new FormValue(f.Name, f.Value ?? string.Empty, f.Kind == FormFieldKind.ListBox ? [] : null),
        });

        entries.Select(e => e.Name).ShouldBe(["nguoi.hoTen", "soTien", "dongY", "gioiTinh", "tinh", "soThich", "maSo"], "buttons hold no data");
        entries[0].ShouldBe(new FormDataEntry("nguoi.hoTen", ["An"]));
        entries[2].Values.ShouldBe(["Off"]);
        entries[5].ShouldBe(new FormDataEntry("soThich", ["doc", "nhac"], IsList: true));
    }

    [Fact]
    public void Match_GivesEachKindOfField_ItsValue()
    {
        var match = FormData.Match(Fields,
        [
            new FormDataEntry("NGUOI.HOTEN", ["Nguyễn Văn Anh Tuấn"]),
            new FormDataEntry("soTien", ["1.250.000"]),
            new FormDataEntry("dongY", ["x"]),
            new FormDataEntry("gioiTinh", ["nu"]),
            new FormDataEntry("tinh", ["Hà Nội"]),
            new FormDataEntry("soThich", ["Âm nhạc", "doc"], IsList: true),
        ]);

        match.Unknown.ShouldBeEmpty();
        match.Rejected.ShouldBeEmpty();
        match.Values.ShouldBe(
        [
            new FormValue("nguoi.hoTen", "Nguyễn Văn"),
            new FormValue("soTien", "1250000"),
            new FormValue("dongY", "Co"),
            new FormValue("gioiTinh", "Nu"),
            new FormValue("tinh", "HN"),
            new FormValue("soThich", "nhac", ["nhac", "doc"]),
        ]);
    }

    [Theory]
    [InlineData("có", "Co")]
    [InlineData("TRUE", "Co")]
    [InlineData("1", "Co")]
    [InlineData("co", "Co")]
    [InlineData("không", "Off")]
    [InlineData("", "Off")]
    [InlineData("false", "Off")]
    public void CheckBoxes_TakeYesAndNoWords(string data, string value)
        => FormData.Value(Agree, [data]).ShouldBe(new FormValue("dongY", value));

    [Fact]
    public void ValuesThatDoNotFit_AndUnknownNames_AreReported()
    {
        var match = FormData.Match(Fields,
        [
            new FormDataEntry("soTien", ["mười nghìn"]),
            new FormDataEntry("dongY", ["có lẽ"]),
            new FormDataEntry("gioiTinh", ["Khác"]),
            new FormDataEntry("tinh", ["Huế"]),
            new FormDataEntry("soThich", ["Thể thao"]),
            new FormDataEntry("gui", ["x"]),
            new FormDataEntry("maSo", ["002"]),
            new FormDataEntry("khongCo", ["x"]),
        ]);

        match.Values.ShouldBeEmpty();
        match.Rejected.ShouldBe(["soTien", "dongY", "gioiTinh", "tinh", "soThich", "gui", "maSo"]);
        match.Unknown.ShouldBe(["khongCo"]);
        FormData.Value(Province with { Flags = FormFieldFlags.Combo | FormFieldFlags.Edit }, ["Huế"]).ShouldBe(new FormValue("tinh", "Huế"), "an editable drop-down takes any text");
        FormData.Value(Hobbies with { Flags = FormFieldFlags.None }, ["doc", "nhac"]).ShouldBeNull("one choice only");
    }

    [Fact]
    public void Json_RoundTrips_AndReadsNestedNamesNumbersAndNull()
    {
        IReadOnlyList<FormDataEntry> entries =
        [
            new("nguoi.hoTen", ["Trần Thị Bích"]),
            new("soThich", ["doc", "nhac"], IsList: true),
            new("ghiChu", ["Dòng 1\nDòng \"2\""]),
        ];

        var json = FormDataJson.Write(entries);
        json.ShouldContain("Trần Thị Bích", customMessage: "Vietnamese is written as it is");
        FormDataJson.Read(json).ShouldBe(entries);

        FormDataJson.Read("""{ "nguoi": { "hoTen": "An", "tuoi": 30 }, "dongY": true, "tinh": null, "x": [1, false] }""").ShouldBe(
        [
            new FormDataEntry("nguoi.hoTen", ["An"]),
            new FormDataEntry("nguoi.tuoi", ["30"]),
            new FormDataEntry("dongY", ["true"]),
            new FormDataEntry("tinh", [""]),
            new FormDataEntry("x", ["1", "false"], IsList: true),
        ]);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("{ \"a\": ")]
    [InlineData("{ \"a\": [ { \"b\": 1 } ] }")]
    public void BadJson_IsRefused(string json)
        => Should.Throw<FormDataFormatException>(() => FormDataJson.Read(json));

    [Fact]
    public void Xfdf_RoundTrips_WithNestedNamesAndSeveralValues()
    {
        IReadOnlyList<FormDataEntry> entries =
        [
            new("nguoi.hoTen", ["Lê Văn Đức"]),
            new("nguoi.diaChi.tinh", ["HN"]),
            new("soThich", ["doc", "nhac"], IsList: true),
            new("dongY", ["Off"]),
        ];

        var xml = FormXfdf.Write(entries, "to-khai.pdf");
        xml.ShouldStartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.ShouldContain("<f href=\"to-khai.pdf\" />");
        xml.ShouldContain("<field name=\"nguoi\">");
        FormXfdf.Read(xml).ShouldBe(entries);
    }

    [Fact]
    public void Xfdf_FromAnotherProgram_IsRead()
    {
        const string Xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
              <fields>
                <field name="hoTen"><value>An</value></field>
                <field name="trong" />
              </fields>
              <annots />
            </xfdf>
            """;

        FormXfdf.Read(Xml).ShouldBe([new FormDataEntry("hoTen", ["An"]), new FormDataEntry("trong", [""])]);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><xfdf><fields><field name=\"a\"><value>&e;</value></field></fields></xfdf>")]
    [InlineData("<root />")]
    [InlineData("not xml")]
    public void BadOrDangerousXfdf_IsRefused(string xml)
        => Should.Throw<FormDataFormatException>(() => FormXfdf.Read(xml));
}
