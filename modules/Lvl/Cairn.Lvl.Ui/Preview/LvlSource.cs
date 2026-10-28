using Cairn.Formats;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Ui.Preview;

/// <summary>A level or group file read for the module: the model, its name and the bytes it was read from.</summary>
public sealed class LvlSource
{
    private LvlSource(string name, byte[] bytes, RfLevel? level, RfGroupFile? groupFile)
    {
        Name = name;
        Bytes = bytes;
        Level = level;
        GroupFile = groupFile;
    }

    /// <summary>The file name (with its extension).</summary>
    public string Name { get; }

    /// <summary>The file's bytes (a level's lightmaps and opaque sections refer into them; never changed).</summary>
    public byte[] Bytes { get; }

    /// <summary>The level, or null for a group file.</summary>
    public RfLevel? Level { get; }

    /// <summary>The group file, or null for a level.</summary>
    public RfGroupFile? GroupFile { get; }

    /// <summary>True for a group file (.rfg).</summary>
    public bool IsGroupFile => GroupFile is not null;

    /// <summary>What the converters take.</summary>
    public LvlConvertInput Input => Level is { } level ? LvlConvertInput.FromLevel(level) with { Name = Name } : LvlConvertInput.FromGroupFile(GroupFile!) with { Name = Name };

    /// <summary>True when the source has compiled geometry (a level; group files only have brushes).</summary>
    public bool HasCompiledGeometry => Level is { } level && (level.StaticGeometry.Faces.Length > 0 || level.Movers.Length > 0);

    /// <summary>True when the source has editor brushes.</summary>
    public bool HasBrushes => Level is { } level ? level.Brushes.Length > 0 : GroupFile!.Groups.Any(g => g.Brushes.Length > 0);

    /// <summary>The problems the reader reported.</summary>
    public ImmutableArray<string> Problems => Level?.Problems ?? GroupFile!.Problems;

    /// <summary>True when <paramref name="name"/> is a level or group file by its extension.</summary>
    public static bool Handles(string name) => IsLevelName(name) || IsGroupName(name);

    public static bool IsLevelName(string name) => Path.GetExtension(name).Equals(".rfl", StringComparison.OrdinalIgnoreCase);

    public static bool IsGroupName(string name) => Path.GetExtension(name).Equals(".rfg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a level (.rfl) or group file (.rfg) by its extension (anything else is read as a level). Any thread; the
    /// largest levels take seconds, so callers run it on a worker.
    /// </summary>
    /// <exception cref="AssetFormatException">The file cannot be read.</exception>
    public static LvlSource Read(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        if (IsGroupName(name)) return new LvlSource(name, bytes, null, RfgReader.Read(bytes, name));
        return new LvlSource(name, bytes, RflLevelReader.Read(bytes, name), null);
    }

    /// <summary>A source over a model already in memory (self-tests).</summary>
    internal static LvlSource FromLevel(RfLevel level, byte[]? bytes = null) => new(level.Name, bytes ?? [], level, null);

    /// <summary>
    /// The world bounds of the compiled geometry without its sky room (else the brushes), from the vertices, or null when there are none.
    /// Quick; for framing a view before anything is built.
    /// </summary>
    public (Vector3 Min, Vector3 Max)? Bounds(bool compiled)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        void Add(Vector3 p)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            any = true;
        }
        if (Level is { } level && compiled)
        {
            // The sky room sits far outside the playable space: leave its faces out unless nothing else is there.
            var geometry = level.StaticGeometry;
            var skyArea = SkyArea(geometry);
            bool IsSky(RfFace f) => f.Room >= 0 && f.Room < skyArea.Length && skyArea[f.Room];
            bool anyPlayable = geometry.Faces.Any(f => !IsSky(f));
            foreach (var f in geometry.Faces)
            {
                if (anyPlayable && IsSky(f)) continue;
                foreach (var c in f.Corners) if (c.Vertex >= 0 && c.Vertex < geometry.Vertices.Length) Add(geometry.Vertices[c.Vertex]);
            }
            foreach (var m in level.Movers) foreach (var v in m.Geometry.Vertices) Add(m.ToWorld(v));
        }
        else
        {
            var brushes = Level is { } l ? l.Brushes.AsEnumerable() : GroupFile!.AllBrushes;
            foreach (var b in brushes) foreach (var v in b.Geometry.Vertices) Add(b.ToWorld(v));
        }
        return any ? (min, max) : null;
    }

    /// <summary>
    /// Per room of <paramref name="geometry"/>: true for a sky room and for rooms lying (almost) entirely inside a sky
    /// room's box, such as the cloud layers some levels build inside the sky without the sky flag. Framing leaves them out.
    /// </summary>
    internal static bool[] SkyArea(RfGeometry geometry)
    {
        var rooms = geometry.Rooms;
        var result = new bool[rooms.Length];
        var skies = rooms.Where(r => r.IsSky).ToList();
        if (skies.Count == 0) return result;
        // Fraction of [a0, a1] inside [b0, b1]; a flat extent counts as inside when it lies within.
        static float Inside(float a0, float a1, float b0, float b1)
        {
            float length = a1 - a0;
            if (length <= 1e-3f) return a0 >= b0 - 1e-3f && a0 <= b1 + 1e-3f ? 1 : 0;
            return Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0)) / length;
        }
        for (int i = 0; i < rooms.Length; i++)
        {
            var r = rooms[i];
            result[i] = r.IsSky || skies.Any(s =>
                Inside(r.BoundsMin.X, r.BoundsMax.X, s.BoundsMin.X, s.BoundsMax.X) >= 0.9f &&
                Inside(r.BoundsMin.Y, r.BoundsMax.Y, s.BoundsMin.Y, s.BoundsMax.Y) >= 0.9f &&
                Inside(r.BoundsMin.Z, r.BoundsMax.Z, s.BoundsMin.Z, s.BoundsMax.Z) >= 0.9f);
        }
        return result;
    }

    /// <summary>
    /// Triangles the preview would draw for <paramref name="compiled"/> geometry (else brushes) with every face, counted
    /// from the model without building anything (the size check before a build).
    /// </summary>
    public long TriangleCount(bool compiled)
    {
        static long Of(RfGeometry g)
        {
            long n = 0;
            foreach (var f in g.Faces) if (f.Corners.Length >= 3) n += f.Corners.Length - 2;
            return n;
        }
        if (Level is { } level)
        {
            if (compiled) return Of(level.StaticGeometry) + level.Movers.Sum(m => Of(m.Geometry));
            return level.Brushes.Sum(b => Of(b.Geometry));
        }
        return GroupFile!.Groups.Sum(g => g.Brushes.Sum(b => Of(b.Geometry)));
    }
}
