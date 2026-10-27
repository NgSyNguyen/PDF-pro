using System.Text.Json;
using System.Text.Json.Serialization;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;

namespace PdfPro.Core.Tests.Annotations;

public sealed class AnnotationItemsTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));
    private static readonly RgbColor Yellow = new(255, 230, 0);

    // The options the session manifest writes operation parameters with.
    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    [Fact]
    public void EveryKind_RoundTripsThroughTheManifestJson_WithItsConversation()
    {
        var items = All();

        var json = JsonSerializer.Serialize(items, ManifestOptions);
        var back = JsonSerializer.Deserialize<AnnotationItem[]>(json, ManifestOptions)!;

        JsonSerializer.Serialize(back, ManifestOptions).ShouldBe(json);
        back.Select(i => i.GetType()).ShouldBe(items.Select(i => i.GetType()));
        back[0].Replies.ShouldHaveSingleItem().Contents.ShouldBe("Đồng ý, sửa lại số liệu");
        back[0].State.ShouldBe(ReviewState.Accepted);
        json.ShouldContain("\"$type\":\"markup\"");
        json.ShouldContain("\"kind\":\"strikeOut\"");
        json.ShouldContain("\"endEnding\":\"closedArrow\"");
        json.ShouldContain("\"box\":{\"left\":100,\"bottom\":560,\"right\":300,\"top\":640}", Case.Sensitive, "rectangles keep their four sides only");
        json.ShouldNotContain("bounds", Case.Insensitive, "computed, not stored");
    }

    [Fact]
    public void EveryKind_IsValid_AndMovesAsAWhole()
    {
        foreach (var item in All())
        {
            item.Validate().ShouldBeEmpty(item.GetType().Name);
            var moved = item.MoveBy(10, -20);
            var (before, after) = (item.Bounds, moved.Bounds);
            after.ShouldBe(new PdfRect(before.Left + 10, before.Bottom - 20, before.Right + 10, before.Top - 20), item.GetType().Name);
            moved.Id.ShouldBe(item.Id);
        }
    }

    [Fact]
    public void Bounds_CoverWhatIsDrawn()
    {
        new NoteAnnotation("n", Yellow, 1, "A", Created, Created, "", new PdfPoint(100, 500)).Bounds
            .ShouldBe(new PdfRect(100, 480, 120, 500), "the icon hangs below its top-left corner");
        Markup(MarkupKind.Highlight, new PdfRect(72, 700, 300, 712), new PdfRect(72, 686, 150, 698)).Bounds
            .ShouldBe(new PdfRect(72, 686, 300, 712));
        FreeText([new PdfPoint(50, 50), new PdfPoint(80, 90)]).Bounds
            .ShouldBe(new PdfRect(50, 50, 300, 640), "the callout's tip is part of it");
    }

    [Fact]
    public void TheLastReview_IsTheState()
    {
        var item = Markup(MarkupKind.Highlight, new PdfRect(0, 0, 10, 10)) with
        {
            Reviews =
            [
                new AnnotationReview("r1", "Lan", Created, ReviewState.Rejected),
                new AnnotationReview("r2", "Minh", Created.AddHours(1), ReviewState.Completed),
            ],
        };

        item.State.ShouldBe(ReviewState.Completed);
        (item with { Reviews = [] }).State.ShouldBe(ReviewState.None);
    }

    public static TheoryData<string, string> Problems => new()
    {
        { "no name", "The name must be 1 to 128 characters." },
        { "opacity", "Opacity must be between 0 and 1." },
        { "long comment", "A comment can have at most 20000 characters." },
        { "long reply", "A comment can have at most 20000 characters." },
        { "huge", "The annotation must have finite coordinates within 14400 points." },
        { "no quads", "Marked text needs at least one line." },
        { "empty box", "The text box must have a size." },
        { "tiny font", "Font size must be between 1 and 400 points." },
        { "odd rotation", "Rotation must be 0, 90, 180 or 270." },
        { "one-point callout", "A callout line has 2 or 3 points." },
        { "empty stroke", "A drawing needs at least one stroke of points." },
        { "zero width", "Line width must be more than 0 and at most 72 points." },
        { "dot line", "A line needs two different points." },
        { "two-point polygon", "A polygon needs at least 3 points." },
        { "one-point polyline", "A polyline needs at least 2 points." },
        { "empty shape", "The shape must have a size." },
        { "stamp with both", "A stamp has either words or a picture." },
        { "stamp with neither", "A stamp has either words or a picture." },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void Validate_ExplainsWhatIsWrong(string problem, string message)
    {
        var highlight = Markup(MarkupKind.Highlight, new PdfRect(72, 700, 300, 712));
        var stamp = new StampAnnotation("s", Yellow, 1, "A", Created, Created, "", new PdfRect(0, 0, 100, 40), "ĐÃ DUYỆT");
        AnnotationItem item = problem switch
        {
            "no name" => highlight with { Id = " " },
            "opacity" => highlight with { Opacity = 1.5 },
            "long comment" => highlight with { Contents = new string('a', AnnotationItem.MaxContentsLength + 1) },
            "long reply" => highlight with { Replies = [new AnnotationReply("r", "A", Created, new string('a', AnnotationItem.MaxContentsLength + 1))] },
            "huge" => highlight with { Quads = [TextQuad.Of(new PdfRect(0, 0, 20_000, 10))] },
            "no quads" => highlight with { Quads = [] },
            "empty box" => FreeText([]) with { Box = new PdfRect(10, 10, 10, 50) },
            "tiny font" => FreeText([]) with { Style = TextStyle.Default with { FontSize = 0.5 } },
            "odd rotation" => FreeText([]) with { Rotation = 45 },
            "one-point callout" => FreeText([new PdfPoint(1, 1)]),
            "empty stroke" => new InkAnnotation("i", Yellow, 1, "A", Created, Created, "", 2, [[]]),
            "zero width" => new InkAnnotation("i", Yellow, 1, "A", Created, Created, "", 0, [[new PdfPoint(1, 1)]]),
            "dot line" => new LineAnnotation("l", Yellow, 1, "A", Created, Created, "", new PdfPoint(5, 5), new PdfPoint(5, 5), 1),
            "two-point polygon" => new PolyAnnotation("p", Yellow, 1, "A", Created, Created, "", true, [new PdfPoint(0, 0), new PdfPoint(5, 5)], 1),
            "one-point polyline" => new PolyAnnotation("p", Yellow, 1, "A", Created, Created, "", false, [new PdfPoint(0, 0)], 1),
            "empty shape" => new ShapeAnnotation("q", Yellow, 1, "A", Created, Created, "", NoteShape.Square, new PdfRect(0, 0, 0, 0), 1, null),
            "stamp with both" => stamp with { ImagePath = "a.png" },
            "stamp with neither" => stamp with { Text = null },
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        item.Validate().ShouldContain(message);
    }

    [Fact]
    public void Quads_FollowTheOrderViewersRead()
    {
        var quad = TextQuad.Of(new PdfRect(10, 20, 110, 32));

        quad.UpperLeft.ShouldBe(new PdfPoint(10, 32));
        quad.UpperRight.ShouldBe(new PdfPoint(110, 32));
        quad.LowerLeft.ShouldBe(new PdfPoint(10, 20));
        quad.LowerRight.ShouldBe(new PdfPoint(110, 20));
        quad.Bounds.ShouldBe(new PdfRect(10, 20, 110, 32));
    }

    private static AnnotationItem[] All() =>
    [
        Markup(MarkupKind.Highlight, new PdfRect(72, 700, 300, 712)) with
        {
            Replies = [new AnnotationReply("a1-r1", "Minh", Created.AddMinutes(5), "Đồng ý, sửa lại số liệu")],
            Reviews = [new AnnotationReview("a1-s1", "Minh", Created.AddMinutes(6), ReviewState.Accepted)],
        },
        Markup(MarkupKind.StrikeOut, new PdfRect(72, 680, 200, 692)),
        new NoteAnnotation("a3", Yellow, 1, "Lan", Created, Created, "Kiểm tra lại ngày ký", new PdfPoint(500, 800)),
        FreeText([new PdfPoint(50, 50), new PdfPoint(80, 90), new PdfPoint(100, 600)]),
        new InkAnnotation("a5", RgbColor.Red, 0.8, "Lan", Created, Created, "", 2, [[new PdfPoint(100, 100), new PdfPoint(120, 130)], [new PdfPoint(150, 100)]]),
        new ShapeAnnotation("a6", RgbColor.Red, 1, "Lan", Created, Created, "", NoteShape.Circle, new PdfRect(300, 300, 400, 360), 1.5, Yellow),
        new LineAnnotation("a7", RgbColor.Red, 1, "Lan", Created, Created, "", new PdfPoint(100, 400), new PdfPoint(250, 450), 2, EndEnding: LineEnding.ClosedArrow),
        new PolyAnnotation("a8", RgbColor.Red, 1, "Lan", Created, Created, "", true, [new PdfPoint(0, 0), new PdfPoint(50, 0), new PdfPoint(25, 40)], 1),
        new StampAnnotation("a9", RgbColor.Red, 1, "Lan", Created, Created, "", new PdfRect(400, 700, 550, 760), "ĐÃ DUYỆT\n27/10/2026"),
    ];

    private static MarkupAnnotation Markup(MarkupKind kind, params PdfRect[] lines)
        => new($"m-{kind}", Yellow, 0.6, "Lan", Created, Created, "Số liệu sai", kind, [.. lines.Select(TextQuad.Of)], "Số: 12/2026/QĐ-UBND");

    private static FreeTextAnnotation FreeText(IReadOnlyList<PdfPoint> callout)
        => new("a4", RgbColor.Black, 1, "Lan", Created, Created, "Ghi chú tiếng Việt", new PdfRect(100, 560, 300, 640), TextStyle.Default, RgbColor.Red, RgbColor.White, callout);
}
