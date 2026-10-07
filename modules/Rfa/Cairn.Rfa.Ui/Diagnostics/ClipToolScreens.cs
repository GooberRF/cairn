using System.Windows;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.ClipTools;
using Cairn.Rfa.Ui.Views.Dialogs.ClipTools;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.Diagnostics;

/// <summary>
/// <c>--dialog &lt;tool&gt;</c> factories for the Clip menu tools (each built with its preview active, so
/// the runner also captures the main window as <c>&lt;out&gt;_window.png</c>), and the <c>--compare
/// &lt;clip&gt;</c> screenshot step that sets up the compare ghost.
/// </summary>
internal static class ClipToolScreens
{
    private static Window? Create(ScreenshotContext ctx, Func<ClipDocumentViewModel, ClipDialogViewModel> create, Action<ClipDialogViewModel>? setup = null)
    {
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("clip tool dialogs need a clip document");
            return null;
        }
        var model = create(doc);
        setup?.Invoke(model);
        return ClipToolWindow.CreateForCapture(model);
    }

    private static double U(ScreenshotContext ctx, int ticks) => TimeFormat.ToUnit(ticks, ctx.Model.TimeUnit);

    [ScreenshotDialog("trim")]
    public static Window? Trim(ScreenshotContext ctx) => Create(ctx, d => new TrimToolViewModel(d), m =>
    {
        var vm = (TrimToolViewModel)m;
        var c = vm.Original;
        int quarter = Math.Max(RfaClip.TicksPerFrame, c.Duration / 4 / RfaClip.TicksPerFrame * RfaClip.TicksPerFrame);
        vm.From = U(ctx, c.StartTime + quarter);
        vm.To = U(ctx, c.EndTime - quarter);
    });

    [ScreenshotDialog("shift")]
    public static Window? Shift(ScreenshotContext ctx) => Create(ctx, d => new ShiftToolViewModel(d), m => ((ShiftToolViewModel)m).Delta = U(ctx, 10 * RfaClip.TicksPerFrame));

    [ScreenshotDialog("retime")]
    public static Window? Retime(ScreenshotContext ctx) => Create(ctx, d => new RetimeToolViewModel(d), m => ((RetimeToolViewModel)m).Percent = 150);

    [ScreenshotDialog("reverse")]
    public static Window? Reverse(ScreenshotContext ctx) => Create(ctx, d => new ReverseToolViewModel(d));

    [ScreenshotDialog("range")]
    public static Window? Range(ScreenshotContext ctx) => Create(ctx, d => new RecomputeRangeToolViewModel(d));

    [ScreenshotDialog("loop")]
    public static Window? Loop(ScreenshotContext ctx) => Create(ctx, d => new LoopToolViewModel(d));

    [ScreenshotDialog("resample")]
    public static Window? Resample(ScreenshotContext ctx) => Create(ctx, d => new ResampleToolViewModel(d), m => ((ResampleToolViewModel)m).Fps = 10);

    [ScreenshotDialog("reduce")]
    public static Window? Reduce(ScreenshotContext ctx) => Create(ctx, d => new ReduceToolViewModel(d), m => ((ReduceToolViewModel)m).RotationTolerance = 0.5);

    [ScreenshotDialog("mirror")]
    public static Window? Mirror(ScreenshotContext ctx) => Create(ctx, d => new MirrorToolViewModel(d));

    [ScreenshotDialog("offset")]
    public static Window? Offset(ScreenshotContext ctx)
    {
        // Offset works on the selected bones: pick an arm when --select-bone did not choose one.
        if (ctx.Clip is { Selection.Count: 0 } doc && doc.FittingSkeleton is { } s)
        {
            int arm = Enumerable.Range(0, s.Count).FirstOrDefault(i => s.Names[i].Contains("arm", StringComparison.OrdinalIgnoreCase), 0);
            doc.Selection.Select(arm);
        }
        return Create(ctx, d => new OffsetToolViewModel(d), m => ((OffsetToolViewModel)m).Yaw = 30);
    }

    [ScreenshotDialog("root")]
    public static Window? Root(ScreenshotContext ctx) => Create(ctx, d => new RootMotionToolViewModel(d));

    [ScreenshotDialog("lengths")]
    public static Window? Lengths(ScreenshotContext ctx) => Create(ctx, d => new BoneLengthsToolViewModel(d));

    [ScreenshotDialog("conform")]
    public static Window? Conform(ScreenshotContext ctx) => Create(ctx, d => new ConformToolViewModel(d), m =>
    {
        var vm = (ConformToolViewModel)m;
        string? wanted = ctx.Extra("conform-mesh");
        vm.Meshes.Selected = (wanted is null ? null : vm.Meshes.All.FirstOrDefault(x => string.Equals(x.Mesh.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            ?? vm.Meshes.All.FirstOrDefault(x => x.Mesh.BoneCount != vm.Original.BoneCount)
            ?? vm.Meshes.All.FirstOrDefault();
    });

    [ScreenshotDialog("weights")]
    public static Window? Weights(ScreenshotContext ctx) => Create(ctx, d => new WeightsToolViewModel(d), m =>
    {
        var vm = (WeightsToolViewModel)m;
        if (vm.HasSkeleton) vm.Target = WeightTarget.UpperBody;
        vm.Weight = 5;
    });

    [ScreenshotDialog("normalize")]
    public static Window? Normalize(ScreenshotContext ctx) => Create(ctx, d => new NormalizeToolViewModel(d));

    [ScreenshotDialog("compare")]
    public static Window? Compare(ScreenshotContext ctx) => Create(ctx, d => new CompareDialogViewModel(d), m =>
    {
        var vm = (CompareDialogViewModel)m;
        string? wanted = ctx.Extra("compare");
        vm.Clips.Selected = (wanted is null ? null : vm.Clips.All.FirstOrDefault(x => Matches(x.Clip.Name, wanted)))
            ?? vm.Clips.All.FirstOrDefault();
    });

    /// <summary><c>--compare &lt;clip&gt;</c>: compares the active clip document with a library clip (the ghost and the chip).</summary>
    [ScreenshotStep(600)]
    public static async Task CompareStep(ScreenshotContext ctx)
    {
        // With --dialog compare the dialog itself picks the clip.
        if (string.Equals(ctx.Options.Dialog, "compare", StringComparison.OrdinalIgnoreCase)) return;
        if (ctx.Extra("compare") is not { } name) return;
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("--compare needs a clip document");
            return;
        }
        var clip = ctx.Model.Assets.Snapshot.Clips.FirstOrDefault(c => Matches(c.Name, name));
        if (clip is null)
        {
            ctx.Log($"--compare: '{name}' is not in the library");
            return;
        }
        try
        {
            var loaded = await ctx.Model.Assets.LoadClipAsync(clip);
            doc.Compare.Set(loaded, clip.Name);
            ctx.Log($"compare: {clip.Name} ({loaded.BoneCount} bones, {loaded.Duration} ticks) as a ghost");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Cairn.Formats.AssetFormatException)
        {
            ctx.Log($"--compare: {clip.Name} could not be read: {ex.Message}");
        }
    }

    private static bool Matches(string fileName, string wanted) =>
        string.Equals(fileName, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileNameWithoutExtension(fileName), wanted, StringComparison.OrdinalIgnoreCase);
}
