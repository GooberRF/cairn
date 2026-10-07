namespace Cairn.Rfa.Ui;

/// <summary>Shell services RFA's controls reach statically (was <c>App.Theme</c>); set by <see cref="RfaModule.Initialize"/>.</summary>
public static class RfaUi
{
    /// <summary>The shell's theme service, or null before the module initialises (designer, tests).</summary>
    public static ThemeService? Theme { get; set; }

    /// <summary>The shell, once the module initialises.</summary>
    public static IShellContext? Shell { get; set; }
}
