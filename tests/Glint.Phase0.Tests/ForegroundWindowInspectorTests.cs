using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class ForegroundWindowInspectorTests
{
    [Theory]
    [InlineData(100u, 100, null, true)]
    [InlineData(200u, 100, null, false)]
    [InlineData(200u, 100, 200, true)]
    [InlineData(100u, 100, 200, true)]
    [InlineData(300u, 100, 200, false)]
    public void TreatsCurrentAndHostProcessesAsSelf(
        uint windowProcessId,
        int currentProcessId,
        int? hostProcessId,
        bool expected)
    {
        Assert.Equal(
            expected,
            ForegroundWindowInspector.IsSelfProcess(windowProcessId, currentProcessId, hostProcessId));
    }
}
