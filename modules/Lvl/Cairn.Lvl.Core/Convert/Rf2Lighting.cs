using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Conversion;

/// <summary>
/// Red Faction II lighting turned into Red Faction lights, as REDUX does it (the author's MIT code, ported): RF2 lights
/// rescaled from inverse-square to linear falloff, a light per coloured corona with a range and intensity fitted to
/// the level's baked vertex colours, fill lights where the baked colours hold light no source explains, and the
/// level's lightmap multiplier. Everything here is an approximation of RF2's light bake, and the report says so.
/// </summary>
internal static class Rf2Lighting
{
    private const float SpotConeLimit = 60f;

    /// <summary>RF2 lights with Red Faction's falloff: range times <paramref name="rangeScale"/>, intensity times <paramref name="intensityScale"/>.</summary>
    public static RfLight Rescale(RfLight light, float rangeScale, float intensityScale) =>
        light with { Range = light.Range * rangeScale, OnIntensity = light.OnIntensity * intensityScale };

    /// <summary>A corona's colour, with the editor's CD CD CD filler (never set) taken as white.</summary>
    public static RflColor CoronaColor(RfCorona corona) =>
        corona.Color is { R: 0xCD, G: 0xCD, B: 0xCD } ? new RflColor(255, 255, 255, 255) : corona.Color;

