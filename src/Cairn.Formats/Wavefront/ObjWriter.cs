using System.Globalization;
using System.Numerics;
using System.Text;
using Cairn.Workspace;

namespace Cairn.Formats.Obj;

/// <summary>Options for <see cref="ObjWriter"/>.</summary>
public sealed record ObjWriteOptions
{
    /// <summary>
    /// Decimal places for coordinates, UVs, normals and colours (trailing zeros dropped); null writes
    /// the shortest text that reads back to the same float. Default 6, as Blender writes.
    /// </summary>
    public int? Decimals { get; init; } = 6;

    /// <summary>True splits every face with more than three corners into triangles (<see cref="ObjTriangulator"/>); false keeps n-gons.</summary>
    public bool Triangulate { get; init; }

    /// <summary>True ends lines with CR LF (Windows tools), false with LF (Blender's own output).</summary>
    public bool UseCrLf { get; init; } = true;
}

/// <summary>
/// Writes <see cref="ObjDocument"/> as .obj text and <see cref="MtlDocument"/> as .mtl text, in the
/// invariant culture. The vertex pools come first, then each object (<c>o</c>) with <c>g</c>,
/// <c>usemtl</c> and <c>s</c> written whenever they change (and <c>usemtl</c> again at the start of
/// every object, so tools that reset materials per object still see it). Comments are put back where
/// <see cref="ObjComment"/> says they stood. Indices are written 1-based and absolute. The document is
/// validated first (every index in range, three or more corners per face, names on one line, one
/// colour per position) and an inconsistent one raises <see cref="ArgumentException"/>; it is never
/// modified.
/// </summary>
public static class ObjWriter
{
    /// <summary>The .obj text.</summary>
    /// <exception cref="ArgumentException">The document is inconsistent.</exception>
    public static string Write(ObjDocument doc, ObjWriteOptions? options = null) => Write(doc, options, null);

