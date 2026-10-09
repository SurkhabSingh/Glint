using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class OutcomeRulesTests
{
    [Theory]
    [InlineData("Send the logs", true)]
    [InlineData("Send Alex the logs before the 10 PM meeting tomorrow", true)]
    [InlineData("No explicit commitments were found", false)]
    [InlineData("No outstanding tasks", false)]
    [InlineData("none", false)]
    [InlineData("N/A", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void MeaningfulSeparatesContentFromDeclining(string? text, bool expected)
    {
        Assert.Equal(expected, OutcomeRules.IsMeaningful(text));
    }
}
