using System.Globalization;
using System.Numerics;
using System.Text;

namespace Cairn.Formats.Obj;

/// <summary>
/// Reads Wavefront .obj and .mtl text into <see cref="ObjDocument"/> / <see cref="MtlDocument"/>.
/// Tolerant of what Blender, 3ds Max and Maya write: any mix of spaces and tabs, trailing spaces,
/// CRLF/LF/CR line ends, a UTF-8 BOM (text that is not valid UTF-8 is read as Latin-1), backslash line
/// continuations, keywords in any case, comments after numbers, <c>vt</c> with one to three values,
/// <c>v</c> with a weight or a vertex colour, n-gons, corners with or without texture coordinates and
/// normals, negative (relative) indices, and normals written as NaN (read as zero, see
/// <see cref="ObjDocument.NonFiniteNormals"/>). Statements for curves, surfaces, lines and points are
/// valid OBJ but are only counted (<see cref="ObjDocument.IgnoredStatements"/>). Anything that cannot be
/// read (a number that does not parse, an index of 0 or out of range, a face with fewer than three
/// corners, binary data) raises <see cref="AssetFormatException"/> naming the file and the line, and
/// nothing else.
/// </summary>
public static class ObjReader
{
    /// <summary>The default size limit for <see cref="ReadFile"/> and <see cref="ReadMtlFile"/>.</summary>
    public const long DefaultMaxBytes = 512L * 1024 * 1024;

    /// <summary>Statements that are valid OBJ but describe things this DOM does not model.</summary>
    private static readonly HashSet<string> KnownIgnored = new(StringComparer.Ordinal)
    {
        "l", "p", "vp", "cstype", "deg", "bmat", "step", "curv", "curv2", "surf", "parm", "trim", "hole",
        "scrv", "sp", "end", "con", "mg", "lod", "bevel", "c_interp", "d_interp", "usemap", "maplib",
        "shadow_obj", "trace_obj", "ctech", "stech", "call", "csh", "fo",
    };

    /// <summary>Reads an .obj file (see <see cref="Read(byte[], string)"/>).</summary>
    /// <exception cref="AssetFormatException">The file is larger than <paramref name="maxBytes"/> or is not a readable OBJ.</exception>
    public static ObjDocument ReadFile(string path, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Read(ReadLimited(path, maxBytes), Path.GetFileName(path));
    }

    /// <summary>Reads an .obj file from its bytes; <paramref name="fileName"/> is used in error messages.</summary>
    /// <exception cref="AssetFormatException">The data is not a readable OBJ.</exception>
    public static ObjDocument Read(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Read(Decode(data, fileName), fileName);
    }

    /// <summary>Reads .obj text; <paramref name="fileName"/> is used in error messages.</summary>
    /// <exception cref="AssetFormatException">The text is not a readable OBJ.</exception>
    public static ObjDocument Read(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        fileName ??= "";
        return new ObjParser(fileName).Parse(text);
    }

    /// <summary>Reads an .mtl file (see <see cref="ReadMtl(byte[], string)"/>).</summary>
    /// <exception cref="AssetFormatException">The file is larger than <paramref name="maxBytes"/> or is not a readable MTL.</exception>
    public static MtlDocument ReadMtlFile(string path, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ReadMtl(ReadLimited(path, maxBytes), Path.GetFileName(path));
    }

    /// <summary>Reads an .mtl file from its bytes; <paramref name="fileName"/> is used in error messages.</summary>
    /// <exception cref="AssetFormatException">The data is not a readable MTL.</exception>
    public static MtlDocument ReadMtl(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ReadMtl(Decode(data, fileName), fileName);
    }

