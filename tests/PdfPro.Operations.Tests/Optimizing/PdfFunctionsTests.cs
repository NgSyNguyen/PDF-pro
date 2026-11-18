using System.Text;
using PdfPro.Operations.Optimizing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Tests.Optimizing;

/// <summary>PDF functions (ISO 32000 7.10) as tint transforms and shadings use them.</summary>
public sealed class PdfFunctionsTests : IDisposable
{
    private readonly PdfDocument _document = new();

    public void Dispose() => _document.Dispose();

    [Fact]
    public void AnExponential_RisesFromC0ToC1()
    {
        var function = PdfFunction.Read(Dictionary(2, ("/Domain", Numbers(0, 1)), ("/C0", Numbers(0, 1)), ("/C1", Numbers(1, 0)), ("/N", new PdfInteger(2)))).ShouldNotBeNull();

        function.Outputs.ShouldBe(2);
        function.Evaluate([0.5]).ShouldBe([0.25, 0.75]);
        function.Evaluate([7]).ShouldBe([1, 0], "inputs are clipped to the domain");
    }

    [Fact]
    public void AStitching_UsesTheFunctionOfEachSubdomain_WithItsEncoding()
    {
        var up = Dictionary(2, ("/Domain", Numbers(0, 1)), ("/N", new PdfInteger(1)));
        var stitching = Dictionary(
            3,
            ("/Domain", Numbers(0, 1)),
            ("/Functions", new PdfArray(_document, up, up)),
            ("/Bounds", Numbers(0.5)),
            ("/Encode", Numbers(0, 1, 1, 0)));

        var function = PdfFunction.Read(stitching).ShouldNotBeNull();

        function.Evaluate([0.25])[0].ShouldBe(0.5, 1e-9);
        function.Evaluate([0.75])[0].ShouldBe(0.5, 1e-9, "the second half runs down again");
    }

    [Fact]
    public void Samples_AreInterpolated_InOneAndTwoDimensions()
    {
        var line = Dictionary(0, ("/Domain", Numbers(0, 1)), ("/Range", Numbers(0, 1)), ("/Size", Numbers(2)), ("/BitsPerSample", new PdfInteger(8)));
        line.CreateStream([0, 255]);
        var square = Dictionary(0, ("/Domain", Numbers(0, 1, 0, 1)), ("/Range", Numbers(0, 1)), ("/Size", Numbers(2, 2)), ("/BitsPerSample", new PdfInteger(8)));
        square.CreateStream([0, 255, 255, 255]);

        PdfFunction.Read(line).ShouldNotBeNull().Evaluate([0.25])[0].ShouldBe(0.25, 1e-9);
        var plane = PdfFunction.Read(square).ShouldNotBeNull();
        plane.Evaluate([0, 0])[0].ShouldBe(0, 1e-9);
        plane.Evaluate([0.5, 0.5])[0].ShouldBe(0.75, 1e-9);
    }

    [Fact]
    public void Samples_OfTwelveBits_AreReadMostSignificantBitFirst()
    {
        var function = Dictionary(0, ("/Domain", Numbers(0, 1)), ("/Range", Numbers(0, 1)), ("/Size", Numbers(2)), ("/BitsPerSample", new PdfInteger(12)));
        function.CreateStream([0x00, 0x0F, 0xFF]); // 0x000, 0xFFF

        PdfFunction.Read(function).ShouldNotBeNull().Evaluate([1])[0].ShouldBe(1, 1e-9);
    }

    [Theory]
    [InlineData("{ 2 mul 1 add }", 0.25, 1.5)]
    [InlineData("{ dup 0.5 gt { pop 1 } { pop 0 } ifelse }", 0.75, 1)]
    [InlineData("{ dup 0.5 gt { pop 1 } { pop 0 } ifelse }", 0.25, 0)]
    [InlineData("{ 1 exch sub 2 exp }", 0.5, 0.25)]
    [InlineData("{ 3 1 2 3 1 roll pop pop }", 0, 2)]
    [InlineData("{ dup 1 index add add }", 0.5, 1.5)]
    [InlineData("{ 0.5 lt { 0.2 } { 0.8 } ifelse }", 0.1, 0.2)]
    public void ACalculator_RunsItsProgram(string program, double input, double expected)
    {
        var function = Calculator(program, range: [0, 2]).ShouldNotBeNull();

        function.Evaluate([input])[^1].ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void ACalculator_ThatFails_SaysSo()
    {
        var broken = Calculator("{ pop pop pop }", range: [0, 1]).ShouldNotBeNull();
        Should.Throw<FormatException>(() => broken.Evaluate([0.5]));
        Calculator("{ 1 add", range: [0, 1]).ShouldBeNull("the program is not closed");
    }

    [Fact]
    public void AnArrayOfFunctions_IsOneFunctionOfSeveralOutputs()
    {
        var a = Dictionary(2, ("/Domain", Numbers(0, 1)), ("/N", new PdfInteger(1)));
        var b = Dictionary(2, ("/Domain", Numbers(0, 1)), ("/C0", Numbers(1)), ("/C1", Numbers(0)), ("/N", new PdfInteger(1)));

        var function = PdfFunction.Read(new PdfArray(_document, a, b)).ShouldNotBeNull();

        function.Outputs.ShouldBe(2);
        function.Evaluate([0.25]).ShouldBe([0.25, 0.75]);
    }

    private PdfFunction? Calculator(string program, double[] range)
    {
        var function = Dictionary(4, ("/Domain", Numbers(0, 1)), ("/Range", Numbers(range)));
        function.CreateStream(Encoding.ASCII.GetBytes(program));
        return PdfFunction.Read(function);
    }

    private PdfDictionary Dictionary(int type, params (string Key, PdfItem Value)[] entries)
    {
        var dictionary = new PdfDictionary(_document);
        dictionary.Elements.SetInteger("/FunctionType", type);
        foreach (var (key, value) in entries)
        {
            dictionary.Elements[key] = value;
        }

        return dictionary;
    }

    private PdfArray Numbers(params double[] numbers) => new(_document, [.. numbers.Select(n => new PdfReal(n))]);
}
