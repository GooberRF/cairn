using Cairn.Atx.Parsing;
using Cairn.Atx.Playback;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Tests;

public class PlaybackTests
{
    private static AtxPlayback Make(AtxAnimationMode mode, int frames, int baseMs = 100,
        params (int Index, int Ms)[] overrides)
    {
        var times = new int?[frames];
        foreach (var (index, ms) in overrides) times[index] = ms;
        return new AtxPlayback(new PlaybackSpec(mode, true, baseMs, times));
    }

    [Fact]
    public void StaticNeverAdvances()
    {
        var playback = Make(AtxAnimationMode.Static, 4);
        Assert.False(playback.Playing);
        Assert.False(playback.Advance(10));
        Assert.Equal(0, playback.CurrentFrame);
    }

    [Fact]
    public void LoopWrapsAroundToZero()
    {
        var playback = Make(AtxAnimationMode.Loop, 3);
        Assert.True(playback.Playing);
        Assert.True(playback.Advance(0.1));
        Assert.Equal(1, playback.CurrentFrame);
        Assert.True(playback.Advance(0.1));
        Assert.Equal(2, playback.CurrentFrame);
        Assert.True(playback.Advance(0.1));
        Assert.Equal(0, playback.CurrentFrame);
    }

    [Fact]
    public void ShortStepsDoNotAdvanceUntilTheFrameTimeIsReached()
    {
        var playback = Make(AtxAnimationMode.Loop, 3);
        Assert.False(playback.Advance(0.04));
        Assert.False(playback.Advance(0.04));
        Assert.Equal(0, playback.CurrentFrame);
        Assert.True(playback.Advance(0.04));
        Assert.Equal(1, playback.CurrentFrame);
    }

    [Fact]
    public void PerFrameTimesAreHonoured()
    {
        var playback = Make(AtxAnimationMode.Loop, 3, 100, (1, 300));
        Assert.Equal(100, playback.FrameTimeMs(0));
        Assert.Equal(300, playback.FrameTimeMs(1));
        Assert.True(playback.Advance(0.1));
        Assert.Equal(1, playback.CurrentFrame);
        Assert.False(playback.Advance(0.2));   // frame 1 needs 300 ms
        Assert.Equal(1, playback.CurrentFrame);
        Assert.True(playback.Advance(0.1));
        Assert.Equal(2, playback.CurrentFrame);
    }

    [Fact]
    public void PingPongTurnsAroundAtBothEnds()
    {
        var playback = Make(AtxAnimationMode.PingPong, 3);
        Assert.Equal([1, 2, 1, 0, 1], Steps(playback, 5));
        Assert.Equal(1, playback.Direction);
    }

    [Fact]
    public void PingPongWithTwoFramesAlternates()
    {
        var playback = Make(AtxAnimationMode.PingPong, 2);
        Assert.Equal([1, 0, 1, 0], Steps(playback, 4));
    }

    [Fact]
    public void PlayOnceStopsAndHoldsTheLastFrame()
    {
        var playback = Make(AtxAnimationMode.PlayOnce, 3);
        Assert.True(playback.Advance(0.1));
        Assert.True(playback.Advance(0.1));
        Assert.Equal(2, playback.CurrentFrame);
        Assert.True(playback.Playing);

        Assert.False(playback.Advance(0.1));
        Assert.Equal(2, playback.CurrentFrame);
        Assert.False(playback.Playing);
        Assert.Equal(0f, playback.TimeInFrameSeconds);

        // It stays put no matter how long the level runs.
        Assert.False(playback.Advance(100));
        Assert.Equal(2, playback.CurrentFrame);
    }

    [Fact]
    public void SingleFrameTexturesNeverAdvance()
    {
        var playback = Make(AtxAnimationMode.Loop, 1);
        Assert.False(playback.Advance(10));
        Assert.Equal(0, playback.CurrentFrame);
    }

    [Fact]
    public void AHugeDeltaIsCappedAndDropsTheLeftover()
    {
        var playback = Make(AtxAnimationMode.Loop, 5, baseMs: 1);
        playback.Advance(600);
        // Two full cycles is the cap, and the leftover time is dropped rather than carried.
        Assert.Equal(0f, playback.TimeInFrameSeconds);
        Assert.InRange(playback.CurrentFrame, 0, 4);
        Assert.True(playback.Playing);
    }

    [Fact]
    public void ZeroOrNegativeDeltaDoesNothing()
    {
        var playback = Make(AtxAnimationMode.Loop, 3);
        Assert.False(playback.Advance(0));
        Assert.False(playback.Advance(-1));
        Assert.Equal(0f, playback.TimeInFrameSeconds);
    }

    [Fact]
    public void InitiallyOffStartsPaused()
    {
        var playback = new AtxPlayback(new PlaybackSpec(AtxAnimationMode.Loop, false, 100, [null, null]));
        Assert.False(playback.Playing);
        Assert.False(playback.Advance(1));
        playback.Play();
        Assert.True(playback.Advance(0.1));
        playback.Pause();
        Assert.False(playback.Advance(1));
    }

