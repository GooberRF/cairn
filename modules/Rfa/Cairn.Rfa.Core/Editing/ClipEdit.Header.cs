using System.Numerics;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Editing;

/// <summary>
/// A change to a clip's header fields for <see cref="ClipEdit.SetHeader"/>. Every field is optional:
/// null keeps the clip's value. The version is not here: changing it converts the morph data, so it
/// goes through <see cref="ClipEdit.ConvertVersion"/>.
/// </summary>
public sealed record ClipHeaderChange
{
    /// <summary>New start time in ticks, or null to keep it.</summary>
    public int? StartTime { get; init; }

    /// <summary>New end time in ticks, or null to keep it.</summary>
    public int? EndTime { get; init; }

    /// <summary>New ramp-in in ticks (0 or more), or null to keep it.</summary>
    public int? RampIn { get; init; }

    /// <summary>New ramp-out in ticks (0 or more), or null to keep it.</summary>
    public int? RampOut { get; init; }

    /// <summary>New exporter position tolerance (never read by the game), or null to keep it.</summary>
    public float? PosReduction { get; init; }

    /// <summary>New exporter rotation tolerance (never read by the game), or null to keep it.</summary>
    public float? RotReduction { get; init; }

    /// <summary>New total rotation, stored as given (raw floats, not normalised; not read by the game), or null to keep it.</summary>
    public Quaternion? TotalRotation { get; init; }

    /// <summary>New total translation (not read by the game), or null to keep it.</summary>
    public Vector3? TotalTranslation { get; init; }
}

public static partial class ClipEdit
{
    /// <summary>
    /// The clip with the header fields that <paramref name="change"/> sets replaced; bones and morph
    /// data are untouched. Times are not clamped to the keys (keys outside the new range are a lint
    /// finding; <see cref="Trim"/> crops instead). Note that a version 7 morph spreads its keyframes
    /// over [start, end], so changing the range re-times a v7 morph.
    /// </summary>
    /// <exception cref="ArgumentException">The resulting end is before the start, a ramp is negative,
    /// or a float field is not finite.</exception>
    public static RfaClip SetHeader(RfaClip clip, ClipHeaderChange change)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(change);
        int start = change.StartTime ?? clip.StartTime;
        int end = change.EndTime ?? clip.EndTime;
        if (end < start)
            throw new ArgumentException($"The end time ({end}) is before the start time ({start}); the end must be at or after the start.", nameof(change));
        int rampIn = change.RampIn ?? clip.RampIn, rampOut = change.RampOut ?? clip.RampOut;
        if (rampIn < 0 || rampOut < 0)
            throw new ArgumentException($"Ramps cannot be negative (ramp in {rampIn}, ramp out {rampOut}); use 0 for no ramp.", nameof(change));
        if (change.PosReduction is { } pr && !float.IsFinite(pr))
            throw new ArgumentException("The position reduction must be a finite number.", nameof(change));
        if (change.RotReduction is { } rr && !float.IsFinite(rr))
            throw new ArgumentException("The rotation reduction must be a finite number.", nameof(change));
        if (change.TotalRotation is { } q && !(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W)))
            throw new ArgumentException("The total rotation must have finite components.", nameof(change));
        if (change.TotalTranslation is { } v && !(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)))
            throw new ArgumentException("The total translation must have finite components.", nameof(change));

        return clip with
        {
            StartTime = start,
            EndTime = end,
            RampIn = rampIn,
            RampOut = rampOut,
            PosReduction = change.PosReduction ?? clip.PosReduction,
            RotReduction = change.RotReduction ?? clip.RotReduction,
            TotalRotation = change.TotalRotation ?? clip.TotalRotation,
            TotalTranslation = change.TotalTranslation ?? clip.TotalTranslation,
        };
    }
}
