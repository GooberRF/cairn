using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Animation;

/// <summary>A live particle as the renderer needs it.</summary>
/// <param name="Position">Effect-space position.</param>
/// <param name="Velocity">Velocity in metres per second.</param>
/// <param name="Angle">Sprite roll in radians.</param>
/// <param name="Size">Sprite radius: twice the emitter's current drop size, times the shrink curve.</param>
/// <param name="Alpha">Emitter opacity times the fade curve, 0..1.</param>
/// <param name="TextureFrame">Frame of the first texture, or -1 when the material has none.</param>
/// <param name="LifeFraction">Age over lifetime, 0..1.</param>
public readonly record struct VfxParticle(Vector3 Position, Vector3 Velocity, float Size, float Alpha, int TextureFrame,
    float Angle, float LifeFraction);

/// <summary>
/// Behaviour outside Red Faction's particle systems, for playing another game's emitter through
/// <see cref="VfxParticleSimulator"/> (a Red Faction II effect: its emitter converted to a particle system, plus what the
/// format has no field for). With extras a particle is born at the emitter with age 0, its life is the lifetime times
/// 1 + U(-v, v), and it is launched along the emitter's local -Y (type 0, from a point of the X by Z rectangle) or from a
/// point at the width's distance in a random direction (type 1).
/// </summary>
public sealed record VfxParticleExtras
{
    /// <summary>A constant acceleration in effect space, m/s².</summary>
    public Vector3 Acceleration { get; init; }

    /// <summary>The cosine of a cone the launch direction is drawn from (uniform over the cap); 1 or more: no cone.</summary>
    public float Spread { get; init; } = 1;

    /// <summary>Type 1: the cosine of the cone (around the launch axis) the spawn direction is drawn from; -1 or less: the whole sphere.</summary>
    public float SpawnCone { get; init; } = -1;

    /// <summary>The launch speed is multiplied by 1 + U(-v, v) (instead of the per-axis jitter of the frame's speed variation).</summary>
    public float SpeedVariance { get; init; }

    /// <summary>The fade and shrink curves as stored: (0, 0) fades over the whole life instead of meaning none.</summary>
    public bool LiteralEnvelopes { get; init; }

    /// <summary>Particles per second at a time in seconds (instead of the frames' per-tick birth rate), or null.</summary>
    public Func<float, float>? BirthRate { get; init; }

    /// <summary>The launch speed at a time in seconds (instead of the frames'), or null.</summary>
    public Func<float, float>? Speed { get; init; }

    /// <summary>The emitter's position and orientation at a time in seconds (instead of the frames'), or null.</summary>
    public Func<float, (Vector3 Position, Quaternion Orientation)>? Pose { get; init; }

    /// <summary>The effect's length in 15 fps frames when it is not a whole number (births stop there; loops wrap there); 0: the file's end frame.</summary>
    public double EndFrame { get; init; }
}

/// <summary>
/// Deterministic simulation of one particle system. Particles are born in effect space (the preview host sits at
/// the origin) and do not follow the emitter after birth. The state advances in fixed steps from time zero, so
/// any time is reached identically whether played forward, scrubbed back (restart and replay) or run fresh.
/// </summary>
public sealed class VfxParticleSimulator
{
    private const float Gravity = 9.8f;
    private readonly VfxSampler sampler;
    private readonly VfxParticleSystem system;
    private readonly int index, seed, material, endFrame;
    private readonly VfxPlaybackMode mode;
    private readonly VfxParticleExtras? extras;
    private readonly double end;
    private readonly float step;
    private readonly uint flags;
    private readonly float shrinkBirth, shrinkDeath, fadeBirth, fadeDeath;
    private readonly int[] warpIndices;
    private readonly float[] age, life, angle;
    private readonly Vector3[] pos, vel;
    private readonly VfxParticle[] output;
    private int outputCount;
    private long steps;
    private uint rng;
    private float accumulator;
    private Vector3 prevEmitter;
    private bool hasPrevEmitter;

