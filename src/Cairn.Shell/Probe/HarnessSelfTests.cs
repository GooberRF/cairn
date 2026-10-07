using Cairn.Ui.Diagnostics;

namespace Cairn.Shell.Probe;

/// <summary>
/// Self-tests of the diagnostics harness itself: <see cref="SelfTestAttribute.Requires"/> activates the
/// command-line document of that kind, options arrive after the files, screenshot steps run once.
/// Run with probe documents on the command line, e.g. <c>--selftest --probe-module a.cairnprobe b.cairnprobe2</c>.
/// </summary>
public static class HarnessSelfTests
{
    private static int _stepRuns;

    [SelfTest("probe.requires-extension", Requires = ".cairnprobe", Order = 1)]
    public static void RequiresExtension(SelfTestContext ctx)
    {
        ctx.Check(ctx.Shell.ActiveDocument is ProbeDocument { Kind.Id: "probe" } d && d.FilePath?.EndsWith(".cairnprobe", StringComparison.OrdinalIgnoreCase) == true,
            $"the .cairnprobe file from the command line is active (active: {ctx.Shell.ActiveDocument?.DisplayName ?? "none"})");
    }

    [SelfTest("probe.requires-kind", Requires = "probe2", Order = 2)]
    public static void RequiresKind(SelfTestContext ctx)
    {
        ctx.Check(ctx.Shell.ActiveDocument is ProbeDocument { Kind.Id: "probe2" }, $"the probe2 file from the command line is active (active: {ctx.Shell.ActiveDocument?.DisplayName ?? "none"})");
        ctx.Check(ProbeModule.DocumentsAtOptions >= 1, $"diagnostic options were applied after the files opened ({ProbeModule.DocumentsAtOptions?.ToString() ?? "never"} documents then)");
    }

    /// <summary>Re-activation after a test closed every document: the runner reopens the file.</summary>
    [SelfTest("probe.requires-after-close", Requires = ".cairnprobe", Order = 3)]
    public static void RequiresAfterClose(SelfTestContext ctx)
    {
        ctx.Check(ctx.Shell.ActiveDocument is ProbeDocument { Kind.Id: "probe" }, "the .cairnprobe document is active again");
        // Discarding: in a combined run, earlier module tests may leave their documents dirty, and a save prompt would block.
        foreach (var doc in ctx.Shell.Documents.ToList()) ((ShellViewModel)ctx.Shell).CloseDiscarding(doc);
        ctx.Check(ctx.Shell.Documents.Count == 0, "closed every document (the next test needing it gets it reopened)");
    }

    [SelfTest("probe.requires-reopened", Requires = ".cairnprobe", Order = 4)]
    public static void RequiresReopened(SelfTestContext ctx) =>
        ctx.Check(ctx.Shell.ActiveDocument is ProbeDocument { Kind.Id: "probe", FilePath: not null }, "the closed command-line document was reopened from disk");

    /// <summary>Counts its runs so a capture log shows whether steps run once (it logs only over a probe document).</summary>
    [ScreenshotStep(50)]
    public static Task CountRuns(ScreenshotContext ctx)
    {
        _stepRuns++;
        if (ctx.Shell.ActiveDocument is ProbeDocument) ctx.Log($"probe screenshot step run {_stepRuns}");
        return Task.CompletedTask;
    }
}
