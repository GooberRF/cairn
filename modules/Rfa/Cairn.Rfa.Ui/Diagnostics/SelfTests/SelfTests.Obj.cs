using System.Windows.Controls;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.ObjTools;
using ShellSelfTestContext = Cairn.Ui.Diagnostics.SelfTestContext;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// OBJ in the meshes module, through the view-models and the module's Convert to… (no windows): a Blender-style file
/// imports with its pre-flight report and opens as a new unsaved .v3m; the export writes .obj + .mtl (+ PNG) that
/// imports back with Cairn's metadata to the same triangles; a too-long texture name blocks Import; the menus, the
/// .obj importer and the Convert to… targets are registered; .obj → .v3m / .glb, glTF → .obj and mesh → .obj run
/// without a dialog. Files under %TEMP%; the documents are left as they were found.
/// </summary>
internal static class ObjSelfTests
{
    [SelfTest("rfa.obj")]
    public static async Task Obj(ShellSelfTestContext ctx)
    {
        var ws = RfaModule.Workspace;
        var module = ctx.Shell.Modules.OfType<RfaModule>().FirstOrDefault();
        if (!ctx.Check(module is not null, "the meshes module is loaded")) return;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-obj-" + Guid.NewGuid().ToString("N")[..8]);
        var before = ws.Documents.ToList();
        var previous = ws.ActiveDocument;
        try
        {
            // Registration: the importer, the menus and the Convert to… targets.
            ctx.Check(module!.Importers.Any(i => i.Extensions.Contains(".obj") && i.Probe("x.obj") > 0), "an .obj importer is registered");
            var headers = module.Menus.Select(m => (m.Slot, Header: (m.Item as MenuItem)?.Header as string)).ToList();
            ctx.Check(headers.Contains((MenuSlot.FileImport, "Mesh from OBJ…")) && headers.Contains((MenuSlot.FileExport, "OBJ…")), "File › Import › Mesh from OBJ… and File › Export › OBJ… are contributed");
            string Targets(string name) => string.Join(",", module.TargetsFor(name).Select(t => t.Extension + (t.Lossy ? "~" : "")));
            ctx.Check(Targets("crate.v3m") == ".gltf,.glb,.obj~" && Targets("guard.v3c") == ".gltf,.glb,.obj~", $"meshes list .obj, marked lossy ({Targets("crate.v3m")})");
            ctx.Check(Targets("scene.obj") == ".v3m,.v3c,.gltf,.glb" && Targets("scene.gltf") == ".obj~" && Targets("scene.glb") == ".obj~", $"an .obj lists .v3m, .v3c and glTF; a glTF lists .obj ({Targets("scene.obj")} / {Targets("scene.gltf")})");
            ctx.Check(Targets("walk.rfa") == ".gltf,.glb" && !Targets("box.v3d").Contains(".obj", StringComparison.Ordinal), "clips and legacy meshes do not list .obj");
            ctx.Check(module.TargetsFor("scene.obj").All(t => module.CanConvert("scene.obj", t.Extension)) && module.TargetsFor("crate.v3m").All(t => module.CanConvert("crate.v3m", t.Extension))
                && !module.CanConvert("walk.rfa", ".obj") && !module.CanConvert("scene.obj", ".rfa"), "CanConvert agrees with the targets");

            // The import dialog's model on a Blender-style file.
            string objPath = ObjSamples.WriteBlenderSample(folder);
            var import = new ObjMeshImportViewModel(ws, objPath);
            bool settled = await import.SettleAsync();
            ctx.Check(settled && import.LoadError is null && import.Result?.Mesh is not null && !import.HasErrors, $"the Blender sample builds ({import.IssuesSummary})");
            ctx.Check(import.IsStatic && !import.HasMetadata && import.OutputName == "crate_scene.v3m", $"a static build without metadata, opening as {import.OutputName}");
            ctx.Check(import.Issues.Any(i => i.Code == "MI023") && import.Issues.Any(i => i.Code == "MI022" && i.HasFix), $"the pre-flight lists the triangulated quads and the material without a map ({string.Join(", ", import.Issues.Select(i => i.Code))})");
            var subs = import.Result?.Mesh?.Submeshes.ToList() ?? [];
            ctx.Check(subs.Select(s => s.Name.Text).SequenceEqual(["Crate", "Floor"]) && subs[0].Materials.Single().DiffuseMap.Text == "crate_wood.tga",
                $"one submesh per object, the texture from map_Kd ({string.Join(", ", subs.Select(s => $"{s.Name.Text}: {string.Join("/", s.Materials.Select(m => m.DiffuseMap.Text))}"))})");
            ctx.Check(import.SummaryLines.Count >= 4 && import.SummaryLines[0].StartsWith("Static mesh (.v3m): 2 submeshes", StringComparison.Ordinal), $"summary: {string.Join(" | ", import.SummaryLines)}");
            ctx.Check(import.Preview.Scene.Mesh is not null, "the preview shows the built mesh");
            import.IsCharacter = true;
            await import.SettleAsync();
            ctx.Check(import.Issues.Any(i => i.Code == "MI017") && import.OutputName.EndsWith(".v3c", StringComparison.Ordinal), "a character build without a kept skeleton warns that it gets no bones");
            import.IsStatic = true;
            await import.SettleAsync();
            var tab = import.Import();
            ctx.Check(tab is { IsDirty: true, FilePath: null, Extension: ".v3m" } && ReferenceEquals(ws.ActiveDocument, tab), $"Import opens {tab?.DisplayName ?? "nothing"} as a new unsaved .v3m tab");
            if (tab is null) return;
            await ctx.SettleAsync();

            // Export back to OBJ, then import that: Cairn's metadata gives the same triangles.
            ctx.Check(RfaModule.Obj.ExportCommand.CanExecute(null), "File › Export › OBJ… is enabled for the mesh");
            var export = new ObjExportViewModel(ws, tab);
            ctx.Check(!export.HasLodChoice && export.LodChoices.Count == 1 && export.CharacterNote is null, "one LOD, no character note for a static mesh");
            string outFolder = Path.Combine(folder, "out");
            export.OutputPath = Path.Combine(outFolder, "crate_scene.obj");
            var result = await export.ExportAsync();
            ctx.Check(result is not null && export.LastFiles.All(File.Exists) && export.LastFiles[1].EndsWith(".mtl", StringComparison.Ordinal),
                $"Export writes {string.Join(", ", export.LastFiles.Select(Path.GetFileName))} ({export.Error ?? export.ProgressText})");
            ctx.Check(export.ResultLines.Count >= 3, $"the result lists what was written: {string.Join(" | ", export.ResultLines.Select(l => l.Text))}");
            var back = new ObjMeshImportViewModel(ws, export.OutputPath);
            await back.SettleAsync();
            int Triangles(V3dFile? m) => m?.Submeshes.Sum(s => s.Lods[0].Batches.Sum(b => b.TriangleCount)) ?? -1;
            ctx.Check(back.HasMetadata && back.Result?.HasMetadata == true && Triangles(back.Result.Mesh) == Triangles(tab.Current) && back.Issues.Any(i => i.Code == "MI026"),
                $"the export imports back with Cairn's metadata to the same {Triangles(tab.Current)} triangles ({back.IssuesSummary})");
            back.End();

            // A pre-flight error blocks Import.
            var errors = new ObjMeshImportViewModel(ws, ObjSamples.WriteErrorSample(folder));
            await errors.SettleAsync();
            var error = errors.Issues.FirstOrDefault(i => i.Severity == MeshImportSeverity.Error);
            int count = ws.Documents.Count;
            ctx.Check(!errors.CanImport && error is { Code: "MI002", HasFix: true } && errors.Problem is not null && errors.Import() is null && ws.Documents.Count == count,
                $"a texture name over 31 characters is an error that blocks Import ({error?.Code}: {error?.Message})");
            errors.End();

            // Convert to… without a dialog.
            byte[] objBytes = File.ReadAllBytes(objPath);
            string convert = Path.Combine(folder, "convert");
            Directory.CreateDirectory(convert);
            AssetConversionRequest Request(string name, byte[] bytes, string target, string? path = null, IDocument? document = null) =>
                new(name, bytes, target) { FilePath = path, Interactive = false, OutputFolder = convert, Document = document };
            string? v3m = await module.ConvertAsync(Request("crate_scene.obj", objBytes, ".v3m", objPath));
            ctx.Check(v3m is not null && File.Exists(v3m) && V3dReader.ReadFile(v3m) is { Kind: V3dKind.StaticMesh } built && built.Submeshes.Count() == 2, $".obj → .v3m writes {v3m}");
            string? glb = await module.ConvertAsync(Request("crate_scene.obj", objBytes, ".glb", objPath));
            ctx.Check(glb is not null && File.Exists(glb) && GltfReader.ReadFile(glb).Meshes.Count == 2, $".obj → .glb writes {glb}");
            // The .mtl comes from the packfile's other entries when the .obj has no folder.
            var entryRequest = new AssetConversionRequest("crate_scene.obj", objBytes, ".v3m")
            {
                Interactive = false, OutputFolder = convert,
                ReadSibling = n => n.Equals("crate_scene.mtl", StringComparison.OrdinalIgnoreCase) ? File.ReadAllBytes(Path.Combine(folder, n)) : null,
            };
            string? fromEntry = await module.ConvertAsync(entryRequest);
            ctx.Check(fromEntry is not null && V3dReader.ReadFile(fromEntry).Submeshes.First().Materials.Single().DiffuseMap.Text == "crate_wood.tga", "an .obj packfile entry finds its .mtl among the other entries");
            string gltfPath = Path.Combine(folder, "crate_scene.gltf");
            GltfExport.ExportToFile(gltfPath, tab.Current);
            string? fromGltf = await module.ConvertAsync(Request("crate_scene.gltf", File.ReadAllBytes(gltfPath), ".obj", gltfPath));
            ctx.Check(fromGltf is not null && File.Exists(fromGltf) && ObjMeshImport.ReadFile(fromGltf).Obj.FaceCount == Triangles(tab.Current), $"glTF → .obj writes {fromGltf}");
            string? fromTab = await module.ConvertAsync(Request("crate.v3m", tab.CurrentBytes(), ".obj", null, tab));
            ctx.Check(fromTab is not null && File.Exists(fromTab) && File.Exists(Path.ChangeExtension(fromTab, ".mtl")), $"a mesh tab → .obj writes {fromTab}");
            var choices = AssetConversions.TargetsFor(ctx.Shell, tab);
            ctx.Check(choices.Any(c => c.Module == module && c.Target.Extension == ".obj" && c.Target.Lossy), "File › Convert to… lists .obj (lossy) for the mesh tab");
        }
        finally
        {
            foreach (var extra in ws.Documents.Where(d => !before.Contains(d)).ToList()) ws.CloseDiscarding(extra);
            if (previous is not null && ws.Documents.Contains(previous)) ws.ActiveDocument = previous;
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ctx.Log("selftest rfa.obj: the temp folder could not be deleted: " + ex.Message);
            }
        }
        ctx.Check(ws.Documents.Count == before.Count, "rfa.obj: the documents are left as they were found");
    }
}
