using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui;

/// <summary>RFA's time base (4800 ticks per second, 160 per frame) for the shared playback types.</summary>
public static class RfaTime
{
    /// <summary>The <see cref="TimeBase"/> of every RFA clip.</summary>
    public static readonly TimeBase Base = new(RfaClip.TicksPerSecond, RfaClip.TicksPerFrame);

    /// <summary>The shared transport's range for <paramref name="clip"/>: rotation and position key times of every bone.</summary>
    public static PlaybackRange? RangeOf(RfaClip? clip)
    {
        if (clip is null) return null;
        var times = new SortedSet<int>();
        foreach (var bone in clip.Bones)
        {
            foreach (var k in bone.RotationKeys) times.Add(k.Time);
            foreach (var k in bone.PositionKeys) times.Add(k.Time);
        }
        return new PlaybackRange(clip.StartTime, clip.EndTime, clip.RampIn, clip.RampOut, times);
    }

    /// <summary>RFA's old <c>SetClip(RfaClip?)</c> over the shared <see cref="PlaybackViewModel.SetClip(PlaybackRange?)"/>.</summary>
    public static void SetClip(this PlaybackViewModel playback, RfaClip? clip) => playback.SetClip(RangeOf(clip));
}

/// <summary>RFA's original <c>TimeFormat</c> signatures (RFA time base implied) forwarding to <see cref="Cairn.Viewport.TimeFormat"/>.</summary>
public static class TimeFormat
{
    /// <inheritdoc cref="Cairn.Viewport.TimeFormat.Suffix"/>
    public static string Suffix(TimeUnit unit) => Cairn.Viewport.TimeFormat.Suffix(unit);
    /// <inheritdoc cref="Cairn.Viewport.TimeFormat.Decimals"/>
    public static int Decimals(TimeUnit unit) => Cairn.Viewport.TimeFormat.Decimals(unit);
    /// <inheritdoc cref="Cairn.Viewport.TimeFormat.TicksPer"/>
    public static double TicksPer(TimeUnit unit) => Cairn.Viewport.TimeFormat.TicksPer(unit, RfaTime.Base);
    /// <inheritdoc cref="Cairn.Viewport.TimeFormat.ToUnit"/>
    public static double ToUnit(double ticks, TimeUnit unit) => Cairn.Viewport.TimeFormat.ToUnit(ticks, unit, RfaTime.Base);
    /// <inheritdoc cref="Cairn.Viewport.TimeFormat.FromUnit"/>
    public static int FromUnit(double value, TimeUnit unit) => Cairn.Viewport.TimeFormat.FromUnit(value, unit, RfaTime.Base);
    /// <inheritdoc cref="Cairn.Viewport.TimeFormat.Number"/>
    public static string Number(double ticks, TimeUnit unit) => Cairn.Viewport.TimeFormat.Number(ticks, unit, RfaTime.Base);
    /// <inheritdoc cref="Cairn.Viewport.TimeFormat.Format"/>
    public static string Format(double ticks, TimeUnit unit) => Cairn.Viewport.TimeFormat.Format(ticks, unit, RfaTime.Base);
    /// <inheritdoc cref="Cairn.Viewport.TimeFormat.Duration"/>
    public static string Duration(int ticks) => Cairn.Viewport.TimeFormat.Duration(ticks, RfaTime.Base);
}