    /// <summary>The .mtl text.</summary>
    /// <exception cref="ArgumentException">A name or value spans lines.</exception>
    public static string WriteMtl(MtlDocument mtl, ObjWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mtl);
        options ??= new ObjWriteOptions();
        var w = new LineWriter(options);
        foreach (var comment in mtl.Comments)
            if (comment.MaterialIndex < 0) w.Comment(comment.Text);
        for (int i = 0; i < mtl.Materials.Count; i++)
        {
            var m = mtl.Materials[i];
            CheckName(m.Name, "Material name");
            if (w.HasContent) w.Line("");
            w.Line("newmtl " + m.Name);
            if (m.SpecularExponent is { } ns) w.Line("Ns " + w.Number(ns));
            if (m.Ambient is { } ka) w.Line("Ka " + w.Vector(ka));
            if (m.Diffuse is { } kd) w.Line("Kd " + w.Vector(kd));
            if (m.Specular is { } ks) w.Line("Ks " + w.Vector(ks));
            if (m.Emissive is { } ke) w.Line("Ke " + w.Vector(ke));
            if (m.Dissolve is { } d) w.Line("d " + w.Number(d));
            if (m.Illumination is { } illum) w.Line("illum " + illum.ToString(CultureInfo.InvariantCulture));
            if (m.DiffuseMap is { } map)
            {
                CheckName(map.Path, "Texture path");
                CheckName(map.Options, "Texture options");
                w.Line("map_Kd " + (map.Options.Length > 0 ? map.Options + " " : "") + map.Path);
            }
            foreach (var (keyword, value) in m.OtherStatements)
            {
                CheckName(keyword, "Statement keyword");
                CheckName(value, "Statement value");
                if (keyword.Length == 0 || keyword.Any(char.IsWhiteSpace) || keyword[0] == '#')
                    throw new ArgumentException($"'{keyword}' is not a valid MTL keyword (material '{m.Name}').", nameof(mtl));
                w.Line(value.Length > 0 ? keyword + " " + value : keyword);
            }
            foreach (var comment in mtl.Comments)
                if (comment.MaterialIndex == i) w.Comment(comment.Text);
        }
        foreach (var comment in mtl.Comments)
            if (comment.MaterialIndex >= mtl.Materials.Count) w.Comment(comment.Text);
        return w.ToString();
    }

    /// <summary>
    /// Writes <paramref name="objPath"/> and, when <paramref name="mtl"/> is given, the .mtl beside it
    /// with the same base name (the .obj's <c>mtllib</c> then names that file instead of
    /// <see cref="ObjDocument.MaterialLibraries"/>). Both are UTF-8 without a BOM and written atomically,
    /// the .mtl first.
    /// </summary>
    /// <exception cref="ArgumentException">The document is inconsistent.</exception>
    public static void WriteFiles(string objPath, ObjDocument doc, MtlDocument? mtl, ObjWriteOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(objPath);
        ArgumentNullException.ThrowIfNull(doc);
        string? mtlName = mtl is null ? null : Path.GetFileNameWithoutExtension(objPath) + ".mtl";
        string objText = Write(doc, options, mtlName);
        if (mtl is not null)
        {
            string mtlText = WriteMtl(mtl, options);
            AtomicFile.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? "", mtlName!), mtlText);
        }
        AtomicFile.WriteAllText(objPath, objText);
    }

    private static string Write(ObjDocument doc, ObjWriteOptions? options, string? materialLibrary)
    {
        ArgumentNullException.ThrowIfNull(doc);
        options ??= new ObjWriteOptions();
        Validate(doc);
        var w = new LineWriter(options);

        foreach (var comment in doc.Comments)
            if (comment.ObjectIndex < 0) w.Comment(comment.Text);
        if (materialLibrary is not null) w.Line("mtllib " + materialLibrary);
        else foreach (string library in doc.MaterialLibraries) w.Line("mtllib " + library);

        var colors = doc.Colors;
        for (int i = 0; i < doc.Positions.Count; i++)
            w.Line(colors is null ? "v " + w.Vector(doc.Positions[i]) : "v " + w.Vector(doc.Positions[i]) + " " + w.Vector(colors[i]));
        foreach (var uv in doc.TexCoords) w.Line("vt " + w.Number(uv.X) + " " + w.Number(uv.Y));
        foreach (var normal in doc.Normals) w.Line("vn " + w.Vector(normal));

        // Each object's comments in face order (stable, so equal positions keep file order).
        var commentsByObject = doc.Comments.Where(c => c.ObjectIndex >= 0)
            .GroupBy(c => c.ObjectIndex)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.FaceIndex).ToList());

        string? material = null;
        IReadOnlyList<string> groups = [];
        int smoothing = 0;
        var corners = new StringBuilder();
        for (int o = 0; o < doc.Objects.Count; o++)
        {
            var obj = doc.Objects[o];
            // An unnamed first object with faces is the reader's default object: no "o" line, so the
            // file reads back the same.
            if (!(o == 0 && obj.Name.Length == 0 && obj.Faces.Count > 0))
                w.Line(obj.Name.Length > 0 ? "o " + obj.Name : "o");
            var comments = commentsByObject.GetValueOrDefault(o) ?? [];
            int nextComment = 0;
            bool newObject = true;
            for (int f = 0; f <= obj.Faces.Count; f++)
            {
                bool last = f == obj.Faces.Count;
                while (nextComment < comments.Count && (comments[nextComment].FaceIndex <= f || last))
                    w.Comment(comments[nextComment++].Text);
                if (last) break;
                var face = obj.Faces[f];
                if (!groups.SequenceEqual(face.Groups, StringComparer.Ordinal))
                {
                    groups = face.Groups;
                    w.Line(groups.Count > 0 ? "g " + string.Join(' ', groups) : "g");
                }
                if (newObject && face.Material is not null || !string.Equals(material, face.Material, StringComparison.Ordinal))
                {
                    material = face.Material;
                    w.Line(material is null ? "usemtl" : "usemtl " + material);
                }
                newObject = false;
                if (face.SmoothingGroup != smoothing)
                {
                    smoothing = face.SmoothingGroup;
                    w.Line(smoothing == 0 ? "s off" : "s " + smoothing.ToString(CultureInfo.InvariantCulture));
                }
                if (options.Triangulate && face.Corners.Count > 3)
                {
                    foreach (var (a, b, c) in ObjTriangulator.Triangulate(doc.Positions, face))
                    {
                        corners.Clear();
                        AppendCorner(corners, face.Corners[a]);
                        AppendCorner(corners, face.Corners[b]);
                        AppendCorner(corners, face.Corners[c]);
                        w.Line("f" + corners);
                    }
                }
                else
                {
                    corners.Clear();
                    foreach (var corner in face.Corners) AppendCorner(corners, corner);
                    w.Line("f" + corners);
                }
            }
        }
        // Comments recorded against an object that no longer exists still come out, at the end.
        foreach (var comment in doc.Comments)
            if (comment.ObjectIndex >= doc.Objects.Count) w.Comment(comment.Text);
        return w.ToString();
    }

    private static void AppendCorner(StringBuilder text, ObjCorner corner)
    {
        text.Append(' ').Append((corner.Position + 1).ToString(CultureInfo.InvariantCulture));
        if (!corner.HasTexCoord && !corner.HasNormal) return;
        text.Append('/');
        if (corner.HasTexCoord) text.Append((corner.TexCoord + 1).ToString(CultureInfo.InvariantCulture));
        if (corner.HasNormal) text.Append('/').Append((corner.Normal + 1).ToString(CultureInfo.InvariantCulture));
    }

    private static void Validate(ObjDocument doc)
    {
        if (doc.Colors is { } colors && colors.Count != doc.Positions.Count)
            throw new ArgumentException($"The document has {colors.Count} vertex colours for {doc.Positions.Count} positions.", nameof(doc));
        CheckFinite(doc.Positions, "position");
        if (doc.Colors is not null) CheckFinite(doc.Colors, "vertex colour");
        CheckFinite(doc.Normals, "normal");
        for (int i = 0; i < doc.TexCoords.Count; i++)
            if (!float.IsFinite(doc.TexCoords[i].X) || !float.IsFinite(doc.TexCoords[i].Y))
                throw new ArgumentException($"Texture coordinate {i} is not finite ({doc.TexCoords[i]}).", nameof(doc));
        foreach (string library in doc.MaterialLibraries) CheckName(library, "Material library");
        for (int o = 0; o < doc.Objects.Count; o++)
        {
            var obj = doc.Objects[o];
            CheckName(obj.Name, "Object name");
            for (int f = 0; f < obj.Faces.Count; f++)
            {
                var face = obj.Faces[f];
                string where = $"face {f} of object {o} ('{obj.Name}')";
                if (face.Corners.Count < 3) throw new ArgumentException($"{where} has {face.Corners.Count} corners; a face needs three or more.", nameof(doc));
                if (face.Material is not null) CheckName(face.Material, "Material name");
                foreach (string group in face.Groups)
                    if (group.Length == 0 || group.Any(char.IsWhiteSpace))
                        throw new ArgumentException($"{where} has the group name '{group}'; group names must be non-empty and contain no spaces.", nameof(doc));
                if (face.SmoothingGroup < 0) throw new ArgumentException($"{where} has a negative smoothing group.", nameof(doc));
                foreach (var corner in face.Corners)
                {
                    if ((uint)corner.Position >= (uint)doc.Positions.Count)
                        throw new ArgumentException($"{where} names position {corner.Position}, but there are {doc.Positions.Count}.", nameof(doc));
                    if (corner.TexCoord < -1 || corner.TexCoord >= doc.TexCoords.Count)
                        throw new ArgumentException($"{where} names texture coordinate {corner.TexCoord}, but there are {doc.TexCoords.Count}.", nameof(doc));
                    if (corner.Normal < -1 || corner.Normal >= doc.Normals.Count)
                        throw new ArgumentException($"{where} names normal {corner.Normal}, but there are {doc.Normals.Count}.", nameof(doc));
                }
            }
        }
    }

    private static void CheckFinite(List<Vector3> values, string what)
    {
        for (int i = 0; i < values.Count; i++)
            if (!float.IsFinite(values[i].X) || !float.IsFinite(values[i].Y) || !float.IsFinite(values[i].Z))
                throw new ArgumentException($"The {what} {i} is not finite ({values[i]}).", "doc");
    }

    private static void CheckName(string value, string what)
    {
        ArgumentNullException.ThrowIfNull(value, what);
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException($"{what} '{value.ReplaceLineEndings(" ")}' spans more than one line.");
    }

    /// <summary>Builds the text with the chosen line ending and number format.</summary>
    private sealed class LineWriter(ObjWriteOptions options)
    {
        private readonly StringBuilder _text = new();
        private readonly string _newLine = options.UseCrLf ? "\r\n" : "\n";
        private readonly string? _format = options.Decimals switch
        {
            null => null,
            < 0 or > 15 => throw new ArgumentOutOfRangeException(nameof(options), options.Decimals, "Decimals must be 0 to 15."),
            0 => "0",
            int d => "0." + new string('#', d),
        };

        public bool HasContent => _text.Length > 0;

        public void Line(string line) => _text.Append(line).Append(_newLine);

        public void Comment(string text)
        {
            foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
                Line(line.Length > 0 ? "# " + line : "#");
        }

        public string Number(float value)
        {
            string text = _format is null
                ? value.ToString("R", CultureInfo.InvariantCulture)
                : ((double)value).ToString(_format, CultureInfo.InvariantCulture);
            // Never "-0": a value that rounds to zero is written as 0.
            return text == "-0" ? "0" : text;
        }

        public string Vector(Vector3 v) => Number(v.X) + " " + Number(v.Y) + " " + Number(v.Z);

        public override string ToString() => _text.ToString();
    }
}
