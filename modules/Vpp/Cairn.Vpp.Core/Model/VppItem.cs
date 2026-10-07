namespace Cairn.Vpp.Model;

/// <summary>What has happened to an entry since the packfile was opened (or last saved).</summary>
public enum VppItemState
{
    /// <summary>Unchanged from the packfile on disk.</summary>
    Original,
    /// <summary>New in this session.</summary>
    Added,
    /// <summary>An original entry whose data was replaced (possibly also renamed).</summary>
    Replaced,
    /// <summary>An original entry with a new name and its original data.</summary>
    Renamed,
}

/// <summary>One entry of a packfile being edited.</summary>
/// <param name="Name">The name stored in the directory.</param>
/// <param name="Source">Where the data comes from.</param>
/// <param name="State">What happened to the entry in this session.</param>
public sealed record VppItem(string Name, VppSource Source, VppItemState State)
{
    /// <summary>The name the entry had in the packfile on disk, or null for an added entry.</summary>
    public string? OriginalName { get; init; }

    /// <summary>Position in the packfile's directory on disk, or -1 for an added entry (used to sort back to the original order).</summary>
    public int OriginalIndex { get; init; } = -1;

    /// <summary>Data length in bytes.</summary>
    public long Size => Source.Size;

    /// <summary>The lower-case extension including the dot ("" when there is none).</summary>
    public string Extension => Editing.VppNames.ExtensionOf(Name);

    /// <summary>True when the name differs from the original (beyond nothing at all).</summary>
    public bool IsRenamed => OriginalName is not null && !string.Equals(OriginalName, Name, StringComparison.Ordinal);
}
