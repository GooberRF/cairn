using System.Collections.Immutable;
using System.Numerics;

namespace Cairn.Rfa.Formats.V3d;

/// <summary>What <see cref="V3dTextureAtlas.Combine"/> made.</summary>
/// <param name="Submesh">The submesh using the atlases (the same one when it was within the limit).</param>
/// <param name="Files">The atlases as <c>.tga</c> files.</param>
/// <param name="Combined">Each atlas's name and the textures in it.</param>
public sealed record V3dAtlasResult(
    V3dSubmeshDescription Submesh,
    ImmutableArray<(string Name, byte[] Bytes)> Files,
    ImmutableArray<(string Atlas, ImmutableArray<string> Sources)> Combined);

/// <summary>
/// Brings a submesh down to <see cref="V3dBuilder.MaxTexturesPerLod"/> textures per LOD by drawing the fewest textures
/// needed into an atlas and moving their UVs, for a mesh that cannot be split (a <c>.v3c</c> is one submesh). A texture
/// can go in only with its UVs inside 0 to 1 (a tiling texture would repeat its neighbours), and only with textures
/// whose materials draw alike. Sources are never scaled: each keeps its pixels in an equal cell of a power-of-two image.
/// </summary>
public static class V3dTextureAtlas
{
    /// <summary>The largest atlas side (larger ones have not been tried in game).</summary>
    public const int MaxSize = 1024;

    // UVs this far outside 0 to 1 are rounding, not tiling.
    private const float UvSlack = 0.001f;

    private sealed record Candidate(int Material, string Name, BgraImage Image, int Triangles)
    {
        public int Pixels => Image.Width * Image.Height;

        /// <summary>The name up to its first digit: <c>FPARM01.tga</c> and <c>FPARM02.tga</c> are parts of one thing.</summary>
        public string Family => new string([.. Name.TakeWhile(c => !char.IsAsciiDigit(c))]).ToLowerInvariant();
    }

    private readonly record struct Layout(int Columns, int CellWidth, int CellHeight, int Width, int Height)
    {
        public int Area => Width * Height;
    }

    private readonly record struct Slot(int X, int Y, int Width, int Height, int AtlasWidth, int AtlasHeight)
    {
        public Vector2 Map(Vector2 uv) => new(
            (X + 0.5f + Math.Clamp(uv.X, 0f, 1f) * (Width - 1)) / AtlasWidth,
            (Y + 0.5f + Math.Clamp(uv.Y, 0f, 1f) * (Height - 1)) / AtlasHeight);
    }

    /// <summary>The most textures any LOD of the submesh uses.</summary>
    public static int MostTextures(V3dSubmeshDescription submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        return submesh.Lods.IsDefaultOrEmpty ? 0 : submesh.Lods.Max(l => l.Groups.Select(g => g.Material).Distinct().Count());
    }

    /// <summary>
    /// Combines textures until every LOD fits; a submesh that fits comes back as it is.
    /// </summary>
    /// <param name="submesh">The one submesh of a character.</param>
    /// <param name="meshName">The mesh's name without extension; the atlases are named after it.</param>
    /// <param name="pixels">The image of a texture name, or null when it is not available (such a texture stays out).</param>
    /// <exception cref="AssetFormatException">Not enough textures can be combined; the message names them and the limit.</exception>
    public static V3dAtlasResult Combine(V3dSubmeshDescription submesh, string meshName, Func<string, BgraImage?> pixels)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        ArgumentNullException.ThrowIfNull(meshName);
        ArgumentNullException.ThrowIfNull(pixels);
        int most = MostTextures(submesh), excess = most - V3dBuilder.MaxTexturesPerLod;
        if (excess <= 0) return new V3dAtlasResult(submesh, [], []);

        var candidates = new List<Candidate>();
        var refused = new List<string>();
        for (int m = 0; m < submesh.Materials.Length; m++)
        {
            int material = m;
            var groups = submesh.Lods.SelectMany(l => l.Groups).Where(g => g.Material == material).ToList();
            if (groups.Count == 0) continue;
            string name = submesh.Materials[m].DiffuseMap.Text;
            if (!groups.All(g => g.Vertices.All(v => InsideOne(v.TexCoord)))) refused.Add($"{name} (it tiles)");
            else if (pixels(name) is { } image) candidates.Add(new Candidate(m, name, image, groups.Sum(g => g.Triangles.Length)));
            else refused.Add($"{name} (its image was not found)");
        }

