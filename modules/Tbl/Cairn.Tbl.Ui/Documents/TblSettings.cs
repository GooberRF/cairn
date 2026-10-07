using System.Collections.Immutable;
using Cairn.Tbl.Linting;
using Cairn.Ui.Modules;
using Cairn.Workspace;

namespace Cairn.Tbl.Ui.Documents;

/// <summary>The table module's settings (keys <c>tbl.*</c> in the shared settings).</summary>
public sealed class TblSettings
{
    private readonly ModuleSettings _values;

    internal TblSettings(AppSettings settings) => _values = new ModuleSettings(settings, "tbl");

    /// <summary>Report errors.</summary>
    public bool LintErrors { get => _values.Get("lint.errors", true); set => _values.Set("lint.errors", value); }
    /// <summary>Report warnings.</summary>
    public bool LintWarnings { get => _values.Get("lint.warnings", true); set => _values.Set("lint.warnings", value); }
    /// <summary>Report information (duplicates the game tolerates, text it skips).</summary>
    public bool LintInformation { get => _values.Get("lint.info", true); set => _values.Set("lint.info", value); }
    /// <summary>Open the completion list by itself after <c>$</c>, <c>+</c> at a line start or after a field's colon.</summary>
    public bool CompletionAutoPopup { get => _values.Get("completion.auto", true); set => _values.Set("completion.auto", value); }
    /// <summary>Wrap long lines in the editor.</summary>
    public bool WordWrap { get => _values.Get("editor.wrap", false); set => _values.Set("editor.wrap", value); }

    /// <summary>Raised after the settings page committed changes.</summary>
    public event EventHandler? Changed;

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The diagnostics whose severity is switched on.</summary>
    public static ImmutableArray<TblDiagnostic> Filter(ImmutableArray<TblDiagnostic> diagnostics, bool errors, bool warnings, bool info)
    {
        if (errors && warnings && info) return diagnostics;
        return [.. diagnostics.Where(d => d.Severity switch
        {
            TblSeverity.Error => errors,
            TblSeverity.Warning => warnings,
            _ => info,
        })];
    }
}