    /// <summary>
    /// REDUX's corona falloff fit: the linear range (5..100 m, clamped to 10..60) and intensity scale (clamped to
    /// 0.1..3) that best reproduce the baked colours of corners far from every regular light. Null when there are too
    /// few such corners (fewer than 100) or when no fit gives a finite error. Non-finite corners and coronas (a
    /// damaged file can hold any float) are left out of the fit.
    /// </summary>
    public static (float Range, float Scale, int Samples)? FitCoronas(Rf2VertexLighting lighting, IReadOnlyList<RfLight> lights, IReadOnlyList<RfCorona> coronas, CancellationToken ct)
    {
        coronas = [.. coronas.Where(c => IsFinite(c.Position) && float.IsFinite(c.Intensity))];
        if (coronas.Count == 0) return null;
        var candidates = new List<(Vector3 Pos, Vector3 Normal, Vector3 Baked, float Clear)>();
        foreach (var entry in lighting.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var v = entry.Vertices;
            foreach (var t in entry.Triangles)
            {
                if ((uint)t.A >= (uint)v.Length || (uint)t.B >= (uint)v.Length || (uint)t.C >= (uint)v.Length) continue;
                var n = Vector3.Cross(v[t.B] - v[t.A], v[t.C] - v[t.A]);
                if (!(n.LengthSquared() >= 1e-8f) || !float.IsFinite(n.LengthSquared())) continue;
                n = Vector3.Normalize(n);
                Add(v[t.A], t.ColorA);
                Add(v[t.B], t.ColorB);
                Add(v[t.C], t.ColorC);

                void Add(Vector3 pos, RflColor c)
                {
                    var baked = new Vector3(c.R, c.G, c.B);
                    if (MathF.Max(baked.X, MathF.Max(baked.Y, baked.Z)) < 1f || !IsFinite(pos)) return;
                    float best = float.MaxValue;
                    foreach (var l in lights)
                    {
                        if (!(l.Range > 0f)) continue;
                        float ratio = (l.Position - pos).Length() / l.Range;
                        if (ratio < best) best = ratio;
                    }
                    candidates.Add((pos, n, baked, best));
                }
            }
        }

        // The strictest distance threshold that leaves at least 100 corners; 0 accepts every lit corner.
        List<(Vector3 Pos, Vector3 Normal, Vector3 Baked)> samples = [];
        foreach (float threshold in (ReadOnlySpan<float>)[2f, 1.5f, 1f, 0.5f, 0f])
        {
            samples = [.. candidates.Where(c => c.Clear >= threshold).Select(c => (c.Pos, c.Normal, c.Baked))];
            if (samples.Count >= 100) break;
        }
        if (samples.Count < 100) return null;
        const int maxSamples = 1500;
        if (samples.Count > maxSamples)
        {
            var rng = new Random(42);
            samples = [.. samples.OrderBy(_ => rng.Next()).Take(maxSamples)];
        }

        var basis = new List<(float D, float Ndl, Vector3 Color)>[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var (pos, normal, _) = samples[i];
            var row = new List<(float, float, Vector3)>();
            foreach (var c in coronas)
            {
                float d = MathF.Max(0.01f, (c.Position - pos).Length());
                float ndl = Vector3.Dot(normal, (c.Position - pos) / d);
                if (ndl <= 0f) continue;
                var color = CoronaColor(c);
                row.Add((d, ndl, new Vector3(color.R, color.G, color.B) / 255f * c.Intensity));
            }
            basis[i] = row;
        }

        var fits = new List<(float R, float Mse, float Scale)>();
        var predicted = new Vector3[samples.Count];
        for (int r2 = 10; r2 <= 200; r2 += 2)
        {
            ct.ThrowIfCancellationRequested();
            float range = r2 * 0.5f;
            float pa = 0f, pp = 0f;
            for (int i = 0; i < samples.Count; i++)
            {
                var p = Vector3.Zero;
                foreach (var (d, ndl, color) in basis[i])
                    if (d < range) p += (1f - d / range) * ndl * color;
                p *= 255f;
                predicted[i] = p;
                pa += Vector3.Dot(p, samples[i].Baked);
                pp += p.LengthSquared();
            }
            if (!(pp >= 1e-6f)) continue;
            float scale = pa / pp;
            float error = 0f;
            for (int i = 0; i < samples.Count; i++) error += (scale * predicted[i] - samples[i].Baked).LengthSquared();
            float mse = error / (samples.Count * 3);
            if (float.IsFinite(mse) && float.IsFinite(scale)) fits.Add((range, mse, scale));
        }
        if (fits.Count == 0) return null;
        float limit = fits.Min(f => f.Mse) * 1.10f;
        var chosen = fits.Where(f => f.Mse <= limit).OrderBy(f => f.R).FirstOrDefault();
        if (chosen == default) return null;
        return (Math.Clamp(chosen.R, 10f, 60f), Math.Clamp(chosen.Scale, 0.1f, 3f), samples.Count);
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>
    /// A light for one corona: a spot light when the cone is narrower than 60 degrees and the corona points
    /// somewhere, else a point light. Null for a corona that gives no light: black, or without intensity (a corona
    /// without a bitmap stores none).
    /// </summary>
    public static RfLight? FromCorona(RfCorona corona, int uid, float range, float intensityScale)
    {
        var color = CoronaColor(corona);
        if (color.R == 0 && color.G == 0 && color.B == 0 || !(corona.Intensity > 0)) return null;
        bool spot = corona.ConeAngle < SpotConeLimit && corona.Orientation.Forward.LengthSquared() > 0.001f;
        var light = Base(uid, corona.Position, new RflColor(color.R, color.G, color.B, 255), range, corona.Intensity * intensityScale, spot ? RfLightType.Spot : RfLightType.Omni)
            with { ScriptName = corona.ScriptName };
        if (!spot) return light with { Fov = 15f, FovDropoff = 15f };
        var o = corona.Orientation;
        return light with
        {
            Fov = corona.ConeAngle / 2f,
            FovDropoff = corona.ConeAngle / 2f,
            Orientation = new RfMatrix(Unit(o.Right), Unit(o.Up), Unit(o.Forward)),
        };

        static Vector3 Unit(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v;
    }

    /// <summary>
    /// REDUX's compensating fill lights: corners whose baked colour points more than 20 degrees away from every light
    /// and corona colour (and every mix of two) are binned on a coarse XZ grid; each well-filled cell (at least 5
    /// corners, at most 8 cells) gets a point light above it with a subtle intensity (at most 0.5).
    /// </summary>
    public static List<RfLight> FillLights(Rf2VertexLighting lighting, IReadOnlyList<RfLight> lights, IReadOnlyList<RfCorona> coronas, ref int nextUid, CancellationToken ct)
    {
        var result = new List<RfLight>();
        var sources = new List<Vector3>();
        foreach (var l in lights) AddSource(new Vector3(l.Color.R, l.Color.G, l.Color.B) / 255f);
        foreach (var c in coronas)
        {
            var color = CoronaColor(c);
            AddSource(new Vector3(color.R, color.G, color.B) / 255f);
        }
        if (sources.Count == 0) return result;
        var reachable = new List<Vector3>(sources);
        for (int i = 0; i < sources.Count; i++)
        {
            for (int j = i + 1; j < sources.Count; j++)
            {
                var mix = sources[i] + sources[j];
                if (mix.LengthSquared() > 0.001f) reachable.Add(Vector3.Normalize(mix));
            }
        }

        var corners = new List<(Vector3 Pos, Vector3 Color)>();
        foreach (var entry in lighting.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var v = entry.Vertices;
            foreach (var t in entry.Triangles)
            {
                Add(t.A, t.ColorA);
                Add(t.B, t.ColorB);
                Add(t.C, t.ColorC);
            }

            void Add(int index, RflColor c)
            {
                if ((uint)index >= (uint)v.Length || !IsFinite(v[index])) return;
                var color = new Vector3(c.R, c.G, c.B);
                if (color.LengthSquared() > 100) corners.Add((v[index], color));
            }
        }
        if (corners.Count < 50) return result;

        var unexplained = new List<(Vector3 Pos, Vector3 Color)>();
        foreach (var (pos, color) in corners)
        {
            var dir = Vector3.Normalize(color);
            float min = float.MaxValue;
            foreach (var r in reachable) min = MathF.Min(min, Angle(dir, r));
            if (min > 20f) unexplained.Add((pos, color));
        }
        if (unexplained.Count < 20) return result;

        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var (pos, _) in unexplained)
        {
            lo = Vector3.Min(lo, pos);
            hi = Vector3.Max(hi, pos);
        }
        var extent = hi - lo;
        float cell = MathF.Max(8f, MathF.Max(extent.X, extent.Z) / 4f);
        int nx = Math.Max(1, (int)MathF.Ceiling(extent.X / cell));
        int nz = Math.Max(1, (int)MathF.Ceiling(extent.Z / cell));
        var cells = new Dictionary<(int, int), (Vector3 Color, Vector3 Pos, int Count, float MaxY)>();
        foreach (var (pos, color) in unexplained)
        {
            var key = (Math.Clamp((int)((pos.X - lo.X) / cell), 0, nx - 1), Math.Clamp((int)((pos.Z - lo.Z) / cell), 0, nz - 1));
            var c = cells.TryGetValue(key, out var existing) ? existing : (Color: Vector3.Zero, Pos: Vector3.Zero, Count: 0, MaxY: float.MinValue);
            cells[key] = (c.Color + color, c.Pos + pos, c.Count + 1, MathF.Max(c.MaxY, pos.Y));
        }
        var clusters = cells.Values.Where(c => c.Count >= 5)
            .Select(c => (Centroid: c.Pos / c.Count, Color: c.Color / c.Count, c.Count, c.MaxY))
            .OrderByDescending(c => c.Count).Take(8).ToList();
        foreach (var (centroid, color, _, maxY) in clusters)
        {
            float max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
            if (max < 1f) continue;
            var normalized = color / max;
            float elevation = maxY + cell * 1.5f;
            float height = elevation - centroid.Y;
            float range = (height + cell) * 2f;
            float attenuation = MathF.Max(0.1f, 1f - height / range);
            float intensity = MathF.Min(max / 255f / attenuation, 0.5f);
            var rgb = new RflColor(ToByte(normalized.X), ToByte(normalized.Y), ToByte(normalized.Z), 255);
            result.Add(Base(nextUid++, new Vector3(centroid.X, elevation, centroid.Z), rgb, range, intensity, RfLightType.Omni) with { Fov = 15f, FovDropoff = 15f });
        }
        return result;

        void AddSource(Vector3 c)
        {
            if (c.LengthSquared() <= 0.001f) return;
            var dir = Vector3.Normalize(c);
            foreach (var s in sources)
                if (Angle(dir, s) < 5f) return;
            sources.Add(dir);
        }
    }

    /// <summary>The median baked colour over every corner of the vertex lighting data (each channel on its own).</summary>
    public static (byte R, byte G, byte B)? MedianBakedColor(Rf2VertexLighting lighting)
    {
        int n = lighting.TriangleCount * 3;
        if (n == 0) return null;
        var r = new byte[n];
        var g = new byte[n];
        var b = new byte[n];
        int i = 0;
        foreach (var entry in lighting.Entries)
        {
            foreach (var t in entry.Triangles)
            {
                foreach (var c in (ReadOnlySpan<RflColor>)[t.ColorA, t.ColorB, t.ColorC])
                {
                    r[i] = c.R;
                    g[i] = c.G;
                    b[i] = c.B;
                    i++;
                }
            }
        }
        Array.Sort(r);
        Array.Sort(g);
        Array.Sort(b);
        return (r[n / 2], g[n / 2], b[n / 2]);
    }

    private static RfLight Base(int uid, Vector3 position, RflColor color, float range, float intensity, RfLightType type) => new()
    {
        Uid = uid,
        ClassName = "Light",
        Position = position,
        // Shadow casting, enabled, the light type, and RED's default initial state 2.
        Flags = RfLightFlags.ShadowCasting | RfLightFlags.Enabled | (RfLightFlags)((uint)type << 4) | (RfLightFlags)(2u << 8),
        Color = color,
        Range = range,
        IntensityAtMaxRange = 0f,
        DropoffType = 0,
        TubeWidth = 4f,
        OnIntensity = intensity,
        OnTime = 1f,
        OffIntensity = 0f,
        OffTime = 1f,
    };

    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * (180f / MathF.PI);

    private static byte ToByte(float unit) => (byte)Math.Clamp((int)MathF.Round(unit * 255f), 0, 255);
}
