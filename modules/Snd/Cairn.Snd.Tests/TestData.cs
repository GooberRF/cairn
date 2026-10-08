namespace Cairn.Snd.Tests;

/// <summary>Synthetic sounds for the tests (built by <see cref="SyntheticSounds"/>; no game data).</summary>
internal static class TestData
{
    public const int Pitch22k = SyntheticSounds.Pitch22k, Pitch11k = SyntheticSounds.Pitch11k, Pitch44k = SyntheticSounds.Pitch44k;

    public static byte[] Frame(int filter, int shift, int flags, IReadOnlyList<int> nibbles) => SyntheticSounds.Frame(filter, shift, flags, nibbles);
    public static byte[] EndMarker() => SyntheticSounds.EndMarker();
    public static short[] Sine(int frames, double period = 50, double amplitude = 12000) => SyntheticSounds.Sine(frames, period, amplitude);
    public static byte[] Encode(short[] pcm, Func<int, int>? flags = null) => SyntheticSounds.Encode(pcm, flags);
    public static byte[] Vse(byte[] data, int pitch = Pitch22k, bool looping = false, int? ms = null, uint envelope = 0x000F0003, int? declaredSize = null) =>
        SyntheticSounds.Vse(data, pitch, looping, ms, envelope, declaredSize);
    public static byte[] VseOld(byte[] data, int ms, int pitch = Pitch22k) => SyntheticSounds.VseOld(data, ms, pitch);
    public static byte[] Vmu(byte[] left, byte[] right, bool looping = false, int pitch = Pitch44k) => SyntheticSounds.Vmu(left, right, looping, pitch);

    /// <summary>Root-mean-square difference of two equally long sample runs.</summary>
    public static double Rms(ReadOnlySpan<short> a, ReadOnlySpan<short> b)
    {
        double sum = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) sum += (double)(a[i] - b[i]) * (a[i] - b[i]);
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
}
