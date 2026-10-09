using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

/// What a look adds: the lines its page did not hold yet.
public sealed class ChangeDetectionTests
{
    private const string TradingPage =
        """
        AAPL Apple Inc
        187.42 ▲0.3%
        Bid 187.40 Ask 187.44
        Buy Sell
        Order book
        """;

    /// The keys a page holds once it has stored these texts.
    private static HashSet<string> Known(params string[] texts) =>
        texts.SelectMany(PageLines.Of).Select(line => line.Key).ToHashSet(StringComparer.Ordinal);

    private static ChangeVerdict Compare(string text, IReadOnlySet<string> known) =>
        ChangeMeter.Compare(PageLines.Of(text), known);

    [Fact]
    public void ATickingPriceIsNotAChange()
    {
        var ticked = TradingPage.Replace("187.42 ▲0.3%", "187.51 ▲0.4%").Replace("Bid 187.40 Ask 187.44", "Bid 187.49 Ask 187.53");

        Assert.Equal(ChangeVerdictKind.Same, Compare(ticked, Known(TradingPage)).Kind);
    }

    [Fact]
    public void AnOcrMisreadInsideANumberIsNotAChange()
    {
        var verdict = Compare("Total due 1,O5O.00 on Friday", Known("Total due 1,050.00 on Friday"));

        Assert.Equal(ChangeVerdictKind.Same, verdict.Kind);
    }

    [Fact]
    public void ANewLineIsSavedOnItsOwn()
    {
        var verdict = Compare(TradingPage + "\nOrder filled · Buy 10 AAPL", Known(TradingPage));

        Assert.Equal(ChangeVerdictKind.New, verdict.Kind);
        Assert.Equal(["Order filled · Buy 10 AAPL"], verdict.Lines.Select(line => line.Text));
    }

    [Fact]
    public void AFirstVisitStoresEveryLineAsItWasRead()
    {
        var verdict = Compare(TradingPage, new HashSet<string>());

        Assert.Equal(ChangeVerdictKind.New, verdict.Kind);
        Assert.Equal(5, verdict.Lines.Count);
        // Compared by its tidied key, stored as it was on screen.
        Assert.Contains(verdict.Lines, line => line.Text == "187.42 ▲0.3%");
    }

    [Fact]
    public void ADifferentScreenStoresOnlyItsOwnLines()
    {
        var verdict = Compare(
            "Portfolio\nHoldings overview\nOrder book\nMonthly statement",
            Known(TradingPage));

        Assert.Equal(["Portfolio", "Holdings overview", "Monthly statement"], verdict.Lines.Select(line => line.Text));
    }

    [Fact]
    public void VideoFrameDebrisIsNotAChange()
    {
        var known = Known("Cyberpunk: Edgerunners Episode 2\nComments\nRelated videos");

        Assert.Equal(ChangeVerdictKind.Same, Compare("Cyberpunk: Edgerunners Episode 2\n、 ミ 、\n4 一 ′ 第", known).Kind);
        Assert.Equal(ChangeVerdictKind.Same, Compare("、 ミ 、", new HashSet<string>()).Kind);
    }

    [Fact]
    public void EmbeddedObjectPlaceholdersAreNotText()
    {
        var verdict = Compare("￼￼Skip Intro and continue watching￼", Known("Skip Intro and continue watching"));

        Assert.Equal(ChangeVerdictKind.Same, verdict.Kind);
    }

    [Theory]
    [InlineData("✓ Payment successful", "payment successful")]
    [InlineData("Message sent", "message sent")]
    [InlineData("Order filled · Buy 10 AAPL", "order filled")]
    [InlineData("Glint suggests done when it sees \"Payment successful\" or \"Message sent\".", null)]
    [InlineData("\"Payment successful\", \"Message sent\"", null)]
    [InlineData("When Glint later sees Message sent or Payment successful, it suggests marking it done.", null)]
    public void ConfirmationsAreBannersNotMentions(string line, string? expected)
    {
        Assert.Equal(expected, ActivityEvidence.FindConfirmation(line));
    }

