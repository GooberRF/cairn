using System.Globalization;
using Cairn.Atx.Model;
using Cairn.Atx.Playback;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Fuzz;

/// <summary>Independent transliteration of the game's atx_do_frame controller.</summary>
public sealed class RefController
{
    private readonly AtxAnimationMode _mode;
    private readonly int[] _times;   // already resolved per frame (override>0 ? override : base)
    public int Frame, Dir = 1;
    public bool Playing;
    public float T;

    public RefController(AtxAnimationMode mode, bool initiallyOn, int baseMs, IReadOnlyList<int?> ovr)
    {
        _mode = mode;
        int b = Math.Max(1, baseMs);
        _times = ovr.Select(o => (o ?? -1) > 0 ? o!.Value : b).ToArray();
        Playing = mode != AtxAnimationMode.Static && initiallyOn;
    }

    public bool Advance(double deltaSeconds)
    {
        float dt = (float)deltaSeconds;
        if (dt <= 0f) return false;
        if (_mode == AtxAnimationMode.Static) return false;
        if (!Playing) return false;
        int n = _times.Length;
        if (n < 2) return false;

        T += dt;
        int prev = Frame;
        float fs = _times[Frame] / 1000.0f;
        if (fs <= 0f) return false;
        int maxAdv = Math.Max(2, n * 2), adv = 0;
        bool stop = false;
        while (T >= fs)
        {
            if (adv++ >= maxAdv) { T = 0f; break; }
            T -= fs;
            switch (_mode)
            {
                case AtxAnimationMode.Loop: Frame = (Frame + 1) % n; break;
                case AtxAnimationMode.PingPong:
                    Frame += Dir;
                    if (Frame >= n - 1) { Frame = n - 1; Dir = -1; }
                    else if (Frame <= 0) { Frame = 0; Dir = 1; }
                    break;
                case AtxAnimationMode.PlayOnce:
                    if (Frame < n - 1) Frame++;
                    else { Playing = false; T = 0f; stop = true; }
                    break;
            }
            if (stop) break;
            fs = _times[Frame] / 1000.0f;
            if (fs <= 0f) break;
        }
        return Frame != prev;
    }
}

