#if CAIRN_MODULE_VPP && CAIRN_MODULE_RFA
using System.Windows;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Ui;
using Cairn.Rfa.Ui.Preview;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Views.Dialogs;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Vpp.Writing;

namespace Cairn.Shell;

/// <summary>
/// Exporter and PS2 meshes in packfiles (the packfile and meshes modules together): the entries get type names and an
/// Info line, preview in 3D with "Open in Cairn", "Convert meshes..." turns a selection into .v3m/.v3c entries as ONE
/// undo step, and a mesh opened from the packfile converts back into it. The packfile is built from synthetic meshes.
/// </summary>
internal static class MeshPackfileSelfTests
{
    [SelfTest("rfa.legacy-packfile")]
    public static async Task MeshesInAPackfile(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var vpp = shell.Modules.OfType<VppModule>().FirstOrDefault();
        var rfa = shell.Modules.OfType<RfaModule>().FirstOrDefault();
        if (vpp is null || rfa is null) { ctx.Skip("needs the packfile and meshes modules"); return; }
        string folder = Path.Combine(Path.GetTempPath(), "cairn-mesh-vpp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<IDocument>();
        try
        {
            var package = VppPackage.Empty;
            foreach (var (name, bytes) in new[] { ("box.v3d", SyntheticExporterMesh.SampleStatic()), ("guard.vcm", SyntheticExporterMesh.SampleCharacter()), ("readme.txt", "not a mesh"u8.ToArray()) })
                package = VppEdit.AddBytes(package, name, bytes, VppClashPolicy.Replace).Package;
            string packPath = Path.Combine(folder, "meshes.vpp");
            VppSaver.Save(package, packPath, null, CancellationToken.None, VppSaveOptions.Default);
            if (!ctx.Check(shell.OpenFile(packPath) && shell.ActiveDocument is VppDocument, "the packfile opens")) return;
            var doc = (VppDocument)shell.ActiveDocument!;
            opened.Add(doc);
            await ctx.SettleAsync();

            // Info column, type names, details
            string Info(VppItem item) => VppInfo.Summarize(item.Name, item.Source.Open, item.Size).Text;
            var box = doc.Current.Find("box.v3d")!;
            ctx.Check(Info(box) == "1 submesh, 12 triangles, 2 LODs", $"Info: {Info(box)}");
            ctx.Check(Info(doc.Current.Find("guard.vcm")!) == "2 bones, 12 triangles", $"Info: {Info(doc.Current.Find("guard.vcm")!)}");
            ctx.Check(VppFileTypes.Describe("x.v3d").DisplayName == "Static mesh (exporter)" && VppFileTypes.Describe("x.rfc").DisplayName == "PS2 character mesh", "type names");
            ctx.Check(VppFacts.Describe(box).Rows.Any(r => r.Label == "Converts to" && r.Value.StartsWith(".v3m", StringComparison.Ordinal)), "details pane: what it converts to");
            ctx.Check(vpp.CairnOpens("box.v3d") && vpp.CairnOpens("x.rfm"), "Open in Cairn is offered for the four types");

            // preview: the 3D mesh
            var window = new Window { Width = 700, Height = 400, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow, Left = -2000, Top = -2000 };
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            window.Show();
            try
            {
                area.Show(box, [box], doc.Current, immediate: true);
                await area.SettleAsync();
                await ctx.SettleAsync();
                var preview = (area.Preview.View as Cairn.Previews.ModulePreviewHost)?.Inner as RfaPreview;
                if (preview is not null) await preview.Loading;
                ctx.Check(preview is { Message: null }, $"the .v3d previews as a 3D mesh ({area.Preview.Kind}, {preview?.Message})");
            }
            finally
            {
                area.Dispose();
                window.Close();
            }

            // batch conversion into the packfile: one undo step
            doc.SelectNames(["box.v3d", "guard.vcm", "readme.txt"]);
            await ctx.SettleAsync();
            string? summary = await vpp.ConvertWithAsync(doc, rfa, interactive: false, options: new LegacyConvertChoice(LegacyConvertTarget.IntoPackfile, null, false));
            ctx.Check(summary?.StartsWith("Converted 2 of 2 meshes", StringComparison.Ordinal) == true, $"summary: {summary}");
            ctx.Check(doc.Current.Find("box.v3m") is not null && doc.Current.Find("guard.v3c") is not null && doc.Current.Find("readme.v3m") is null, "box.v3m and guard.v3c were added; the text file was skipped");
            ctx.Check(doc.CanUndo && doc.UndoLabel?.Contains("Convert 2 meshes", StringComparison.Ordinal) == true, $"one undo step: {doc.UndoLabel}");
            ctx.Check(Info(doc.Current.Find("guard.v3c")!).StartsWith("2 bones", StringComparison.Ordinal), $"the .v3c entry's Info: {Info(doc.Current.Find("guard.v3c")!)}");
            doc.Undo();
            ctx.Check(doc.Current.Find("box.v3m") is null && doc.Current.Find("guard.v3c") is null, "Undo removes both");
            doc.Redo();
            ctx.Check(doc.Current.Find("box.v3m") is not null, "Redo adds them back");

            // Open in Cairn, then convert back into the packfile
            await doc.Commands.OpenInCairnAsync(box);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is MeshDocumentViewModel { IsLegacy: true }, "Open in Cairn opens a read-only mesh tab"))
            {
                var mesh = (MeshDocumentViewModel)shell.ActiveDocument!;
                opened.Add(mesh);
                var tools = RfaModule.Workspace.Legacy;
                ctx.Check(tools.PackfileOf(mesh) == "meshes.vpp" && tools.NextToFolder(mesh) == folder, $"the tab knows its packfile ({tools.PackfileOf(mesh)}, {tools.NextToFolder(mesh)})");
                var written = await tools.ConvertDocumentAsync(mesh, new LegacyConvertChoice(LegacyConvertTarget.IntoPackfile, null, false));
                ctx.Check(written is { Count: 1 } && written[0] == "box (2).v3m" && doc.Current.Find("box (2).v3m") is not null, $"converted into the packfile as {(written is { Count: 1 } ? written[0] : "nothing")} (box.v3m was taken)");
                ctx.Check(doc.UndoLabel == "Convert box.v3d to .v3m", $"one undo step in the packfile: {doc.UndoLabel}");
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
#endif
