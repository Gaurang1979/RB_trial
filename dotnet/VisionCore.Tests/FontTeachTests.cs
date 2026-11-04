using VisionCore;
using Xunit;

public class FontTeachTests
{
    private static readonly PointF[] NoBox = new PointF[4];

    private static PointF[] Box(float x) => new[] { new PointF(x, 10), new PointF(x + 10, 10), new PointF(x + 10, 40), new PointF(x, 40) };

    private static OcrChar Read(string c, CharStatus s, float x) => new(c, c, s == CharStatus.Ok ? 95 : 30, s, Box(x));

    private static OcrLine Line(string text, bool check, string expected, params OcrChar[] chars) =>
        new(text, expected, check, check && chars.All(c => c.Status == CharStatus.Ok), chars.Min(c => c.Score), chars, Box(0), 0, 0, 30);

    [Fact]
    public void OnlyCharactersBelowOkAreAsked()
    {
        var l = Line("A6B", false, "", Read("A", CharStatus.Ok, 0), Read("6", CharStatus.Unsure, 20), Read("B", CharStatus.Unknown, 40));
        var asks = FontTeach.Asks(l);
        Assert.Equal(new[] { true, false, false }, asks.Select(a => a.Sure));
        Assert.Equal("A", asks[0].Read);
    }

    [Fact]
    public void ComposedTextNeedsOneCharacterInEveryBox()
    {
        Assert.Equal("A67", FontTeach.Compose(new[] { "A", " 6 ", "7" }, out int bad));
        Assert.Equal(-1, bad);
        Assert.Null(FontTeach.Compose(new[] { "A", "", "7" }, out bad));   // an unsure box left empty
        Assert.Equal(1, bad);
        Assert.Null(FontTeach.Compose(new[] { "A", "67" }, out bad));      // two characters in one box
        Assert.Equal(1, bad);
        Assert.Equal("₹1", FontTeach.Compose(new[] { "₹", "1" }, out _));
        Assert.Null(FontTeach.Compose(Array.Empty<string?>(), out _));
    }

    [Fact]
    public void CandidatesAreFailedCharactersWithAPlaceNeverTaughtFirst()
    {
        var checkedLine = Line("LOT A1234B", true, "LOT A12345",
            new OcrChar("4", "4", 95, CharStatus.Ok, Box(0)),
            new OcrChar("5", "B", 20, CharStatus.Unknown, Box(20)),
            new OcrChar("6", "", 0, CharStatus.Missing, NoBox));
        var readLine = Line("X1", false, "", Read("X", CharStatus.Unsure, 0), Read("1", CharStatus.Ok, 20));
        var list = FontTeach.Candidates(new[] { readLine, checkedLine });
        Assert.Equal(2, list.Count);
        Assert.Equal((1, CharStatus.Unknown, "5"), (list[0].Line, list[0].Char.Status, list[0].Should));
        Assert.Equal(new PointF(25, 25), list[0].At);                    // the box centre
        Assert.Equal((0, ""), (list[1].Line, list[1].Should));            // read only: nothing it should be
    }

    [Fact]
    public void ACharacterNeverTaughtIsExplainedAsSuch()
    {
        var l = Line("LOT A1234B", true, "LOT A12345",
            new OcrChar("4", "4", 95, CharStatus.Ok, Box(0)), new OcrChar("5", "B", 20, CharStatus.Unknown, Box(20)));
        Assert.Equal(2, TextTool.Unknown(l));
        Assert.Equal("Character 2 is not in the taught font. Teach it", TextTool.Describe(l));
        var poor = Line("A1", true, "A1", Read("A", CharStatus.Ok, 0), Read("1", CharStatus.Unsure, 20));
        Assert.Equal(0, TextTool.Unknown(poor));
        Assert.DoesNotContain("taught font", TextTool.Describe(poor));
    }

    [Fact]
    public void ReadModeFormatCheckNamesTheUntaughtCharacter()
    {
        var l = Line("LOT W12", false, "", Read("L", CharStatus.Ok, 0), Read("O", CharStatus.Ok, 10), Read("T", CharStatus.Ok, 20),
            Read("W", CharStatus.Unknown, 30), Read("1", CharStatus.Ok, 40), Read("2", CharStatus.Ok, 50));
        var (problem, explain, _) = TextTool.CheckFormats(new[] { l }, new[] { TextFormat.Parse("LOT {X:3}") }, new DateRules(), 60, DateTime.Now);
        Assert.Equal("explain.ocr.unknown_char", explain);
        Assert.Equal("Character 4 is not in the taught font. Teach it", problem);
    }
}
