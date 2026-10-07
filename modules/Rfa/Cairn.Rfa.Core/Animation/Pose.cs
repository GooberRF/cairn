using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Animation;

/// <summary>
/// Reusable per-frame buffers for one skeleton's pose: a local and a world transform per bone. Built
/// once per document and refilled every frame, so playback does not allocate.
/// </summary>
public sealed class Pose
{
    /// <param name="skeleton">The skeleton this pose belongs to.</param>
    public Pose(Skeleton skeleton)
    {
        Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        Local = new Rigid[skeleton.Count];
        World = new Rigid[skeleton.Count];
        ResetToRest();
    }

    /// <summary>The skeleton the buffers are sized for.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>Number of bones.</summary>
    public int Count => Local.Length;

    /// <summary>Each bone's transform relative to its parent (model space for roots).</summary>
    public Rigid[] Local { get; }

    /// <summary>Each bone's model-space transform, valid after <see cref="SolveWorld"/>.</summary>
    public Rigid[] World { get; }

    /// <summary>Sets every local to the bind pose and solves.</summary>
    public void ResetToRest()
    {
        Skeleton.RestLocal.CopyTo(Local);
        SolveWorld();
    }

    /// <summary>
    /// Samples a clip into the locals and solves. Bones the clip lacks keep their rest local (a
    /// bone-count mismatch is something to show, not to hide).
    /// </summary>
    public void Sample(RfaClip clip, float time)
    {
        ClipSampler.SampleLocals(clip, time, Local, Skeleton.RestLocal.AsSpan());
        SolveWorld();
    }

    /// <summary>Recomputes <see cref="World"/> from <see cref="Local"/>.</summary>
    public void SolveWorld() => ForwardKinematics.Solve(Skeleton, Local, World);
}
