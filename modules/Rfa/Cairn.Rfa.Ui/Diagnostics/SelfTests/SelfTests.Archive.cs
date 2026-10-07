using System.Buffers.Binary;
using System.IO;
using System.Text;
using Cairn.Formats.Vpp;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// An archived clip opened through the shell (<see cref="IShellContext.OpenLocation"/> -> <c>OpenBytes</c>) is a
/// read-only-origin document as in 1.0.1: origin shown, clean, no path, so Save asks where to write a copy.
/// </summary>
internal static class ArchiveSelfTests
{
    [SelfTest("archive-entry", Order = 900)]
    public static async Task ArchiveEntry(SelfTestContext ctx)
    {
        if (ctx.Clip is not { FilePath: { } clipPath })
        {
            ctx.Log("selftest archive-entry: needs a clip document saved on disk");
            return;
        }
        string dir = Path.Combine(Path.GetTempPath(), "cairn-rfa-archive-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string vpp = Path.Combine(dir, "rfa_selftest.vpp");
        File.WriteAllBytes(vpp, BuildVpp([("archived_walk.rfa", File.ReadAllBytes(clipPath))]));
        var entry = VppArchive.Open(vpp).Entries.First(e => e.Name.Equals("archived_walk.rfa", StringComparison.OrdinalIgnoreCase));
        var location = new AssetLocation("archived_walk.rfa", "archived_walk.rfa", AssetSourceKind.SearchFolderArchive, null, vpp, entry);

        ctx.Check(ctx.Shell.OpenLocation(location), "the shell opens the archive entry");
        await ctx.SettleAsync();
        if (ctx.Model.ActiveDocument is not ClipDocumentViewModel doc || doc.DisplayName != "archived_walk.rfa")
        {
            ctx.Check(false, $"the archived clip is the active RFA document ({ctx.Model.ActiveDocument?.DisplayName})");
            return;
        }
        ctx.Check(doc.IsFromArchive && doc.ArchiveOrigin is { } origin
            && string.Equals(Path.GetFileName(origin.ArchivePath), "rfa_selftest.vpp", StringComparison.OrdinalIgnoreCase),
            $"archive origin recorded ({doc.ArchiveOrigin?.ArchivePath})");
        ctx.Check(doc.FilePath is null && !doc.IsDirty, "no path, not dirty");
        ctx.Check(doc.OriginText.Contains("archived_walk.rfa in", StringComparison.Ordinal) && doc.OriginText.Contains(".vpp", StringComparison.OrdinalIgnoreCase)
            && doc.TabToolTip.Contains("read-only origin", StringComparison.Ordinal), $"origin text names the archive ('{doc.OriginText}')");
        ctx.Check(((IDocument)doc).FilePath is null, "Save becomes Save As (the shell sees no path)");
        ctx.Check(doc.Current.BoneCount == ctx.Clip!.Current.BoneCount, "same clip as the loose file");
        doc.DiscardOnClose = true;
        ctx.Shell.Close(doc);
        await ctx.SettleAsync();
    }

    /// <summary>A value the shell's first-run import stored ("rfa." + 1.0.1's key, as JSON) reads back in the module.</summary>
    [SelfTest("imported-settings", Order = 901)]
    public static Task ImportedSettings(SelfTestContext ctx)
    {
        var settings = ctx.Model.Settings;
        string clip = @"c:\cairn-selftest\imported.rfa";
        settings.Values.TryGetValue("rfa.previewMeshByClip", out var saved);
        bool hadValue = settings.Values.ContainsKey("rfa.previewMeshByClip");
        try
        {
            var old = System.Text.Json.Nodes.JsonNode.Parse("{\"" + clip.Replace(@"\", @"\\", StringComparison.Ordinal) + "\":\"ult2.v3c\"}")!;
            settings.Values["rfa.previewMeshByClip"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(old.ToJsonString());
            ctx.Check(ctx.Model.RememberedPreviewMesh(clip) == "ult2.v3c", "an imported rfa.previewMeshByClip entry is read back");
        }
        finally
        {
            if (hadValue) settings.Values["rfa.previewMeshByClip"] = saved;
            else settings.Values.Remove("rfa.previewMeshByClip");
        }
        return Task.CompletedTask;
    }

    private static byte[] BuildVpp(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        const int Block = VppArchive.BlockSize;
        static int Align(int v) => (v + Block - 1) / Block * Block;
        using var ms = new MemoryStream();
        var header = new byte[Block];
        BinaryPrimitives.WriteUInt32LittleEndian(header, VppArchive.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)files.Count);
        ms.Write(header);
        var directory = new byte[Align(files.Count * VppArchive.EntryBytes)];
        for (int i = 0; i < files.Count; i++)
        {
            int at = i * VppArchive.EntryBytes;
            Encoding.ASCII.GetBytes(files[i].Name).CopyTo(directory, at);
            BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(at + VppArchive.NameBytes), files[i].Data.Length);
        }
        ms.Write(directory);
        foreach (var (_, data) in files)
        {
            ms.Write(data);
            ms.Write(new byte[Align(data.Length) - data.Length]);
        }
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)bytes.Length);
        return bytes;
    }
}