    /// <param name="sampler">The sampler of the effect.</param>
    /// <param name="systemIndex">Index into <see cref="VfxSampler.ParticleSystems"/>.</param>
    /// <param name="seed">Random seed; equal seeds give equal results.</param>
    /// <param name="mode">Playback mode (looping re-emits, one-shot and hold stop emitting at the end).</param>
    /// <param name="stepsPerFrame">Simulation steps per 15 fps frame (the game ticks at the render rate; 2 = 30 Hz).</param>
    /// <param name="extras">Behaviour of another game's emitter (see <see cref="VfxParticleExtras"/>), or null for Red Faction's.</param>
    public VfxParticleSimulator(VfxSampler sampler, int systemIndex, int seed = 1, VfxPlaybackMode mode = VfxPlaybackMode.Loop,
        int stepsPerFrame = 2, VfxParticleExtras? extras = null)
    {
        this.sampler = sampler;
        index = systemIndex;
        this.seed = seed;
        this.mode = mode;
        this.extras = extras;
        system = sampler.ParticleSystems[systemIndex];
        material = sampler.ParticleMaterial(systemIndex);
        endFrame = sampler.EndFrame;
        end = extras is { EndFrame: > 0 } x && double.IsFinite(x.EndFrame) ? x.EndFrame : endFrame;
        step = 1f / Math.Max(1, stepsPerFrame);
        flags = system.Flags ?? (uint)(system.LegacyFlags ?? 0);
        var shrink = system.Shrink ?? new Vector2(system.LegacyShrinkBirth ?? 0, system.LegacyShrinkDeath ?? 0);
        (shrinkBirth, shrinkDeath) = extras?.LiteralEnvelopes == true ? (shrink.X, shrink.Y) : Curve(shrink);
        var fade = system.Fade ?? Vector2.Zero;
        (fadeBirth, fadeDeath) = extras?.LiteralEnvelopes == true ? (fade.X, fade.Y) : Curve(fade);
        var warpList = new List<int>();
        foreach (var name in system.Warps)
            for (int w = 0; w < sampler.Spacewarps.Count; w++)
                if (string.Equals(sampler.Spacewarps[w].Name, name, StringComparison.OrdinalIgnoreCase)) { warpList.Add(w); break; }
        warpIndices = [.. warpList.Take(3)];
        int cap = Math.Max(0, system.ParticleCount);
        age = new float[cap]; life = new float[cap]; angle = new float[cap];
        pos = new Vector3[cap]; vel = new Vector3[cap];
        output = new VfxParticle[cap];
        Reset();
    }

    /// <summary>Slot cap: births beyond it are dropped.</summary>
    public int Capacity => age.Length;

    /// <summary>True when the system uses spacewarps.</summary>
    public bool UsesSpacewarps => warpIndices.Length > 0;

    /// <summary>True for "drops" systems, drawn as streaks along the velocity.</summary>
    public bool IsDrops => system.IsDrops;

    /// <summary>True when the result relies on approximated behaviour (spacewarp forces or drop streaks).</summary>
    public bool IsApproximate => UsesSpacewarps || IsDrops || extras is not null;

    /// <summary>Approximate streak length for a drops particle: the stored tail distance, else the distance travelled in one frame.</summary>
    public float ApproximateStreakLength(in VfxParticle p) =>
        system.TailDistance is > 0 ? system.TailDistance.Value : p.Velocity.Length() / VfxTime.FramesPerSecond;

    /// <summary>The timeline frame of the last <see cref="Advance"/>.</summary>
    public float Frame { get; private set; }

    /// <summary>Live particles at <see cref="Frame"/>; valid until the next <see cref="Advance"/>.</summary>
    public ReadOnlySpan<VfxParticle> Particles => output.AsSpan(0, outputCount);

    /// <summary>The largest playback frame <see cref="Advance"/> simulates: one hour at 15 fps (review-findings-2 #3).</summary>
    public const float MaxTimelineFrame = 15 * 3600;

    private const int MaxSnapshots = 32;
    private long snapshotInterval = 256;
    private readonly List<Snapshot> snapshots = [];

    private sealed record Snapshot(long Steps, uint Rng, float Accumulator, Vector3 PrevEmitter, bool HasPrevEmitter,
        float[] Age, float[] Life, float[] Angle, Vector3[] Pos, Vector3[] Vel);

    private void TakeSnapshot()
    {
        if (snapshots.Count >= MaxSnapshots)
        {
            // Thin to every other snapshot and double the spacing: memory stays bounded, replay stays ~interval steps.
            snapshotInterval *= 2;
            snapshots.RemoveAll(s => s.Steps % snapshotInterval != 0);
            if (steps % snapshotInterval != 0) return;
        }
        snapshots.Add(new Snapshot(steps, rng, accumulator, prevEmitter, hasPrevEmitter,
            (float[])age.Clone(), (float[])life.Clone(), (float[])angle.Clone(), (Vector3[])pos.Clone(), (Vector3[])vel.Clone()));
    }

