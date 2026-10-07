using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>Entry points for the About dialog and the file-association settings page (in <c>Dialogs/</c>), plus a few shared helpers.</summary>
public static class ShellWindows
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static string Embedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream is null ? string.Empty : new StreamReader(stream).ReadToEnd();
    }

    public static void ShowAbout(Window owner) => Dialogs.AboutDialog.Show(owner);

    /// <summary>Tools &gt; File Associations…: the settings dialog at its File associations page.</summary>
    public static void ShowAssociations(ShellViewModel shell) => shell.ShowSettings(Dialogs.AssociationsModel.PageTitle);
}
