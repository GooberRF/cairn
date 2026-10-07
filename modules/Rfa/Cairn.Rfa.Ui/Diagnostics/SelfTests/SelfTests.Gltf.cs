using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.GltfTools;
using Cairn.Rfa.Interchange;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of the glTF dialogs (phase 6), through their view-models (no windows): open the stock
/// ult2_guard.v3c, export it with three library clips to .glb and .gltf under %TEMP%, import the
/// animations back (with key extras every clip must come back byte-identical) and the mesh back (same
/// bones, spheres and prop points, no errors), check that Import opens new unsaved tabs (closed again),
/// and that a pre-flight error blocks Import. Leaves the documents as it found them.
/// </summary>
internal static class GltfSelfTests
{
    [SelfTest("gltf", Order = 700)]
    public static async Task Gltf(SelfTestContext ctx)
    {
        var model = ctx.Model;
        string? path = FindStockFile("ult2_guard.v3c");
        if (path is null)
        {
            ctx.Log("selftest gltf: skipped (stock ult2_guard.v3c not found; set CAIRN_RESEARCH)");
            return;
        }
        await ctx.SettleAsync();
        var before = model.Documents.ToList();
        var previous = model.ActiveDocument;
        if (model.OpenFile(path) is not MeshDocumentViewModel meshDoc)
        {
            ctx.Check(false, $"gltf: {path} opens as a mesh document");
            return;
        }
        bool openedHere = !before.Contains(meshDoc);
        await ctx.SettleAsync();
        string temp = Path.Combine(Path.GetTempPath(), "Cairn-rfa-selftest-gltf");
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            model.ActiveDocument = meshDoc;
            ctx.Check(model.Gltf.ExportCommand.CanExecute(null), "gltf: File › Export to glTF is enabled for a mesh document");
            ctx.Check(model.Gltf.ImportAnimationCommand.CanExecute(null) && model.Gltf.ImportMeshCommand.CanExecute(null), "gltf: the import commands are enabled");

            await GltfScreens.RoundTripAsync(model, meshDoc, temp, ["ult2_walk", "ult2_stand", "ult2_run"], ctx.Log, ctx.Check, closeImports: true);

            // A pre-flight error blocks Import.
            var errors = new GltfMeshImportViewModel(model, Path.Combine(temp, "pre-flight-errors.gltf"), GltfScreens.ErrorDocument(meshDoc.Current));
            await errors.SettleAsync();
            var error = errors.Issues.FirstOrDefault(i => i.Severity == MeshImportSeverity.Error);
            ctx.Check(!errors.CanImport && error is { Code: "MI002" } && errors.Problem is not null,
                $"gltf: a bone name over 23 characters is a pre-flight error that blocks Import ({error?.Code}: {error?.Message})");
            ctx.Check(error is { HasFix: true }, $"gltf: the error carries a fix ({error?.Fix})");
            int count = model.Documents.Count;
            ctx.Check(errors.Import() is null && model.Documents.Count == count, "gltf: Import does nothing while there are errors");
            errors.End();

            // A clip without a preview mesh is refused with a message.
            var refused = false;
            if (model.Documents.OfType<ClipDocumentViewModel>().FirstOrDefault(d => d.PreviewMesh is null) is { } bare)
            {
                try { _ = new GltfExportViewModel(model, bare); }
                catch (ArgumentException) { refused = true; }
                ctx.Check(refused, "gltf: exporting a clip without a preview mesh is refused with a message");
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ctx.Log("selftest gltf: the temp folder could not be deleted: " + ex.Message);
            }
            foreach (var extra in model.Documents.Where(d => !before.Contains(d) && !ReferenceEquals(d, meshDoc)).ToList()) model.CloseDiscarding(extra);
            if (openedHere) model.CloseDiscarding(meshDoc);
            if (previous is not null && model.Documents.Contains(previous)) model.ActiveDocument = previous;
        }
        ctx.Check(model.Documents.Count == before.Count && ReferenceEquals(model.ActiveDocument, previous ?? model.ActiveDocument),
            "gltf: the documents are left as they were found");
        ctx.Check(!Directory.Exists(temp), "gltf: the temp folder is removed");
    }

    /// <summary>A stock file from the research corpus (RFAWB_RESEARCH, or the repository's research folder above the exe), or null.</summary>
    internal static string? FindStockFile(string name) => LocalPaths.CorpusFile(name);
}