    /// <summary>Returns to the latest snapshot at or before <paramref name="target"/> steps (time zero when none).</summary>
    private void Restore(long target)
    {
        int k = snapshots.FindLastIndex(s => s.Steps <= target);
        var keep = snapshots.ToArray();
        var interval = snapshotInterval;
        Reset();
        snapshotInterval = interval;
        snapshots.AddRange(keep);
        if (k < 0) return;
        var s = keep[k];
        steps = s.Steps; rng = s.Rng; accumulator = s.Accumulator; prevEmitter = s.PrevEmitter; hasPrevEmitter = s.HasPrevEmitter;
        s.Age.CopyTo(age, 0); s.Life.CopyTo(life, 0); s.Angle.CopyTo(angle, 0); s.Pos.CopyTo(pos, 0); s.Vel.CopyTo(vel, 0);
    }

    /// <summary>Clears all particles and returns to time zero.</summary>
    public void Reset()
    {
        snapshots.Clear();
        snapshotInterval = 256;
        Array.Fill(age, -1f);
        steps = 0;
        rng = (uint)seed * 2654435761u ^ 0x9E3779B9u;
        if (rng == 0) rng = 1;
        accumulator = 0;
        hasPrevEmitter = false;
        outputCount = 0;
        Frame = 0;
    }

    /// <summary>
    /// Moves to playback time <paramref name="timelineFrame"/> (frames since the effect started, not wrapped):
    /// incremental forwards, restart and replay backwards.
    /// </summary>
    /// <remarks>
    /// A non-finite frame counts as 0, and frames are clamped to <see cref="MaxTimelineFrame"/> (one hour of playback),
    /// so one call costs at most that many frames of steps. Going backwards restores the nearest earlier snapshot
    /// (kept every <c>snapshotInterval</c> steps, at most <c>MaxSnapshots</c> of them) instead of replaying from 0.
    /// </remarks>
    public void Advance(float timelineFrame)
    {
        timelineFrame = float.IsFinite(timelineFrame) ? Math.Clamp(timelineFrame, 0, MaxTimelineFrame) : 0;
        long target = (long)MathF.Floor(timelineFrame / step);
        if (target < steps) Restore(target);
        while (steps < target)
        {
            Step();
            if (steps % snapshotInterval == 0) TakeSnapshot();
        }
        Frame = timelineFrame;
        Emit(timelineFrame, (timelineFrame - steps * step) / VfxTime.FramesPerSecond);
    }

    /// <summary>Simulation steps run since construction (a work counter for tests; restores do not count).</summary>
    internal long StepsRun { get; private set; }

    private void Step()
    {
        StepsRun++;
        float t1 = (steps + 1) * step;
        steps++;
        var state = VfxPlayback.Evaluate(mode, t1 / VfxTime.FramesPerSecond, end);
        float local = state.Frame - system.StartTime;
        if (local < 0) return;
        float dt = step / VfxTime.FramesPerSecond;
        for (int i = 0; i < age.Length; i++)
        {
            if (age[i] < 0) continue;
            age[i] += dt;
            if (age[i] >= life[i]) age[i] = -1;
        }
        for (int i = 0; i < age.Length; i++)
        {
            if (age[i] < 0) continue;
            Vector3 a = (flags & 2) != 0 ? new Vector3(0, -Gravity, 0) : Vector3.Zero;
            foreach (int w in warpIndices) a += WarpForce(w, state.Frame, pos[i]);
            if (extras is not null) a += extras.Acceleration;
            vel[i] += a * dt;
            pos[i] += vel[i] * dt;
        }
        var e = sampler.SampleEmitter(index, local);
        if (extras is not null) { StepExtras(extras, state, local, dt, e); return; }
        if (!hasPrevEmitter) { prevEmitter = e.Position; hasPrevEmitter = true; }
        if (state.Emitting && system.Frames.Length > 0 && local > system.Start && local < system.Frames.Length)
        {
            accumulator += e.BirthRate * step * VfxTime.TicksPerFrame;
            int n = (int)MathF.Floor(accumulator);
            accumulator -= n;
            int slot = 0;
            for (int j = 0; j < n; j++)
            {
                while (slot < age.Length && age[slot] >= 0) slot++;
                if (slot >= age.Length) break;
                Birth(slot, e, Vector3.Lerp(e.Position, prevEmitter, (n - j) / (float)n), dt);
            }
        }
        prevEmitter = e.Position;
    }

