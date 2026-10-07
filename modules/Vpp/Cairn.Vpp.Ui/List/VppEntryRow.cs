using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Ui.List;

/// <summary>One line of the file list: an entry of the current snapshot plus its inline-rename state.</summary>
public sealed class VppEntryRow : ObservableObject
{
    private bool _isEditing;
    private string _editText = string.Empty;

    public VppEntryRow(VppItem item, int index, IReadOnlyList<VppProblem>? problems = null)
    {
        Item = item;
        Index = index;
        var type = VppFileTypes.Describe(item.Name);
        TypeName = type.DisplayName;
        Category = type.Category;
        Extension = item.Extension;
        Problems = problems ?? [];
    }

    /// <summary>This entry's warnings and errors (information-only problems are left out).</summary>
    public IReadOnlyList<VppProblem> Problems { get; }
    public bool HasProblem => Problems.Count > 0;
    public bool HasError => Problems.Any(p => p.Severity == VppSeverity.Error);
    /// <summary>True when the name does not fit the game's 31-character texture/sound/font name slot (VPP027).</summary>
    public bool NameTooLong => Problems.Any(p => p.Code == LongNameCode);
    /// <summary>Marker glyph for the problem column (Segoe MDL2 "Warning"), empty without problems.</summary>
    public string ProblemGlyph => HasProblem ? "" : string.Empty;
    public string? ProblemToolTip => HasProblem ? string.Join("\n", Problems.Select(p => p.Message)) : null;

    internal const string LongNameCode = "VPP027";

    public VppItem Item { get; }
    /// <summary>Position in the packfile (directory order).</summary>
    public int Index { get; }
    public string Name => Item.Name;
    public string TypeName { get; }
    public VppFileCategory Category { get; }
    public string Extension { get; }
    public long Size => Item.Size;
    public string SizeText => FormatSize(Item.Size);
    public string SizeToolTip => Item.Size.ToString("N0", CultureInfo.CurrentCulture) + " bytes";

    /// <summary>"added", "replaced", "renamed" or empty; renamed-and-replaced shows "replaced"; an unchanged entry whose name is too long for the game shows "name too long".</summary>
    public string StateText => Item.State switch
    {
        VppItemState.Added => "added",
        VppItemState.Replaced => "replaced",
        VppItemState.Renamed => "renamed",
        _ => Item.IsRenamed ? "renamed" : NameTooLong ? "name too long" : string.Empty,
    };

    public string? StateToolTip => Item.State switch
    {
        VppItemState.Added => "Added: " + Item.Source.Describe(),
        VppItemState.Replaced => "Replaced from " + Item.Source.Describe() + (Item.IsRenamed ? $"; was named {Item.OriginalName}" : string.Empty),
        _ => Item.IsRenamed ? $"Renamed from {Item.OriginalName}" : ProblemToolTip,
    };

    private VppInfoLine? _info;

    /// <summary>The one-line summary for the Info column (filled in the background by <see cref="VppInfoCache"/>); null until known.</summary>
    public VppInfoLine? Info
    {
        get => _info;
        internal set
        {
            if (Equals(_info, value)) return;
            _info = value;
            RaiseAll(nameof(Info), nameof(InfoText), nameof(InfoToolTip), nameof(InfoIsUnreadable));
        }
    }

    /// <summary>"1024x1024, 32-bit, RLE compressed"; empty while unknown or when there is nothing to say.</summary>
    public string InfoText => _info?.Text ?? string.Empty;
    public string? InfoToolTip => _info?.ToolTip;
    /// <summary>True when the data could not be read as its type (the cell shows a dim marker).</summary>
    public bool InfoIsUnreadable => _info?.IsUnreadable == true;

    /// <summary>True while the name cell shows its text box (F2).</summary>
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
    public string EditText { get => _editText; set => Set(ref _editText, value); }

    /// <summary>"0 bytes", "12.3 KB", "4.56 MB", "1.20 GB" (1 KB = 1,024 bytes).</summary>
    public static string FormatSize(long bytes)
    {
        var c = CultureInfo.CurrentCulture;
        if (bytes < 1024) return bytes.ToString("N0", c) + (bytes == 1 ? " byte" : " bytes");
        double v = bytes / 1024.0;
        if (v < 1024) return v.ToString(v < 10 ? "0.0" : "N0", c) + " KB";
        v /= 1024;
        if (v < 1024) return v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "N0", c) + " MB";
        v /= 1024;
        return v.ToString("0.00", c) + " GB";
    }
}
