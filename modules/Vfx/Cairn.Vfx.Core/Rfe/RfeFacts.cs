using System.Globalization;

namespace Cairn.Vfx.Rfe;

/// <summary>One row of an effect's facts: a section ("Effect", "Mesh 1"...), a label and a value.</summary>
public sealed record RfeFact(string Section, string Label, string Value);

/// <summary>
/// The facts of a Red Faction II effect as the tab and the packfile's details pane show them, and the behaviours Cairn's
/// preview only approximates.
/// </summary>
public static class RfeFacts
{
    /// <summary>What the preview approximates (the game's code was read for the rest).</summary>
    public static IReadOnlyList<string> PreviewApproximations { get; } =
    [
        "Material blend bit 1 is ignored and bit 4 is drawn additive like bit 2.",
        "Material track 1 is used as self-illumination (its probable meaning; it is the meshes' RGB level).",
        "Emitter flag bit 2 is ignored.",
        "Streaks are drawn along the velocity with a length of streak / 30 s of travel (the length factor is not known).",
        "Particle colour and lighting were not traced: particles are drawn unlit with their texture and their material's first opacity value.",
        "The random value the game keeps per particle (probably a roll or a start frame) is not applied.",
        "Animated .vbm textures play at 15 fps (the .peg packs store no rate).",
        "Meshes are lit by the viewport's lights, not a level's.",
        "Particle randomness uses Cairn's own generator, so single particles differ from the game.",
    ];

    /// <summary>How each Red Faction II field converts to a Red Faction .vfx: (field, Red Faction counterpart, exact / approximated / dropped).</summary>
    public static IReadOnlyList<(string Rf2, string Rf1, RfeNoteKind Kind)> ConversionMapping { get; } =
    [
        ("duration", "end frame (15 fps); an odd number of 1/30 s holds the last pose 1/30 s", RfeNoteKind.Approximated),
        ("texture names", "material texture: translated (REDUX tables) or kept with .tga written from the .peg", RfeNoteKind.Exact),
        ("animated .vbm textures (kept names)", "the first frame as .tga", RfeNoteKind.Approximated),
        ("material blend bit 2", "additive", RfeNoteKind.Exact),
        ("material blend bit 4", "additive", RfeNoteKind.Approximated),
        ("material blend bit 1", "none", RfeNoteKind.Dropped),
        ("material opacity track (seconds)", "material opacity, sampled every 1/30 s", RfeNoteKind.Exact),
        ("material track 1 (RGB level)", "material self-illumination, sampled every 1/30 s", RfeNoteKind.Approximated),
        ("mesh body (vertices, triangles, UVs)", "mesh frame 0, per-corner UVs", RfeNoteKind.Exact),
        ("mesh position / rotation / scale keys (evenly spread)", "keyframes at their times (whole ticks), straight-line tangents, no ease", RfeNoteKind.Exact),
        ("mesh facing 1 (billboard)", "facing flag (one screen-aligned quad of the mesh's X by Y size)", RfeNoteKind.Approximated),
        ("mesh facing 2 (turns about its Y axis)", "facing rod about the first key's Y axis", RfeNoteKind.Approximated),
        ("mesh facing 2 lying flat across its axis", "a plain mesh with its keyed orientation (no spin in its plane), back faces added", RfeNoteKind.Approximated),
        ("meshes drawn from both sides (culling off)", "front faces only", RfeNoteKind.Approximated),
        ("facing mesh flat in its XZ or YZ plane (edge-on in the game, never shown)", "the same: a facing quad with no width or height", RfeNoteKind.Exact),
        ("emitter shape 1 (rectangle X by Z)", "type 0, width and height", RfeNoteKind.Exact),
        ("emitter shape 0 (sphere)", "type 1, width = half the diameter", RfeNoteKind.Exact),
        ("emitter shape 0 spawn cone", "whole sphere", RfeNoteKind.Approximated),
        ("emitter position / rotation keys", "particle frames every 1/15 s", RfeNoteKind.Approximated),
        ("birth rate (per second, keyed in seconds)", "birth rate per tick, every 1/15 s", RfeNoteKind.Approximated),
        ("launch speed track", "frame speed, every 1/15 s", RfeNoteKind.Exact),
        ("speed variance (sphere)", "speed variation", RfeNoteKind.Exact),
        ("speed variance (rectangle), launch cone (spread)", "per-axis speed variation", RfeNoteKind.Approximated),
        ("max particles", "particle count", RfeNoteKind.Exact),
        ("radius", "drop size (sprites twice it)", RfeNoteKind.Exact),
        ("lifetime and its variance", "lifetime (ticks) and variation", RfeNoteKind.Exact),
        ("gravity (flag bit 1)", "gravity flag 0x2", RfeNoteKind.Exact),
        ("fade in / out, grow / shrink", "fade and shrink birth / death ((0, 0) becomes (0, 0.001))", RfeNoteKind.Exact),
        ("streak", "drops flag with a tail of streak / 30 s of travel at the first speed", RfeNoteKind.Approximated),
        ("constant acceleration (flag bit 4)", "none", RfeNoteKind.Dropped),
        ("emitter flag bit 2, the float at 0x44", "none", RfeNoteKind.Dropped),
        ("particle opacity (the material's first value)", "a separate constant-opacity material", RfeNoteKind.Exact),
        ("particle colour and lighting", "the material as Red Faction draws it", RfeNoteKind.Approximated),
        ("per-particle random value", "none (no roll)", RfeNoteKind.Dropped),
    ];

