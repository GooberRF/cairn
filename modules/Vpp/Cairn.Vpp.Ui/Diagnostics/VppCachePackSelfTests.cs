using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Vpp.Alpine;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// Alpine Faction cache packs (<c>Cairn.exe --probe-module --selftest-only vpp.cache*</c>): a synthetic pack opened
/// through the shell (read only; JPEG thumbnails named after levels are listed, previewed and extracted as JPEGs,
/// byte-exact), and the game folder's map thumbnails when they exist (only read: nothing is ever written beside them).
/// </summary>
public static class VppCachePackSelfTests
{
    private static string NewFolder() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairn-cache-test-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    /// <summary>A small decodable JPEG of one colour.</summary>
    private static byte[] Jpeg(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static object? PreviewView(VppPreviewArea area) => (area.Preview.View as ModulePreviewHost)?.Inner ?? area.Preview.View;

    [SelfTest("vpp.cache-synthetic")]
    public static async Task Synthetic(SelfTestContext ctx)
    {
        string root = NewFolder();
        VppDocument? doc = null;
        try
        {
            byte[] dm01 = Jpeg(48, 27, Colors.SteelBlue), ctf = Jpeg(32, 18, Colors.OrangeRed);
            byte[] notes = Encoding.ASCII.GetBytes("written by another tool");
            string path = Path.Combine(root, "map_thumbs.cache");
            File.WriteAllBytes(path, SyntheticCachePack.Build(0xC0FFEE, ("dm01.rfl", 0, dm01), ("ctf-bridge.rfl", 0x1234, ctf), ("notes.txt", 0, notes)));
            var stamp = VppArchiveStamp.Of(path);

            ctx.Check(ctx.Shell.OpenFile(path), "the shell opens a .cache");
            doc = ctx.Shell.ActiveDocument as VppDocument;
            if (doc?.Current.Cache is null) { ctx.Check(false, "... as an Alpine Faction cache pack"); return; }
            ctx.Check(doc.Kind.Id == "vpp.cache" && doc.Kind.Extensions.SequenceEqual([".cache"]), "its document kind is the cache pack kind");
            ctx.Check(doc.IsReadOnly && !((IDocument)doc).CanSave && !doc.IsDirty, "read only: Save and Save As are off");
            ctx.Check(doc.Current.Items.Select(i => i.Name).SequenceEqual(["dm01.rfl.jpg", "ctf-bridge.rfl.jpg", "notes.txt"]),
                "the thumbnails are listed as JPEGs, the text under its own name: " + string.Join(", ", doc.Current.Items.Select(i => i.Name)));
            var view = (VppDocumentView)doc.View;
            ctx.Check(view.CacheBannerText?.Contains("read only", StringComparison.Ordinal) == true, "the banner says it is read only: " + view.CacheBannerText);
            ctx.Check(view.Rf2BannerText is null && view.Ps2BannerText is null, "no Red Faction II or PlayStation 2 banner");
            ctx.Check(view.VisibleEditTools.Count == 0, "no Add/Remove/Rename on the toolbar: " + string.Join(", ", view.VisibleEditTools));
            ctx.Check(doc.Problems.All(p => p.Severity == Validation.VppSeverity.Info), "no packfile rule applies: " + string.Join("; ", doc.Problems));
            ctx.Check(doc.ReadOnlyReason.Contains("cache pack", StringComparison.Ordinal), "the read-only reason names a cache pack: " + doc.ReadOnlyReason);

            // nothing changes it
            ctx.Check(!doc.ApplyEdit("probe", p => Editing.VppEdit.Remove(p, ["notes.txt"])), "an edit is refused");
            ctx.Check(doc.Commands.Rename("notes.txt", "other.txt") is not null, "renaming is refused");
            ctx.Check(!ctx.Shell.Save(doc), "File > Save does nothing");
            ctx.Check(doc.Current.Count == 3 && !doc.IsDirty && !doc.CanUndo, "the pack is unchanged");

            // extraction is byte-exact, under the listed names
            string outFolder = Path.Combine(root, "out");
            var written = await doc.Commands.ExtractAsync(doc.Current.Items, outFolder, Commands.VppOverwritePolicy.Overwrite, quiet: true);
            ctx.Check(written?.Count == 3, $"Extract writes every entry ({written?.Count})");
            ctx.Check(File.Exists(Path.Combine(outFolder, "dm01.rfl.jpg")) && File.ReadAllBytes(Path.Combine(outFolder, "dm01.rfl.jpg")).AsSpan().SequenceEqual(dm01)
                && File.ReadAllBytes(Path.Combine(outFolder, "notes.txt")).AsSpan().SequenceEqual(notes), "... byte for byte, as dm01.rfl.jpg");

            // previews: a thumbnail shows as an image; the details name the stored entry; with nothing selected, the pack
            var window = new Window { Width = 700, Height = 600, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000, Top = -10000 };
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            window.Show();
            try
            {
                var thumb = doc.Current.Find("ctf-bridge.rfl.jpg")!;
                area.Show(thumb, [thumb], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(PreviewView(area) is ImagePreview, $"the thumbnail previews as an image ({area.Preview.Kind}, {PreviewView(area)?.GetType().Name})");
                ctx.Check(area.Details.Rows.Any(r => r.Label == "Stored name" && r.Value.StartsWith("ctf-bridge.rfl ", StringComparison.Ordinal))
                    && area.Details.Rows.Any(r => r.Label == "Tag" && r.Value.StartsWith("0x0000000000001234", StringComparison.Ordinal))
                    && area.Details.Rows.Any(r => r.Label == "CRC" && r.Value.EndsWith("(matches the data)", StringComparison.Ordinal)), "its details show the stored name, the tag and the CRC");
                ctx.Check(area.Details.Rows.Any(r => r.Label == "Game" && r.Value.Contains("cache", StringComparison.Ordinal)), "... and that it is cache data, not a game asset");
                area.Show(null, [], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(area.Details.Rows.Any(r => r.Label == "Write ID" && r.Value.StartsWith("0x0000000000C0FFEE", StringComparison.Ordinal)), "with nothing selected, the details show the pack's facts");
            }
            finally
            {
                area.Dispose();
                window.Close();
            }
            ctx.Check(VppArchiveStamp.Of(path) == stamp, "the pack on disk was never written");
        }
        finally
        {
            if (doc is not null && ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc);
            Work.VppWorkFolder.TryDeleteFolder(root);
        }
    }

    [SelfTest("vpp.cache-map-thumbs", Order = 310)]
    public static async Task MapThumbs(SelfTestContext ctx)
    {
        string? dir = LocalPaths.GameDirectory;
        string? path = dir is null ? null : Path.Combine(dir, "alpine_cache", "map_thumbs.cache");
        if (path is null || !File.Exists(path)) { ctx.Skip("no map thumbnail cache in the game folder (" + LocalPaths.HowToSet(LocalPaths.GameDirectoryVariable, "gameDirectory") + ")"); return; }
        var folder = Path.GetDirectoryName(path)!;
        var stamps = Directory.GetFiles(folder).ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
        VppDocument? doc = null;
        try
        {
            ctx.Check(ctx.Shell.OpenFile(path), "the shell opens the game's map_thumbs.cache");
            doc = ctx.Shell.ActiveDocument as VppDocument;
            if (doc?.Current.Cache is not { } cache) { ctx.Check(false, "... as an Alpine Faction cache pack"); return; }
            ctx.Log($"  {cache.Pack.Entries.Count:N0} entries, {cache.Typed:N0} listed with their type, {cache.Pack.DataBytes:N0} bytes of data");
            ctx.Check(cache.Pack.Entries.Count > 0 && cache.Typed == cache.Pack.Entries.Count, "every thumbnail is listed as a JPEG");
            ctx.Check(cache.Corrupt.Count == 0, $"every thumbnail matches its CRC ({cache.Corrupt.Count:N0} corrupt)");
            var window = new Window { Width = 700, Height = 600, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000, Top = -10000 };
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            window.Show();
            try
            {
                var first = doc.Current.Items[0];
                area.Show(first, [first], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(PreviewView(area) is ImagePreview, $"{first.Name} previews as an image ({area.Preview.Kind})");
            }
            finally
            {
                area.Dispose();
                window.Close();
            }
        }
        finally
        {
            if (doc is not null && ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc);
        }
        ctx.Check(Directory.GetFiles(folder).Length == stamps.Count && stamps.All(s => File.GetLastWriteTimeUtc(s.Key) == s.Value), "nothing in the cache folder was written");
    }
}