        var picks = Pick(candidates, excess, submesh);
        if (picks is null) throw TooMany(meshName, most, refused);

        var slots = new Dictionary<int, Slot>();
        var atlasOf = new Dictionary<int, (int Pick, string Name)>();
        var files = ImmutableArray.CreateBuilder<(string, byte[])>(picks.Count);
        var combined = ImmutableArray.CreateBuilder<(string, ImmutableArray<string>)>(picks.Count);
        for (int p = 0; p < picks.Count; p++)
        {
            string name = AtlasName(meshName, p);
            var image = Draw(picks[p], slots);
            foreach (var c in picks[p]) atlasOf[c.Material] = (p, name);
            files.Add((name, TgaWriter.Write(image, PegTextureSet.HasAlpha(image))));
            combined.Add((name, [.. picks[p].Select(c => c.Name)]));
        }

        // the first texture of each atlas becomes the atlas; the others go
        var materials = new List<V3dMaterial>();
        var moved = new int[submesh.Materials.Length];
        var placed = new Dictionary<int, int>();
        for (int m = 0; m < moved.Length; m++)
        {
            if (!atlasOf.TryGetValue(m, out var atlas))
            {
                moved[m] = materials.Count;
                materials.Add(submesh.Materials[m]);
            }
            else if (placed.TryGetValue(atlas.Pick, out int at))
            {
                moved[m] = at;
            }
            else
            {
                moved[m] = placed[atlas.Pick] = materials.Count;
                materials.Add(submesh.Materials[m] with { DiffuseMap = FixedString.FromText(atlas.Name, V3dMaterial.NameSize) });
            }
        }

