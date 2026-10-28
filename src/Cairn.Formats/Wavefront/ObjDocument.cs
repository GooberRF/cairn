using System.Numerics;

namespace Cairn.Formats.Obj;

/// <summary>
/// A whole Wavefront .obj file: the shared vertex pools, the objects with their faces, and the
/// comments in file order. Format-neutral: coordinates and UVs are exactly as the file stores them
/// (no axis or handedness conversion, no V flip). Every index is resolved to a 0-based position in
/// the matching pool, so relative (negative) indices never reach the caller. Collections are
/// mutable so importers and exporters can build or edit a document in place.
/// </summary>
public sealed class ObjDocument
{
    /// <summary>The <c>mtllib</c> file names in file order (usually one, relative to the .obj).</summary>
    public List<string> MaterialLibraries { get; } = new();

    /// <summary>Vertex positions (<c>v</c>); a fourth (weight) component is dropped.</summary>
    public List<Vector3> Positions { get; } = new();

    /// <summary>
    /// Per-position vertex colours (<c>v x y z r g b</c>, as some exporters write), parallel to
    /// <see cref="Positions"/>; null when no vertex has one. Vertices without a colour in a file that
    /// has some get white.
    /// </summary>
    public List<Vector3>? Colors { get; set; }

    /// <summary>Texture coordinates (<c>vt</c>) as stored: V is not flipped, a third (w) component is dropped.</summary>
    public List<Vector2> TexCoords { get; } = new();

    /// <summary>
    /// Vertex normals (<c>vn</c>) as stored (not renormalised). A normal written as NaN or infinity
    /// (some exporters do this for degenerate faces) is read as the zero vector and counted in
    /// <see cref="NonFiniteNormals"/>, so an importer can recompute it.
    /// </summary>
    public List<Vector3> Normals { get; } = new();

    /// <summary>How many <c>vn</c> lines held NaN or infinity and were read as zero vectors.</summary>
    public int NonFiniteNormals { get; set; }

    /// <summary>
    /// The objects in file order. Faces before the first <c>o</c> statement belong to an object with an
    /// empty name; an <c>o</c> with no faces is kept (empty).
    /// </summary>
    public List<ObjObject> Objects { get; } = new();

    /// <summary>
    /// Every comment line, in file order, with where it stood (see <see cref="ObjComment"/>), so a
    /// writer can put it back in place and Cairn can read back the metadata lines it wrote.
    /// </summary>
    public List<ObjComment> Comments { get; } = new();

    /// <summary>
    /// Statements the reader recognised as valid OBJ but does not model (lines, points, curves,
    /// surfaces, parameter-space vertices, display attributes), keyword to count. Empty for a
    /// polygon-only file. The writer never emits these.
    /// </summary>
    public Dictionary<string, int> IgnoredStatements { get; } = new(StringComparer.Ordinal);

    /// <summary>Total number of faces over every object.</summary>
    public int FaceCount => Objects.Sum(o => o.Faces.Count);

    /// <summary>
    /// Comments whose text starts with <paramref name="prefix"/> (ordinal), for reading back metadata
    /// lines such as <c># rf_brush uid=…</c>.
    /// </summary>
    public IEnumerable<ObjComment> CommentsStartingWith(string prefix) =>
        Comments.Where(c => c.Text.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// Splits the faces into parts by (object, first group name), in first-seen order, skipping parts
    /// with no faces. This is "one part per <c>o</c> or <c>g</c>": Blender writes <c>o</c>, 3ds Max and
    /// Maya write <c>g</c>, and a file with both splits an object only where its faces name more than one
    /// group. A part's name is the object name when the object is not split, the group name when the
    /// object is unnamed, or "object/group" otherwise; an unnamed part is called "default".
    /// </summary>
    public IReadOnlyList<ObjPart> SplitIntoParts()
    {
        var parts = new List<ObjPart>();
        for (int o = 0; o < Objects.Count; o++)
        {
            var obj = Objects[o];
            var byGroup = new Dictionary<string, List<ObjFace>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var face in obj.Faces)
            {
                string group = face.Groups.Count > 0 ? face.Groups[0] : "";
                if (!byGroup.TryGetValue(group, out var list))
                {
                    byGroup[group] = list = new List<ObjFace>();
                    order.Add(group);
                }
                list.Add(face);
            }
            foreach (string group in order)
            {
                string name = obj.Name.Length == 0 ? group
                    : order.Count == 1 || group.Length == 0 || group == obj.Name ? obj.Name
                    : obj.Name + "/" + group;
                if (name.Length == 0) name = "default";
                parts.Add(new ObjPart(name, o, group, byGroup[group]));
            }
        }
        return parts;
    }
}

/// <summary>One <c>o</c> block: a name and the faces that follow it until the next <c>o</c>.</summary>
public sealed class ObjObject
{
    public ObjObject() { }

    public ObjObject(string name) { Name = name ?? ""; }

    /// <summary>The object name (the rest of the <c>o</c> line, spaces kept); empty for faces before any <c>o</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>The faces in file order.</summary>
    public List<ObjFace> Faces { get; } = new();
}

/// <summary>
/// One polygon (<c>f</c>) with three or more corners in file winding order, plus the state that was
/// active when it was read: material, groups and smoothing group.
/// </summary>
public sealed class ObjFace
{
    /// <summary>The corners (three or more).</summary>
    public List<ObjCorner> Corners { get; } = new();

    /// <summary>The active <c>usemtl</c> name, or null when none was set.</summary>
    public string? Material { get; set; }