public static class PlaybackFuzz
{
    public static void Run(int iterations)
    {
        for (int seed = 1; seed <= iterations; seed++)
        {
            var r = new Rng(seed * 104729);
            int n = r.Next(101) switch { < 10 => 0, < 25 => 1, < 60 => r.Next(2, 6), < 95 => r.Next(2, 40), _ => 2000 };
            var mode = (AtxAnimationMode)r.Next(4);
            bool io = r.Chance(70);
            int baseMs = r.Next(100) switch { < 70 => r.Next(1, 500), < 85 => r.Next(-5, 2), _ => int.MaxValue };
            var ovr = new List<int?>();
            for (int i = 0; i < n; i++)
                ovr.Add(r.Chance(40) ? (r.Chance(90) ? r.Next(1, 1000) : (r.Chance(50) ? int.MaxValue : r.Next(-5, 1))) : null);

            var spec = new PlaybackSpec(mode, io, baseMs, ovr);
            AtxPlayback pb;
            Program.Cases++;
            try { pb = new AtxPlayback(spec); }
            catch (Exception ex) { Program.Report("PB-CTOR", ex.ToString(), Desc(mode, io, baseMs, ovr), "ctor", seed); continue; }
            var reference = new RefController(mode, io, baseMs, ovr);

            int steps = r.Next(1, 60);
            for (int s = 0; s < steps; s++)
            {
                double dt = r.Next(100) switch
                {
                    < 55 => r.NextDouble() * 0.1,
                    < 65 => 0,
                    < 72 => -r.NextDouble(),
                    < 80 => r.NextDouble() * 1e6,
                    < 86 => double.NaN,
                    < 92 => double.PositiveInfinity,
                    < 96 => double.NegativeInfinity,
                    _ => 1e-40,
                };
                Program.Cases++;
                try { pb.Advance(dt); } catch (Exception ex)
                { Program.Report("PB-ADVANCE-THROW", ex.ToString(), Desc(mode, io, baseMs, ovr), "advance " + dt, seed); break; }
                reference.Advance(dt);

                if (pb.CurrentFrame < 0 || (n > 0 && pb.CurrentFrame >= n) || (n == 0 && pb.CurrentFrame != 0))
                    Program.Report("PB-RANGE", $"CurrentFrame {pb.CurrentFrame} with {n} frames after dt {dt}",
                        Desc(mode, io, baseMs, ovr), "advance", seed);
                if (pb.CurrentFrame != reference.Frame || pb.Playing != reference.Playing || pb.Direction != reference.Dir)
                    Program.Report("PB-PARITY",
                        $"after dt {dt}: frame {pb.CurrentFrame} vs {reference.Frame}, playing {pb.Playing} vs {reference.Playing}, dir {pb.Direction} vs {reference.Dir}",
                        Desc(mode, io, baseMs, ovr), "advance", seed);

                if (r.Chance(15))
                {
                    double target = r.Next(100) switch
                    {
                        < 50 => r.NextDouble() * 10,
                        < 60 => -r.NextDouble() * 10,
                        < 75 => 1e12,
                        < 85 => double.NaN,
                        _ => double.PositiveInfinity,
                    };
                    Program.Cases++;
                    try { pb.Seek(target); } catch (Exception ex)
                    { Program.Report("PB-SEEK-THROW", ex.ToString(), Desc(mode, io, baseMs, ovr), "seek " + target, seed); break; }
                    if (pb.CurrentFrame < 0 || (n > 0 && pb.CurrentFrame >= n))
                        Program.Report("PB-SEEK-RANGE", $"Seek({target}) -> frame {pb.CurrentFrame} of {n}",
                            Desc(mode, io, baseMs, ovr), "seek", seed);
                    // resync the reference to whatever the controller now shows
                    reference.Frame = pb.CurrentFrame; reference.Dir = pb.Direction; reference.T = 0;
                    pb.GetType(); // no-op
                    typeof(AtxPlayback).GetProperty("TimeInFrameSeconds")!.GetValue(pb);
                    reference.T = pb.TimeInFrameSeconds;
                    reference.Playing = pb.Playing;
                }
            }

            Program.Cases++;
            try
            {
                _ = pb.TimelineFrames; _ = pb.TimelineOffsets; _ = pb.FrameStartOffsets; _ = pb.TotalLoopDurationSeconds;
                foreach (double o in pb.TimelineOffsets)
                    if (double.IsNaN(o) || double.IsInfinity(o) || o < 0)
                        Program.Report("PB-OFFSETS", $"offset {o}", Desc(mode, io, baseMs, ovr), "offsets", seed);
            }
            catch (Exception ex) { Program.Report("PB-TIMELINE-THROW", ex.ToString(), Desc(mode, io, baseMs, ovr), "timeline", seed); }

            // CycleDurationMs overflow.
            Program.Cases++;
            var model = new AtxModel
            {
                Header = new AtxHeader { IsPresent = true },
                Frames = Enumerable.Range(0, n).Select(i => new AtxFrame { Index = i }).ToList(),
            };
            try
            {
                long cyc = AtxPlayback.CycleDurationMs(model);
                if (cyc < 0) Program.Report("CYCLE-NEGATIVE", $"CycleDurationMs = {cyc} for {n} frames", Desc(mode, io, baseMs, ovr), "cycle", seed);
            }
            catch (Exception ex) { Program.Report("CYCLE-THROW", ex.ToString(), Desc(mode, io, baseMs, ovr), "cycle", seed); }
        }

        // Targeted overflow: 2000 frames x int.MaxValue.
        {
            var frames = Enumerable.Range(0, 2000).Select(i => new AtxFrame
            {
                Index = i,
                FrameTime = new Located<long>(int.MaxValue, true, default, default, default, TomlValueKind.Integer, "x"),
            }).ToList();
            var m = new AtxModel
            {
                Header = new AtxHeader
                {
                    IsPresent = true,
                    AnimationMode = new Located<long>(2, true, default, default, default, TomlValueKind.Integer, "2"),
                },
                Frames = frames,
            };
            Program.Cases++;
            try
            {
                long cyc = AtxPlayback.CycleDurationMs(m);
                if (cyc <= 0)
                    Program.Report("CYCLE-OVERFLOW",
                        $"CycleDurationMs for 2000 frames of int.MaxValue ms returned {cyc} (int overflow)",
                        "2000 frames x int.MaxValue, animation_mode = 2", "CycleDurationMs", 0);
            }
            catch (Exception ex)
            { Program.Report("CYCLE-OVERFLOW-THROW", ex.ToString(), "2000 x int.MaxValue", "CycleDurationMs", 0); }
        }

        Console.WriteLine($"[playback] {iterations} controller runs");
    }

    private static string Desc(AtxAnimationMode m, bool io, int b, IReadOnlyList<int?> o) =>
        $"mode={m} initially_on={io} base={b} frames=[{string.Join(",", o.Select(x => x?.ToString(CultureInfo.InvariantCulture) ?? "-"))}]";
}
