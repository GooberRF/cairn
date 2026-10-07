using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cairn.Formats.Tbl;

namespace Cairn.Rfa.Retarget;

/// <summary>A preset suggested for one clip, and why.</summary>
/// <param name="Preset">The suggestion (never <see cref="RetargetPreset.Custom"/>).</param>
/// <param name="Reason">Plain words: "the tables play it as jeep_drive", "its name says seated", ...</param>
public sealed record PresetSuggestion(RetargetPreset Preset, string Reason);

/// <summary>
/// The three retarget presets (phase 7a): which options each sets, a one-line "when to use it", how to
/// recognise a preset in a set of options, and the per-clip suggestion (seated when the tables play the
/// clip as a fixed-contact state, or failing table data when its name says so; rotation only for the
/// swim states; otherwise standing / locomotion).
/// <para>A preset governs <see cref="RetargetOptions.RootMode"/>, <see cref="RetargetOptions.Ik"/>,
/// <see cref="RetargetOptions.IkArms"/>, <see cref="RetargetOptions.IkLegs"/>, <see cref="RetargetOptions.ScaleStride"/>,
/// <see cref="RetargetOptions.RestAlignment"/>, <see cref="RetargetOptions.BoneLengths"/>,
/// <see cref="RetargetOptions.ExtraBonePose"/> and <see cref="RetargetOptions.DisabledIkChains"/>. The output
/// encoding (<see cref="RetargetOptions.Quantization"/>, <see cref="RetargetOptions.ResampleStep"/>) is not
/// part of a preset and is kept when one is applied. The two-handed grip
/// (<see cref="RetargetOptions.OffHandFollowsMainHand"/>) goes with any preset and is not part of a preset's
/// identity, but each preset has a default for it (<see cref="GripByDefault"/>: on for standing / locomotion,
/// phase 7b): <see cref="Options"/> uses that default unless <c>keep</c> is given.</para>
/// </summary>
public static class RetargetPresets
{
    /// <summary>The presets offered as a first choice, in order.</summary>
    public static ImmutableArray<RetargetPreset> All { get; } = [RetargetPreset.Seated, RetargetPreset.Locomotion, RetargetPreset.RotationOnly];

    /// <summary>
    /// States whose stock clips hold fixed contacts (hands on controls, seat under the hips): the
    /// vehicle seats and the stationary turret. Every other stock state stands on the ground.
    /// </summary>
    public static ImmutableArray<string> SeatedStates { get; } = ["jeep_drive", "jeep_gun", "on_turret"];

    /// <summary>
    /// States in which nothing touches the ground or a control (swimming): suggested
    /// <see cref="RetargetPreset.RotationOnly"/>, since holding a dangling foot to the floor means nothing.
    /// </summary>
    public static ImmutableArray<string> FreeStates { get; } = ["swim_stand", "swim_walk"];

    /// <summary>Words in a clip name that say it is seated or holds fixed controls (used when no table names the clip).</summary>
    public static ImmutableArray<string> SeatedNameWords { get; } =
        ["sit", "sitting", "seated", "seat", "chair", "jeep", "driver", "drive", "gunner", "turret", "pilot", "cockpit"];

    /// <summary>Short title, e.g. "Seated / fixed controls".</summary>
    public static string Title(RetargetPreset preset) => preset switch
    {
        RetargetPreset.Seated => "Seated / fixed controls",
        RetargetPreset.Locomotion => "Standing / locomotion",
        RetargetPreset.RotationOnly => "Rotation only",
        _ => "Custom",
    };

    /// <summary>One plain-language line saying when to use the preset.</summary>
    public static string Description(RetargetPreset preset) => preset switch
    {
        RetargetPreset.Seated =>
            "For riders and turret gunners: the hips stay on the seat and the hands and feet stay on the source's controls (the reference method).",
        RetargetPreset.Locomotion =>
            "For standing, walking, running, crouching, jumping and dying: the hips stand at the target's own height and the feet stay on the ground; arms swing as the source's, and while the hands are together the off hand keeps its grip on the main hand.",
        RetargetPreset.RotationOnly =>
            "Every joint turns exactly as the source's, nothing is pinned: hips at the target's height, feet and hands land wherever the target's proportions put them.",
        _ => "The options below were changed from the preset.",
    };

    /// <summary>
    /// Whether the two-handed grip (<see cref="RetargetOptions.OffHandFollowsMainHand"/>) is on by default with a
    /// preset: on for <see cref="RetargetPreset.Locomotion"/> (measured in phase 7a on miner1's 91 clips whose hands
    /// come together: the off hand drifts 8.6-30 cm from the grip without it and at most 0.6 cm with it); off for
    /// <see cref="RetargetPreset.Seated"/> (the hands are already IK'd onto the source's controls; the reference
    /// method, whose nine goldens must stay byte-identical) and <see cref="RetargetPreset.RotationOnly"/> (no IK).
    /// </summary>
    public static bool GripByDefault(RetargetPreset preset) => preset == RetargetPreset.Locomotion;

    /// <summary>
    /// The options of a preset, keeping <paramref name="keep"/>'s output encoding (quantisation, resampling) and
    /// two-handed grip; without <paramref name="keep"/> the grip is the preset's default (<see cref="GripByDefault"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="RetargetPreset.Custom"/> has no options of its own.</exception>
    public static RetargetOptions Options(RetargetPreset preset, RetargetOptions? keep = null)
    {
        var start = new RetargetOptions
        {
            Quantization = keep?.Quantization ?? KeyQuantization.WithinUnit,
            ResampleStep = keep?.ResampleStep,
            OffHandFollowsMainHand = keep?.OffHandFollowsMainHand ?? GripByDefault(preset),
            OffHandGripDistance = keep?.OffHandGripDistance ?? new RetargetOptions().OffHandGripDistance,
        };
        return preset switch
        {
            RetargetPreset.Seated => start,
            RetargetPreset.Locomotion => start with { RootMode = RootMode.HipHeight, Ik = true, IkArms = false, IkLegs = true },
            RetargetPreset.RotationOnly => start with { RootMode = RootMode.HipHeight, Ik = false },
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Custom is not a preset with options of its own."),
        };
    }

