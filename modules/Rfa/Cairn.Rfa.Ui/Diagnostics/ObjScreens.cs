using System.Windows;
using Cairn.Rfa.Interchange;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.ObjTools;
using Cairn.Rfa.Ui.Views.Dialogs.Obj;
using ShellScreenshotContext = Cairn.Ui.Diagnostics.ScreenshotContext;

namespace Cairn.Rfa.Ui.Diagnostics;

/// <summary>
/// <c>--dialog</c> factories for the OBJ dialogs:
/// <list type="bullet">
/// <item><c>--dialog rfa.obj-import</c>: the import dialog for <c>--obj &lt;file&gt;</c>, or for a Blender-style sample
/// written to %TEMP% when no file is given (<c>--obj-kind character</c> starts with the character build, using the
/// skeleton of the open mesh when there is one).</item>
/// <item><c>--dialog rfa.obj-import-errors</c>: a sample whose texture name is too long, so the pre-flight shows an error.</item>
/// <item><c>--dialog rfa.obj-export</c>: the export dialog for the active mesh document; <c>--obj-export-to &lt;file&gt;</c>
/// also runs the export so the result shows.</item>
/// </list>
/// </summary>
internal static class ObjScreens
{
    [ScreenshotDialog("rfa.obj-import")]
    public static Window? Import(ShellScreenshotContext ctx)
    {
        string path = ctx.Options.TryGetValue("obj", out var given) ? given : ObjSamples.WriteBlenderSample(Path.Combine(Path.GetTempPath(), "cairn-obj-sample"));
        bool character = ctx.Options.TryGetValue("obj-kind", out var kind) && string.Equals(kind, "character", StringComparison.OrdinalIgnoreCase);
        var model = new ObjMeshImportViewModel(RfaModule.Workspace, path, null, character ? Formats.V3d.V3dKind.Character : null);
        if (character && model.HasKeepSources) model.KeepSkeleton = true;
        return ObjMeshImportWindow.CreateForCapture(model);
    }

    [ScreenshotDialog("rfa.obj-import-errors")]
    public static Window? ImportErrors(ShellScreenshotContext ctx)
    {
        string path = ObjSamples.WriteErrorSample(Path.Combine(Path.GetTempPath(), "cairn-obj-sample"));
        return ObjMeshImportWindow.CreateForCapture(new ObjMeshImportViewModel(RfaModule.Workspace, path));
    }

    [ScreenshotDialog("rfa.obj-export")]
    public static Window? Export(ShellScreenshotContext ctx)
    {
        if (RfaModule.Workspace.ActiveDocument is not MeshDocumentViewModel document)
        {
            ctx.Log("rfa.obj-export needs a mesh document");
            return null;
        }
        ObjExportViewModel model;
        try
        {
            model = new ObjExportViewModel(RfaModule.Workspace, document);
        }
        catch (ArgumentException ex)
        {
            ctx.Log("rfa.obj-export refused: " + ex.Message);
            return null;
        }
        if (ctx.Options.TryGetValue("obj-export-to", out var target))
        {
            model.OutputPath = target;
            _ = RunExport(ctx, model);
        }
        return ObjExportWindow.CreateForCapture(model);
    }

    private static async Task RunExport(ShellScreenshotContext ctx, ObjExportViewModel model)
    {
        using var busy = BusyTracker.Begin("OBJ export for capture");
        var result = await model.ExportAsync();
        ctx.Log(result is null
            ? $"rfa.obj-export: no result ({model.Error ?? model.ProgressText})"
            : $"rfa.obj-export: wrote {string.Join(", ", model.LastFiles.Select(Path.GetFileName))}; {result.Warnings.Length} warnings");
    }
}

