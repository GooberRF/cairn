using System.Globalization;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Ui.ViewModels.ClipTools;

/// <summary>How the keys of two clips differ, compared track by track at equal times.</summary>
/// <param name="Before">Keys in the first clip.</param>
/// <param name="After">Keys in the second clip.</param>
/// <param name="Added">Keys at a time the track had no key at.</param>
/// <param name="Removed">Keys whose time the track no longer has.</param>
/// <param name="Changed">Keys at the same time whose value (or eases, control points) differ.</param>
/// <param name="BonesChanged">Bones whose track or weight differs.</param>
public readonly record struct KeyDiff(int Before, int After, int Added, int Removed, int Changed, int BonesChanged)
{
    /// <summary>Compares <paramref name="before"/> with <paramref name="after"/> (bones by index, up to the shorter list).</summary>
    public static KeyDiff Of(RfaClip before, RfaClip after)
    {
        int added = 0, removed = 0, changed = 0, bones = 0;
        int n = Math.Min(before.BoneCount, after.BoneCount);
        for (int b = 0; b < n; b++)
        {
            var x = before.Bones[b];
            var y = after.Bones[b];
            if (ReferenceEquals(x, y)) continue;
            int a0 = added, r0 = removed, c0 = changed;
            Compare(x.RotationKeys, y.RotationKeys, k => k.Time, ref added, ref removed, ref changed);
            Compare(x.PositionKeys, y.PositionKeys, k => k.Time, ref added, ref removed, ref changed);
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (added != a0 || removed != r0 || changed != c0 || x.Weight != y.Weight) bones++;
        }
        for (int b = n; b < after.BoneCount; b++) added += after.Bones[b].RotationKeys.Length + after.Bones[b].PositionKeys.Length;
        for (int b = n; b < before.BoneCount; b++) removed += before.Bones[b].RotationKeys.Length + before.Bones[b].PositionKeys.Length;
        bones += Math.Abs(after.BoneCount - before.BoneCount);
        return new KeyDiff(KeyCount(before), KeyCount(after), added, removed, changed, bones);
    }

    private static void Compare<T>(IReadOnlyList<T> x, IReadOnlyList<T> y, Func<T, int> time, ref int added, ref int removed, ref int changed)
        where T : struct
    {
        var byTime = new Dictionary<int, T>(x.Count);
        foreach (var k in x) byTime[time(k)] = k;
        var seen = new HashSet<int>();
        foreach (var k in y)
        {
            int t = time(k);
            seen.Add(t);
            if (!byTime.TryGetValue(t, out var old)) added++;
            else if (!EqualityComparer<T>.Default.Equals(old, k)) changed++;
        }
        foreach (int t in byTime.Keys)
        {
            if (!seen.Contains(t)) removed++;
        }
    }

    /// <summary>Rotation plus position keys of a clip.</summary>
    public static int KeyCount(RfaClip clip) => clip.Bones.Sum(b => b.RotationKeys.Length + b.PositionKeys.Length);

    /// <summary>"Keys: 3,000 → 112 (2,888 removed)" and the like.</summary>
    public string KeysLine()
    {
        var parts = new List<string>();
        if (Added > 0) parts.Add($"{Added:N0} added");
        if (Removed > 0) parts.Add($"{Removed:N0} removed");
        if (Changed > 0) parts.Add($"{Changed:N0} changed");
        string detail = parts.Count == 0 ? "none changed" : string.Join(", ", parts);
        return string.Format(CultureInfo.CurrentCulture, "Keys: {0:N0} → {1:N0} ({2})", Before, After, detail);
    }
}

/// <summary>Plain-language summary lines and measurements the clip tools share.</summary>
public static class ClipToolSummary
{
    /// <summary>"Range: 1–40 f → 12–40 f · duration 39 → 28 frames (1.300 → 0.933 s)", or the unchanged range.</summary>
    public static string RangeLine(RfaClip before, RfaClip after, TimeUnit unit)
    {
        string a = $"{TimeFormat.Number(before.StartTime, unit)}–{TimeFormat.Number(before.EndTime, unit)} {TimeFormat.Suffix(unit)}";
        string b = $"{TimeFormat.Number(after.StartTime, unit)}–{TimeFormat.Number(after.EndTime, unit)} {TimeFormat.Suffix(unit)}";
        if (before.StartTime == after.StartTime && before.EndTime == after.EndTime)
            return $"Range: {a} (unchanged) · {TimeFormat.Duration(before.Duration)}";
        return string.Format(CultureInfo.CurrentCulture, "Range: {0} → {1} · duration {2:0.##} → {3:0.##} frames ({4:0.000} → {5:0.000} s)",
            a, b, before.Duration / (double)RfaClip.TicksPerFrame, after.Duration / (double)RfaClip.TicksPerFrame,
            before.DurationSeconds, after.DurationSeconds);
    }

