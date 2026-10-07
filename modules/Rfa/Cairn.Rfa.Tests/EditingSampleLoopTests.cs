using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.SampleGen;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Make loopable on the generated sample walk (samples/rfa/sample_figure_walk.rfa). The walk is built as a
/// closed cycle (every track's last key repeats its first), so blending the end into the start has nothing to
/// change: the in-app self-test's "the test parameters change the clip" cannot hold for it.
/// </summary>
public class EditingSampleLoopTests
{
    [Fact]
    public void SampleWalkAlreadyLoopsSoBlendingTheEndChangesNothing()
    {
        // As the app opens it: the written file read back (rotations quantised as stored).
        var walk = RfaReader.Read(RfaWriter.Write(SampleClips.Walk()), SampleSet.WalkFile);
        // Precondition: every track ends on its first key's value.
        foreach (var track in walk.Bones)
        {
            if (track.RotationKeys.Length > 1)
                Assert.Equal(ClipEdit.KeyRotation(track.RotationKeys[0]), ClipEdit.KeyRotation(track.RotationKeys[^1]));
            if (track.PositionKeys.Length > 1)
                Assert.Equal(track.PositionKeys[0].Position, track.PositionKeys[^1].Position);
        }
        // Keys every 4 frames: a window of up to 4 frames holds only the end key, which already equals the
        // first, so the self-test's 4-frame blend changes nothing (with or without root-motion options).
        foreach (int frames in new[] { 0, 4 })
        {
            foreach (var options in new LoopOptions?[] { null, new(0), new(0, RootMotionAxes.All) })
            {
                var looped = ClipEdit.MakeLoopable(walk, frames * RfaClip.TicksPerFrame, LoopMode.BlendEndToStart, options);
                Assert.Equal(RfaWriter.Write(walk), RfaWriter.Write(looped));
            }
        }
        // A wider window reaches the keys before the end and blends them towards the start.
        var wide = ClipEdit.MakeLoopable(walk, 8 * RfaClip.TicksPerFrame, LoopMode.BlendEndToStart);
        Assert.NotEqual(RfaWriter.Write(walk), RfaWriter.Write(wide));
    }

    [Fact]
    public void AClipThatDoesNotLoopIsChanged()
    {
        var clip = EditingTestClips.Make();
        var looped = ClipEdit.MakeLoopable(clip, 4 * RfaClip.TicksPerFrame, LoopMode.BlendEndToStart);
        Assert.NotEqual(RfaWriter.Write(clip), RfaWriter.Write(looped));
    }
}
