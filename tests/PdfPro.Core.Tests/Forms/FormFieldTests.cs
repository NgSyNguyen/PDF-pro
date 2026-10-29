using PdfPro.Core.Forms;

namespace PdfPro.Core.Tests.Forms;

public sealed class FormFieldTests
{
    [Theory]
    [InlineData(FormFieldKind.Text, null, FormFieldFlags.Required, true)]
    [InlineData(FormFieldKind.Text, "", FormFieldFlags.Required, true)]
    [InlineData(FormFieldKind.Text, "An", FormFieldFlags.Required, false)]
    [InlineData(FormFieldKind.CheckBox, FormField.Off, FormFieldFlags.Required, true)]
    [InlineData(FormFieldKind.CheckBox, "Yes", FormFieldFlags.Required, false)]
    [InlineData(FormFieldKind.Text, null, FormFieldFlags.None, false)]
    [InlineData(FormFieldKind.Text, null, FormFieldFlags.Required | FormFieldFlags.ReadOnly, false)]
    [InlineData(FormFieldKind.PushButton, null, FormFieldFlags.Required, false)]
    public void ARequiredField_IsMissingUntilFilled(FormFieldKind kind, string? value, FormFieldFlags flags, bool missing)
        => Field(kind, value, flags).IsMissing.ShouldBe(missing);

    [Fact]
    public void AListBox_IsFilledByItsChoices()
    {
        var list = Field(FormFieldKind.ListBox, null, FormFieldFlags.Required);

        list.IsMissing.ShouldBeTrue();
        (list with { Selected = ["Đọc sách"] }).IsMissing.ShouldBeFalse();
    }

    [Fact]
    public void ButtonsSignaturesAndReadOnlyFields_AreNotFilled()
    {
        Field(FormFieldKind.PushButton, null, FormFieldFlags.None).IsFillable.ShouldBeFalse();
        Field(FormFieldKind.Signature, null, FormFieldFlags.None).IsFillable.ShouldBeFalse();
        Field(FormFieldKind.Text, null, FormFieldFlags.ReadOnly).IsFillable.ShouldBeFalse();
        Field(FormFieldKind.ComboBox, null, FormFieldFlags.None).IsFillable.ShouldBeTrue();
    }

    [Fact]
    public void Values_AreEqualByTheirChoices()
    {
        new FormValue("a", "x", ["1", "2"]).ShouldBe(new FormValue("a", "x", ["1", "2"]));
        new FormValue("a", "x", ["1", "2"]).ShouldNotBe(new FormValue("a", "x", ["2", "1"]));
        new FormValue("a", "x").ShouldNotBe(new FormValue("a", "x", []));
    }

    private static FormField Field(FormFieldKind kind, string? value, FormFieldFlags flags)
        => new("f", kind, value, [], flags, [], []);
}
