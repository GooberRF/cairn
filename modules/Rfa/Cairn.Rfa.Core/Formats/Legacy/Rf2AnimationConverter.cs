using System.Collections.Immutable;
using System.Globalization;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>What converting a Red Faction II clip made: the version 8 clip, its bytes and the report.</summary>
/// <param name="SourceName">The Red Faction II clip's file name.</param>
/// <param name="OutputName">The converted clip's file name (the same name: both are <c>.rfa</c>).</param>
/// <param name="Clip">The version 8 clip.</param>
/// <param name="Bytes">Its file.</param>
/// <param name="Report">What was approximated or is worth knowing, in plain sentences.</param>
public sealed record Rf2ClipConversion(string SourceName, string OutputName, RfaClip Clip, byte[] Bytes, ImmutableArray<string> Report)
{
    /// <summary>True when nothing was approximated (never for a Red Faction II clip: its keys are re-laid out).</summary>
    public bool IsLossless => Report.IsEmpty;
}

/// <summary>
/// Red Faction II clips (<c>.rfa</c> version 9) to the version 8 clips Red Faction plays, and the Red Faction II character
/// a clip plays on. A version 9 clip reads into <see cref="RfaClip"/> with its rotation keys as stored (no eases) and
/// its position keys on straight lines (<see cref="RfaReader"/>), so the conversion keeps every key and changes only
/// what the formats disagree on; the report says what that is.
/// </summary>
public static class Rf2AnimationConverter
{
    /// <summary>True when the bytes are a Red Faction II clip ("VMVF", version 9).</summary>
    public static bool IsRedFactionII(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && BitConverter.ToUInt32(data) == RfaClip.Signature && BitConverter.ToInt32(data[4..]) == RfaClip.Rf2Version;

    /// <summary>Reads and converts a Red Faction II clip.</summary>
    /// <param name="data">The clip file.</param>
    /// <param name="name">Its file name (the output takes it).</param>
    /// <exception cref="AssetFormatException">The bytes are not a readable Red Faction II clip; the message says why.</exception>
    public static Rf2ClipConversion Convert(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(name);
        if (!IsRedFactionII(data))
        {
            string version = data.Length >= 8 && BitConverter.ToUInt32(data) == RfaClip.Signature
                ? $"it is a Red Faction clip (version {BitConverter.ToInt32(data, 4)}), which needs no conversion" : "it is not an RFA clip";
            throw new AssetFormatException($"'{name}' is not a Red Faction II clip: {version}.");
        }
        return Convert(RfaReader.Read(data, name), name);
    }

    /// <summary>Converts a Red Faction II clip read by <see cref="RfaReader"/> to version 8.</summary>
    /// <param name="clip">The version 9 clip.</param>
    /// <param name="sourceName">Its file name (the output takes it).</param>
    /// <exception cref="AssetFormatException">The clip is not a Red Faction II clip, or the result cannot be written.</exception>
    public static Rf2ClipConversion Convert(RfaClip clip, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(sourceName);
        if (!clip.IsRedFactionII) throw new AssetFormatException($"'{sourceName}' is not a Red Faction II clip (it is version {clip.Version}).");

        var notes = new List<string>();
        int keys = 0, shortened = 0;
        var bones = ImmutableArray.CreateBuilder<RfaBoneTrack>(clip.BoneCount);
        foreach (var bone in clip.Bones)
        {
            var rotation = bone.RotationKeys.ToBuilder();
            for (int k = 0; k < rotation.Count; k++)
            {
                var key = rotation[k];
                keys++;
                // the engine stops interpolating between two keys longer than 1 (see RfaRotKey.QuantizeWithinUnit)
                long length = (long)key.X * key.X + (long)key.Y * key.Y + (long)key.Z * key.Z + (long)key.W * key.W;
                if (length <= (long)RfaClip.QuaternionScale * (long)RfaClip.QuaternionScale) continue;
                double scale = RfaClip.QuaternionScale / Math.Sqrt(length);
                var (x, y, z, w) = RfaRotKey.WithinUnit(key.X * scale, key.Y * scale, key.Z * scale, key.W * scale);
                rotation[k] = key with { X = x, Y = y, Z = z, W = w };
                shortened++;
            }
            bones.Add(bone with { RotationKeys = rotation.ToImmutable() });
        }
        var converted = clip with { Version = 8, Bones = bones.MoveToImmutable() };

        notes.Add("Red Faction II stores rotation keys without eases and position keys without control points. The clip keeps every key; "
            + "it moves between them in straight lines (no eases, control points on the line between keys), which is how the keys read, "
            + "though no Red Faction II documentation confirms how that game blends them.");
        if (shortened > 0)
            notes.Add(string.Format(CultureInfo.InvariantCulture,
                "{0:N0} of the {1:N0} rotation keys were a hair longer than 1 and were shortened (by less than 0.01 degrees), so the game interpolates them.", shortened, keys));
        notes.Add(string.Format(CultureInfo.InvariantCulture,
            "The game matches bones by index: the clip plays on a mesh with exactly its {0:N0} bones in Red Faction II's order, such as a Red Faction II character converted to .v3c.",
            clip.BoneCount));
        if (!clip.Morph.IsEmpty)
        {
            notes.Add(clip.Morph.VertexCount > 0
                ? string.Format(CultureInfo.InvariantCulture,
                    "The clip moves {0:N0} vertices of the Red Faction II mesh it was made for (vertex animation, {1:N0} keyframes); they are numbered as in that mesh, so they move the right vertices only on the character converted from it.",
                    clip.Morph.VertexCount, clip.Morph.KeyframeCount)
                : string.Format(CultureInfo.InvariantCulture, "The clip stores {0:N0} vertex animation keyframes but no vertices; they were kept as stored.", clip.Morph.KeyframeCount));
        }

        string output = Path.ChangeExtension(Path.GetFileName(sourceName), ".rfa");
        byte[] bytes;
        try
        {
            bytes = RfaWriter.Write(converted);
        }
        catch (ArgumentException ex)
        {
            throw new AssetFormatException($"'{sourceName}' cannot be converted: {ex.Message.Split(" (Parameter")[0]}");
        }
        foreach (var d in ClipLinter.AnalyzeStructure(converted))
        {
            if (d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning) notes.Add($"{output}: {d.Message}");
        }
        return new Rf2ClipConversion(Path.GetFileName(sourceName), output, converted, bytes, [.. notes]);
    }

    // ── The Red Faction II character a clip plays on ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far the clip's bone offsets are from the mesh's bind pose: the mean distance, in metres, between where the clip
    /// puts each bone's joint in its parent's frame (half way through the clip) and where the mesh's skeleton has it; null
    /// when the bone counts differ. Clips store no bone names, so this is what tells a skeleton the clip was made on
    /// (a few millimetres) from one with other proportions (centimetres) or another bone order (decimetres).
    /// </summary>
    public static double? JointOffset(RfaClip clip, IReadOnlyList<V3dBone> bones)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(bones);
        if (bones.Count != clip.BoneCount || bones.Count == 0) return null;
        var skeleton = Animation.Skeleton.FromBones(bones);
        float time = clip.StartTime + clip.Duration / 2f;
        double sum = 0;
        int count = 0;
        for (int b = 0; b < skeleton.Count; b++)
        {
            if (skeleton.EffectiveParents[b] < 0 || clip.Bones[b].PositionKeys.IsDefaultOrEmpty) continue;
            var position = Animation.ClipSampler.SamplePosition(clip.Bones[b].PositionKeys.AsSpan(), time);
            sum += System.Numerics.Vector3.Distance(position, skeleton.RestLocal[b].Position);
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    /// <summary>A shared name start shorter than this says nothing (<c>feml_cower</c> and <c>fodder_cop</c>).</summary>
    private const int MinNameMatch = 3;

    /// <summary>
    /// The Red Faction II characters among <paramref name="meshes"/> that can play <paramref name="clip"/> (the same bone
    /// count), best first, each with its <see cref="JointOffset"/>. Clips store no bone names, so the order is: a
    /// head-only clip (<c>*_hud*</c>) wants a head-only mesh; then the name (the longest start the clip's name shares with
    /// the mesh's name or its bones' prefix, as <c>tang_hudtalk60</c> with <c>tangier_hudhead</c> and its
    /// <c>tang-bdbn-*</c> bones, or an abbreviation, as <c>fp_sr_draw</c> with <c>fp_sniper_rifle</c>; at least three
    /// letters); then the smallest joint offset (the skeleton the clip was made on; within a millimetre of the best is a
    /// tie), then the shorter name.
    /// </summary>
    public static IReadOnlyList<(string Name, double JointOffset)> RankMeshes(RfaClip clip, string clipName, IEnumerable<(string Name, IReadOnlyList<V3dBone> Bones)> meshes)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(clipName);
        ArgumentNullException.ThrowIfNull(meshes);
        var fits = new List<(string Name, double Offset, bool Hud, int Match)>();
        foreach (var (name, bones) in meshes)
        {
            if (JointOffset(clip, bones) is not { } offset) continue;
            var (hud, match) = NameFit(clipName, name, bones);
            fits.Add((name, offset, hud, match));
        }
        return Order(fits);
    }

    /// <summary>
    /// The reverse of <see cref="RankMeshes"/>: the Red Faction II clips among <paramref name="clips"/> that play on the
    /// character <paramref name="meshName"/> with the skeleton <paramref name="bones"/> (the same bone count), best first,
    /// by the same rules (a head-only mesh wants a head-only clip, then the name, then the smallest joint offset, then the
    /// shorter name), each with its <see cref="JointOffset"/>.
    /// </summary>
    public static IReadOnlyList<(string Name, double JointOffset)> RankClips(string meshName, IReadOnlyList<V3dBone> bones, IEnumerable<(string Name, RfaClip Clip)> clips)
    {
        ArgumentNullException.ThrowIfNull(meshName);
        ArgumentNullException.ThrowIfNull(bones);
        ArgumentNullException.ThrowIfNull(clips);
        var fits = new List<(string Name, double Offset, bool Hud, int Match)>();
        foreach (var (name, clip) in clips)
        {
            if (clip is null || JointOffset(clip, bones) is not { } offset) continue;
            var (hud, match) = NameFit(name, meshName, bones);
            fits.Add((name, offset, hud, match));
        }
        return Order(fits);
    }

    /// <summary>
    /// How a clip's name fits a mesh's: whether both are head-only (<c>hud</c>) or neither is, and the length of the start
    /// the clip's name shares with the mesh's name or its bones' prefix, or of the abbreviation it is (0 below three letters).
    /// </summary>
    private static (bool Hud, int Match) NameFit(string clipName, string meshName, IReadOnlyList<V3dBone> bones)
    {
        string stem = Path.GetFileNameWithoutExtension(clipName).ToLowerInvariant();
        bool hud = stem.Contains("hud", StringComparison.Ordinal);
        string mesh = Path.GetFileNameWithoutExtension(meshName).ToLowerInvariant();
        string prefix = bones.Count > 0 && bones[0].Name.Text is { } first && first.IndexOf('-') is > 0 and int dash ? first[..dash].ToLowerInvariant() : "";
        int match = Math.Max(Math.Max(Shared(stem, mesh), Shared(stem, prefix)), Abbreviation(stem, mesh));
        return (mesh.Contains("hud", StringComparison.Ordinal) == hud, match >= MinNameMatch ? match : 0);

        // a separator shared at the end says nothing more ("swarmer_walk" shares "swarmer" with swarmer and swarmer_b alike)
        static int Shared(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            while (i > 0 && a[i - 1] is '_' or '-') i--;
            return i;
        }

        // "fp_sr_draw" and "fp_sniper_rifle": the same first word, then the clip's second word is the initials of the rest
        static int Abbreviation(string clip, string mesh)
        {
            string[] c = clip.Split('_'), m = mesh.Split('_');
            if (c.Length < 2 || m.Length < 2 || c[0] != m[0] || c[1].Length < 2) return 0;
            string initials = string.Concat(m.Skip(1).Where(w => w.Length > 0).Select(w => w[0]));
            return initials == c[1] ? c[0].Length + 1 + c[1].Length : 0;
        }
    }

    /// <summary>Best first: head-only agreement, then the name, then the joint offset (a millimetre is a tie), then the shorter name.</summary>
    private static List<(string Name, double JointOffset)> Order(List<(string Name, double Offset, bool Hud, int Match)> fits)
    {
        // within a millimetre of the best fit of its kind is a tie: the base model (burke) then wins over its variants (burke_b)
        var best = fits.GroupBy(f => (f.Hud, f.Match)).ToDictionary(g => g.Key, g => g.Min(f => f.Offset));
        return [.. fits.OrderByDescending(f => f.Hud).ThenByDescending(f => f.Match).ThenBy(f => f.Offset - best[(f.Hud, f.Match)] < 0.001 ? 0 : f.Offset)
            .ThenBy(f => f.Name.Length).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => (f.Name, f.Offset))];
    }
}
