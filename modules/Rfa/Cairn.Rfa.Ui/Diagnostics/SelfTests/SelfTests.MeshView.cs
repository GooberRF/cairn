using System.Globalization;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Viewport;
using Size = System.Windows.Size;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// The mesh viewport's wireframe, edges on the mesh and statistics, on the repository's sample figure: the counts
/// match the mesh's LOD 0, the edges are each triangle edge once, drawn 1 pixel wide facing the camera, over the
/// faces or alone. Restores the display settings and closes the tab it opened.
/// </summary>
internal static class MeshViewSelfTests
{
    [SelfTest("meshview", Order = 665)]
    public static async Task MeshView(SelfTestContext ctx)
    {
        string? root = LocalPaths.RepositoryRoot;
        string? sample = root is null ? null : Path.Combine(root, "samples", "rfa", "sample_figure.v3c");
        if (sample is null || !File.Exists(sample))
        {
            ctx.Log("selftest meshview: samples/sample_figure.v3c not found; skipped");
            return;
        }
        var model = ctx.Model;
        var display = model.Display;
        var saved = (display.MeshMode, display.ShowEdges, display.ShowStatistics, display.Perspective);
        var startActive = model.ActiveDocument;
        bool wasOpen = model.Documents.Any(d => string.Equals(d.FilePath, sample, StringComparison.OrdinalIgnoreCase));
        if (model.OpenFile(sample) is not MeshDocumentViewModel doc)
        {
            ctx.Check(false, "meshview: the sample figure opens as a mesh document");
            return;
        }
        try
        {
            model.ActiveDocument = doc;
            display.MeshMode = MeshDisplayMode.Flat;
            display.ShowEdges = false;
            display.Perspective = true;
            display.ShowStatistics = true;
            await ctx.SettleAsync();
            ViewportControl.FlushAll();
            if (ViewportControl.Visible().FirstOrDefault(v => ReferenceEquals(v.Scene, doc.Scene)) is not { } view)
            {
                ctx.Check(false, "meshview: the mesh tab's viewport is shown");
                return;
            }

            // the counts of LOD 0, and each triangle edge once (counted here without the renderer's code)
            var mesh = doc.Current;
            var batches = mesh.Submeshes.Where(s => s.Lods.Length > 0).SelectMany(s => s.Lods[0].Batches).ToList();
            int triangles = batches.Sum(b => b.TriangleCount), vertices = batches.Sum(b => b.VertexCount);
            int edges = batches.Sum(b => b.Triangles.SelectMany(t => new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                .Select(e => (Math.Min(e.Item1, e.Item2), Math.Max(e.Item1, e.Item2))).Distinct().Count());
            string Stat(string label) => view.StatisticsRows.FirstOrDefault(r => r.Label == label).Value ?? "";
            string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
            ctx.Check(Stat("Triangles").StartsWith(N(triangles), StringComparison.Ordinal) && Stat("Vertices") == N(vertices) && Stat("Bones") == N(mesh.Bones.Length),
                "meshview: statistics " + string.Join(", ", view.StatisticsRows.Select(r => $"{r.Label} {r.Value}")));

            display.ShowEdges = true;
            ViewportControl.FlushAll();
            var lines = view.Renderer.Edges;
            ctx.Check(lines.EdgeCount == edges && view.Renderer.FaceModelCount > 0, $"meshview: edges on the mesh: {lines.EdgeCount} edges (want {edges}) over {view.Renderer.FaceModelCount} face models");
            var size = new Size(view.View.ActualWidth, view.View.ActualHeight);
            var camera = doc.Scene.Camera;
            var vp = camera.ViewProjection(size);
            bool facing = Enumerable.Range(0, Math.Min(20, lines.EdgeCount)).All(i => lines.Quad(i) is { } q
                && camera.Project(q[0], size, vp, out var s0, out _) && camera.Project(q[1], size, vp, out var s1, out _) && Math.Abs((s0 - s1).Length - 1) < 0.05);
            ctx.Check(facing, "meshview: the edges are 1 pixel wide on screen, facing the camera");

            display.MeshMode = MeshDisplayMode.Wireframe;
            ViewportControl.FlushAll();
            ctx.Check(view.Renderer.FaceModelCount == 0 && view.Renderer.Edges.EdgeCount == edges && view.Renderer.Edges.Color != WireframeLines.OverFacesColor,
                $"meshview: wireframe draws the edges alone ({view.Renderer.Edges.EdgeCount} edges, {view.Renderer.FaceModelCount} face models)");

            display.MeshMode = MeshDisplayMode.Flat;
            display.ShowEdges = false;
            display.ShowStatistics = false;
            ViewportControl.FlushAll();
            ctx.Check(view.Renderer.Edges.EdgeCount == 0 && view.StatisticsRows.Count == 0, "meshview: with both off, no edges and no statistics");
        }
        finally
        {
            (display.MeshMode, display.ShowEdges, display.ShowStatistics, display.Perspective) = saved;
            if (!wasOpen) model.CloseDiscarding(doc);
            if (startActive is not null && model.Documents.Contains(startActive)) model.ActiveDocument = startActive;
            await ctx.SettleAsync();
        }
    }
}
