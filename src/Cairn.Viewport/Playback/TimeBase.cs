namespace Cairn.Viewport;

/// <summary>
/// The tick rate a document's times are stored in: ticks per second and ticks per frame. RFA clips use
/// 4800 / 160 (30 fps); VFX files 4800 / 320 (15 fps).
/// </summary>
public readonly record struct TimeBase(double TicksPerSecond, double TicksPerFrame)
{
    /// <summary>Frames per second (<see cref="TicksPerSecond"/> / <see cref="TicksPerFrame"/>).</summary>
    public double FramesPerSecond => TicksPerFrame > 0 ? TicksPerSecond / TicksPerFrame : 0;
}