        var result = submesh with
        {
            Materials = [.. materials],
            Lods = [.. submesh.Lods.Select(l => l with
            {
                Textures = null,
                Groups = [.. l.Groups.Select(g => g with
                {
                    Material = moved[g.Material],
                    Vertices = slots.TryGetValue(g.Material, out var slot)
                        ? [.. g.Vertices.Select(v => v with { TexCoord = slot.Map(v.TexCoord) })]
                        : g.Vertices,
                })],
            })],
        };
        // a texture a smaller LOD does not use saves that LOD nothing
        if (MostTextures(result) > V3dBuilder.MaxTexturesPerLod) throw TooMany(meshName, most, refused);
        return new V3dAtlasResult(result, files.MoveToImmutable(), combined.MoveToImmutable());
    }

    private static AssetFormatException TooMany(string meshName, int most, List<string> refused) => new(
        $"{meshName} uses {most} textures in one level of detail, but a .v3c holds {V3dBuilder.MaxTexturesPerLod} in its one submesh, and too few can be combined into an atlas"
        + (refused.Count > 0 ? $": {string.Join(", ", refused)} cannot go in one" : "")
        + $". Textures in an atlas need UVs inside 0 to 1, the same material settings, and images found beside the mesh; an atlas side stays within {MaxSize} pixels.");

    private static bool InsideOne(Vector2 uv) => uv.X >= -UvSlack && uv.X <= 1f + UvSlack && uv.Y >= -UvSlack && uv.Y <= 1f + UvSlack;

    /// <summary>
    /// The atlases to make, removing <paramref name="excess"/> textures: one when one material setting can do it all,
    /// else as many as it takes, the largest setting first. Null when the candidates cannot.
    /// </summary>
    private static List<List<Candidate>>? Pick(List<Candidate> candidates, int excess, V3dSubmeshDescription submesh)
    {
        var settings = candidates.GroupBy(c => V3dSubmeshMerge.Settings(submesh.Materials[c.Material])).Select(g => g.ToList()).OrderByDescending(g => g.Count).ToList();
        var one = settings.Where(g => g.Count > excess).Select(g => Best(g, excess + 1)).OfType<List<Candidate>>().OrderBy(Families).ThenBy(s => Arrange(s)!.Value.Area).ThenBy(Triangles).FirstOrDefault();
        if (one is not null) return [one];

        var picks = new List<List<Candidate>>();
        foreach (var group in settings)
        {
            while (excess > 0 && group.Count >= 2)
            {
                var sizes = Enumerable.Range(2, Math.Min(group.Count, excess + 1) - 1).Reverse();
                if (sizes.Select(take => Best(group, take)).OfType<List<Candidate>>().FirstOrDefault() is not { } set) break;
                picks.Add(set);
                group.RemoveAll(set.Contains);
                excess -= set.Count - 1;
            }
        }
        return excess <= 0 ? picks : null;
    }

    /// <summary><paramref name="take"/> textures of one material setting that fit an atlas: of one family if one has enough, then the smallest atlas, then the fewest triangles.</summary>
    private static List<Candidate>? Best(List<Candidate> group, int take)
    {
        var options = group.GroupBy(c => c.Family).Where(f => f.Count() >= take).Select(f => Cheapest(f, take)).Append(Cheapest(group, take));
        return options.Where(o => Arrange(o) is not null).OrderBy(Families).ThenBy(o => Arrange(o)!.Value.Area).ThenBy(Triangles).FirstOrDefault();
    }

    private static List<Candidate> Cheapest(IEnumerable<Candidate> candidates, int take) =>
        [.. candidates.OrderBy(c => c.Pixels).ThenBy(c => c.Triangles).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Take(take)];

    private static int Families(List<Candidate> set) => set.Select(c => c.Family).Distinct().Count();

    private static int Triangles(List<Candidate> set) => set.Sum(c => c.Triangles);

    /// <summary>The grid for these textures: the smallest power-of-two image within <see cref="MaxSize"/>, the squarest of equals; null when none fits.</summary>
    private static Layout? Arrange(List<Candidate> set)
    {
        int cellWidth = set.Max(c => c.Image.Width), cellHeight = set.Max(c => c.Image.Height);
        Layout? best = null;
        for (int columns = 1; columns <= set.Count; columns++)
        {
            int rows = (set.Count + columns - 1) / columns;
            ulong width = BitOperations.RoundUpToPowerOf2((ulong)(columns * (long)cellWidth));
            ulong height = BitOperations.RoundUpToPowerOf2((ulong)(rows * (long)cellHeight));
            if (width > MaxSize || height > MaxSize) continue;
            var layout = new Layout(columns, cellWidth, cellHeight, (int)width, (int)height);
            if (best is not { } b || layout.Area < b.Area || (layout.Area == b.Area && Math.Abs(layout.Width - layout.Height) < Math.Abs(b.Width - b.Height))) best = layout;
        }
        return best;
    }

    /// <summary>Draws the textures in their cells (first at the top left, each at its cell's top left), recording where each went.</summary>
    private static BgraImage Draw(List<Candidate> set, Dictionary<int, Slot> slots)
    {
        var layout = Arrange(set)!.Value;
        var atlas = new BgraImage(layout.Width, layout.Height);
        // the unused cells are opaque, so an atlas of opaque textures stays a 24-bit file
        for (int i = 3; i < atlas.Pixels.Length; i += 4) atlas.Pixels[i] = 255;
        for (int i = 0; i < set.Count; i++)
        {
            var image = set[i].Image;
            int x = i % layout.Columns * layout.CellWidth, y = i / layout.Columns * layout.CellHeight;
            for (int row = 0; row < image.Height; row++)
                Array.Copy(image.Pixels, row * image.Width * 4, atlas.Pixels, ((y + row) * atlas.Width + x) * 4, image.Width * 4);
            slots[set[i].Material] = new Slot(x, y, image.Width, image.Height, atlas.Width, atlas.Height);
        }
        return atlas;
    }

    /// <summary><c>&lt;mesh&gt;_atlas.tga</c> (<c>_atlas2</c> for the second and so on), the mesh name cut to fit a texture name.</summary>
    private static string AtlasName(string meshName, int index)
    {
        string suffix = (index == 0 ? "_atlas" : "_atlas" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) + ".tga";
        string clean = new([.. meshName.Select(c => c is <= ' ' or > '~' ? '_' : c)]);
        int room = V3dMaterial.NameSize - 1 - suffix.Length;
        return (clean.Length > room ? clean[..room] : clean) + suffix;
    }
}