/// <summary>Sample OBJ files the self-test and the captures write (shaped like Blender's output).</summary>
internal static class ObjSamples
{
    /// <summary>
    /// A Blender 4.x style export into <paramref name="folder"/>: a crate (quads, Y-up, -Z forward) and a floor, with a
    /// .mtl naming a PNG texture written beside it. Returns the .obj path.
    /// </summary>
    public static string WriteBlenderSample(string folder)
    {
        Directory.CreateDirectory(folder);
        string obj = Lines(
            "# Blender 4.2.0",
            "# www.blender.org",
            "mtllib crate_scene.mtl",
            "o Crate",
            "v 0.500000 1.000000 -0.500000", "v 0.500000 0.000000 -0.500000", "v 0.500000 1.000000 0.500000", "v 0.500000 0.000000 0.500000",
            "v -0.500000 1.000000 -0.500000", "v -0.500000 0.000000 -0.500000", "v -0.500000 1.000000 0.500000", "v -0.500000 0.000000 0.500000",
            "vn -0.0000 1.0000 -0.0000", "vn -0.0000 -0.0000 1.0000", "vn -1.0000 -0.0000 -0.0000", "vn -0.0000 -1.0000 -0.0000", "vn 1.0000 -0.0000 -0.0000", "vn -0.0000 -0.0000 -1.0000",
            "vt 0.000000 0.000000", "vt 1.000000 0.000000", "vt 1.000000 1.000000", "vt 0.000000 1.000000",
            "s 0",
            "usemtl Wood",
            "f 1/1/1 5/2/1 7/3/1 3/4/1",
            "f 4/1/2 3/2/2 7/3/2 8/4/2",
            "f 8/1/3 7/2/3 5/3/3 6/4/3",
            "f 6/1/4 2/2/4 4/3/4 8/4/4",
            "f 2/1/5 1/2/5 3/3/5 4/4/5",
            "f 6/1/6 5/2/6 1/3/6 2/4/6",
            "o Floor",
            "v -1.500000 0.000000 1.500000", "v 1.500000 0.000000 1.500000", "v -1.500000 0.000000 -1.500000", "v 1.500000 0.000000 -1.500000",
            "vn -0.0000 1.0000 -0.0000",
            "s 0",
            "usemtl Stone",
            "f 9/1/7 10/2/7 12/3/7 11/4/7");
        string mtl = Lines(
            "# Blender 4.2.0 MTL File: 'crate_scene.blend'",
            "newmtl Wood",
            "Kd 0.800000 0.800000 0.800000",
            "d 1.000000",
            "illum 2",
            "map_Kd crate_wood.png",
            "",
            "newmtl Stone",
            "Kd 0.500000 0.500000 0.500000",
            "illum 2");
        File.WriteAllText(Path.Combine(folder, "crate_scene.obj"), obj);
        File.WriteAllText(Path.Combine(folder, "crate_scene.mtl"), mtl);
        File.WriteAllBytes(Path.Combine(folder, "crate_wood.png"), PngEncoder.Encode(Checker(16, 16)));
        return Path.Combine(folder, "crate_scene.obj");
    }

    /// <summary>A one-triangle file whose texture name is too long for a mesh material, into <paramref name="folder"/>.</summary>
    public static string WriteErrorSample(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "too_long.obj"), Lines("mtllib too_long.mtl", "o Panel", "v 0 0 0", "v 1 0 0", "v 0 1 0", "vt 0 0", "vt 1 0", "vt 0 1",
            "usemtl Panel", "f 1/1 2/2 3/3"));
        File.WriteAllText(Path.Combine(folder, "too_long.mtl"), Lines("newmtl Panel", "map_Kd textures/an_extremely_long_texture_name_for_rf.png"));
        return Path.Combine(folder, "too_long.obj");
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static BgraImage Checker(int width, int height)
    {
        var image = new BgraImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool dark = ((x / 4) + (y / 4)) % 2 == 0;
                int o = (y * width + x) * 4;
                image.Pixels[o] = (byte)(dark ? 40 : 90);
                image.Pixels[o + 1] = (byte)(dark ? 80 : 150);
                image.Pixels[o + 2] = (byte)(dark ? 120 : 200);
                image.Pixels[o + 3] = 255;
            }
        }
        return image;
    }
}