    /// <summary>
    /// The active <c>g</c> names (a statement may name several; Maya writes the hierarchy this way);
    /// empty when no group was set. Faces read under the same statement share one list instance.
    /// </summary>
    public IReadOnlyList<string> Groups { get; set; } = [];

    /// <summary>The active smoothing group (<c>s</c>); 0 for none (<c>s 0</c> / <c>s off</c>).</summary>
    public int SmoothingGroup { get; set; }
}

/// <summary>
/// One face corner: 0-based indices into <see cref="ObjDocument.Positions"/>,
/// <see cref="ObjDocument.TexCoords"/> and <see cref="ObjDocument.Normals"/>; -1 means the corner has
/// no texture coordinate or normal.
/// </summary>
public readonly record struct ObjCorner(int Position, int TexCoord = -1, int Normal = -1)
{
    /// <summary>True when the corner names a texture coordinate.</summary>
    public bool HasTexCoord => TexCoord >= 0;

    /// <summary>True when the corner names a normal.</summary>
    public bool HasNormal => Normal >= 0;
}

/// <summary>
/// A comment line. <see cref="Text"/> is what follows the <c>#</c>, trimmed. Its place is recorded as
/// the object it appeared in (<see cref="ObjectIndex"/>, -1 before the first object, which is also the
/// file header) and how many of that object's faces came before it (<see cref="FaceIndex"/>).
/// </summary>
public sealed record ObjComment(string Text, int ObjectIndex = -1, int FaceIndex = 0);

/// <summary>A part from <see cref="ObjDocument.SplitIntoParts"/>: a display name and its faces.</summary>
public sealed record ObjPart(string Name, int ObjectIndex, string Group, IReadOnlyList<ObjFace> Faces);

/// <summary>A whole .mtl material library: the materials in file order plus the comments.</summary>
public sealed class MtlDocument
{
    /// <summary>The materials (<c>newmtl</c>) in file order.</summary>
    public List<MtlMaterial> Materials { get; } = new();

    /// <summary>
    /// Comment lines (text after <c>#</c>, trimmed) with the material they followed
    /// (<see cref="MtlComment.MaterialIndex"/>, -1 before the first <c>newmtl</c>).
    /// </summary>
    public List<MtlComment> Comments { get; } = new();

    /// <summary>The material with this name (ordinal, the last one when a name repeats), or null.</summary>
    public MtlMaterial? Find(string? name)
    {
        if (name is null) return null;
        for (int i = Materials.Count - 1; i >= 0; i--)
            if (string.Equals(Materials[i].Name, name, StringComparison.Ordinal)) return Materials[i];
        return null;
    }
}

/// <summary>A comment in a .mtl file and the material it followed (-1 before the first).</summary>
public sealed record MtlComment(string Text, int MaterialIndex = -1);

/// <summary>
/// One <c>newmtl</c> block. The statements Cairn uses are typed; every other statement is kept in
/// <see cref="OtherStatements"/> verbatim, in file order, and written back after the typed ones.
/// </summary>
public sealed class MtlMaterial
{
    public MtlMaterial() { }

    public MtlMaterial(string name) { Name = name ?? ""; }

    /// <summary>The material name (the rest of the <c>newmtl</c> line).</summary>
    public string Name { get; set; } = "";

    /// <summary>Ambient colour (<c>Ka</c>), or null when absent.</summary>
    public Vector3? Ambient { get; set; }

    /// <summary>Diffuse colour (<c>Kd</c>), or null when absent.</summary>
    public Vector3? Diffuse { get; set; }

    /// <summary>Specular colour (<c>Ks</c>), or null when absent.</summary>
    public Vector3? Specular { get; set; }

    /// <summary>Emissive colour (<c>Ke</c>), or null when absent.</summary>
    public Vector3? Emissive { get; set; }

    /// <summary>Specular exponent (<c>Ns</c>), or null when absent.</summary>
    public float? SpecularExponent { get; set; }

    /// <summary>
    /// Opacity (<c>d</c>, 1 = opaque), or null when absent. A file that writes <c>Tr</c> (transparency)
    /// instead is read as <c>d = 1 - Tr</c>; the writer always writes <c>d</c>.
    /// </summary>
    public float? Dissolve { get; set; }

    /// <summary>Illumination model (<c>illum</c>), or null when absent.</summary>
    public int? Illumination { get; set; }

    /// <summary>The diffuse texture (<c>map_Kd</c>), or null when absent.</summary>
    public MtlTextureMap? DiffuseMap { get; set; }

    /// <summary>
    /// Every other statement (<c>Ni</c>, <c>map_Bump</c>, <c>map_d</c>, PBR extensions and so on) as
    /// (keyword, rest of line), in file order.
    /// </summary>
    public List<KeyValuePair<string, string>> OtherStatements { get; } = new();
}

/// <summary>
/// A texture map statement: the file as written (relative or absolute, either slash; quotes removed)
/// and any leading options (<c>-s 1 1 1</c>, <c>-clamp on</c>, …) kept raw.
/// </summary>
public sealed record MtlTextureMap(string Path, string Options = "")
{
    /// <summary>
    /// The file name without its folder, for either slash style (<c>textures\brick.tga</c> and
    /// <c>C:/x/brick.tga</c> both give <c>brick.tga</c>). RF finds textures by name, so this is what
    /// importers use.
    /// </summary>
    public string FileName
    {
        get
        {
            int cut = Math.Max(Path.LastIndexOf('/'), Path.LastIndexOf('\\'));
            return cut < 0 ? Path : Path[(cut + 1)..];
        }
    }
}
