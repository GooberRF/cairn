namespace Cairn.Viewport;


/// <summary>Frame-time figures for the report and the diagnostics switch.</summary>
public sealed class ViewportStats
{
    private readonly double[] _update = new double[240];
    private readonly double[] _interval = new double[240];
    private int _count;
    private int _next;

    /// <summary>Vertices drawn.</summary>
    public int Vertices { get; set; }

    /// <summary>Bytes allocated inside the skinning and vertex-write path since the last reset.</summary>
    public long SkinAllocatedBytes { get; set; }

    /// <summary>Bytes allocated inside pose sampling (ClipSampler + FK) since the last reset.</summary>
    public long PoseAllocatedBytes { get; set; }

    public void Add(double updateMs, double intervalMs)
    {
        _update[_next] = updateMs;
        _interval[_next] = intervalMs;
        _next = (_next + 1) % _update.Length;
        _count = Math.Min(_count + 1, _update.Length);
    }

    /// <summary>Frames recorded (up to 240).</summary>
    public int Frames => _count;

    /// <summary>Mean ms per frame spent on pose sampling, skinning, vertex upload and overlay.</summary>
    public double MeanUpdateMs => _count == 0 ? 0 : _update.Take(_count).Average();

    /// <summary>Worst update in the window.</summary>
    public double MaxUpdateMs => _count == 0 ? 0 : _update.Take(_count).Max();

    /// <summary>Mean interval between rendered frames (1000 / fps).</summary>
    public double MeanIntervalMs => _count < 2 ? 0 : _interval.Take(_count).Where(i => i > 0).DefaultIfEmpty(0).Average();

    public void Reset()
    {
        _count = 0;
        _next = 0;
        SkinAllocatedBytes = 0;
        PoseAllocatedBytes = 0;
    }
}