    [Fact]
    public void EventsMirrorTheGame()
    {
        var playback = Make(AtxAnimationMode.Loop, 3);
        Assert.False(playback.SetFrame(-1));
        Assert.False(playback.SetFrame(3));
        Assert.True(playback.SetFrame(2));
        Assert.Equal(2, playback.CurrentFrame);
        Assert.Equal(0f, playback.TimeInFrameSeconds);

        playback.SetFrameTime(0);
        Assert.Equal(AtxSchema.MinFrameTimeMs, playback.BaseFrameTimeMs);
        playback.SetFrameTime(250);
        Assert.Equal(250, playback.BaseFrameTimeMs);
        Assert.Equal(250, playback.FrameTimeMs(0));
    }

    [Fact]
    public void ResetReturnsToTheLevelLoadState()
    {
        var playback = Make(AtxAnimationMode.Loop, 3);
        playback.Advance(0.1);
        playback.Pause();
        playback.Reset();
        Assert.True(playback.Playing);
        Assert.Equal(0, playback.CurrentFrame);
        Assert.Equal(1, playback.Direction);
    }

    // ── Timeline helpers ─────────────────────────────────────────────────────

    [Fact]
    public void LoopDurationIsTheSumOfTheFrameTimes()
    {
        var playback = Make(AtxAnimationMode.Loop, 3, 100, (1, 300));
        Assert.Equal(0.5, playback.TotalLoopDurationSeconds, 5);
        Assert.Equal([0, 0.1, 0.4], playback.FrameStartOffsets.Select(o => Math.Round(o, 5)));
    }

    [Fact]
    public void PingPongDurationCoversTheWayBack()
    {
        var playback = Make(AtxAnimationMode.PingPong, 3, 100);
        Assert.Equal([0, 1, 2, 1], playback.TimelineFrames);
        Assert.Equal(0.4, playback.TotalLoopDurationSeconds, 5);

        var twoFrames = Make(AtxAnimationMode.PingPong, 2, 100);
        Assert.Equal([0, 1], twoFrames.TimelineFrames);
        Assert.Equal(0.2, twoFrames.TotalLoopDurationSeconds, 5);
    }

    [Fact]
    public void PlayOnceDurationExcludesTheHeldFrame()
    {
        var playback = Make(AtxAnimationMode.PlayOnce, 3, 100);
        Assert.Equal(0.2, playback.TotalLoopDurationSeconds, 5);
    }

    [Fact]
    public void StaticHasNoDuration()
    {
        Assert.Equal(0, Make(AtxAnimationMode.Static, 4).TotalLoopDurationSeconds);
    }

    [Fact]
    public void SeekLandsOnTheRightFrame()
    {
        var playback = Make(AtxAnimationMode.Loop, 4, 100);
        playback.Seek(0.25);
        Assert.Equal(2, playback.CurrentFrame);
        playback.Seek(0.45);
        Assert.Equal(0, playback.CurrentFrame); // wraps after 0.4 s
    }

    [Fact]
    public void SeekSetsPingPongDirection()
    {
        var playback = Make(AtxAnimationMode.PingPong, 3, 100);
        playback.Seek(0.35); // the way back down
        Assert.Equal(1, playback.CurrentFrame);
        Assert.Equal(-1, playback.Direction);
    }

    [Fact]
    public void SeekingOntoAnEndFrameCarriesTheDirectionTheGameWouldHave()
    {
        // atx_do_frame flips the direction in the same statement that lands on an end frame, so
        // "showing the last frame" always means "about to go back". Deriving the direction from
        // the timeline alone would stall a two-frame ping-pong for one extra step after a scrub.
        var two = Make(AtxAnimationMode.PingPong, 2, 100);
        two.Seek(0.1);
        Assert.Equal(1, two.CurrentFrame);
        Assert.Equal(-1, two.Direction);
        two.Advance(0.1);
        Assert.Equal(0, two.CurrentFrame);

        var three = Make(AtxAnimationMode.PingPong, 3, 100);
        three.Seek(0.2);
        Assert.Equal(2, three.CurrentFrame);
        Assert.Equal(-1, three.Direction);
        three.Seek(0.0);
        Assert.Equal(0, three.CurrentFrame);
        Assert.Equal(1, three.Direction);
    }

    [Fact]
    public void ASingleFrameNeverAdvancesInAnyMode()
    {
        // atx_do_frame skips any controller with fewer than two frames outright, so Play Once
        // never even reaches its "hold and stop" branch.
        foreach (var mode in new[]
                 { AtxAnimationMode.Loop, AtxAnimationMode.PingPong, AtxAnimationMode.PlayOnce })
        {
            var playback = Make(mode, 1, 10);
            Assert.False(playback.Advance(5));
            Assert.Equal(0, playback.CurrentFrame);
            Assert.True(playback.Playing);
            Assert.Equal(0f, playback.TimeInFrameSeconds);
        }
    }

