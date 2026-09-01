using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class WindowsCompatibilityServiceTests
{
    [Fact]
    public void ReportHasUniqueChecksAndConsistentCoreReadiness()
    {
        var report = new WindowsCompatibilityService().Inspect();

        Assert.NotEmpty(report.Checks);
        Assert.Equal(
            report.Checks.Count,
            report.Checks.Select(check => check.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            report.Checks.Where(check => check.Required).All(check => check.Passed),
            report.ReadyForCoreCapture);
    }

    [Fact]
    public void DeclaredMinimumIncludesWindows10Version2004()
    {
        Assert.Equal(19041, WindowsCompatibilityService.MinimumBuild);
        Assert.True(
            WindowsCompatibilityService.BorderlessCaptureBuild
            > WindowsCompatibilityService.MinimumBuild);
    }
}