    /// <summary>"Largest change from the current clip: 0.21° rotation, 1.3 mm position", from <c>ClipEdit.MeasureError</c>.</summary>
    public static string ErrorLine(string lead, float degrees, float metres) =>
        string.Format(CultureInfo.CurrentCulture, "{0}: {1:0.###}° rotation, {2} position", lead, degrees, Metres(metres));

    /// <summary>A length in metres as "12.3 cm", "1.25 mm" or "1.234 m".</summary>
    public static string Metres(float metres)
    {
        float a = MathF.Abs(metres);
        if (a >= 1f) return metres.ToString("0.### m", CultureInfo.CurrentCulture);
        if (a >= 0.01f) return (metres * 100f).ToString("0.## cm", CultureInfo.CurrentCulture);
        return (metres * 1000f).ToString("0.### mm", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The seam of a loop: the largest local rotation difference (degrees) and position difference
    /// (metres) of any bone between the clip's start and end poses.
    /// </summary>
    public static (float Degrees, float Metres) Seam(RfaClip clip)
    {
        float deg = 0, m = 0;
        foreach (var track in clip.Bones)
        {
            if (track.RotationKeys.Length > 0)
            {
                var a = ClipSampler.SampleRotation(track.RotationKeys.AsSpan(), clip.StartTime);
                var b = ClipSampler.SampleRotation(track.RotationKeys.AsSpan(), clip.EndTime);
                deg = MathF.Max(deg, Quat.AngleDegrees(a, b));
            }
            if (track.PositionKeys.Length > 0)
            {
                var a = ClipSampler.SamplePosition(track.PositionKeys.AsSpan(), clip.StartTime);
                var b = ClipSampler.SamplePosition(track.PositionKeys.AsSpan(), clip.EndTime);
                m = MathF.Max(m, Vector3.Distance(a, b));
            }
        }
        return (deg, m);
    }

    /// <summary>The seam measured on bones other than <paramref name="skipBone"/> (the root's travel is not a seam).</summary>
    public static (float Degrees, float Metres) SeamExcept(RfaClip clip, int skipBone)
    {
        float deg = 0, m = 0;
        for (int i = 0; i < clip.BoneCount; i++)
        {
            var track = clip.Bones[i];
            if (track.RotationKeys.Length > 0)
            {
                var a = ClipSampler.SampleRotation(track.RotationKeys.AsSpan(), clip.StartTime);
                var b = ClipSampler.SampleRotation(track.RotationKeys.AsSpan(), clip.EndTime);
                deg = MathF.Max(deg, Quat.AngleDegrees(a, b));
            }
            if (i != skipBone && track.PositionKeys.Length > 0)
            {
                var a = ClipSampler.SamplePosition(track.PositionKeys.AsSpan(), clip.StartTime);
                var b = ClipSampler.SamplePosition(track.PositionKeys.AsSpan(), clip.EndTime);
                m = MathF.Max(m, Vector3.Distance(a, b));
            }
        }
        return (deg, m);
    }

    /// <summary>The root's travel from start to end (model space = its local position).</summary>
    public static Vector3 Travel(RfaClip clip, int root)
    {
        if ((uint)root >= (uint)clip.BoneCount || clip.Bones[root].PositionKeys.Length == 0) return Vector3.Zero;
        var keys = clip.Bones[root].PositionKeys.AsSpan();
        return ClipSampler.SamplePosition(keys, clip.EndTime) - ClipSampler.SamplePosition(keys, clip.StartTime);
    }

    /// <summary>"(0.00, 0.00, 1.52) m".</summary>
    public static string Vector(Vector3 v) =>
        string.Format(CultureInfo.CurrentCulture, "({0:0.00}, {1:0.00}, {2:0.00}) m", Tidy(v.X), Tidy(v.Y), Tidy(v.Z));

    /// <summary>Values that round to zero print as "0.00", not "-0.00".</summary>
    private static float Tidy(float value) => MathF.Abs(value) < 0.005f ? 0f : value;

    /// <summary>A short list of names: "a, b, c and 4 more".</summary>
    public static string Names(IReadOnlyList<string> names, int max = 6)
    {
        if (names.Count == 0) return "none";
        if (names.Count <= max) return string.Join(", ", names);
        return string.Join(", ", names.Take(max)) + $" and {names.Count - max} more";
    }

    /// <summary>"1 bone" / "3 bones".</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        n.ToString("N0", CultureInfo.CurrentCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");
}
