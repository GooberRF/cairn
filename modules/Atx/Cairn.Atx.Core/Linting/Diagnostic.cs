using Cairn.Atx.Text;

namespace Cairn.Atx.Linting;

/// <summary>
/// How much a problem matters. Errors mean the game will refuse to load the ATX; warnings mean it
/// loads but not the way it was written (or we could not verify it); info is tidy-up only.
/// </summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>What a quick fix does. Edit fixes are applied by the editor; the others need the UI.</summary>
public enum QuickFixKind
{
    /// <summary>Applies <see cref="QuickFix.CreateEdits"/> to the document.</summary>
    Edit,
    /// <summary>Opens a file picker so the user can point at the missing image (<see cref="QuickFix.Payload"/>).</summary>
    LocateFile,
    /// <summary>Opens the Add Frames dialog.</summary>
    AddFrames,
    /// <summary>Runs the "Convert to standard layout" command.</summary>
    ConvertToStandardLayout,
    /// <summary>
    /// Opens Settings, where the game directory and the extra search folders live. Offered
    /// alongside "not found" diagnostics so the advice in their help text is one click away.
    /// </summary>
    OpenSearchSettings,
}

/// <summary>One offered repair for a diagnostic.</summary>
/// <param name="Title">Button text, e.g. "Set to 1".</param>
/// <param name="Kind">How the UI should carry the fix out.</param>
/// <param name="CreateEdits">Produces the edits for <see cref="QuickFixKind.Edit"/> fixes.</param>
/// <param name="Payload">Extra context for UI-driven fixes, such as the filename to locate.</param>
public sealed record QuickFix(
    string Title,
    QuickFixKind Kind,
    Func<TextEditBatch>? CreateEdits = null,
    string? Payload = null)
{
    /// <summary>Builds the edits for an <see cref="QuickFixKind.Edit"/> fix.</summary>
    public TextEditBatch Apply() => CreateEdits?.Invoke() ?? TextEditBatch.Empty;
}

/// <summary>
/// One problem found in a document. <see cref="Message"/> says what is wrong, <see cref="Help"/>
/// says exactly how to fix it; both are written for level designers rather than programmers.
/// </summary>
/// <param name="Code">Stable rule id, e.g. <c>ATX021</c>.</param>
/// <param name="Severity">Error, warning or info.</param>
/// <param name="Message">What is wrong, in plain language.</param>
/// <param name="Help">How to fix it.</param>
/// <param name="Span">Where to put the squiggle.</param>
/// <param name="FrameIndex">The 0-based frame this concerns, if any.</param>
/// <param name="Key">The key this concerns, if any.</param>
/// <param name="QuickFixes">Offered repairs.</param>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string Help,
    TextSpan Span,
    int? FrameIndex = null,
    string? Key = null,
    IReadOnlyList<QuickFix>? QuickFixes = null)
{
    /// <summary>Offered repairs; never null.</summary>
    public IReadOnlyList<QuickFix> QuickFixes { get; init; } = QuickFixes ?? [];

    public override string ToString() => $"{Code} {Severity}: {Message}";
}