    [Fact]
    public void AHugeDeltaStopsAtTheTwoCycleCapAndDropsTheLeftover()
    {
        // max_advances is max(2, n * 2) and the leftover time is dropped when the cap is hit.
        // Below the cap, the leftover is kept and carried into the next frame as usual.
        var normal = Make(AtxAnimationMode.Loop, 3, 10);
        Assert.True(normal.Advance(0.025));
        Assert.Equal(2, normal.CurrentFrame);
        Assert.Equal(0.005f, normal.TimeInFrameSeconds, 4);

        // A ten-minute stall still only advances 2n times, and the deficit is discarded rather
        // than carried, so the next Advance does not race through the whole file again.
        var stalled = Make(AtxAnimationMode.Loop, 3, 10);
        Assert.False(stalled.Advance(600));   // 2n advances on a 3-frame loop lands back on 0
        Assert.Equal(0, stalled.CurrentFrame);
        Assert.Equal(0f, stalled.TimeInFrameSeconds);

        // Ping-pong does not return to phase after 2n from a standing start, so the cap is
        // visible there: six advances from frame 0 end on the last frame.
        var pingPong = Make(AtxAnimationMode.PingPong, 3, 10);
        Assert.True(pingPong.Advance(600));
        Assert.Equal(2, pingPong.CurrentFrame);
        Assert.Equal(0f, pingPong.TimeInFrameSeconds);
    }

    [Fact]
    public void PlayOnceHoldsTheLastFrameAndStops()
    {
        var playback = Make(AtxAnimationMode.PlayOnce, 3, 10);
        Assert.True(playback.Advance(0.02));
        Assert.Equal(2, playback.CurrentFrame);
        Assert.True(playback.Playing);

        // The step that would go past the end stops the controller instead.
        Assert.False(playback.Advance(0.01));
        Assert.Equal(2, playback.CurrentFrame);
        Assert.False(playback.Playing);
        Assert.Equal(0f, playback.TimeInFrameSeconds);

        // And it stays stopped until something plays it again.
        Assert.False(playback.Advance(10));
        playback.Play();
        Assert.False(playback.Advance(10));  // already on the last frame
        Assert.Equal(2, playback.CurrentFrame);
    }

    [Theory]
    // Loop: every frame once.
    [InlineData(2, 300)]
    // Ping-Pong: frames 1..n-2 are shown again on the way back, so 100+100+100 plus the middle one.
    [InlineData(1, 400)]
    // Play Once: the last frame is held rather than timed, so it is not part of the run.
    [InlineData(3, 200)]
    // Static: never advances at all.
    [InlineData(0, 0)]
    public void CycleDurationFollowsTheAnimationMode(int mode, int expected)
    {
        var model = AtxParser.Parse($"""
            [header]
            frame_time = 100
            animation_mode = {mode}
            [[frame]]
            file = "a.tga"
            [[frame]]
            file = "b.tga"
            [[frame]]
            file = "c.tga"
            """).Model!;
        Assert.Equal(expected, AtxPlayback.CycleDurationMs(model));
    }

    [Fact]
    public void CycleDurationIsZeroWhenNothingCanAnimate()
    {
        // atx_do_frame skips any controller with fewer than two frames, whatever the mode says.
        var single = AtxParser.Parse(
            "[header]\nanimation_mode = 2\n[[frame]]\nfile = \"a.tga\"\n").Model!;
        Assert.Equal(0, AtxPlayback.CycleDurationMs(single));
    }

    [Fact]
    public void CycleDurationCountsPerFrameOverrides()
    {
        var model = AtxParser.Parse("""
            [header]
            frame_time = 100
            animation_mode = 1
            [[frame]]
            file = "a.tga"
            [[frame]]
            file = "b.tga"
            frame_time = 250
            [[frame]]
            file = "c.tga"
            """).Model!;
        // 100 + 250 + 100 forward, then 250 again on the way back down.
        Assert.Equal(700, AtxPlayback.CycleDurationMs(model));
    }

    [Fact]
    public void SpecComesFromTheModelWithTheGamesClamps()
    {
        var model = AtxParser.Parse("""
            [header]
            frame_time = 0
            animation_mode = 2
            initially_on = false
            [[frame]]
            file = "a.tga"
            [[frame]]
            file = "b.tga"
            frame_time = -9
            """).Model!;
        var spec = PlaybackSpec.FromModel(model);
        Assert.Equal(AtxAnimationMode.Loop, spec.Mode);
        Assert.False(spec.InitiallyOn);
        Assert.Equal(1, spec.BaseFrameTimeMs);
        Assert.Equal([null, 1], spec.FrameTimeOverridesMs);
    }

    private static List<int> Steps(AtxPlayback playback, int count)
    {
        var frames = new List<int>();
        for (int i = 0; i < count; i++)
        {
            playback.Advance(playback.FrameTimeMs(playback.CurrentFrame) / 1000.0);
            frames.Add(playback.CurrentFrame);
        }
        return frames;
    }
}
