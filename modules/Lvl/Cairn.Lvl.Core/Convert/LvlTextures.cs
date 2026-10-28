using Cairn.Formats;
using Cairn.Formats.Imaging;

namespace Cairn.Lvl.Conversion;

/// <summary>A texture decoded and written as PNG beside a conversion's output.</summary>
/// <param name="FileName">The PNG's file name (unique within the output).</param>
/// <param name="RfName">The RF texture name it came from.</param>
/// <param name="Png">The PNG bytes.</param>
/// <param name="HasAlpha">True when any pixel is not fully opaque.</param>
public sealed record LvlImageFile(string FileName, string RfName, byte[] Png, bool HasAlpha);

/// <summary>Finds, decodes and encodes each texture once; names that cannot be found are remembered for the report.</summary>
internal sealed class LvlTextureSet(LvlConvertOptions options, LvlReportBuilder report)
{
    private readonly Dictionary<string, LvlImageFile?> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fileNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _unreadableNames = [];
    private int _unreadable;

    /// <summary>The images in first-use order.</summary>
    public List<LvlImageFile> Images { get; } = [];

    /// <summary>True when textures are decoded at all.</summary>
    public bool Enabled => options.ExportTextures && (options.TextureLoader is not null || options.TextureResolver is not null);

    /// <summary>The PNG of a texture, or null (not exported, not found or not readable).</summary>
    public LvlImageFile? Get(string rfName)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(rfName)) return null;
        if (_byName.TryGetValue(rfName, out var cached)) return cached;
        LvlImageFile? result = null;
        try
        {
            var image = Load(rfName);
            if (image is null)
            {
                report.Missing(rfName);
            }
            else
            {
                bool alpha = false;
                for (int i = 3; i < image.Pixels.Length; i += 4)
                {
                    if (image.Pixels[i] != 255)
                    {
                        alpha = true;
                        break;
                    }
                }
                result = new LvlImageFile(UniqueFileName(rfName), rfName, PngEncoder.Encode(image), alpha);
                Images.Add(result);
            }
        }
        catch (Exception e) when (e is ImageDecodeException or IOException or UnauthorizedAccessException or AssetFormatException or NotSupportedException)
        {
            _unreadable++;
            _unreadableNames.Add(rfName);
            report.Missing(rfName);
        }
        _byName[rfName] = result;
        return result;
    }

    private BgraImage? Load(string name)
    {
        if (options.TextureLoader?.Invoke(name) is { } own) return own;
        if (options.TextureResolver?.Resolve(name) is { } location)
            return ImageDecoder.Decode(location.ReadAllBytes(), location.ResolvedName);
        return null;
    }

    private string UniqueFileName(string rfName)
    {
        string stem = Path.GetFileNameWithoutExtension(rfName.Replace('\\', '/'));
        var clean = new string(stem.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
        if (clean.Length == 0) clean = "texture";
        string name = clean + ".png";
        for (int i = 2; !_fileNames.Add(name); i++) name = $"{clean}_{i}.png";
        return name;
    }

    /// <summary>Adds the texture lines of the report.</summary>
    public void Summarise(LvlReportBuilder r, IReadOnlyCollection<string> usedNames)
    {
        r.Textures = Images.Count;
        if (!options.ExportTextures)
        {
            r.Info(LvlConvertCodes.TextureNamesOnly, "Textures were not exported: materials name the Red Faction textures only.");
            return;
        }
        if (!Enabled)
        {
            r.Info(LvlConvertCodes.TextureNamesOnly, "No texture source was given (game folder or search folders), so materials name the Red Faction textures only.");
            return;
        }
        if (Images.Count > 0)
            r.Info(LvlConvertCodes.TexturesWritten, $"{LvlReportBuilder.Count(Images.Count, "texture")} written as PNG; materials keep the Red Faction names in rf_texture.");
        var notFound = usedNames.Where(n => n.Length > 0 && _byName.TryGetValue(n, out var f) && f is null).Except(_unreadableNames, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (notFound.Count > 0)
            r.Warning(LvlConvertCodes.MissingTexture, $"{LvlReportBuilder.Count(notFound.Count, "texture was", "textures were")} not found ({Names(notFound)}); check the game folder and search folders. Their materials have no image.", LvlIssueKind.Dropped);
        if (_unreadable > 0)
            r.Warning(LvlConvertCodes.UnreadableTexture, $"{LvlReportBuilder.Count(_unreadable, "texture")} could not be decoded ({Names(_unreadableNames)}); their materials have no image.", LvlIssueKind.Dropped);
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return string.Join(", ", list.Take(5)) + (list.Count > 5 ? $" and {list.Count - 5} more" : "");
    }
}

/// <summary>Shared geometry helpers of the converters.</summary>
internal static class LvlGeometryHelpers
{
    /// <summary>The triangles of a polygon (indices into its corners), keeping its winding; concave faces are ear-clipped.</summary>
    public static IReadOnlyList<(int A, int B, int C)> Triangulate(LvlPolygon polygon)
    {
        var corners = polygon.Corners;
        if (corners.Length == 3) return [(0, 1, 2)];
        Span<Vector3> points = corners.Length <= 64 ? stackalloc Vector3[corners.Length] : new Vector3[corners.Length];
        for (int i = 0; i < corners.Length; i++) points[i] = corners[i].Position;
        return Formats.Obj.ObjTriangulator.Triangulate(points);
    }

    /// <summary>A part's orientation as a rotation quaternion (RF space, active, row-vector convention).</summary>
    public static Quaternion Rotation(Model.RfMatrix m) => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m.ToMatrix4x4(Vector3.Zero)));
}
