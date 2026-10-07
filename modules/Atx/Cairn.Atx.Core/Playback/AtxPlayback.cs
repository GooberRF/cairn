using Cairn.Atx.Model;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Playback;

/// <summary>Everything playback needs from a document.</summary>
/// <param name="Mode">The animation mode.</param>
/// <param name="InitiallyOn">Whether the controller starts playing.</param>
/// <param name="BaseFrameTimeMs">The header frame time, already clamped to at least 1.</param>
/// <param name="FrameTimeOverridesMs">Per-frame overrides; null entries inherit the base time.</param>
public sealed record PlaybackSpec(
    AtxAnimationMode Mode,
    bool InitiallyOn,
    int BaseFrameTimeMs,
    IReadOnlyList<int?> FrameTimeOverridesMs)
{
    /// <summary>Number of frames.</summary>
    public int FrameCount => FrameTimeOverridesMs.Count;

    /// <summary>Builds a spec from a parsed document, applying the same clamps the game applies.</summary>
    public static PlaybackSpec FromModel(AtxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new PlaybackSpec(
            model.Header.EffectiveAnimationMode,
            model.Header.EffectiveInitiallyOn,
            model.Header.EffectiveFrameTimeMs,
            [.. model.Frames.Select(f => f.FrameTimeOverrideMs)]);
    }
}

/// <summary>
/// A line-for-line port of the controller state in <c>atx_do_frame</c> and the ATX level events,
/// so the preview's timing, ping-pong direction and Play Once hold behave exactly like the game's.
/// </summary>
public sealed class AtxPlayback
{
    /// <summary>
    /// How long one cycle of <paramref name="model"/> lasts, in milliseconds, with the meaning
    /// <c>atx_do_frame</c> gives each mode: Loop is the sum of every frame time, Ping-Pong also
    /// pays for the way back down, Play Once ends when it reaches the last frame (which it then
    /// holds), and Static never advances at all. A single frame never animates in any mode.
    /// </summary>
    public static long CycleDurationMs(AtxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        int n = model.Frames.Count;
        if (n < 2 || model.Header.EffectiveAnimationMode == AtxAnimationMode.Static) return 0;

        // Accumulate in 64 bits: a few thousand frames each asking for close to int.MaxValue ms is
        // a silly file, but it must still report a silly duration rather than a negative one.
        long total = 0;
        for (int i = 0; i < n; i++) total += model.FrameTimeMs(i);
        switch (model.Header.EffectiveAnimationMode)
        {
            case AtxAnimationMode.PingPong:
                // Frames 1..n-2 are shown a second time on the way back.
                for (int i = 1; i < n - 1; i++) total += model.FrameTimeMs(i);
                break;
            case AtxAnimationMode.PlayOnce:
                // The last frame is held rather than timed, so it is not part of the run.
                total -= model.FrameTimeMs(n - 1);
                break;
        }
        return total;
    }

    private readonly PlaybackSpec _spec;

