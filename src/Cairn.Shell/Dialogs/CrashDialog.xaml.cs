using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Cairn.Ui.Services;

namespace Cairn.Shell.Dialogs;

/// <summary>
/// The unexpected-error report: what happened, which documents were rescued, and the details with
/// a way to copy them or open the log they were written to.
/// </summary>
public partial class CrashDialog : Window
{
    private CrashDialog(string body, IReadOnlyList<string> rescued, string details)
    {
        InitializeComponent();
        BodyText.Text = body;
        RescuedList.ItemsSource = rescued;
        RescuedPanel.Visibility = rescued.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsText.Text = details;
        OpenLogButton.IsEnabled = LogPath is { } path && File.Exists(path);
        Loaded += (_, _) => CloseButton.Focus();
    }

    /// <summary>The crash log Open log opens; set by the app at startup; the button is disabled while there is no log.</summary>
    public static string? LogPath { get; set; }

    /// <summary>Shows the report modally.</summary>
    /// <param name="owner">Window to centre on, if one is loaded.</param>
    /// <param name="body">What happened and what it means for the user's work.</param>
    /// <param name="rescued">Names of the documents copied to the recovery folder.</param>
    /// <param name="details">The exception text.</param>
    public static void Show(Window? owner, string body, IReadOnlyList<string> rescued, string details)
    {
        var dialog = new CrashDialog(body, rescued, details) { Owner = owner is { IsLoaded: true, IsVisible: true } ? owner : null };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (ModalScope.Enter()) dialog.ShowDialog();
    }

    /// <summary>A non-modal instance for the diagnostic capture.</summary>
    internal static Window CreateForCapture(Window owner, string body, IReadOnlyList<string> rescued, string details) =>
        new CrashDialog(body, rescued, details) { Owner = owner, ShowInTaskbar = false };

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DetailsText.Text);
        }
        catch (COMException)
        {
            // Another process holds the clipboard; the text box still allows select and Ctrl+C.
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (LogPath is not { } path) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            // No handler for .log files: the path is in the details.
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
