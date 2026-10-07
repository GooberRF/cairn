using System.Diagnostics;

namespace Cairn.Atx.Tests;

/// <summary>
/// A short, fixed-seed slice of the property-based harness in <c>tools/Cairn.Atx.Fuzz</c>, so a
/// regression in the editor's text handling fails the ordinary test run rather than waiting for
/// someone to remember the harness. The full sweep is a separate, much longer job — see the README.
/// </summary>
[Collection(nameof(FuzzSliceTests))]
public class FuzzSliceTests
{
    /// <summary>Documents to generate. Enough to hit every operation; short enough to stay a test.</summary>
    private const int Documents = 400;

    [Fact]
    public void TheEditorPropertiesHoldForASliceOfGeneratedDocuments()
    {
        Fuzz.Program.Reset();
        var watch = Stopwatch.StartNew();
        Fuzz.EditorFuzz.Run(Documents);
        watch.Stop();

        Assert.True(Fuzz.Program.Cases > 0, "the harness checked nothing at all");
        Assert.True(Fuzz.Program.Failures.Count == 0,
            $"{Fuzz.Program.Failures.Count} property failure(s):{Environment.NewLine}"
            + Fuzz.Program.FailureReport());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30),
            $"the slice took {watch.Elapsed.TotalSeconds:0.0} s, which is too slow for a test");
    }
}
