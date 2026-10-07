namespace Cairn.Vfx.Animation;

/// <summary>How the game plays an effect: ambient effects loop, one-shot effects vanish at the end (live particles finish), or hold the last frame.</summary>
public enum VfxPlaybackMode { Loop, OneShot, HoldLastFrame }

/// <summary>The engine state at a playback time.</summary>
/// <param name="Frame">Effect frame (15 fps units) to sample meshes, materials, dummies and lights at.</param>
/// <param name="MeshesVisible">False once a one-shot has finished (meshes and lights stop drawing).</param>
/// <param name="Emitting">False once a one-shot or hold has finished: emitters stop, live particles carry on.</param>
/// <param name="Loop">How many times a looping effect has wrapped.</param>
public readonly record struct VfxPlaybackState(float Frame, bool MeshesVisible, bool Emitting, int Loop);

/// <summary>Maps a playback time to effect time the way the game's per-tick update does.</summary>
public static class VfxPlayback
{
    /// <summary>The state at <paramref name="seconds"/> since the effect started, for an effect of <paramref name="endFrame"/> frames.</summary>
    public static VfxPlaybackState Evaluate(VfxPlaybackMode mode, double seconds, int endFrame)
    {
        double frame = Math.Max(0, seconds) * VfxTime.FramesPerSecond;
        if (endFrame <= 0) return new((float)frame, mode != VfxPlaybackMode.OneShot, true, 0);
        if (frame < endFrame) return new((float)frame, true, true, 0);
        switch (mode)
        {
            case VfxPlaybackMode.Loop:
                int loop = (int)Math.Floor(frame / endFrame);
                return new((float)(frame - (double)loop * endFrame), true, true, loop);
            case VfxPlaybackMode.HoldLastFrame:
                return new(endFrame, true, false, 0);
            default:
                return new(endFrame, false, false, 0);
        }
    }
}

/// <summary>A preview behaviour that only approximates the game.</summary>
public sealed record VfxEngineNote(string Id, string Text);

/// <summary>Behaviours the preview approximates, for display in the UI.</summary>
public static class VfxEngineNotes
{
    public static readonly IReadOnlyList<VfxEngineNote> All =
    [
        new("spacewarp-force", "Spacewarp forces (falloff, sign, turbulence noise and the meaning of the warp type) are approximated."),
        new("drops-streak", "Drop particles are drawn as approximate streaks along their velocity; the tail length is estimated."),
        new("sprite-size", "Particle and facing sprite sizes assume the stored size is a half extent."),
        new("rand-sphere", "Sphere emitters assume a uniformly random unit direction."),
        new("random-sequence", "Particle randomness uses the preview's own seeded generator, so individual particles differ from the game."),
        new("no-host", "The effect is previewed without a host object: no attachment tag transform and no inherited host velocity."),
        new("no-lighting", "Level lighting is not applied; the sky, see-through and corona mesh flags have no effect."),
        new("no-cull", "The particle no-cull flag is ignored."),
        new("uv-stepped", "Per-frame UVs are stepped to the current frame rather than blended."),
        new("pivot-order", "Keyframed meshes apply the pivot transform before the animated transform; parent links are not composed."),
        new("transform-lerp", "Meshes animated by per-frame transforms blend the transformed results linearly between frames."),
        new("ease-units", "Rotation ease-in and ease-out values are used as stored, assumed to be 0..1 fractions."),
        new("hold-last", "Hold-last-frame shows the final frame rather than the last update before the end."),
    ];
}
