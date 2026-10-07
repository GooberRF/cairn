using System.Collections.ObjectModel;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.ViewModels.ClipTools;

/// <summary>One repair of <see cref="NormalizeOptions"/> in the normalise dialog: a checkbox and what it alone would change.</summary>
public sealed class NormalizeFix : ObservableObject
{
    private readonly NormalizeToolViewModel _owner;
    private bool _enabled = true;
    private string _effect = "…";
    private bool _hasEffect;

    internal NormalizeFix(NormalizeToolViewModel owner, string id, string label, string toolTip, Func<NormalizeOptions, bool, NormalizeOptions> with)
    {
        _owner = owner;
        Id = id;
        Label = label;
        ToolTip = toolTip;
        With = with;
    }

    public string Id { get; }

    public string Label { get; }

    public string ToolTip { get; }

    /// <summary>Sets this repair on or off in a set of options.</summary>
    internal Func<NormalizeOptions, bool, NormalizeOptions> With { get; }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value)) _owner.OnFixChanged();
        }
    }

    /// <summary>"355 rotation signs flipped" or "nothing to fix", for this repair alone.</summary>
    public string Effect
    {
        get => _effect;
        internal set => Set(ref _effect, value);
    }

    /// <summary>True when this repair alone would change something.</summary>
    public bool HasEffect
    {
        get => _hasEffect;
        internal set => Set(ref _hasEffect, value);
    }
}

/// <summary>Clip › Normalise: <see cref="ClipEdit.Normalize(RfaClip, NormalizeOptions?, out NormalizeReport)"/> with a checkbox per repair.</summary>
public sealed class NormalizeToolViewModel : ClipToolViewModel
{
    private static readonly NormalizeOptions None = new()
    {
        UnitQuaternions = false, SignContinuity = false, IncreasingTimes = false, ClampToRange = false,
        ZeroPad = false, FixControlPoints = false, MinimumKeys = false,
    };

    public NormalizeToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        Fixes =
        [
            new(this, "times", "Sort keys by time, drop duplicates", "Re-sort each track by time and drop keys that share a time (the last stored wins)", (o, v) => o with { IncreasingTimes = v }),
            new(this, "range", "Keep keys inside start–end", "Drop keys outside [start, end], adding a sampled key at the boundary so the motion inside is unchanged", (o, v) => o with { ClampToRange = v }),
            new(this, "unit", "Unit-length rotations", "Re-quantise rotation keys whose stored length is off 1 by more than 0.002 (only those)", (o, v) => o with { UnitQuaternions = v }),
            new(this, "signs", "Sign continuity", "Negate rotation keys in the other hemisphere from the key before (exact; the game plays them the same)", (o, v) => o with { SignContinuity = v }),
            new(this, "pad", "Clear pad words", "Set every rotation key's unused pad word to 0, as in every stock file", (o, v) => o with { ZeroPad = v }),
            new(this, "controls", "Fix missing control points", "Replace control points stored as (0, 0, 0) by linear auto control points", (o, v) => o with { FixControlPoints = v }),
            new(this, "minimum", "Minimum keys per bone", "Give every bone at least 1 rotation key and 2 position keys", (o, v) => o with { MinimumKeys = v }),
        ];
        Start();
    }

    public override string ToolId => "normalize";

    public override string Title => "Normalise";

    public override string Heading => "Repair structural problems";

    public override string Description =>
        "Fixes what can make a clip misbehave or hard to edit, and leaves everything else bit for bit as it is. "
        + "Each line says what that repair alone would change in this clip.";

    /// <summary>The repairs, in the order the Core runs them.</summary>
    public ObservableCollection<NormalizeFix> Fixes { get; }

    /// <summary>The options the checkboxes give.</summary>
    public NormalizeOptions Options
    {
        get
        {
            var o = None;
            foreach (var f in Fixes) o = f.With(o, f.IsEnabled);
            return o;
        }
    }

    internal void OnFixChanged() => OnParametersChanged();

    protected override void OnResult(ClipToolResult result)
    {
        if (result.Extra is not string[] effects) return;
        for (int i = 0; i < Fixes.Count && i < effects.Length; i++)
        {
            Fixes[i].Effect = effects[i];
            Fixes[i].HasEffect = effects[i] != "nothing to fix";
        }
    }

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        var clip = Original;
        var options = Options;
        var singles = Fixes.Select(f => f.With(None, true)).ToArray();
        return Work(ct =>
        {
            string[] effects = new string[singles.Length];
            for (int i = 0; i < singles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                ClipEdit.Normalize(clip, singles[i], out var single);
                effects[i] = single.ToString();
            }
            var result = ClipEdit.Normalize(clip, options, out var report);
            if (ReferenceEquals(result, clip))
                return new ClipToolResult(clip, "Normalise", ["Nothing to fix with the repairs chosen."], effects);
            return new ClipToolResult(result, "Normalise",
            [
                "Repairs: " + report,
                KeyDiff.Of(clip, result).KeysLine(),
            ], effects);
        });
    }
}
