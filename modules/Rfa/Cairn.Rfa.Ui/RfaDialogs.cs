namespace Cairn.Rfa.Ui;

/// <summary>
/// RFA's dialog launches over the shell's <see cref="IDialogService"/>: the old RFA signatures, which implied
/// RFA's file filters (the shared service takes the filter explicitly).
/// </summary>
public static class RfaDialogs
{
    /// <summary>Every file RFA opens.</summary>
    public const string OpenFilter =
        "RF animations and meshes (*.rfa;*.v3c;*.v3m)|*.rfa;*.v3c;*.v3m|RF animations (*.rfa)|*.rfa|Character meshes (*.v3c)|*.v3c|Static meshes (*.v3m)|*.v3m|All files (*.*)|*.*";

    /// <summary>The save filter for <paramref name="extension"/>.</summary>
    public static string FilterFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".rfa" => "RF animations (*.rfa)|*.rfa",
        ".v3c" => "Character meshes (*.v3c)|*.v3c",
        ".v3m" => "Static meshes (*.v3m)|*.v3m",
        ".gltf" or ".glb" => "glTF (*.gltf;*.glb)|*.gltf;*.glb",
        _ => $"*{extension}|*{extension}",
    };

    /// <summary>RFA's <c>OpenDocuments(folder)</c>.</summary>
    public static string[] OpenDocuments(this IDialogService dialogs, string? initialFolder) => dialogs.OpenDocuments(initialFolder, OpenFilter);

    /// <summary>RFA's <c>SaveDocument(folder, name, extension)</c>.</summary>
    public static string? SaveDocument(this IDialogService dialogs, string? initialFolder, string suggestedName, string extension) =>
        dialogs.SaveDocument(initialFolder, suggestedName, extension, FilterFor(extension));

    /// <summary>RFA's Settings launch: the shell's dialog at the RFA page.</summary>
    public static void ShowSettings(this IDialogService dialogs, object? owner) => RfaUi.Shell?.ShowSettings(RfaModule.SettingsPageTitle);
    /// <summary>RFA's format reference: the shell's help window at the RFA topic.</summary>
    public static void ShowFormatReference(this IDialogService dialogs) => RfaUi.Shell?.ShowHelp(RfaModule.FormatHelpTopicId);
    /// <summary>The shell's shortcut table.</summary>
    public static void ShowShortcuts(this IDialogService dialogs) => RfaUi.Shell?.ShowHelp("shortcuts");
    /// <summary>The shell's About is in its Help menu; RFA's command opens the format reference instead.</summary>
    public static void ShowAbout(this IDialogService dialogs) => RfaUi.Shell?.ShowHelp(RfaModule.FormatHelpTopicId);
}