    /// <summary>Births with <see cref="VfxParticleExtras"/>: rate per second over the effect's own length, at the emitter, age 0.</summary>
    private void StepExtras(VfxParticleExtras x, VfxPlaybackState state, float local, float dt, VfxEmitterSample e)
    {
        float seconds = local / VfxTime.FramesPerSecond;
        if (!state.Emitting || local >= end) return;
        if (x.Pose is { } pose)
        {
            var (p, q) = pose(seconds);
            e = e with { Position = p, Orientation = q };
        }
        float rate = x.BirthRate is { } br ? br(seconds) : e.BirthRate * VfxTime.TicksPerSecond;
        accumulator += (float.IsFinite(rate) ? MathF.Max(0, rate) : 0) * dt;
        int n = (int)MathF.Floor(accumulator);
        accumulator -= n;
        float speed = x.Speed is { } sp ? sp(seconds) : e.Speed;
        int slot = 0;
        for (int j = 0; j < n; j++)
        {
            while (slot < age.Length && age[slot] >= 0) slot++;
            if (slot >= age.Length) break;
            BirthExtras(slot, x, e, speed);
        }
    }

    private void BirthExtras(int i, VfxParticleExtras x, in VfxEmitterSample e, float speed)
    {
        life[i] = MathF.Max(1e-4f, system.Lifetime / (float)VfxTime.TicksPerSecond * (1 + Jitter(system.LifetimeVariation)));
        if (x.SpeedVariance != 0) speed *= 1 + Jitter(x.SpeedVariance);
        var axis = Vector3.Transform(-Vector3.UnitY, e.Orientation);
        Vector3 dir;
        if (system.EmitterType == 1)
        {
            var d0 = x.SpawnCone <= -1 ? RandomUnit() : Cone(axis, x.SpawnCone);
            pos[i] = e.Position + d0 * e.Width;
            dir = x.Spread >= 1 ? d0 : Cone(d0, x.Spread);
        }
        else
        {
            var r = new Vector3(Uniform(-e.Width / 2, e.Width / 2), 0, Uniform(-e.Height / 2, e.Height / 2));
            pos[i] = Vector3.Transform(r, e.Orientation) + e.Position;
            dir = x.Spread >= 1 ? axis : Cone(axis, x.Spread);
        }
        vel[i] = float.IsFinite(speed) ? dir * speed : Vector3.Zero;
        angle[i] = (flags & 0x10) != 0 ? Uniform(0, MathF.Tau) : 0;
        age[i] = 0;
    }

    /// <summary>A random direction within the cone around <paramref name="axis"/> whose half angle has cosine <paramref name="cosine"/>, uniform over the cap.</summary>
    private Vector3 Cone(Vector3 axis, float cosine)
    {
        float lengthSquared = axis.LengthSquared();
        var z = lengthSquared > 1e-12f && float.IsFinite(lengthSquared) ? axis / MathF.Sqrt(lengthSquared) : -Vector3.UnitY;
        float c = float.IsFinite(cosine) ? Math.Clamp(cosine, -1, 1) : 1;
        float h = Uniform(c, 1), t = Uniform(0, MathF.Tau), r = MathF.Sqrt(MathF.Max(0, 1 - h * h));
        var helper = MathF.Abs(z.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(helper, z));
        var v = Vector3.Cross(z, u);
        return u * (r * MathF.Cos(t)) + v * (r * MathF.Sin(t)) + z * h;
    }

    private void Birth(int i, in VfxEmitterSample e, Vector3 origin, float dt)
    {
        float sv = e.SpeedVariation;
        if (system.EmitterType == 1)
        {
            var d = RandomUnit();
            vel[i] = d * (e.Speed + Jitter(sv));
            pos[i] = d * e.Width + origin;
        }
        else
        {
            var v = sv == 0 ? new Vector3(0, -e.Speed, 0) : new Vector3(Jitter(sv), Jitter(sv) - e.Speed, Jitter(sv));
            vel[i] = Vector3.Transform(v, e.Orientation);
            var r = new Vector3(Uniform(-e.Width / 2, e.Width / 2), 0, Uniform(-e.Height / 2, e.Height / 2));
            pos[i] = Vector3.Transform(r, e.Orientation) + origin;
        }
        angle[i] = (flags & 0x10) != 0 ? Uniform(0, MathF.Tau) : 0;
        float a0 = Uniform(0, dt);
        pos[i] += vel[i] * a0;
        age[i] = a0;
        float l = system.Lifetime;
        life[i] = MathF.Max(1e-4f, (l + Jitter(l * system.LifetimeVariation)) / VfxTime.TicksPerSecond);
    }

