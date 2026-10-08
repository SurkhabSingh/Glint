using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

/// The similarity check that replaced the exact hash of the whole text.
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

    [Fact]
    public void ATickingPriceIsNotAChange()
    {
        var basis = ChangeMeter.Basis([TradingPage]);
        var ticked = TradingPage.Replace("187.42 ▲0.3%", "187.51 ▲0.4%").Replace("Bid 187.40 Ask 187.44", "Bid 187.49 Ask 187.53");

        var verdict = ChangeMeter.Compare(ticked, basis, 10_000);

        Assert.Equal(ChangeVerdictKind.Same, verdict.Kind);
    }

    [Fact]
    public void AnOcrMisreadInsideANumberIsNotAChange()
    {
        var basis = ChangeMeter.Basis(["Total due 1,050.00 on Friday"]);

        var verdict = ChangeMeter.Compare("Total due 1,O5O.00 on Friday", basis, 10_000);

        Assert.Equal(ChangeVerdictKind.Same, verdict.Kind);
    }

    [Fact]
    public void ANewLineIsSavedOnItsOwn()
    {
        var basis = ChangeMeter.Basis([TradingPage]);

        var verdict = ChangeMeter.Compare(TradingPage + "\nOrder filled · Buy 10 AAPL", basis, 10_000);

        Assert.Equal(ChangeVerdictKind.Delta, verdict.Kind);
        Assert.Equal(["Order filled · Buy 10 AAPL"], verdict.Lines);
    }

    [Fact]
    public void ADifferentScreenGetsAFullCopy()
    {
        var basis = ChangeMeter.Basis([TradingPage]);

        var verdict = ChangeMeter.Compare(
            "Portfolio\nHoldings overview\nCash balance\nMonthly statement\nTax documents",
            basis,
            10_000);

        Assert.Equal(ChangeVerdictKind.Keyframe, verdict.Kind);
    }

    [Fact]
    public void AFirstVisitAndAStaleCopyBothGetAFullCopy()
    {
        Assert.Equal(ChangeVerdictKind.Keyframe, ChangeMeter.Compare(TradingPage, new HashSet<string>(), null).Kind);

        var basis = ChangeMeter.Basis([TradingPage]);
        var later = ChangeMeter.Compare(TradingPage + "\nPrice alert set", basis, ChangeMeter.KeyframeEveryMilliseconds);
        Assert.Equal(ChangeVerdictKind.Keyframe, later.Kind);
    }

    [Fact]
    public void VideoFrameDebrisIsNotAChange()
    {
        var basis = ChangeMeter.Basis(["Cyberpunk: Edgerunners Episode 2\nComments\nRelated videos"]);

        Assert.Equal(ChangeVerdictKind.Same, ChangeMeter.Compare("Cyberpunk: Edgerunners Episode 2\n、 ミ 、\n4 一 ′ 第", basis, 10_000).Kind);
        Assert.Equal(ChangeVerdictKind.Same, ChangeMeter.Compare("、 ミ 、", new HashSet<string>(), null).Kind);
    }

    [Fact]
    public void EmbeddedObjectPlaceholdersAreNotText()
    {
        var basis = ChangeMeter.Basis(["Skip Intro and continue watching"]);

        var verdict = ChangeMeter.Compare("\uFFFC\uFFFCSkip Intro and continue watching\uFFFC", basis, 10_000);

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
        var basis = ChangeMeter.Basis([TradingPage + "\n" + ChangeMeter.InputPrefix + "Quantity 1"]);

        var verdict = ChangeMeter.Compare(TradingPage + "\n" + ChangeMeter.InputPrefix + "Quantity 10", basis, 10_000);

        Assert.Equal(ChangeVerdictKind.Delta, verdict.Kind);
        Assert.Contains(ChangeMeter.InputPrefix + "Quantity 10", verdict.Lines);
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
