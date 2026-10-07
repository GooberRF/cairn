using System.Globalization;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Editing;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Screenshot switches for the timeline, the inspectors and the layered preview:
/// <c>--select-keys all|bone|bone:&lt;name&gt;|time:&lt;ticks&gt;|&lt;name&gt;:&lt;from&gt;-&lt;to&gt;</c> (key selection;
/// "bone" = every key of the selected bone), <c>--layer &lt;state clip&gt;</c> (play as action over that
/// state), <c>--rows &lt;filter&gt;</c> (timeline bone filter).
/// </summary>
internal static class EditingScreenshotSteps
{
    [ScreenshotStep(10)]
    public static async Task Apply(ScreenshotContext ctx)
    {
        if (ctx.Clip is not { } doc) return;
        if (ctx.Extra("rows") is { } filter) doc.Timeline.Filter = filter;
        if (ctx.Extra("select-keys") is { } spec)
        {
            var clip = doc.Current;
            KeySelection selection = KeySelection.Empty;
            if (spec.Equals("all", StringComparison.OrdinalIgnoreCase)) selection = KeySelection.All(clip);
            else if (spec.Equals("bone", StringComparison.OrdinalIgnoreCase)) selection = KeySelection.Bones(clip, doc.Selection.Bones);
            else if (spec.StartsWith("time:", StringComparison.OrdinalIgnoreCase) && int.TryParse(spec[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int t))
                selection = doc.Timeline.KeysAtTime(t);
            else if (spec.StartsWith("bone:", StringComparison.OrdinalIgnoreCase) && doc.FittingSkeleton?.IndexOf(spec[5..]) is int b and >= 0)
                selection = KeySelection.Bones(clip, [b]);
            else if (spec.LastIndexOf(':') is int colon and > 0 && doc.FittingSkeleton?.IndexOf(spec[..colon]) is int rb and >= 0
                     && spec[(colon + 1)..].Split('-') is [var a, var z]
                     && int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int from)
                     && int.TryParse(z, NumberStyles.Integer, CultureInfo.InvariantCulture, out int to))
                selection = KeySelection.InTimeRange(clip, from, to, [rb]);
            else ctx.Log($"--select-keys '{spec}' not understood (all, bone, bone:<name>, time:<ticks>, <name>:<from>-<to>)");
            doc.SetKeySelection(selection);
            ctx.Log($"selected {selection.Count} keys");
        }
        if (ctx.Extra("layer") is { } state)
        {
            bool ok = await doc.Layered.UseStateAsync(state);
            ctx.Log(ok ? $"layered preview over {state}" : $"layered preview: '{state}' is not offered");
        }
    }
}
