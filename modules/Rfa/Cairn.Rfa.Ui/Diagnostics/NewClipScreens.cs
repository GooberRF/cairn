using System.Windows;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.ClipCreation;
using Cairn.Rfa.Ui.Views.Dialogs;

namespace Cairn.Rfa.Ui.Diagnostics;

/// <summary>
/// Diagnostics for File › New Clip…:
/// <list type="bullet">
/// <item><c>--dialog new-clip</c>: the dialog for the tab in front (<c>--new-clip-mesh &lt;mesh&gt;</c> picks a library
/// character instead, <c>--new-clip-kind action</c> makes it an action, <c>--new-clip-advanced on</c> opens Advanced and
/// scrolls the options down to it).</item>
/// <item><c>--new-clip &lt;mesh&gt;|active</c>: creates a new clip (defaults) for that library character, or for the
/// tab in front, before the capture, so the window shows the new tab.</item>
/// </list>
/// </summary>
internal static class NewClipScreens
{
    [ScreenshotDialog("new-clip")]
    public static Window? Dialog(ScreenshotContext ctx)
    {
        var vm = Model(ctx, ctx.Extra("new-clip-mesh"));
        if (string.Equals(ctx.Extra("new-clip-kind"), "action", StringComparison.OrdinalIgnoreCase)) vm.IsAction = true;
        bool advanced = ctx.Extra("new-clip-advanced") is not null;
        if (advanced) vm.IsAdvancedExpanded = true;
        return NewClipWindow.CreateForCapture(vm, scrollToEnd: advanced);
    }

    [ScreenshotStep(720)]
    public static async Task Create(ScreenshotContext ctx)
    {
        if (ctx.Extra("new-clip") is not { } target) return;
        var vm = Model(ctx, string.Equals(target, "active", StringComparison.OrdinalIgnoreCase) ? null : target);
        try
        {
            using (BusyTracker.Begin("new clip for capture")) await vm.SettleAsync();
            var document = vm.Create();
            ctx.Log(document is null
                ? $"new-clip: nothing created ({vm.Problem ?? vm.Error})"
                : $"new-clip: opened {document.DisplayName} on {vm.TargetMeshName} ({(vm.UseReference ? "pose of " + vm.References.Selected?.Clip.Name : "bind pose")})");
        }
        finally { vm.End(); }
    }

    private static NewClipViewModel Model(ScreenshotContext ctx, string? meshName)
    {
        var mesh = meshName is null ? null : ctx.Model.Assets.Snapshot.FindMesh(meshName);
        if (meshName is not null && mesh is null) ctx.Log($"new-clip: '{meshName}' is not in the library; using the default mesh");
        return new NewClipViewModel(ctx.Model, mesh);
    }
}