    [Fact]
    public void NumbersTheUserTypesAreKept()
    {
        var known = Known(TradingPage + "\n" + ChangeMeter.InputPrefix + "Quantity 1");

        var verdict = Compare(TradingPage + "\n" + ChangeMeter.InputPrefix + "Quantity 10", known);

        Assert.Equal(ChangeVerdictKind.New, verdict.Kind);
        var typed = Assert.Single(verdict.Lines);
        Assert.Equal(ChangeMeter.InputPrefix + "Quantity 10", typed.Text);
        Assert.True(typed.Typed);
    }

    [Fact]
    public void AnActivitysTextReadsAsItsScreenThenWhatWasNew()
    {
        const long start = 10_000;
        PageChunk Chunk(long id, string text, long first, string scan, bool you = false) =>
            new(id, "chrome|mail|inbox", text, first, first + 60_000, scan, false, you);

        var texts = ActivityTexts.Group(
            [
                Chunk(1, "Inbox (3)", 1_000, "earlier"),
                Chunk(2, "Meeting moved to Friday", start + 1_000, "look-1"),
                Chunk(3, "Thanks, will do", start + 5_000, "look-2", you: true)
            ],
            start);

        // Already on the page when it began: its screen.
        Assert.Equal(CaptureChange.Keyframe, texts[0].Change);
        Assert.Equal("Inbox (3)", texts[0].Text);
        // What appeared during it, look by look, the user's own reply marked.
        Assert.Equal([CaptureChange.Delta, CaptureChange.Delta], texts.Skip(1).Select(text => text.Change));
        Assert.True(texts[2].UserCaused);
    }

    [Fact]
    public void NothingMovedMeansNothingToRead()
    {
        var frame = Frame(cell => 40);

        var diff = FrameSignature.Compare(frame, Frame(cell => 40), null);

        Assert.True(diff.NothingMoved);
        Assert.False(diff.Moved);
    }

    [Fact]
    public void ASpotThatAlwaysMovesIsLearnedAsLive()
    {
        // A ticker in one corner changes on every look.
        byte[]? counts = null;
        var previous = Frame(cell => 40);
        for (var look = 1; look <= FrameSignature.LiveThreshold + 1; look++)
        {
            var value = (byte)(40 + (look % 2 == 0 ? 0 : 60));
            var current = Frame(cell => cell < 8 ? value : (byte)40);
            var diff = FrameSignature.Compare(previous, current, counts);
            counts = diff.LiveCounts;
            previous = current;
        }

        var tick = FrameSignature.Compare(previous, Frame(cell => cell < 8 ? (byte)200 : (byte)40), counts);
        Assert.True(tick.OnlyLiveCells);

        // Something new elsewhere on the page is not "only live".
        var message = FrameSignature.Compare(previous, Frame(cell => cell is > 500 and < 520 ? (byte)200 : (cell < 8 ? previous.Cells[cell] : (byte)40)), counts);
        Assert.False(message.OnlyLiveCells);
        Assert.True(message.Moved);
    }

    [Fact]
    public void SignaturesRoundTripAndSurviveResizes()
    {
        var frame = Frame(cell => (byte)(cell % 251));
        var parsed = FrameSignature.TryParse(frame.ToBase64());

        Assert.NotNull(parsed);
        Assert.True(FrameSignature.Compare(frame, parsed, null).NothingMoved);
        var resized = new FrameSignature(frame.Cells.ToArray(), 200, 100);
        Assert.True(FrameSignature.Compare(frame, resized, null).FirstLook);
    }

    [Fact]
    public void SignaturesAreBuiltFromPixels()
    {
        const int width = 64;
        const int height = 64;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                var bright = x >= width / 2 ? (byte)255 : (byte)0;
                pixels[offset] = bright;
                pixels[offset + 1] = bright;
                pixels[offset + 2] = bright;
            }
        }

        var signature = FrameSignature.FromBgra(pixels, width, height, width * 4);

        Assert.Equal(0, signature.Cells[0]);
        Assert.Equal(255, signature.Cells[FrameSignature.GridSize - 1]);
    }

    internal static FrameSignature Frame(Func<int, byte> cell)
    {
        var cells = new byte[FrameSignature.CellCount];
        for (var index = 0; index < cells.Length; index++)
        {
            cells[index] = cell(index);
        }

        return new FrameSignature(cells, 100, 100);
    }
}