    public AtxPlayback(PlaybackSpec spec)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        BaseFrameTimeMs = Math.Max(AtxSchema.MinFrameTimeMs, spec.BaseFrameTimeMs);
        Reset();
    }

    /// <summary>The spec this controller plays.</summary>
    public PlaybackSpec Spec => _spec;

    /// <summary>The frame currently showing, 0-based.</summary>
    public int CurrentFrame { get; private set; }

    /// <summary>Ping-pong direction: +1 forward, -1 backward.</summary>
    public int Direction { get; private set; } = 1;

    /// <summary>Whether frames are advancing.</summary>
    public bool Playing { get; private set; }

    /// <summary>How long the current frame has been showing, in seconds.</summary>
    public float TimeInFrameSeconds { get; private set; }

    /// <summary>The texture-wide frame time, which <c>ATX_Set_Frame_Time</c> can change at runtime.</summary>
    public int BaseFrameTimeMs { get; private set; }

    /// <summary>Number of frames.</summary>
    public int FrameCount => _spec.FrameCount;

    /// <summary>Returns the controller to its level-load state.</summary>
    public void Reset()
    {
        Playing = _spec.Mode != AtxAnimationMode.Static && _spec.InitiallyOn;
        CurrentFrame = 0;
        Direction = 1;
        TimeInFrameSeconds = 0f;
    }

    /// <summary>Effective time for a frame, exactly as <c>frame_time_for</c> computes it.</summary>
    public int FrameTimeMs(int index)
    {
        if (index < 0 || index >= FrameCount) return BaseFrameTimeMs;
        int overrideMs = _spec.FrameTimeOverridesMs[index] ?? -1;
        return overrideMs > 0 ? overrideMs : BaseFrameTimeMs;
    }

    /// <summary>
    /// Advances by <paramref name="deltaSeconds"/>, returning true when the visible frame changed.
    /// Like the game, this caps the inner loop at two full cycles so a long stall cannot spin, and
    /// discards the leftover time when the cap is hit.
    /// </summary>
    public bool Advance(double deltaSeconds)
    {
        float dt = (float)deltaSeconds;
        if (dt <= 0f) return false;
        if (_spec.Mode == AtxAnimationMode.Static) return false;
        if (!Playing) return false;
        int n = FrameCount;
        if (n < 2) return false;

        TimeInFrameSeconds += dt;
        int previous = CurrentFrame;

        float frameSeconds = FrameTimeMs(CurrentFrame) / 1000.0f;
        if (frameSeconds <= 0f) return false;

        int maxAdvances = Math.Max(2, n * 2);
        int advances = 0;
        bool stopAdvancing = false;
        while (TimeInFrameSeconds >= frameSeconds)
        {
            if (advances++ >= maxAdvances)
            {
                TimeInFrameSeconds = 0f;
                break;
            }
            TimeInFrameSeconds -= frameSeconds;
            switch (_spec.Mode)
            {
                case AtxAnimationMode.Loop:
                    CurrentFrame = (CurrentFrame + 1) % n;
                    break;
                case AtxAnimationMode.PingPong:
                    CurrentFrame += Direction;
                    if (CurrentFrame >= n - 1)
                    {
                        CurrentFrame = n - 1;
                        Direction = -1;
                    }
                    else if (CurrentFrame <= 0)
                    {
                        CurrentFrame = 0;
                        Direction = 1;
                    }
                    break;
                case AtxAnimationMode.PlayOnce:
                    if (CurrentFrame < n - 1)
                    {
                        CurrentFrame++;
                    }
                    else
                    {
                        Playing = false;
                        TimeInFrameSeconds = 0f;
                        stopAdvancing = true;
                    }
                    break;
                case AtxAnimationMode.Static:
                    break;
            }
            if (stopAdvancing) break;
            frameSeconds = FrameTimeMs(CurrentFrame) / 1000.0f;
            if (frameSeconds <= 0f) break;
        }

        return CurrentFrame != previous;
    }

    /// <summary>Mirrors <c>ATX_Set_Frame</c>: out-of-range indices are rejected.</summary>
    public bool SetFrame(int index)
    {
        if (index < 0 || index >= FrameCount) return false;
        if (CurrentFrame != index)
        {
            CurrentFrame = index;
            TimeInFrameSeconds = 0f;
        }
        return true;
    }

    /// <summary>Mirrors <c>ATX_Play</c>.</summary>
    public void Play() => Playing = true;

    /// <summary>Mirrors <c>ATX_Pause</c>.</summary>
    public void Pause() => Playing = false;

    /// <summary>Mirrors <c>ATX_Set_Frame_Time</c>, including its clamp to at least 1 ms.</summary>
    public void SetFrameTime(int milliseconds) =>
        BaseFrameTimeMs = Math.Max(AtxSchema.MinFrameTimeMs, milliseconds);

    // ── Timeline helpers for the transport bar ────────────────────────────────

    /// <summary>
    /// The frames one full cycle visits, in order. Loop and Play Once run 0..n-1; Ping-Pong runs
    /// 0..n-1 and back down to 1; Static is a single frame.
    /// </summary>
    public IReadOnlyList<int> TimelineFrames => BuildTimeline(_spec.Mode, FrameCount);

    /// <summary>Start time of each step of <see cref="TimelineFrames"/>, in seconds.</summary>
    public IReadOnlyList<double> TimelineOffsets
    {
        get
        {
            var frames = TimelineFrames;
            var offsets = new double[frames.Count];
            double t = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                offsets[i] = t;
                t += FrameTimeMs(frames[i]) / 1000.0;
            }
            return offsets;
        }
    }

    /// <summary>
    /// Start time of each frame when played straight through, in seconds. Useful for drawing
    /// per-frame tick widths in the frames list.
    /// </summary>
    public IReadOnlyList<double> FrameStartOffsets
    {
        get
        {
            var offsets = new double[FrameCount];
            double t = 0;
            for (int i = 0; i < FrameCount; i++)
            {
                offsets[i] = t;
                t += FrameTimeMs(i) / 1000.0;
            }
            return offsets;
        }
    }

    /// <summary>
    /// How long one full cycle takes, in seconds: the sum of the frame times for Loop, the sum
    /// without the held last frame for Play Once, and the full there-and-back sum for Ping-Pong.
    /// Static has no duration.
    /// </summary>
    public double TotalLoopDurationSeconds
    {
        get
        {
            if (_spec.Mode == AtxAnimationMode.Static || FrameCount == 0) return 0;
            var frames = TimelineFrames;
            double total = 0;
            foreach (int f in frames) total += FrameTimeMs(f) / 1000.0;
            if (_spec.Mode == AtxAnimationMode.PlayOnce && FrameCount > 0)
                total -= FrameTimeMs(FrameCount - 1) / 1000.0;
            return total;
        }
    }

    /// <summary>
    /// Jumps to the frame that would be showing <paramref name="seconds"/> into the cycle, and
    /// sets the direction Ping-Pong would have at that point.
    /// </summary>
    public void Seek(double seconds)
    {
        if (FrameCount == 0) return;
        var frames = TimelineFrames;
        if (frames.Count == 0) return;

        double cycle = 0;
        foreach (int f in frames) cycle += FrameTimeMs(f) / 1000.0;

        if (_spec.Mode == AtxAnimationMode.PlayOnce)
        {
            seconds = Math.Max(0, seconds);
        }
        else if (cycle > 0)
        {
            seconds -= Math.Floor(seconds / cycle) * cycle;
        }

        double t = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            double length = FrameTimeMs(frames[i]) / 1000.0;
            if (seconds < t + length || i == frames.Count - 1)
            {
                CurrentFrame = frames[i];
                Direction = DirectionAt(frames, i);
                TimeInFrameSeconds = (float)Math.Max(0, Math.Min(seconds - t, length));
                return;
            }
            t += length;
        }
    }

    /// <summary>
    /// The direction the game's controller would be carrying at step <paramref name="step"/> of the
    /// timeline. <c>atx_do_frame</c> flips the direction in the same statement that lands on an end
    /// frame, so sitting on the last frame always means "about to go back" and sitting on frame 0
    /// always means "about to go forward" — which matters for a two-frame ping-pong, where every
    /// frame is an end frame and reading the direction off the timeline alone would stall a step.
    /// </summary>
    private int DirectionAt(IReadOnlyList<int> frames, int step)
    {
        if (_spec.Mode == AtxAnimationMode.PingPong && FrameCount >= 2)
        {
            if (frames[step] == FrameCount - 1) return -1;
            if (frames[step] == 0) return 1;
        }
        return step > 0 && frames[step] < frames[step - 1] ? -1 : 1;
    }

    private static IReadOnlyList<int> BuildTimeline(AtxAnimationMode mode, int count)
    {
        if (count <= 0) return [];
        if (count == 1 || mode == AtxAnimationMode.Static) return [0];
        var list = new List<int>(count * 2);
        for (int i = 0; i < count; i++) list.Add(i);
        if (mode == AtxAnimationMode.PingPong)
        {
            for (int i = count - 2; i >= 1; i--) list.Add(i);
        }
        return list;
    }
}
