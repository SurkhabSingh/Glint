using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class PipelineTests
{
    [Fact]
    public void CombinesAndDeduplicatesLines()
    {
        var combined = ScreenText.Combine(
            "First line\nDuplicate line",
            "duplicate line\r\nSecond line");

        Assert.Equal(
            $"First line{Environment.NewLine}Duplicate line{Environment.NewLine}Second line",
            combined);
    }
}