    private void Emit(float timelineFrame, float extra)
    {
        var state = VfxPlayback.Evaluate(mode, timelineFrame / VfxTime.FramesPerSecond, end);
        var e = sampler.SampleEmitter(index, state.Frame - system.StartTime);
        var tex = material >= 0 ? sampler.Materials[material].Texture0 : null;
        int effectTex = VfxSampler.TextureFrame(tex, state.Frame);
        outputCount = 0;
        for (int i = 0; i < age.Length; i++)
        {
            if (age[i] < 0) continue;
            float f = Math.Clamp((age[i] + extra) / life[i], 0, 1);
            output[outputCount++] = new VfxParticle(pos[i] + vel[i] * extra, vel[i],
                2 * e.DropSize * Shape(f, shrinkBirth, shrinkDeath), Math.Clamp(e.Opacity * Shape(f, fadeBirth, fadeDeath), 0, 1),
                (flags & 4) != 0 ? VfxSampler.TextureFrameByLife(tex, f) : effectTex, angle[i], f);
        }
    }

    // Approximation: radial (type 0) or planar (type 1) push with exponential falloff, plus value-noise turbulence.
    private Vector3 WarpForce(int w, float frame, Vector3 p)
    {
        var s = sampler.SampleSpacewarp(w, frame);
        if (s is not { } ws) return Vector3.Zero;
        Vector3 dir; float d;
        if (sampler.Spacewarps[w].Type == 1)
        {
            dir = Vector3.Transform(Vector3.UnitY, ws.Orientation);
            d = MathF.Abs(Vector3.Dot(p - ws.Position, dir));
        }
        else
        {
            var off = p - ws.Position;
            d = off.Length();
            dir = d > 1e-6f ? off / d : Vector3.UnitY;
        }
        var force = dir * ws.Strength * MathF.Exp(-ws.Decay * d);
        if (ws.Turbulence != 0)
        {
            var q = p * (ws.Frequency * ws.Scale * 48f / 16f);
            force += ws.Turbulence * new Vector3(Noise(q), Noise(q + new Vector3(31.4f, 0, 0)), Noise(q + new Vector3(0, 47.2f, 0)));
        }
        return force;
    }

    private static float Noise(Vector3 p)
    {
        float x = MathF.Floor(p.X), y = MathF.Floor(p.Y), z = MathF.Floor(p.Z);
        float fx = p.X - x, fy = p.Y - y, fz = p.Z - z;
        float Corner(float dx, float dy, float dz)
        {
            uint h = (uint)(int)(x + dx) * 73856093u ^ (uint)(int)(y + dy) * 19349663u ^ (uint)(int)(z + dz) * 83492791u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return (h & 0xFFFF) / 32767.5f - 1;
        }
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(Corner(0, 0, 0), Corner(1, 0, 0), fx), L(Corner(0, 1, 0), Corner(1, 1, 0), fx), fy),
                 L(L(Corner(0, 0, 1), Corner(1, 0, 1), fx), L(Corner(0, 1, 1), Corner(1, 1, 1), fx), fy), fz);
    }

    /// <summary>Shrink/fade curve: ramps up until birth, holds 1, ramps down after death (fractions of life).</summary>
    private static float Shape(float f, float birth, float death) =>
        f < birth ? f / birth : f > death ? (1 - f) / (1 - death) : 1;

    private static (float Birth, float Death) Curve(Vector2 v)
    {
        float b = v.X > 1 ? v.X * 0.01f : v.X, d = v.Y > 1 ? v.Y * 0.01f : v.Y;
        if (b == 0 && d == 0) d = 1;
        return (b, d);
    }

    private float Next()
    {
        uint x = rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    private float Uniform(float lo, float hi) => lo + (hi - lo) * Next();

    private float Jitter(float a) => Uniform(-a, a);

    private Vector3 RandomUnit()
    {
        float z = Uniform(-1, 1), t = Uniform(0, MathF.Tau), r = MathF.Sqrt(MathF.Max(0, 1 - z * z));
        return new Vector3(r * MathF.Cos(t), r * MathF.Sin(t), z);
    }
}