    /// <summary>The rows for <paramref name="effect"/>; <paramref name="textureWhere"/> says where a texture was found (null: not looked up).</summary>
    public static IReadOnlyList<RfeFact> Describe(RfeFile effect, Func<string, string>? textureWhere = null)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var rows = new List<RfeFact>();
        void Add(string section, string label, string value) => rows.Add(new RfeFact(section, label, value));
        const string E = "Effect";
        Add(E, "Kind", "Red Faction II effect (read-only in Cairn; converts to a Red Faction .vfx)");
        Add(E, "Duration", $"{F(effect.Duration, "0.###")} s ({RfeToVfx.Steps(effect.Duration):N0} × 1/30 s)");
        Add(E, "Bounding radius", effect.Radius > 0 ? $"{F(effect.Radius, "0.###")} m" : "0 (no meshes)");
        Add(E, "Contents", $"{N(effect.Textures.Length, "texture")}, {N(effect.Materials.Length, "material")}, {N(effect.Meshes.Length, "mesh object")}, {N(effect.Emitters.Length, "emitter")}");
        if (effect.Meshes.Length > 0)
            Add(E, "Geometry", $"{effect.Meshes.Sum(m => m.Body.VertexCount):N0} vertices, {effect.Meshes.Sum(m => m.Body.TriangleCount):N0} triangles");
        if (effect.Emitters.Length > 0) Add(E, "Particle budget", $"{effect.ParticleBudget:N0} slots");

        for (int i = 0; i < effect.Textures.Length; i++)
        {
            string name = effect.Textures[i];
            Add("Textures", $"{i + 1}. {(name.Length == 0 ? "(empty)" : name)}", name.Length == 0 ? "no name" : textureWhere?.Invoke(name) ?? "");
        }
        for (int i = 0; i < effect.Materials.Length; i++)
        {
            var m = effect.Materials[i];
            string blend = m.Blend switch { 0 => "0 (normal)", _ => $"{m.Blend} ({string.Join(" + ", Bits(m.Blend))})" };
            Add("Materials", $"{i + 1}. {Texture(effect, m.Texture)}", $"blend {blend}; opacity {Track(m.Opacity)}; self-illumination {Track(m.SelfIllumination)}");
        }
        for (int i = 0; i < effect.Meshes.Length; i++)
        {
            var o = effect.Meshes[i];
            string facing = o.Facing switch { 0 => "0 (none)", 1 => "1 (billboard: local +Z at the camera)", 2 => "2 (turns about its Y axis to the camera)", _ => $"{o.Facing} (unknown, drawn as 0)" }
                + RfeSampler.Lie(o) switch
                {
                    RfeFacingLie.EdgeOn => ", but flat in its XZ or YZ plane: edge-on to the camera, never shown in the game",
                    RfeFacingLie.AcrossAxis => ", flat across its axis: it only spins in its own plane",
                    _ => "",
                };
            Add("Mesh objects", $"{i + 1}. {Texture(effect, effect.Materials[o.Material].Texture)}",
                $"facing {facing}; material {o.Material + 1}; keys {o.Positions.Length} position, {o.Rotations.Length} rotation, {o.Scales.Length} scale; {o.Body.VertexCount:N0} vertices, {o.Body.TriangleCount:N0} triangles");
        }
        for (int i = 0; i < effect.Emitters.Length; i++)
        {
            var e = effect.Emitters[i];
            string shape = e.Shape switch
            {
                1 => $"rectangle {F(e.Width, "0.###")} × {F(e.Height, "0.###")} m",
                0 => $"sphere {F(e.Width / 2, "0.###")} m{(e.SpawnCone > -1 ? $", cone {F(Degrees(e.SpawnCone), "0")}°" : "")}",
                _ => $"shape {e.Shape} (spawns nothing)",
            };
            var flags = new List<string>();
            if (e.HasGravity) flags.Add("gravity");
            if (e.HasAcceleration) flags.Add($"acceleration ({F(e.Acceleration.X, "0.##")}, {F(e.Acceleration.Y, "0.##")}, {F(e.Acceleration.Z, "0.##")}) m/s²");
            if ((e.Flags & 2) != 0) flags.Add("bit 2 (unknown)");
            Add("Emitters", $"{i + 1}. {Texture(effect, effect.Materials[e.Material].Texture)}",
                $"{shape}; max {e.MaxParticles:N0}; rate {Range(e.BirthRate)}/s; speed {Range(e.Speed)} m/s ±{F(e.SpeedVariance * 100, "0")} %"
                + $"{(e.Spread < 1 ? $", cone {F(Degrees(e.Spread), "0")}°" : "")}; life {F(e.Lifetime, "0.###")} s ±{F(e.LifetimeVariance * 100, "0")} %; size {F(e.Radius * 2, "0.###")} m"
                + $"; fade {F(e.FadeIn, "0.##")}/{F(e.FadeOut, "0.##")}, size {F(e.Grow, "0.##")}/{F(e.Shrink, "0.##")}"
                + (e.Streak > 0 ? $"; streak {F(e.Streak, "0.##")}" : "") + (flags.Count > 0 ? "; " + string.Join(", ", flags) : ""));
        }
        return rows;
    }

    private static IEnumerable<string> Bits(uint blend)
    {
        if ((blend & 1) != 0) yield return "bit 1, unknown";
        if ((blend & 2) != 0) yield return "bit 2, additive";
        if ((blend & 4) != 0) yield return "bit 4, additive-style";
        if ((blend & ~7u) != 0) yield return "other bits";
    }

    private static string Texture(RfeFile effect, int index) =>
        (uint)index < (uint)effect.Textures.Length && effect.Textures[index].Length > 0 ? effect.Textures[index] : "(no texture)";

    private static string Track(ImmutableArray<RfeKey> keys) =>
        keys.IsDefaultOrEmpty ? "none" : keys.Length == 1 ? F(keys[0].Value, "0.##") : $"{keys.Length} keys, {Range(keys)}";

    private static string Range(ImmutableArray<RfeKey> keys)
    {
        if (keys.IsDefaultOrEmpty) return "0";
        float min = keys.Min(k => k.Value), max = keys.Max(k => k.Value);
        return min == max ? F(min, "0.##") : $"{F(min, "0.##")}–{F(max, "0.##")}";
    }

    private static float Degrees(float cosine) => float.IsFinite(cosine) ? MathF.Acos(Math.Clamp(cosine, -1, 1)) * 180 / MathF.PI : 0;

    private static string F(float v, string format) => v.ToString(format, CultureInfo.CurrentCulture);

    private static string N(int n, string noun) => $"{n.ToString("N0", CultureInfo.CurrentCulture)} {noun}{(n == 1 ? "" : "s")}";
}
