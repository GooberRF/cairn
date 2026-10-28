using System.Text;
using Cairn.Formats;
using Cairn.Formats.Rf2;

namespace Cairn.Lvl.Conversion;

/// <summary>One clutter class of Red Faction II's <c>clutter.tbl</c>: the mesh it draws and its flags.</summary>
/// <param name="ClassName">The class name levels use.</param>
/// <param name="MeshFile">The mesh as the table names it (<c>.V3D</c> static, <c>.VCM</c> animated; the game ships them as <c>.rfm</c> / <c>.rfc</c>).</param>
/// <param name="Flags">The flag names (<c>collide_weapon</c>, <c>collide_object</c>, <c>moves</c> ...).</param>
public sealed record Rf2ClutterClass(string ClassName, string MeshFile, ImmutableArray<string> Flags)
{
    /// <summary>The name of the mesh once converted for Red Faction: <c>.v3m</c> for a static mesh, <c>.v3c</c> for an animated one.</summary>
    public string ConvertedMeshFile
    {
        get
        {
            string stem = Path.GetFileNameWithoutExtension(MeshFile);
            string ext = Path.GetExtension(MeshFile).ToLowerInvariant();
            return stem + (ext is ".vcm" or ".rfc" ? ".v3c" : ".v3m");
        }
    }

    /// <summary>Alpine mesh collision: 2 (everything) for <c>collide_object</c>, 1 (weapons only) for <c>collide_weapon</c>, else 0.</summary>
    public byte Collision =>
        Flags.Contains("collide_object", StringComparer.OrdinalIgnoreCase) ? (byte)2
        : Flags.Contains("collide_weapon", StringComparer.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
}

/// <summary>
/// Reads Red Faction II's <c>clutter.tbl</c> (in the install's <c>Permanent_data</c> group) for the class-to-mesh
/// mapping a level conversion needs to place RF2 clutter as Alpine static meshes. Only <c>$Class Name</c>,
/// <c>$V3D Filename</c> and <c>$Flags</c> are read; the table is never written.
/// </summary>
public static class Rf2ClutterTable
{
    /// <summary>The classes of a <c>clutter.tbl</c> text, by class name (case-insensitive; the first definition wins).</summary>
    public static IReadOnlyDictionary<string, Rf2ClutterClass> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, Rf2ClutterClass>(StringComparer.OrdinalIgnoreCase);
        string? cls = null, mesh = null;
        var flags = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.StartsWith("$Class Name:", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                cls = Quoted(line).FirstOrDefault();
            }
            else if (line.StartsWith("$V3D Filename:", StringComparison.OrdinalIgnoreCase))
            {
                mesh = Quoted(line).FirstOrDefault();
            }
            else if (line.StartsWith("$Flags:", StringComparison.OrdinalIgnoreCase))
            {
                flags.AddRange(Quoted(line));
            }
            else if (line.StartsWith('#'))
            {
                Flush();
            }
        }
        Flush();
        return result;

        void Flush()
        {
            if (cls is { Length: > 0 } && mesh is { Length: > 0 }) result.TryAdd(cls, new Rf2ClutterClass(cls, mesh, [.. flags]));
            cls = null;
            mesh = null;
            flags.Clear();
        }
    }

    /// <summary>The <c>clutter.tbl</c> classes of a Red Faction II install, or null when the table is not there or cannot be read.</summary>
    /// <param name="rf2GameDirectory">The install folder (the one holding <c>pc_media</c>).</param>
    public static IReadOnlyDictionary<string, Rf2ClutterClass>? FromGameFolder(string rf2GameDirectory)
    {
        ArgumentNullException.ThrowIfNull(rf2GameDirectory);
        string toc = Path.Combine(rf2GameDirectory, "pc_media", "Permanent_data" + Rf2TocGroup.Extension);
        try
        {
            if (!File.Exists(toc)) return null;
            var group = Rf2TocGroup.Open(toc);
            var entry = group.Entries.FirstOrDefault(e => e.Name.Equals("clutter.tbl", StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Size <= 0 || entry.Size > 16 << 20) return null;
            byte[] bytes;
            if (group.IsPacked)
            {
                if (group.FindPackfile(toc) is not { } pack) return null;
                using var f = File.OpenRead(pack);
                if (entry.Offset + entry.Size > f.Length) return null;
                f.Position = entry.Offset;
                bytes = new byte[entry.Size];
                f.ReadExactly(bytes);
            }
            else
            {
                if (Rf2TocGroup.LooseFilePath(toc, entry) is not { } path || !File.Exists(path)) return null;
                bytes = File.ReadAllBytes(path);
            }
            return Parse(Encoding.Latin1.GetString(bytes));
        }
        catch (Exception e) when (e is AssetFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Quoted(string line)
    {
        int i = 0;
        while ((i = line.IndexOf('"', i)) >= 0)
        {
            int end = line.IndexOf('"', i + 1);
            if (end < 0) yield break;
            yield return line[(i + 1)..end];
            i = end + 1;
        }
    }
}
