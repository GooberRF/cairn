using System.Globalization;

namespace Cairn.Viewport;

/// <summary>How times are shown everywhere in the UI (Settings, and the transport's readout button).</summary>
public enum TimeUnit
{
    /// <summary>Frames (<see cref="TimeBase.TicksPerFrame"/> ticks).</summary>
    Frames,
    /// <summary>Seconds (<see cref="TimeBase.TicksPerSecond"/> ticks).</summary>
    Seconds,
    /// <summary>Raw ticks, as the file stores them.</summary>
    Ticks,
}

/// <summary>Formatting and conversion between ticks and the display unit.</summary>
public static class TimeFormat
{
    /// <summary>The unit's short suffix: "f", "s" or "ticks".</summary>
    public static string Suffix(TimeUnit unit) => unit switch
    {
        TimeUnit.Frames => "f",
        TimeUnit.Seconds => "s",
        _ => "ticks",
    };

    /// <summary>Digits after the decimal point an editor uses for the unit.</summary>
    public static int Decimals(TimeUnit unit) => unit switch
    {
        TimeUnit.Frames => 2,
        TimeUnit.Seconds => 3,
        _ => 0,
    };

    /// <summary>Ticks per display unit.</summary>
    public static double TicksPer(TimeUnit unit, TimeBase timeBase) => unit switch
    {
        TimeUnit.Frames => timeBase.TicksPerFrame,
        TimeUnit.Seconds => timeBase.TicksPerSecond,
        _ => 1,
    };

    /// <summary>A tick count in the unit, as a number (frames and seconds fractional).</summary>
    public static double ToUnit(double ticks, TimeUnit unit, TimeBase timeBase) => ticks / TicksPer(unit, timeBase);

    /// <summary>A value in the unit back to whole ticks (rounded).</summary>
    public static int FromUnit(double value, TimeUnit unit, TimeBase timeBase) =>
        (int)Math.Clamp(Math.Round(value * TicksPer(unit, timeBase)), int.MinValue, int.MaxValue);

    /// <summary>A time as text without the suffix, e.g. "12", "12.5", "0.400", "1920".</summary>
    public static string Number(double ticks, TimeUnit unit, TimeBase timeBase) => unit switch
    {
        TimeUnit.Frames => (ticks / timeBase.TicksPerFrame).ToString("0.##", CultureInfo.CurrentCulture),
        TimeUnit.Seconds => (ticks / timeBase.TicksPerSecond).ToString("0.000", CultureInfo.CurrentCulture),
        _ => Math.Round(ticks).ToString("0", CultureInfo.CurrentCulture),
    };

    /// <summary>A time with its suffix, e.g. "12 f", "0.400 s", "1920 ticks".</summary>
    public static string Format(double ticks, TimeUnit unit, TimeBase timeBase) => Number(ticks, unit, timeBase) + " " + Suffix(unit);

    /// <summary>A duration in both frames and seconds, e.g. "40 frames (1.333 s)".</summary>
    public static string Duration(int ticks, TimeBase timeBase) =>
        string.Format(CultureInfo.CurrentCulture, "{0:0.##} frames ({1:0.000} s)",
            ticks / timeBase.TicksPerFrame, ticks / timeBase.TicksPerSecond);
}
