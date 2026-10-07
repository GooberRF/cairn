using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Cairn.Ui.Services;

namespace Cairn.Shell.Dialogs;

/// <summary>Help &gt; About: what this is, who wrote it, and what it stands on.</summary>
public partial class AboutDialog : Window
{
    private const string AlpineFactionUrl = "https://alpinefaction.com";

    private AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = string.Format(CultureInfo.CurrentCulture, "Version {0} · Windows x64 · .NET 9", ShellWindows.Version);
        NoticesText.Text = ShellWindows.Embedded("Cairn.LICENSE").TrimEnd() + Environment.NewLine + Environment.NewLine
            + ShellWindows.Embedded("Cairn.THIRD-PARTY-NOTICES.md").TrimEnd();
        Loaded += (_, _) =>
        {
            // A read-only TextBox handed several kilobytes before it is measured can come up
            // showing the end of them; start where the reader would.
            NoticesText.CaretIndex = 0;
            NoticesText.ScrollToHome();
            CloseButton.Focus();
        };
    }

    /// <summary>A non-modal instance for the diagnostic capture.</summary>
    internal static Window CreateForCapture(Window owner) => new AboutDialog { Owner = owner };

    /// <summary>Shows the dialog modally.</summary>
    /// <param name="owner">Window to centre on.</param>
    public static void Show(Window? owner)
    {
        var dialog = new AboutDialog { Owner = owner is { IsLoaded: true } ? owner : null };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (ModalScope.Enter()) dialog.ShowDialog();
    }

    private void OnOpenSite(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AlpineFactionUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            // No default browser, or the shell refused: the address is on screen either way.
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