    /// <summary>The preset whose governed options equal these, else <see cref="RetargetPreset.Custom"/>.</summary>
    public static RetargetPreset Identify(RetargetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var preset in All)
        {
            if (Governed(Options(preset, options)) == Governed(options)) return preset;
        }
        return RetargetPreset.Custom;
    }

    // The fields a preset sets; IK details only matter while IK runs.
    private static (bool, bool, bool, bool, RootMode, bool, BoneLengthSource, ExtraBonePose, string) Governed(RetargetOptions o) =>
        (o.RestAlignment, o.Ik, o.Ik && o.IkArms, o.Ik && o.IkLegs, o.RootMode, o.RootMode == RootMode.HipHeight && o.ScaleStride,
            o.BoneLengths, o.ExtraBonePose,
            o.Ik && !o.DisabledIkChains.IsDefaultOrEmpty ? string.Join(",", o.DisabledIkChains.Order(StringComparer.OrdinalIgnoreCase)) : string.Empty);

    /// <summary>
    /// The preset for one clip: <see cref="RetargetPreset.Seated"/> when the tables play it as a
    /// <see cref="SeatedStates"/> state (or, when no table names it, its name has a
    /// <see cref="SeatedNameWords"/> word); <see cref="RetargetPreset.RotationOnly"/> for a
    /// <see cref="FreeStates"/> state (or a name with "swim"); otherwise <see cref="RetargetPreset.Locomotion"/>.
    /// </summary>
    /// <param name="clipName">The clip's file or base name.</param>
    /// <param name="usage">The table usage index, or null when no tables are loaded.</param>
    public static PresetSuggestion Suggest(string clipName, ClipUsageIndex? usage)
    {
        ArgumentNullException.ThrowIfNull(clipName);
        var usages = usage?.UsagesOf(clipName) ?? [];
        var seated = usages.FirstOrDefault(u => u.Kind == ClipUsageKind.State && SeatedStates.Contains(u.SlotName, StringComparer.OrdinalIgnoreCase));
        if (seated is not null)
            return new PresetSuggestion(RetargetPreset.Seated, $"the tables play it as the {seated.SlotName} state");
        var free = usages.FirstOrDefault(u => u.Kind == ClipUsageKind.State && FreeStates.Contains(u.SlotName, StringComparer.OrdinalIgnoreCase));
        if (free is not null)
            return new PresetSuggestion(RetargetPreset.RotationOnly, $"the tables play it as the {free.SlotName} state, where nothing touches the ground");
        if (usages.Count > 0)
        {
            var first = usages[0];
            return new PresetSuggestion(RetargetPreset.Locomotion,
                $"the tables play it as {(first.Kind == ClipUsageKind.State ? "the" : "the action")} {first.SlotName}{(first.Kind == ClipUsageKind.State ? " state" : string.Empty)}, which stands on the ground");
        }
        string baseName = Path.GetFileNameWithoutExtension(clipName);
        var words = BoneTokensForName(baseName);
        string? word = SeatedNameWords.FirstOrDefault(w => words.Contains(w));
        if (word is not null) return new PresetSuggestion(RetargetPreset.Seated, $"no table names it and its name says '{word}'");
        if (words.Contains("swim")) return new PresetSuggestion(RetargetPreset.RotationOnly, "no table names it and its name says 'swim'");
        return new PresetSuggestion(RetargetPreset.Locomotion, "no table names it and nothing in its name says seated");
    }

    private static HashSet<string> BoneTokensForName(string name)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new System.Text.StringBuilder();
        char prev = '\0';
        foreach (char ch in name + "_")
        {
            bool boundary = !char.IsLetter(ch) || (current.Length > 0 && char.IsUpper(ch) && char.IsLower(prev));
            if (boundary && current.Length > 0)
            {
                words.Add(current.ToString().ToLowerInvariant());
                current.Clear();
            }
            if (char.IsLetter(ch)) current.Append(ch);
            prev = ch;
        }
        return words;
    }

    /// <summary>
    /// Brings a saved options object (a retarget profile's <c>options</c>) up to date in place:
    /// <c>RootMode: "ScaleByLegLength"</c> (retired in phase 7a) becomes <c>"HipHeight"</c>. Returns a
    /// plain-language note when something changed, else null. Property names are matched without case.
    /// </summary>
    /// <exception cref="FormatException">The object names a property twice.</exception>
    public static string? MigrateOptionsJson(JsonObject options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<KeyValuePair<string, JsonNode?>> properties;
        // A parsed JsonObject only checks for repeated property names when it is first enumerated.
        try { properties = [.. options]; }
        catch (ArgumentException ex) { throw new FormatException($"The saved options name a setting twice ({ex.Message}). Remove the repeated setting.", ex); }
        foreach (var (key, value) in properties)
        {
            if (!string.Equals(key, nameof(RetargetOptions.RootMode), StringComparison.OrdinalIgnoreCase)) continue;
            if (value is JsonValue v && v.TryGetValue(out string? s) && string.Equals(s, "ScaleByLegLength", StringComparison.OrdinalIgnoreCase))
            {
                options[key] = nameof(RootMode.HipHeight);
                return "The profile used the retired root mode 'scale by leg length'; it now uses 'hip height', which places the hips above the target's own ground and scales them by the leg ratio.";
            }
        }
        return null;
    }
}
