using System.Windows;
using Cairn.Rfa.Ui.ViewModels.Retargeting;
using Cairn.Rfa.Ui.Views.Retarget;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Ui.Diagnostics;

/// <summary>
/// <c>--dialog retarget</c> / <c>--dialog batch-retarget</c> factories and the <c>--batch-e2e &lt;folder&gt;</c>
/// step (phase 6). Switches: <c>--retarget-clip</c> (library clip; default the active clip document),
/// <c>--retarget-source</c> / <c>--retarget-target</c> (mesh names; default the clip's preview mesh / nurse1.v3c),
/// <c>--retarget-page 0..3</c> (setup, bone map, options, report), <c>--side-by-side 1</c>, <c>--retarget-unmap
/// &lt;bone suffix&gt;</c> (sets that target bone to none, to show a status change), <c>--retarget-preset
/// seated|standing|rotation</c>, <c>--retarget-camera yaw,pitch[,zoom]</c> and <c>--retarget-time &lt;ticks&gt;</c> (the
/// preview paused at that moment); <c>--batch-class</c> (default miner1), <c>--batch-target</c> (default nurse1.v3c),
/// <c>--batch-out</c> (default a temp folder), <c>--batch-run none|running|done</c>, <c>--batch-preset
/// auto|seated|standing|rotation</c> (also for <c>--batch-e2e</c>).
/// </summary>
internal static class RetargetScreens
{
    [ScreenshotDialog("retarget")]
    public static Window? Retarget(ScreenshotContext ctx)
    {
        var shell = ctx.Model;
        var clip = ctx.Extra("retarget-clip") is { } name ? shell.Assets.Snapshot.FindClip(name) : null;
        if (clip is null && ctx.Clip is null) clip = shell.Assets.Snapshot.FindClip("park_jeep_driver.rfa");
        var vm = new RetargetDialogViewModel(shell, clip is null ? ctx.Clip : null, clip);
        vm.Setup.Select(ctx.Extra("retarget-source"), ctx.Extra("retarget-target") ?? "nurse1.v3c");
        if (ctx.Extra("side-by-side") is "1" or "true") vm.IsSideBySide = true;
        SelectPreset(vm.Setup, ctx.Extra("retarget-preset"));
        // --retarget-camera yaw,pitch[,zoom] and --retarget-time <ticks>: the preview's view and a paused moment.
        string? camera = ctx.Extra("retarget-camera");
        string? time = ctx.Extra("retarget-time");
        if (camera is not null || time is not null)
        {
            _ = shell.Dispatcher.InvokeAsync(async () =>
            {
                await vm.SettleAsync();
                for (int i = 0; i < 100 && vm.Preview.Clip is null; i++) await Task.Delay(20);
                await Task.Delay(300);
                if (time is not null && int.TryParse(time, out int ticks))
                {
                    vm.Preview.Playback.Pause();
                    vm.Preview.Playback.Seek(ticks);
                }
                if (camera?.Split(',') is { Length: >= 2 } parts
                    && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double yaw)
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pitch))
                {
                    if (parts.Length > 2 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double zoom))
                        vm.Preview.Scene.Camera.Distance *= zoom;
                    vm.Preview.Scene.Camera.SetView(yaw, pitch);
                }
            });
        }
        string? unmap = ctx.Extra("retarget-unmap");
        if (unmap is not null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                await shell.Dispatcher.InvokeAsync(async () =>
                {
                    await vm.SettleAsync();
                    var row = vm.Setup.Map.Rows.FirstOrDefault(r => r.TargetName.EndsWith(unmap, StringComparison.OrdinalIgnoreCase));
                    if (row is not null) row.Selected = row.Options[0];
                });
            });
        }
        var window = RetargetWindow.CreateForCapture(vm);
        if (int.TryParse(ctx.Extra("retarget-page"), out int page)) window.ShowPage(page);
        return window;
    }

    [ScreenshotDialog("batch-retarget")]
    public static Window? Batch(ScreenshotContext ctx)
    {
        var shell = ctx.Model;
        var vm = new BatchRetargetViewModel(shell);
        string mode = ctx.Extra("batch-run") ?? "none";
        string className = ctx.Extra("batch-class") ?? "miner1";
        string folder = ctx.Extra("batch-out") ?? Path.Combine(Path.GetTempPath(), "Cairn-rfa-batch-capture");
        Directory.CreateDirectory(folder);
        vm.Setup.Select("ult2_guard.v3c", ctx.Extra("batch-target") ?? "nurse1.v3c");
        vm.OutputFolder = folder;
        vm.AskCollision = _ => CollisionDecision.Overwrite;
        vm.TrackBusy = mode != "running";
        var busy = mode == "done" ? Cairn.Ui.Services.BusyTracker.Begin("batch capture setup") : null;
        _ = shell.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await vm.Setup.SettleAsync();
                SelectPreset(vm.Setup, ctx.Extra("batch-preset"));
                vm.AddClass(className);
                if (mode is "running" or "done")
                {
                    for (int i = 0; i < 200 && vm.Setup.Inputs is null; i++) await Task.Delay(25);
                    _ = vm.RunAsync();
                }
            }
            finally { busy?.Dispose(); }
        });
        return BatchRetargetWindow.CreateForCapture(vm);
    }

    /// <summary>
    /// <c>--batch-e2e &lt;folder&gt;</c>: batch-retargets every clip the tables give <c>--batch-class</c> (miner1)
    /// from ult2_guard onto <c>--batch-target</c> (nurse1.v3c) through the batch view-model, writing into the
    /// folder, and logs the outcome by category.
    /// </summary>
    [ScreenshotStep(650)]
    public static async Task BatchEndToEnd(ScreenshotContext ctx)
    {
        if (ctx.Extra("batch-e2e") is not { } folder) return;
        if (!string.IsNullOrEmpty(ctx.Options.Dialog)) return;
        Directory.CreateDirectory(folder);
        var vm = new BatchRetargetViewModel(ctx.Model);
        try
        {
            string className = ctx.Extra("batch-class") ?? "miner1";
            string target = ctx.Extra("batch-target") ?? "nurse1.v3c";
            vm.Setup.Select("ult2_guard.v3c", target);
            await vm.Setup.SettleAsync();
            for (int i = 0; i < 200 && vm.Setup.Inputs is null; i++) await Task.Delay(25);
            SelectPreset(vm.Setup, ctx.Extra("batch-preset"));
            ctx.Log($"batch-e2e: preset {(vm.Setup.IsAutomatic ? "automatic (per clip)" : RetargetPresets.Title(vm.Setup.CurrentPreset))}");
            vm.OutputFolder = folder;
            vm.AskCollision = _ => CollisionDecision.Overwrite;
            int added = vm.AddClass(className);
            ctx.Log($"batch-e2e: {added} clips the tables give {className}; source ult2_guard.v3c → {target}; profiles {vm.Setup.SourceProfile?.Name} → {vm.Setup.TargetProfile?.Name}; ready: {vm.Setup.Inputs is not null} {vm.RunBlocker}");
            foreach (var row in vm.Clips.Where(c => c.HasProblem)) ctx.Log($"batch-e2e: name/clip note {row.Name} → {row.OutputName}: {row.Problem}");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var results = await vm.RunAsync();
            ctx.Log($"batch-e2e: {results.Count} results in {clock.Elapsed.TotalSeconds:0.0} s: {vm.Summary}");
            foreach (var group in results.GroupBy(r => r.Status)) ctx.Log($"batch-e2e: status {group.Key}: {group.Count()}");
            foreach (var r in results.Where(r => !r.Success)) ctx.Log($"batch-e2e: {r.Status} {r.Item.SourceName}: {r.Error}");
            var warnings = results.Where(r => r.Result is not null).SelectMany(r => r.Result!.Warnings.Select(w => (r.Item.SourceName, Warning: w))).ToList();
            foreach (var group in warnings.GroupBy(w => Category(w.Warning)))
                ctx.Log($"batch-e2e: warning ×{group.Count()} [{group.Key}] e.g. {group.First().SourceName}: {group.First().Warning}");
            foreach (var group in vm.Results.Where(r => r.Preset is not null).GroupBy(r => r.PresetText))
                ctx.Log($"batch-e2e: preset {group.Key}: {group.Count()} clips");
            var stretched = vm.Results.Where(r => r.StretchedCount > 0).ToList();
            ctx.Log($"batch-e2e: clips with a fully stretched limb: {stretched.Count} (legs {stretched.Sum(r => r.Result.Result!.Contacts.Count(c => c.Stretched && c.IsLeg))}, arms {stretched.Sum(r => r.Result.Result!.Contacts.Count(c => c.Stretched && !c.IsLeg))})");
            foreach (var r in stretched) ctx.Log($"batch-e2e: stretched {r.Clip} [{r.PresetText}]: {r.PinnedText}");
            foreach (var r in vm.Results.OrderByDescending(r => double.IsNaN(r.PinnedCm) ? -1 : r.PinnedCm).Take(8))
                ctx.Log($"batch-e2e: worst pinned contact {r.Clip} [{r.PresetText}]: {r.PinnedText}");
            var reachable = vm.Results.SelectMany(r => (r.Result.Result?.Contacts ?? []).Where(c => !c.Stretched).Select(c => (r.Clip, c))).OrderByDescending(x => x.c.MaxErrorCm).FirstOrDefault();
            if (reachable.c is not null) ctx.Log($"batch-e2e: worst pinned contact while reachable: {reachable.Clip} {reachable.c.EndBone} {reachable.c.MaxErrorCm:0.00} cm");
            var grips = vm.Results.SelectMany(r => (r.Result.Result?.Contacts ?? []).Where(c => c.HeldTo.Contains("grip", StringComparison.Ordinal)).Select(c => (r.Clip, c))).ToList();
            if (grips.Count > 0)
            {
                var worstGrip = grips.OrderByDescending(g => g.c.MaxErrorCm).First();
                ctx.Log($"batch-e2e: two-handed grip held on {grips.Count} clips; worst {worstGrip.Clip} {worstGrip.c.MaxErrorCm:0.00} cm; fully stretched off hand on {grips.Count(g => g.c.Stretched)}; mean of the per-clip max {grips.Average(g => g.c.MaxErrorCm):0.000} cm");
            }
            else ctx.Log("batch-e2e: two-handed grip: no clip held");
            var failedChecks = results.Where(r => r.Report is { StructureOk: false }).ToList();
            foreach (var r in failedChecks)
                ctx.Log($"batch-e2e: checklist FAIL {r.Item.SourceName}: {string.Join(" | ", r.Report!.Checks.Where(c => !c.Passed).Select(c => c.Message))}");
            File.WriteAllText(Path.Combine(folder, "batch_report.md"), vm.ReportMarkdown());
            ctx.Log($"batch-e2e: report written to {Path.Combine(folder, "batch_report.md")}");
        }
        finally { vm.Dispose(); }
    }

    /// <summary><c>auto|seated|standing|rotation</c> → that preset row (the user's choice, as a click would make it).</summary>
    private static void SelectPreset(RetargetSetupViewModel setup, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var row = name.ToLowerInvariant() switch
        {
            "auto" or "automatic" => setup.Presets.FirstOrDefault(p => p.IsAutomatic),
            "seated" => setup.Presets.FirstOrDefault(p => !p.IsAutomatic && p.Preset == RetargetPreset.Seated),
            "standing" or "locomotion" => setup.Presets.FirstOrDefault(p => !p.IsAutomatic && p.Preset == RetargetPreset.Locomotion),
            "rotation" => setup.Presets.FirstOrDefault(p => !p.IsAutomatic && p.Preset == RetargetPreset.RotationOnly),
            _ => null,
        };
        if (row is not null) setup.SelectedPreset = row;
    }

    private static string Category(string warning)
    {
        int cut = warning.IndexOfAny(['.', ':', '(']);
        string head = cut > 0 ? warning[..cut] : warning;
        // Names vary per clip; keep the shape of the sentence.
        return head.Length > 70 ? head[..70] : head;
    }
}
