using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class QuickCommandTests
{
    [Theory]
    [InlineData("@glint capture this", QuickCommandKind.CaptureCurrentWindow)]
    [InlineData("start scanning", QuickCommandKind.StartScanning)]
    [InlineData("pause scanning", QuickCommandKind.PauseScanning)]
    [InlineData("open Glint", QuickCommandKind.OpenDashboard)]
    public void ParsesActionCommands(string input, QuickCommandKind expected)
    {
        Assert.Equal(expected, QuickCommandParser.Parse(input).Kind);
    }

    [Fact]
    public void ParsesSearchQuery()
    {
        var command = QuickCommandParser.Parse("@glint search deployment blockers");

        Assert.Equal(QuickCommandKind.SearchContext, command.Kind);
        Assert.Equal("deployment blockers", command.Query);
    }

    [Fact]
    public void UnknownTextFallsBackToContextSearch()
    {
        var command = QuickCommandParser.Parse("meeting with Alex");

        Assert.Equal(QuickCommandKind.SearchContext, command.Kind);
        Assert.Equal("meeting with Alex", command.Query);
    }
}
