using System.Globalization;
using System.IO;
using Cairn.Assets;
using Cairn.Formats;
using Cairn.Formats.Rf2;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Rfe;

/// <summary>Options of a Red Faction II effect's conversion to a Red Faction <c>.vfx</c>.</summary>
public sealed record RfeConvertOptions
{
    /// <summary>The defaults: texture names translated, nothing written beside the effect.</summary>
    public static RfeConvertOptions Default { get; } = new();

    /// <summary>Translate the texture names (the default) or keep them and write the textures from the packs.</summary>
    public Rf2TextureMode Textures { get; init; } = Rf2TextureMode.Translate;

    /// <summary>The <c>.peg</c> packs the effect's textures are in (to write them, and to name the ones not found), or null.</summary>
    public PegTextureSet? TexturePacks { get; init; }
}

/// <summary>One line of a conversion report.</summary>
/// <param name="Kind">Exact, approximated or dropped.</param>
/// <param name="Text">What happened.</param>
public sealed record RfeConversionNote(RfeNoteKind Kind, string Text)
{
    /// <inheritdoc/>
    public override string ToString() => Kind switch { RfeNoteKind.Approximated => "Approximated: ", RfeNoteKind.Dropped => "Dropped: ", _ => "" } + Text;
}

/// <summary>How a part of the effect came through the conversion.</summary>
public enum RfeNoteKind
{
    /// <summary>Kept as it is (or a plain statement).</summary>
    Exact,
    /// <summary>Kept, but changed to the nearest thing Red Faction has.</summary>
    Approximated,
    /// <summary>Not kept: Red Faction has nothing for it.</summary>
    Dropped,
}

/// <summary>The result of <see cref="RfeToVfx.Convert"/>.</summary>
/// <param name="File">The Red Faction effect (version 0x40006).</param>
/// <param name="Notes">The report: what was kept exactly, approximated or dropped.</param>
/// <param name="Textures">Texture files to write beside it (when the textures are kept), in order.</param>
public sealed record RfeConversion(VfxFile File, ImmutableArray<RfeConversionNote> Notes, ImmutableArray<(string Name, byte[] Bytes)> Textures)
{
    /// <summary>The file name of the converted effect.</summary>
    public string OutputName { get; init; } = "effect.vfx";

    /// <summary>Lines that approximate or drop something.</summary>
    public int Approximations => Notes.Count(n => n.Kind != RfeNoteKind.Exact);

    /// <summary>The effect as written.</summary>
    public byte[] Bytes => VfxWriter.Write(File);

    /// <summary>The effect and its textures, in writing order.</summary>
    public IReadOnlyList<(string Name, byte[] Bytes)> AllFiles => [(OutputName, Bytes), .. Textures];
}

/// <summary>
/// Converts a Red Faction II effect (<c>.rfe</c>) to a Red Faction effect (<c>.vfx</c> version 0x40006, through
/// <see cref="VfxMeshBuilder"/> and <see cref="VfxBuilder"/>). Meshes and materials map exactly where Red Faction has the
/// same thing: the evenly spread transform keys become keyframes at their times, with straight-line tangents and no ease,
/// which Red Faction evaluates as the same lerp and slerp; the material tracks are sampled every 1/30 s. Particles are
/// mapped as closely as the format allows, and everything else is listed in the report.
/// </summary>
public static class RfeToVfx
{
    /// <summary>The rate the material tracks are sampled at (the Red Faction II particle step, so tracks keyed on it survive).</summary>
    public const int MaterialFps = 30;

    /// <summary>The 1/30 s steps of a duration (the stock durations are whole steps plus about 0.0002 s).</summary>
    /// <exception cref="OverflowException">The duration is far beyond <see cref="RfeReader.MaxDuration"/>.</exception>
    public static long Steps(float duration) => float.IsFinite(duration) && duration > 0 ? Math.Max(1, checked((long)MathF.Round(duration * 30))) : 1;

    /// <summary>The Red Faction end frame (15 fps) for a duration: whole frames, rounded up from whole 1/30 s.</summary>
    /// <exception cref="OverflowException">The duration is far beyond <see cref="RfeReader.MaxDuration"/>.</exception>
    public static int EndFrame(float duration) => checked((int)Math.Max(1, (Steps(duration) + 1) / 2));