    /// <summary>Reads .mtl text; <paramref name="fileName"/> is used in error messages.</summary>
    /// <exception cref="AssetFormatException">The text is not a readable MTL.</exception>
    public static MtlDocument ReadMtl(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        fileName ??= "";
        var doc = new MtlDocument();
        MtlMaterial? current = null;
        foreach (var (line, _) in LogicalLines(text, fileName))
        {
            var rest = line.AsSpan();
            var keyword = NextToken(ref rest);
            if (keyword[0] == '#')
            {
                doc.Comments.Add(new MtlComment(CommentText(line), doc.Materials.Count - 1));
                continue;
            }
            rest = rest.Trim();
            string key = keyword.ToString();
            if (key.Equals("newmtl", StringComparison.OrdinalIgnoreCase))
            {
                current = new MtlMaterial(rest.ToString());
                doc.Materials.Add(current);
                continue;
            }
            // Statements before the first newmtl have nothing to apply to; exporters do not write them.
            if (current is null) continue;
            if (!ReadMaterialStatement(current, key.ToLowerInvariant(), rest))
                current.OtherStatements.Add(new KeyValuePair<string, string>(key, rest.ToString()));
        }
        return doc;
    }

    /// <summary>
    /// Reads every <c>mtllib</c> the document names, relative to <paramref name="objFolder"/>, into one
    /// library (materials in file order). A library that is not found under its stored path is also
    /// looked for by its file name in the folder (exporters often store the author's absolute path);
    /// still missing ones are listed in <c>Missing</c>.
    /// </summary>
    /// <exception cref="AssetFormatException">A library exists but is not a readable MTL.</exception>
    public static (MtlDocument Materials, IReadOnlyList<string> Missing) ReadMaterialLibraries(ObjDocument obj, string objFolder, long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentException.ThrowIfNullOrEmpty(objFolder);
        var merged = new MtlDocument();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string library in obj.MaterialLibraries)
        {
            string? path = FindLibrary(objFolder, library);
            if (path is null)
            {
                missing.Add(library);
                continue;
            }
            if (!seen.Add(path)) continue;
            var mtl = ReadMtlFile(path, maxBytes);
            int offset = merged.Materials.Count;
            merged.Materials.AddRange(mtl.Materials);
            foreach (var comment in mtl.Comments)
                merged.Comments.Add(comment with { MaterialIndex = comment.MaterialIndex < 0 ? offset - 1 : comment.MaterialIndex + offset });
        }
        return (merged, missing);
    }

    private static string? FindLibrary(string folder, string library)
    {
        try
        {
            string stored = Path.GetFullPath(Path.Combine(folder, library));
            if (File.Exists(stored)) return stored;
            int cut = Math.Max(library.LastIndexOf('/'), library.LastIndexOf('\\'));
            if (cut >= 0)
            {
                string byName = Path.Combine(folder, library[(cut + 1)..]);
                if (File.Exists(byName)) return Path.GetFullPath(byName);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }

    /// <summary>Applies one typed material statement; false when the keyword is not typed or its value does not parse.</summary>
    private static bool ReadMaterialStatement(MtlMaterial material, string key, ReadOnlySpan<char> rest)
    {
        switch (key)
        {
            case "ka": return SetColor(rest, c => material.Ambient = c);
            case "kd": return SetColor(rest, c => material.Diffuse = c);
            case "ks": return SetColor(rest, c => material.Specular = c);
            case "ke": return SetColor(rest, c => material.Emissive = c);
            case "ns":
                if (!TryParseSingle(rest, out float ns)) return false;
                material.SpecularExponent = ns;
                return true;
            case "d":
                if (!TryParseSingle(rest, out float d)) return false;
                material.Dissolve = d;
                return true;
            case "tr":
                if (!TryParseSingle(rest, out float tr)) return false;
                if (material.Dissolve is null) material.Dissolve = 1f - tr;
                return true;
            case "illum":
                if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int illum)) return false;
                material.Illumination = illum;
                return true;
            case "map_kd":
                if (ParseTextureMap(rest) is not { } map) return false;
                material.DiffuseMap = map;
                return true;
            default:
                return false;
        }

        static bool SetColor(ReadOnlySpan<char> rest, Action<Vector3> set)
        {
            Span<float> values = stackalloc float[3];
            int count = 0;
            var tokens = rest;
            while (!tokens.IsEmpty)
            {
                var token = NextToken(ref tokens);
                if (token.IsEmpty) break;
                // "Kd spectral file.rfl" / "Kd xyz ..." and anything else odd stay untyped.
                if (count == 3 || !TryParseFinite(token, out values[count])) return false;
                count++;
            }
            if (count == 0 || count == 2) return false;
            set(count == 1 ? new Vector3(values[0]) : new Vector3(values[0], values[1], values[2]));
            return true;
        }

        static bool TryParseSingle(ReadOnlySpan<char> rest, out float value)
        {
            var tokens = rest;
            var token = NextToken(ref tokens);
            value = 0;
            return !token.IsEmpty && tokens.Trim().IsEmpty && TryParseFinite(token, out value);
        }
    }

    /// <summary>Splits a texture statement into its leading options and the file (rest of the line, spaces kept, quotes removed).</summary>
    private static MtlTextureMap? ParseTextureMap(ReadOnlySpan<char> rest)
    {
        var text = rest.Trim();
        int position = 0;
        while (true)
        {
            var option = TokenAt(text, position, out int optionEnd);
            int arguments = option.Length > 1 && option[0] == '-' ? OptionArguments(option.ToString().ToLowerInvariant()) : -1;
            if (arguments < 0) break;
            position = optionEnd;
            // -o/-s/-t take one to three numbers (the first is required); the others a fixed count.
            for (int i = 0; i < arguments; i++)
            {
                var argument = TokenAt(text, position, out int argumentEnd);
                if (argument.IsEmpty || arguments == 3 && i > 0 && !TryParseFinite(argument, out _)) break;
                position = argumentEnd;
            }
        }
        string options = text[..position].Trim().ToString();
        var file = text[position..].Trim();
        if (file.Length >= 2 && file[0] == '"' && file[^1] == '"') file = file[1..^1].Trim();
        return file.IsEmpty ? null : new MtlTextureMap(file.ToString(), options);

        static ReadOnlySpan<char> TokenAt(ReadOnlySpan<char> text, int start, out int end)
        {
            while (start < text.Length && IsSpace(text[start])) start++;
            end = start;
            while (end < text.Length && !IsSpace(text[end])) end++;
            return text[start..end];
        }

        static int OptionArguments(string option) => option switch
        {
            "-blendu" or "-blendv" or "-cc" or "-clamp" or "-boost" or "-texres" or "-bm" or "-imfchan" or "-type" => 1,
            "-mm" => 2,
            "-o" or "-s" or "-t" => 3,
            _ => -1,
        };
    }

    /// <summary>The state machine for one .obj text.</summary>
    private sealed class ObjParser(string fileName)
    {
        private readonly ObjDocument _doc = new();
        private ObjObject? _object;
        private string? _material;
        private IReadOnlyList<string> _groups = [];
        private int _smoothing;
        private int _line;
        // The largest positive index seen per pool and the line it was on, checked once every
        // vertex is known (a forward reference is unusual but not wrong).
        private int _maxPosition = -1, _maxPositionLine;
        private int _maxTexCoord = -1, _maxTexCoordLine;
        private int _maxNormal = -1, _maxNormalLine;

        public ObjDocument Parse(string text)
        {
            foreach (var (line, number) in LogicalLines(text, fileName))
            {
                _line = number;
                Statement(line);
            }
            CheckRange(_maxPosition, _doc.Positions.Count, _maxPositionLine, "position");
            CheckRange(_maxTexCoord, _doc.TexCoords.Count, _maxTexCoordLine, "texture coordinate");
            CheckRange(_maxNormal, _doc.Normals.Count, _maxNormalLine, "normal");
            return _doc;
        }

        private void Statement(ReadOnlySpan<char> line)
        {
            var rest = line;
            var keyword = NextToken(ref rest);
            if (keyword[0] == '#')
            {
                int objectIndex = _doc.Objects.Count - 1;
                _doc.Comments.Add(new ObjComment(CommentText(line), objectIndex, objectIndex < 0 ? 0 : _object!.Faces.Count));
                return;
            }
            if (Is(keyword, "v")) Vertex(StripComment(rest));
            else if (Is(keyword, "vt")) TexCoord(StripComment(rest));
            else if (Is(keyword, "vn")) Normal(StripComment(rest));
            else if (Is(keyword, "f")) Face(StripComment(rest));
            else if (Is(keyword, "usemtl"))
            {
                var name = rest.Trim();
                _material = name.IsEmpty ? null : name.ToString();
            }
            else if (Is(keyword, "g")) Groups(StripComment(rest));
            else if (Is(keyword, "s")) Smoothing(StripComment(rest));
            else if (Is(keyword, "o"))
            {
                _object = new ObjObject(rest.Trim().ToString());
                _doc.Objects.Add(_object);
            }
            else if (Is(keyword, "mtllib")) MaterialLibraries(rest.Trim());
            else
            {
                string key = keyword.ToString();
                if (!KnownIgnored.Contains(key.ToLowerInvariant()) && !IsPlainWord(keyword))
                    throw Damaged($"'{Shorten(key)}' is not an OBJ statement");
                CountIgnored(key.ToLowerInvariant());
            }
        }

        private void CountIgnored(string key)
        {
            _doc.IgnoredStatements.TryGetValue(key, out int count);
            _doc.IgnoredStatements[key] = count + 1;
        }

        private void Vertex(ReadOnlySpan<char> rest)
        {
            Span<float> values = stackalloc float[7];
            int count = Numbers(rest, values, "vertex");
            if (count < 3) throw Damaged($"a vertex needs three coordinates (found {count})");
            _doc.Positions.Add(new Vector3(values[0], values[1], values[2]));
            // x y z | x y z w | x y z r g b | x y z w r g b (some tools add a weight before the colour)
            Vector3? color = count switch
            {
                6 => new Vector3(values[3], values[4], values[5]),
                7 => new Vector3(values[4], values[5], values[6]),
                _ => null,
            };
            if (color is not null && _doc.Colors is null)
            {
                _doc.Colors = new List<Vector3>(_doc.Positions.Capacity);
                for (int i = 0; i < _doc.Positions.Count - 1; i++) _doc.Colors.Add(Vector3.One);
            }
            _doc.Colors?.Add(color ?? Vector3.One);
        }

        private void TexCoord(ReadOnlySpan<char> rest)
        {
            Span<float> values = stackalloc float[3];
            int count = Numbers(rest, values, "texture coordinate");
            if (count < 1) throw Damaged("a texture coordinate needs at least one value");
            _doc.TexCoords.Add(new Vector2(values[0], count > 1 ? values[1] : 0f));
        }

        private void Normal(ReadOnlySpan<char> rest)
        {
            Span<float> values = stackalloc float[3];
            int count = Numbers(rest, values, "normal", allowNonFinite: true);
            if (count < 3) throw Damaged($"a normal needs three values (found {count})");
            var normal = new Vector3(values[0], values[1], values[2]);
            if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z))
            {
                // Some exporters (REDUX among them) write "vn NaN NaN NaN" for degenerate faces.
                normal = Vector3.Zero;
                _doc.NonFiniteNormals++;
            }
            _doc.Normals.Add(normal);
        }

        /// <summary>Parses up to values.Length numbers; extra values are ignored, as other readers do.</summary>
        private int Numbers(ReadOnlySpan<char> rest, Span<float> values, string what, bool allowNonFinite = false)
        {
            int count = 0;
            while (true)
            {
                var token = NextToken(ref rest);
                if (token.IsEmpty) return count;
                if (count == values.Length) continue;
                bool parsed = allowNonFinite
                    ? float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out values[count])
                    : TryParseFinite(token, out values[count]);
                if (!parsed) throw Damaged($"'{Shorten(token.ToString())}' in a {what} is not a finite number");
                count++;
            }
        }

        private void Face(ReadOnlySpan<char> rest)
        {
            var face = new ObjFace { Material = _material, Groups = _groups, SmoothingGroup = _smoothing };
            while (true)
            {
                var token = NextToken(ref rest);
                if (token.IsEmpty) break;
                face.Corners.Add(Corner(token));
            }
            if (face.Corners.Count < 3)
                throw Damaged($"a face needs at least three corners (found {face.Corners.Count})");
            if (_object is null)
            {
                _object = new ObjObject();
                _doc.Objects.Add(_object);
            }
            _object.Faces.Add(face);
        }

        /// <summary>One "v", "v/vt", "v//vn" or "v/vt/vn" corner (a trailing slash is tolerated).</summary>
        private ObjCorner Corner(ReadOnlySpan<char> token)
        {
            int first = token.IndexOf('/');
            var position = first < 0 ? token : token[..first];
            ReadOnlySpan<char> texCoord = default, normal = default;
            if (first >= 0)
            {
                var after = token[(first + 1)..];
                int second = after.IndexOf('/');
                texCoord = second < 0 ? after : after[..second];
                if (second >= 0)
                {
                    normal = after[(second + 1)..];
                    if (normal.IndexOf('/') >= 0) throw Damaged($"face corner '{Shorten(token.ToString())}' has more than three parts");
                }
            }
            if (position.IsEmpty) throw Damaged($"face corner '{Shorten(token.ToString())}' has no position index");
            return new ObjCorner(
                Index(position, _doc.Positions.Count, ref _maxPosition, ref _maxPositionLine, "position"),
                texCoord.IsEmpty ? -1 : Index(texCoord, _doc.TexCoords.Count, ref _maxTexCoord, ref _maxTexCoordLine, "texture coordinate"),
                normal.IsEmpty ? -1 : Index(normal, _doc.Normals.Count, ref _maxNormal, ref _maxNormalLine, "normal"));
        }

        private int Index(ReadOnlySpan<char> text, int countSoFar, ref int max, ref int maxLine, string what)
        {
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
                throw Damaged($"'{Shorten(text.ToString())}' is not a valid {what} index");
            if (value == 0) throw Damaged($"{what} index 0 is not valid (indices start at 1)");
            if (value < 0)
            {
                // Relative: -1 is the last one defined so far.
                long resolved = (long)countSoFar + value;
                if (resolved < 0)
                    throw Damaged($"relative {what} index {value} points before the first {what} (only {countSoFar} defined so far)");
                return (int)resolved;
            }
            int index = value - 1;
            if (index > max)
            {
                max = index;
                maxLine = _line;
            }
            return index;
        }

        private void CheckRange(int max, int count, int line, string what)
        {
            if (max >= count)
                throw new AssetFormatException($"'{fileName}' is damaged: line {line}: {what} index {max + 1} is out of range (the file has {count}).");
        }

        private void Groups(ReadOnlySpan<char> rest)
        {
            var names = new List<string>();
            while (true)
            {
                var token = NextToken(ref rest);
                if (token.IsEmpty) break;
                names.Add(token.ToString());
            }
            _groups = names.ToArray();
        }

        private void Smoothing(ReadOnlySpan<char> rest)
        {
            var value = rest.Trim();
            if (value.IsEmpty || value.Equals("off", StringComparison.OrdinalIgnoreCase)) _smoothing = 0;
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int group) && group >= 0) _smoothing = group;
            else
            {
                // "s on" and other oddities: no smoothing group, counted so an import report can say so.
                _smoothing = 0;
                CountIgnored("s");
            }
        }

        /// <summary>
        /// The <c>mtllib</c> names. The statement lists names separated by spaces, but Blender and 3ds Max
        /// write a single name with spaces unquoted, so a line with exactly one ".mtl" in it is one name.
        /// </summary>
        private void MaterialLibraries(ReadOnlySpan<char> rest)
        {
            if (rest.IsEmpty) return;
            string text = rest.ToString();
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            {
                _doc.MaterialLibraries.Add(text[1..^1]);
                return;
            }
            int first = text.IndexOf(".mtl", StringComparison.OrdinalIgnoreCase);
            if (first >= 0 && text.IndexOf(".mtl", first + 4, StringComparison.OrdinalIgnoreCase) < 0 && text.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase))
            {
                _doc.MaterialLibraries.Add(text);
                return;
            }
            var tokens = rest;
            while (true)
            {
                var token = NextToken(ref tokens);
                if (token.IsEmpty) break;
                _doc.MaterialLibraries.Add(token.ToString());
            }
        }

        private AssetFormatException Damaged(string what) => new($"'{fileName}' is damaged: line {_line}: {what}.");
    }

    /// <summary>
    /// The non-empty logical lines (continuations joined, trimmed) with their first physical line
    /// number. Control characters other than tab and form feed mean the data is not text.
    /// </summary>
    private static IEnumerable<(string Line, int Number)> LogicalLines(string text, string fileName)
    {
        int position = 0, number = 0;
        StringBuilder? pending = null;
        int pendingNumber = 0;
        while (position < text.Length)
        {
            int end = text.AsSpan(position).IndexOfAny('\n', '\r');
            int lineEnd = end < 0 ? text.Length : position + end;
            int next = lineEnd;
            if (next < text.Length)
            {
                next++;
                if (text[lineEnd] == '\r' && next < text.Length && text[next] == '\n') next++;
            }
            number++;
            var raw = text.AsSpan(position, lineEnd - position);
            position = next;
            foreach (char c in raw)
                if (c < ' ' && c != '\t' && c != '\f' && c != '\v')
                    throw new AssetFormatException($"'{fileName}' is not a text OBJ/MTL file (line {number} holds binary data).");
            var trimmed = raw.Trim();
            // A backslash at the end continues the statement on the next line (not inside a comment).
            if (trimmed.Length > 0 && trimmed[^1] == '\\' && trimmed[0] != '#')
            {
                if (pending is null)
                {
                    pending = new StringBuilder();
                    pendingNumber = number;
                }
                pending.Append(trimmed[..^1]).Append(' ');
                continue;
            }
            if (pending is not null)
            {
                pending.Append(trimmed);
                string joined = pending.ToString().Trim();
                pending = null;
                if (joined.Length > 0) yield return (joined, pendingNumber);
                continue;
            }
            if (trimmed.Length > 0) yield return (trimmed.ToString(), number);
        }
        if (pending is not null && pending.ToString().Trim() is { Length: > 0 } last) yield return (last, pendingNumber);
    }

    /// <summary>The next whitespace-separated token (empty at the end); <paramref name="rest"/> moves past it.</summary>
    private static ReadOnlySpan<char> NextToken(ref ReadOnlySpan<char> rest)
    {
        int start = 0;
        while (start < rest.Length && IsSpace(rest[start])) start++;
        int end = start;
        while (end < rest.Length && !IsSpace(rest[end])) end++;
        var token = rest[start..end];
        rest = rest[end..];
        return token;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\f' or '\v';

    /// <summary>A keyword that begins with '#' is a comment ("#comment" without a space included).</summary>
    private static string CommentText(ReadOnlySpan<char> line) => line.TrimStart()[1..].Trim().ToString();

    /// <summary>Drops a trailing "# ..." from a statement whose arguments are numbers or plain names.</summary>
    private static ReadOnlySpan<char> StripComment(ReadOnlySpan<char> rest)
    {
        int hash = rest.IndexOf('#');
        return hash < 0 ? rest : rest[..hash];
    }

    private static bool Is(ReadOnlySpan<char> keyword, string expected) => keyword.Equals(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>An unknown statement made of letters, digits and underscores is ignored (some tools add their own); anything else is not OBJ.</summary>
    private static bool IsPlainWord(ReadOnlySpan<char> keyword)
    {
        if (keyword.Length > 32 || !char.IsAsciiLetter(keyword[0])) return false;
        foreach (char c in keyword)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    private static bool TryParseFinite(ReadOnlySpan<char> token, out float value) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";

    private static string Decode(byte[] data, string fileName)
    {
        fileName ??= "";
        ReadOnlySpan<byte> bytes = data;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        if (bytes.IndexOf((byte)0) >= 0)
            throw new AssetFormatException($"'{fileName}' is not a text OBJ/MTL file (it contains binary data).");
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 3ds Max and older tools write names in the system code page; Latin-1 keeps every byte.
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static byte[] ReadLimited(string path, long maxBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes || stream.Length > Array.MaxLength)
            throw new AssetFormatException($"'{Path.GetFileName(path)}' is larger than the {maxBytes:N0}-byte limit.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
