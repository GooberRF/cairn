using System.Collections.Immutable;
using System.Numerics;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>One triangle of a <see cref="Rf2StaticGeometry"/>: three vertex indices and a UV per corner, as stored.</summary>
public readonly record struct Rf2StaticTriangle(int A, int B, int C, Vector2 UvA, Vector2 UvB, Vector2 UvC);

/// <summary>One geometry of a <see cref="Rf2StaticBody"/>: its own vertices (position, normal), triangles and texture index.</summary>
public sealed record Rf2StaticGeometry(ImmutableArray<Vector3> Positions, ImmutableArray<Vector3> Normals, ImmutableArray<Rf2StaticTriangle> Triangles, int Texture, int Material);

/// <summary>
/// A static Red Faction II LOD body (the record a <c>.rfm</c> LOD holds, read by the same code): texture names, the number
/// of material records, geometries, and the LOD's offset, radius and box. A Red Faction II effect (<c>.rfe</c>) embeds one
/// per mesh object. Positions, normals, UVs and winding are as stored (Cairn's Red Faction II mesh conversion keeps them
/// unchanged too).
/// </summary>
public sealed record Rf2StaticBody(ImmutableArray<string> Textures, int MaterialCount, ImmutableArray<Rf2StaticGeometry> Geometries,
    Vector3 Offset, float Radius, Vector3 BoxMin, Vector3 BoxMax)
{
    /// <summary>Vertices over every geometry.</summary>
    public int VertexCount => Geometries.Sum(g => g.Positions.Length);

    /// <summary>Triangles over every geometry.</summary>
    public int TriangleCount => Geometries.Sum(g => g.Triangles.Length);

    /// <summary>
    /// Reads the body at <paramref name="position"/> in <paramref name="data"/> and moves <paramref name="position"/> past it.
    /// </summary>
    /// <param name="data">The whole file.</param>
    /// <param name="position">Where the body starts; on return, where it ends.</param>
    /// <param name="name">The file's name, for messages.</param>
    /// <exception cref="AssetFormatException">The body is damaged or cut short.</exception>
    public static Rf2StaticBody Read(ReadOnlySpan<byte> data, ref int position, string name)
    {
        Rf2Lod lod;
        try
        {
            lod = Rf2MeshParser.ReadStaticLod(data, ref position);
        }
        catch (Rf2FormatException ex)
        {
            throw new AssetFormatException($"'{name}' is damaged: a mesh body at 0x{ex.Offset:X}: {ex.Message}");
        }
        var geometries = lod.Geometries.Select(g => new Rf2StaticGeometry(
            [.. g.Vertices.Select(v => v.Position)], [.. g.Vertices.Select(v => v.Normal)],
            [.. g.Triangles.Select(t => new Rf2StaticTriangle(t.A.Vertex, t.B.Vertex, t.C.Vertex, t.A.Uv, t.B.Uv, t.C.Uv))],
            g.Texture, g.Material)).ToImmutableArray();
        return new Rf2StaticBody(lod.Textures, lod.Materials.Length, geometries, lod.Offset, lod.Radius, lod.BoxMin, lod.BoxMax);
    }
}
