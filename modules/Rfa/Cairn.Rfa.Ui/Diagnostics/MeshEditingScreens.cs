using System.Globalization;
using System.Windows;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.MeshEditing;
using Cairn.Rfa.Ui.Views.Dialogs.MeshEditing;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.Diagnostics;

/// <summary>
/// Screenshot support for mesh document editing (phase 6): <c>--mesh-node &lt;kind&gt;:&lt;index&gt;</c> (or
/// <c>kind:submesh:index</c>) selects a Structure node so its editor shows; <c>--dialog reorder-bones</c>
/// (the warning, with an open clip of the mesh's bone count made to preview it) and <c>--dialog
/// texture-browser</c> (<c>--texture-filter</c> narrows it).
/// </summary>
internal static class MeshEditingScreens
{
    /// <summary>Parses "bone:3", "sphere:0", "material:0", "material:1:2", "lod:1", "prop:0", "submesh:0", "batch:0", "texture:0", "header".</summary>
    internal static MeshNodeRef? ParseNode(string text)
    {
        var parts = text.Split(':', StringSplitOptions.TrimEntries);
        int[] numbers = [.. parts.Skip(1).Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)];
        if (numbers.Any(v => v < 0)) return null;
        int a = numbers.Length > 0 ? numbers[0] : 0;
        int sub = numbers.Length > 1 ? numbers[0] : 0;
        int idx = numbers.Length > 1 ? numbers[1] : a;
        return parts[0].ToLowerInvariant() switch
        {
            "header" => new MeshNodeRef(MeshNodeKind.Header),
            "bone" => new MeshNodeRef(MeshNodeKind.Bone, Index: a),
            "sphere" or "csphere" => new MeshNodeRef(MeshNodeKind.CollisionSphere, Index: a),
            "prop" => new MeshNodeRef(MeshNodeKind.PropPoint, 0, 0, a),
            "submesh" => new MeshNodeRef(MeshNodeKind.Submesh, a),
            "material" => new MeshNodeRef(MeshNodeKind.Material, sub, -1, idx),
            "lod" => new MeshNodeRef(MeshNodeKind.Lod, sub, idx),
            "batch" => new MeshNodeRef(MeshNodeKind.Batch, sub, 0, idx),
            "texture" => new MeshNodeRef(MeshNodeKind.Texture, sub, 0, idx),
            _ => null,
        };
    }

    /// <summary><c>--mesh-node &lt;kind&gt;:&lt;index&gt;</c>: selects that node in the active mesh document's Structure tab.</summary>
    [ScreenshotStep(650)]
    public static Task MeshNodeStep(ScreenshotContext ctx)
    {
        if (ctx.Extra("mesh-node") is not { } text) return Task.CompletedTask;
        if (ctx.Model.ActiveDocument is not MeshDocumentViewModel doc)
        {
            ctx.Log("--mesh-node needs a mesh document");
            return Task.CompletedTask;
        }
        if (ParseNode(text) is not { } node)
        {
            ctx.Log($"--mesh-node '{text}' is not kind:index (bone, sphere, prop, material, lod, submesh, batch, texture, header)");
            return Task.CompletedTask;
        }
        ctx.Model.IsInspectorVisible = true;
        doc.SelectedInspectorTab = doc.InspectorTabs.FirstOrDefault(t => t.Id == "structure") ?? doc.SelectedInspectorTab;
        doc.Structure.Reveal(node);
        ctx.Log($"mesh node: {node} -> {doc.Structure.Selected?.Header ?? "not found"}; editor {doc.Structure.Editor?.GetType().Name ?? "none"}");
        return Task.CompletedTask;
    }

    [ScreenshotDialog("reorder-bones")]
    public static Window? ReorderBones(ScreenshotContext ctx)
    {
        if (ctx.Model.ActiveDocument is not MeshDocumentViewModel doc || doc.Current.Bones.Length < 2)
        {
            ctx.Log("--dialog reorder-bones needs a mesh document with bones");
            return null;
        }
        // A clip with the mesh's bone count previews it, so the dialog lists it.
        if (!ReorderBonesViewModel.ClipsPreviewing(doc).Any()
            && ctx.Model.Documents.OfType<ClipDocumentViewModel>().FirstOrDefault(c => c.Current.BoneCount == doc.Current.Bones.Length) is { } clip)
        {
            clip.UsePreviewMesh(doc.Current, doc.DisplayName, doc.Folder);
        }
        int bone = doc.Structure.Selected?.Node is { Kind: MeshNodeKind.Bone } n ? n.Index : Math.Min(2, doc.Current.Bones.Length - 2);
        if (bone >= doc.Current.Bones.Length - 1) bone = doc.Current.Bones.Length - 2;
        var order = Enumerable.Range(0, doc.Current.Bones.Length).ToArray();
        (order[bone], order[bone + 1]) = (order[bone + 1], order[bone]);
        var model = new ReorderBonesViewModel(doc, order, $"Move bone '{doc.Current.Bones[bone].Name.Text}' down", bone);
        return ReorderBonesDialog.CreateForCapture(model);
    }

    [ScreenshotDialog("texture-browser")]
    public static Window? TextureBrowser(ScreenshotContext ctx)
    {
        if (ctx.Model.ActiveDocument is not MeshDocumentViewModel doc)
        {
            ctx.Log("--dialog texture-browser needs a mesh document");
            return null;
        }
        string current = doc.Current.Submeshes.FirstOrDefault()?.Materials.FirstOrDefault()?.DiffuseMap.Text ?? string.Empty;
        var model = new TextureBrowserViewModel(ctx.Model, doc.Resolver, current);
        if (ctx.Extra("texture-filter") is { } filter) model.Filter = filter;
        return TextureBrowserDialog.CreateForCapture(model);
    }
}