    /// <summary>The duration as a length in 15 fps frames, unrounded.</summary>
    public static double Frames(float duration) => float.IsFinite(duration) && duration > 0 ? duration * (double)VfxTime.FramesPerSecond : 1;

    /// <summary>Converts <paramref name="effect"/>.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="sourceName">Its file name (the output is named after it).</param>
    /// <param name="options">The options, or null for <see cref="RfeConvertOptions.Default"/>.</param>
    /// <exception cref="AssetFormatException">The effect lasts longer than <see cref="RfeReader.MaxDuration"/> (only an effect made in memory can).</exception>
    public static RfeConversion Convert(RfeFile effect, string sourceName, RfeConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (effect.Duration > RfeReader.MaxDuration)
            throw new AssetFormatException($"'{(string.IsNullOrEmpty(sourceName) ? "effect" : sourceName)}' cannot be converted: {RfeReader.DurationTooLong(effect.Duration)}.");
        options ??= RfeConvertOptions.Default;
        var notes = new List<RfeConversionNote>();
        void Note(RfeNoteKind kind, string text) => notes.Add(new RfeConversionNote(kind, text));
        int endFrame = EndFrame(effect.Duration);
        double ticks = effect.Duration * (double)VfxTime.TicksPerSecond;

        // textures: Red Faction II names, translated or kept (written from the packs)
        var (names, textureFiles) = Textures(effect, options, Note);

        var sections = ImmutableArray.CreateBuilder<VfxSection>();
        var meshMaterial = new Dictionary<int, int>();
        var particleMaterial = new Dictionary<int, int>();
        foreach (var m in effect.Meshes)
        {
            if (!meshMaterial.ContainsKey(m.Material)) { meshMaterial[m.Material] = sections.Count; sections.Add(Material(effect, m.Material, names, particles: false)); }
        }
        foreach (var e in effect.Emitters)
        {
            if (!particleMaterial.ContainsKey(e.Material)) { particleMaterial[e.Material] = sections.Count; sections.Add(Material(effect, e.Material, names, particles: true)); }
        }
        int materialCount = sections.Count;
        for (int i = 0; i < effect.Meshes.Length; i++) sections.Add(Mesh(effect.Meshes[i], i, meshMaterial[effect.Meshes[i].Material], endFrame, ticks));
        for (int i = 0; i < effect.Emitters.Length; i++) sections.Add(ParticleSystem(effect, i, particleMaterial[effect.Emitters[i].Material], endFrame));

        var file = VfxBuilder.NewFile() with { EndFrame = endFrame, Sections = sections.ToImmutable() };
        Report(effect, Note, endFrame, materialCount);
        string stem = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(sourceName) ? "effect" : sourceName);
        return new RfeConversion(file, [.. notes], textureFiles) { OutputName = stem + ".vfx" };
    }

    /// <summary>
    /// What a Red Faction II emitter needs on top of its converted particle system to play as the game plays it (the
    /// preview): the acceleration, the launch cone, the speed variance, the literal envelopes, the time-keyed rate and
    /// speed, the evenly spread pose and the effect's own length.
    /// </summary>
    public static VfxParticleExtras Extras(RfeFile effect, int emitter)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var e = effect.Emitters[emitter];
        float duration = effect.Duration;
        return new VfxParticleExtras
        {
            Acceleration = e.HasAcceleration && IsFinite(e.Acceleration) ? e.Acceleration : Vector3.Zero,
            Spread = Finite(e.Spread, 1),
            SpawnCone = Finite(e.SpawnCone, -1),
            SpeedVariance = Finite(e.SpeedVariance, 0),
            LiteralEnvelopes = true,
            BirthRate = e.Spawns ? s => RfeSampler.Track(e.BirthRate, s, 0) : _ => 0,
            Speed = s => RfeSampler.Track(e.Speed, s, 0),
            Pose = s => RfeSampler.Pose(e, RfeSampler.Fraction(s, duration)),
            EndFrame = Frames(duration),
        };
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static float Finite(float v, float fallback) => float.IsFinite(v) ? v : fallback;

    private static Vector3 Finite(Vector3 v, Vector3 fallback) => IsFinite(v) ? v : fallback;

    private static Quaternion Finite(Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W) ? q : Quaternion.Identity;

    // ---- textures ----------------------------------------------------------------------------------------------

    private static (Dictionary<string, string> Names, ImmutableArray<(string, byte[])> Files) Textures(RfeFile effect, RfeConvertOptions options, Action<RfeNoteKind, string> note)
    {
        var used = effect.Materials.Select(m => effect.Textures[m.Texture]).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packs = options.TexturePacks;
        var missing = packs is { IsEmpty: false } ? used.Where(n => !packs.Contains(n)).ToList() : [];
        if (used.Count == 0) return (map, []);
        if (options.Textures == Rf2TextureMode.KeepOriginals)
        {
            var files = ImmutableArray.CreateBuilder<(string, byte[])>();
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var animated = new List<string>();
            foreach (string name in used)
            {
                map[name] = name;
                if (packs is null || packs.Find(name) is not { } hit || packs.ReadAsTga(name) is not { } tga) continue;
                string file = Path.ChangeExtension(Path.GetFileName(name), ".tga");
                map[name] = file;
                if (hit.Texture.FrameCount > 1 || name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase)) animated.Add(name);
                if (written.Add(file)) files.Add((file, tga));
            }
            if (packs is null || packs.IsEmpty) note(RfeNoteKind.Dropped, "The Red Faction II texture names were kept, but no .peg texture packs were found for the effect, so no textures were written.");
            else note(RfeNoteKind.Exact, $"The Red Faction II texture names were kept; {Count(files.Count, "texture")} written beside the effect as .tga from {packs.Label}.");
            if (animated.Count > 0)
                note(RfeNoteKind.Approximated, $"{Count(animated.Count, "animated texture")} written as the first frame only ({List(animated)}); the effect names the .tga.");
            if (missing.Count > 0) note(RfeNoteKind.Dropped, $"{Count(missing.Count, "texture")} not found in any .peg pack, so not written: {List(missing)}.");
            return (map, files.ToImmutable());
        }
        var translations = used.Select(Rf2TextureNames.Translate).ToList();
        foreach (var t in translations) map[t.Original] = t.Translated;
        var changed = translations.Where(t => t.IsTranslated).ToList();
        if (changed.Count > 0)
        {
            note(RfeNoteKind.Approximated, $"Texture names translated to Red Faction names ({changed.Count:N0} of {used.Count:N0}): {List(changed.Select(t => $"{t.Original} → {t.Translated}"))}.");
            var weak = changed.Where(t => t.IsWeak).ToList();
            if (weak.Count > 0) note(RfeNoteKind.Approximated, $"{weak.Count:N0} of those translations are uncertain (the first of several matches was taken): {List(weak.Select(t => $"{t.Original} → {t.Translated}"))}.");
            note(RfeNoteKind.Exact, Rf2TextureNames.TextureSetNote);
        }
        var kept = translations.Where(t => !t.IsTranslated).ToList();
        if (kept.Count > 0)
            note(RfeNoteKind.Exact, $"{Count(kept.Count, "texture name")} without a Red Faction translation keep their Red Faction II names: {List(kept.Select(t => t.Original))}. Convert with the Red Faction II textures kept to write them beside the effect.");
        if (missing.Count > 0) note(RfeNoteKind.Dropped, $"{Count(missing.Count, "texture")} not found in any .peg pack of the effect's install: {List(missing)}.");
        return (map, []);
    }

    // ---- materials -------------------------------------------------------------------------------------------

    private static VfxMaterial Material(RfeFile effect, int index, Dictionary<string, string> names, bool particles)
    {
        var m = effect.Materials[index];
        string texture = effect.Textures[m.Texture];
        string name = texture.Length > 0 && names.TryGetValue(texture, out var mapped) ? mapped : texture;
        int steps = checked((int)Steps(effect.Duration));
        float[] self = particles ? [0] : Samples(m.SelfIllumination, steps, 0);
        float[] opacity = particles ? [RfeSampler.ParticleOpacity(m)] : Samples(m.Opacity, steps, 1);
        var material = name.Length == 0 ? VfxBuilder.ColorMaterial(new VfxColorI(255, 255, 255), m.IsAdditive) : VfxBuilder.ImageMaterial(name, m.IsAdditive);
        return material with { Fps = MaterialFps, SelfIllumination = ImmutableArray.Create(self), Opacity = ImmutableArray.Create(opacity) };
    }

    /// <summary>A time-keyed track every 1/30 s over the effect (one value when it never changes), clamped to 0..1.</summary>
    private static float[] Samples(ImmutableArray<RfeKey> keys, int steps, float none)
    {
        var values = new float[steps + 1];
        for (int i = 0; i <= steps; i++) values[i] = Math.Clamp(Finite(RfeSampler.Track(keys, i / (float)MaterialFps, none), none), 0, 1);
        return values.All(v => v == values[0]) ? [values[0]] : values;
    }

    // ---- meshes ----------------------------------------------------------------------------------------------

    private static VfxMesh Mesh(RfeMeshObject mesh, int index, int material, int endFrame, double ticks)
    {
        var positions = new List<Vector3>();
        var triangles = new List<(int, int, int)>();
        var uvs = new List<Vector2>();
        foreach (var g in mesh.Body.Geometries)
        {
            int at = positions.Count;
            positions.AddRange(g.Positions.Select(p => Finite(p, Vector3.Zero)));
            foreach (var t in g.Triangles)
            {
                triangles.Add((at + t.A, at + t.B, at + t.C));
                uvs.Add(FiniteUv(t.UvA)); uvs.Add(FiniteUv(t.UvB)); uvs.Add(FiniteUv(t.UvC));
            }
        }
        if (positions.Count == 0) positions.Add(Vector3.Zero);
        var lie = RfeSampler.Lie(mesh);
        int[]? smoothing = null;
        if (lie == RfeFacingLie.AcrossAxis)
        {
            // kept as a plain mesh, which Red Faction draws from the front only, while Red Faction II draws effect meshes
            // from both sides: the back faces are added (their own smoothing group, so the normals do not cancel)
            int front = triangles.Count;
            for (int i = 0; i < front; i++)
            {
                var (a, b, c) = triangles[i];
                triangles.Add((a, c, b));
                uvs.Add(uvs[3 * i]); uvs.Add(uvs[3 * i + 2]); uvs.Add(uvs[3 * i + 1]);
            }
            smoothing = [.. Enumerable.Repeat(1, front), .. Enumerable.Repeat(2, front)];
        }
        var builder = new VfxMeshBuilder
        {
            Name = $"mesh_{index:00}",
            Frames = [[.. positions]],
            Triangles = triangles,
            Uvs = [.. uvs],
            SmoothingGroups = smoothing,
            MaterialIndices = [material],
            FrameCount = endFrame,
            Keys = Keys(mesh, ticks),
        };
        // the game turns a facing object's local XY plane to the camera, so its quad is the X by Y extent; an axis-facing
        // object lying across its axis only spins in its own plane, so it stays a plain mesh with its keyed orientation
        if (mesh.FacingMode != RfeFacing.None && lie != RfeFacingLie.AcrossAxis)
        {
            var min = positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            var scale = mesh.Scales.IsDefaultOrEmpty ? Vector3.One : Vector3.Abs(Finite(mesh.Scales[0], Vector3.One));
            builder.Flags = mesh.FacingMode == RfeFacing.Billboard ? VfxMeshFlags.Facing : VfxMeshFlags.FacingRod;
            builder.FacingSize = new Vector2((max.X - min.X) * scale.X, (max.Y - min.Y) * scale.Y);
            // Red Faction uses a rod's up vector as it is (not turned by the keys): the first key's Y axis, turned once
            builder.UpVector = Vector3.Transform(Vector3.UnitY, RfeSampler.Rotation(mesh.Rotations.IsDefaultOrEmpty ? Quaternion.Identity : Finite(mesh.Rotations[0])));
        }
        return builder.Build();

        static Vector2 FiniteUv(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) ? v : Vector2.Zero;
    }

    /// <summary>
    /// Keyframes for evenly spread keys: key i at i/(n-1) of the duration (rounded to whole ticks); straight-line Bezier
    /// control points (a third of the way to each neighbour), so Red Faction's cubic is the lerp; rotations with no ease,
    /// as the conjugate (Red Faction applies a key as <see cref="Vector3.Transform(Vector3, Quaternion)"/>).
    /// </summary>
    private static VfxKeyLists Keys(RfeMeshObject mesh, double ticks) => new(
        VectorKeys(mesh.Positions, ticks, Vector3.Zero),
        [.. mesh.Rotations.Select((q, i) => new VfxRotationKey(Time(i, mesh.Rotations.Length, ticks), RfeSampler.Rotation(Finite(q)), 0, 0, 0, 0, 0))],
        VectorKeys(mesh.Scales, ticks, Vector3.One));

    private static ImmutableArray<VfxVectorKey> VectorKeys(ImmutableArray<Vector3> keys, double ticks, Vector3 none)
    {
        int n = keys.Length;
        var list = ImmutableArray.CreateBuilder<VfxVectorKey>(n);
        for (int i = 0; i < n; i++)
        {
            var v = Finite(keys[i], none);
            var previous = i > 0 ? Finite(keys[i - 1], none) : v;
            var next = i < n - 1 ? Finite(keys[i + 1], none) : v;
            list.Add(new VfxVectorKey(Time(i, n, ticks), v, v - (v - previous) / 3, v + (next - v) / 3));
        }
        return list.MoveToImmutable();
    }

    private static int Time(int i, int n, double ticks) => n <= 1 ? 0 : (int)Math.Round(i * ticks / (n - 1));

    // ---- particles -------------------------------------------------------------------------------------------

    private static VfxParticleSystem ParticleSystem(RfeFile effect, int index, int material, int endFrame)
    {
        var e = effect.Emitters[index];
        bool sphere = e.Shape == 0;
        float width = Math.Max(0, Finite(e.Width, 0)), height = Math.Max(0, Finite(e.Height, 0));
        float radius = Math.Max(0, Finite(e.Radius, 0));
        float variance = Math.Max(0, Finite(e.SpeedVariance, 0));
        float spread = Finite(e.Spread, 1);
        // the launch cone, as a jitter of the speed on each axis (Red Faction has no cone)
        float cone = spread >= 1 ? 0 : spread <= 0 ? 1 : MathF.Sqrt(1 - spread * spread);
        var frames = ImmutableArray.CreateBuilder<VfxParticleFrame>(endFrame);
        for (int i = 0; i < endFrame; i++)
        {
            float seconds = i / (float)VfxTime.FramesPerSecond;
            var (p, q) = RfeSampler.Pose(e, RfeSampler.Fraction(seconds, effect.Duration));
            float speed = Finite(RfeSampler.Track(e.Speed, seconds, 0), 0);
            float rate = e.Spawns ? Math.Max(0, Finite(RfeSampler.Track(e.BirthRate, seconds, 0), 0)) : 0;
            float jitter = MathF.Abs(speed) * (sphere ? variance : Math.Max(variance, cone));
            frames.Add(new VfxParticleFrame(Finite(p, Vector3.Zero), q, sphere ? width / 2 : width, height, radius, speed, jitter,
                rate / VfxTime.TicksPerSecond, null));
        }
        bool streak = e.Streak > 0 && float.IsFinite(e.Streak);
        uint flags = (e.HasGravity ? 2u : 0) | (streak ? 0x100u : 0);
        int lifetime = (int)Math.Clamp(Math.Round(Finite(e.Lifetime, 0) * (double)VfxTime.TicksPerSecond), 1, int.MaxValue / 2);
        // a streak's length is not known: taken as the distance covered in streak / 30 s at the first launch speed
        float? tail = streak ? StreakLength(e.Streak, MathF.Abs(Finite(RfeSampler.Track(e.Speed, 0, 0), 0))) : null;
        return new VfxParticleSystem($"emitter_{index:00}", "Scene Root", 0, flags, [], 0, material, null, Math.Max(0, e.MaxParticles), 0,
            lifetime, Math.Max(0, Finite(e.LifetimeVariance, 0)), sphere ? 1 : 0, null, Envelope(e.Grow, e.Shrink), null, null, Envelope(e.FadeIn, e.FadeOut),
            tail, null, frames.MoveToImmutable());
    }

    /// <summary>The length a streak is drawn at (an approximation: the distance covered in <paramref name="streak"/> / 30 s at <paramref name="speed"/>).</summary>
    public static float StreakLength(float streak, float speed)
    {
        float length = streak * speed * RfeSampler.ParticleStep;
        return float.IsFinite(length) ? Math.Clamp(length, 0, 100) : 0;
    }

    /// <summary>A fade or size envelope; (0, 0), which Red Faction II plays as a ramp over the whole life, becomes (0, 0.001).</summary>
    private static Vector2 Envelope(float birth, float death)
    {
        birth = Math.Clamp(Finite(birth, 0), 0, 1);
        death = Math.Clamp(Finite(death, 1), 0, 1);
        return birth == 0 && death == 0 ? new Vector2(0, 0.001f) : new Vector2(birth, death);
    }

    // ---- the report --------------------------------------------------------------------------------------------

    private static void Report(RfeFile effect, Action<RfeNoteKind, string> note, int endFrame, int materials)
    {
        long steps = Steps(effect.Duration);
        note(RfeNoteKind.Exact, $"{Count(effect.Meshes.Length, "mesh object")}, {Count(effect.Emitters.Length, "emitter")} and {Count(effect.Materials.Length, "material")} became {Count(effect.Meshes.Length, "mesh", "meshes")}, {Count(effect.Emitters.Length, "particle system")} and {Count(materials, "material")}; the effect is {Count(endFrame, "frame")} long at 15 fps.");
        if (steps % 2 == 1)
            note(RfeNoteKind.Approximated, $"The effect lasts {steps} / 30 s, not a whole number of Red Faction frames, so it ends {endFrame} frames in: the last 1/30 s holds the final pose before a loop.");
        if (effect.Meshes.Length > 0)
            note(RfeNoteKind.Exact, "Mesh motion is kept: the evenly spread position, rotation and scale keys became keyframes at their times with straight-line tangents and no ease (the same lerp and slerp), to the nearest 1/4800 s.");
        if (effect.Materials.Any(m => m.Opacity.Length > 1 || m.SelfIllumination.Length > 1))
            note(RfeNoteKind.Exact, $"Material opacity and self-illumination tracks are sampled every 1/{MaterialFps} s.");
        if (effect.Materials.Any(m => m.SelfIllumination.Any(k => k.Value != 0)))
            note(RfeNoteKind.Approximated, "Material track 1 (the RGB level of meshes) became self-illumination, its probable meaning.");
        int bit1 = effect.Materials.Count(m => (m.Blend & 1) != 0), bit4 = effect.Materials.Count(m => (m.Blend & 4) != 0);
        if (bit1 > 0) note(RfeNoteKind.Dropped, $"Blend bit 1 ({Count(bit1, "material")}) has no known meaning and was not kept.");
        if (bit4 > 0) note(RfeNoteKind.Approximated, $"Blend bit 4 ({Count(bit4, "material")}) became additive, like bit 2.");
        var lies = effect.Meshes.Select(RfeSampler.Lie).ToList();
        int facing = effect.Meshes.Count(m => m.FacingMode == RfeFacing.Billboard);
        int rods = effect.Meshes.Where((m, i) => m.FacingMode == RfeFacing.Axial && lies[i] != RfeFacingLie.AcrossAxis).Count();
        int across = lies.Count(l => l == RfeFacingLie.AcrossAxis), edgeOn = lies.Count(l => l == RfeFacingLie.EdgeOn);
        if (facing > 0)
            note(RfeNoteKind.Approximated, $"{Count(facing, "camera-facing mesh", "camera-facing meshes")} (facing 1) became {(facing == 1 ? "a facing mesh" : "facing meshes")}: Red Faction draws a facing mesh as one screen-aligned quad of its X by Y size showing the whole texture, not the mesh itself, and does not scale it with its scale keys.");
        if (rods > 0)
            note(RfeNoteKind.Approximated, $"{Count(rods, "axis-facing mesh", "axis-facing meshes")} (facing 2) became facing rods turning about their first key's Y axis.");
        if (across > 0)
            note(RfeNoteKind.Approximated, $"{Count(across, "axis-facing mesh", "axis-facing meshes")} (facing 2) {(across == 1 ? "lies flat across its axis, so Red Faction II only spins it in its own plane to face the camera: kept as a plain mesh" : "lie flat across their axis, so Red Faction II only spins them in their own plane to face the camera: kept as plain meshes")} with the keyed orientation (no spin), drawn from both sides.");
        int plain = effect.Meshes.Count(m => m.FacingMode == RfeFacing.None);
        if (plain > 0)
            note(RfeNoteKind.Approximated, $"Red Faction II draws effect meshes from both sides (it turns culling off for them); Red Faction draws only their front faces, so {(plain == 1 ? "the mesh does" : $"the {plain:N0} meshes do")} not show from behind.");
        if (edgeOn > 0)
            note(RfeNoteKind.Dropped, $"{Count(edgeOn, "facing mesh", "facing meshes")} {(edgeOn == 1 ? "lies flat in its" : "lie flat in their")} local XZ or YZ plane, which Red Faction II turns edge-on to the camera, so the game never shows {(edgeOn == 1 ? "it" : "them")}; converted the same way (a facing quad with no width or height).");
        if (effect.Emitters.Length == 0) return;
        note(RfeNoteKind.Exact, "Particles: lifetime and its variance, gravity, the rectangle (X by Z) and sphere spawns, the fade and size envelopes, the particle size (twice the radius), the launch speed and the slot count map to Red Faction's fields; the birth rate (per second, sampled every frame) became per tick.");
        int cones = effect.Emitters.Count(e => e.Spread < 1);
        if (cones > 0) note(RfeNoteKind.Approximated, $"The launch cone of {Count(cones, "emitter")} became a random jitter of the launch speed on each axis (Red Faction has no cone).");
        int variance = effect.Emitters.Count(e => e.Shape == 1 && e.SpeedVariance != 0);
        if (variance > 0) note(RfeNoteKind.Approximated, $"The speed variance of {Count(variance, "rectangle emitter")} became Red Faction's per-axis speed jitter instead of a variation of the speed along the launch direction.");
        int spawnCones = effect.Emitters.Count(e => e.Shape == 0 && e.SpawnCone > -1);
        if (spawnCones > 0) note(RfeNoteKind.Approximated, $"{Count(spawnCones, "sphere emitter")} spawn in a cone in Red Faction II; Red Faction spawns over the whole sphere.");
        int accel = effect.Emitters.Count(e => e.HasAcceleration && e.Acceleration != Vector3.Zero);
        if (accel > 0) note(RfeNoteKind.Dropped, $"The constant acceleration of {Count(accel, "emitter")} has no Red Faction field and was not kept.");
        int streaks = effect.Emitters.Count(e => e.Streak > 0);
        if (streaks > 0) note(RfeNoteKind.Approximated, $"{Count(streaks, "emitter")} drawn as streaks became drop particles (Red Faction's streaks); the streak length is not known.");
        int other = effect.Emitters.Count(e => !e.Spawns);
        if (other > 0) note(RfeNoteKind.Dropped, $"{Count(other, "emitter")} of an unknown shape spawn nothing in the game and were kept without births.");
        int envelopes = effect.Emitters.Count(e => (e.FadeIn == 0 && e.FadeOut == 0) || (e.Grow == 0 && e.Shrink == 0));
        if (envelopes > 0) note(RfeNoteKind.Approximated, $"{Count(envelopes, "emitter")} fade or shrink over the whole life (stored as 0, 0); that became 0, 0.001.");
        if (effect.Emitters.Any(e => (e.Flags & 2) != 0)) note(RfeNoteKind.Dropped, "Emitter flag bit 2 has no known meaning and was not kept.");
        if (effect.Materials.Any(m => m.Opacity.Length > 1) && effect.Emitters.Length > 0)
            note(RfeNoteKind.Exact, "Particles use their material's first opacity value, as in Red Faction II (their materials are separate copies).");
        note(RfeNoteKind.Approximated, "Particle colour and lighting were not traced in Red Faction II; the particles take their material as Red Faction draws it.");
        note(RfeNoteKind.Dropped, "The random value Red Faction II stores per particle (probably a roll or a start frame) is not kept: particles do not roll.");
    }

    private static string Count(int n, string noun, string? plural = null) =>
        n.ToString("N0", CultureInfo.CurrentCulture) + " " + (n == 1 ? noun : plural ?? noun + "s");

    private static string List(IEnumerable<string> items)
    {
        var all = items.ToList();
        return string.Join(", ", all.Take(8)) + (all.Count > 8 ? $" and {all.Count - 8:N0} more" : "");
    }
}
