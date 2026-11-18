using W11CleanerTool.Core;
using W11CleanerTool.Core.Checks;
using Xunit.Abstractions;

namespace W11CleanerTool.Tests;

/// <summary>
/// Fa girare i controlli veri (sola lettura) sulla macchina dei test, per vedere che non si rompano.
/// I risultati finiscono nel log della build.
/// </summary>
public class SmokeTests(ITestOutputHelper output)
{
    private void Print(IEnumerable<CheckResult> results)
    {
        foreach (var r in results)
        {
            output.WriteLine($"[{r.Status}] {r.Area} · {r.Title}: {r.Explanation} || {r.Detail}");
        }
    }

    [Fact]
    public void GamingChecks_Run()
    {
        var results = GamingChecks.Run();
        Print(results);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void HardwareChecks_Run()
    {
        var results = HardwareChecks.Run();
        Print(results);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void CrashReport_Build()
    {
        var report = CrashReport.Build(days: 7);
        Print(report.Results);
        output.WriteLine(report.Text);
        Assert.Equal(5, report.Results.Count);
        Assert.Contains("# Rapporto crash", report.Text);
        Assert.DoesNotContain(Environment.MachineName, report.Text, StringComparison.OrdinalIgnoreCase);
    }
}
