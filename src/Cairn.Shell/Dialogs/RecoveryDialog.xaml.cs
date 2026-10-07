using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Shell.Dialogs;

/// <summary>One recoverable document, with its tick box.</summary>
public sealed class RecoveryItemViewModel : ObservableObject
{
    private bool _isSelected = true;

    internal RecoveryItemViewModel(RecoverySnapshot snapshot) => Snapshot = snapshot;

    /// <summary>The snapshot this row offers.</summary>
    public RecoverySnapshot Snapshot { get; }

    /// <summary>Whether Restore includes it; unticked rows are deleted on Restore.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>
    /// The tab caption the document had. A snapshot is a file on disk like any other, so what is
    /// in it is not necessarily what this app wrote; it is printed safely rather than trusted.
    /// </summary>
    public string DisplayName => Printable(Snapshot.DisplayName, 120);

    /// <summary>The whole original path, so restoring is never a guess about what it overwrites.</summary>
    public string PathText => Snapshot.OriginalPath is { Length: > 0 } path ? Printable(path, 260) : "Never saved to disk";

    /// <summary>When the snapshot was taken, in the user's local time.</summary>
    public string SavedText => Snapshot.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>True when the file on disk was written after this copy was made.</summary>
    public bool DiskIsNewer
    {
        get
        {
            try
            {
                return Snapshot.OriginalPath is { Length: > 0 } path && File.Exists(path) && File.GetLastWriteTimeUtc(path) > Snapshot.SavedUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
    }

    /// <summary>The warning under the row, or null when the file on disk is older.</summary>
    public string? DiskNote => DiskIsNewer ? "The file on disk is newer than this copy; saving would overwrite it." : null;

    /// <summary>Shows the warning line only when there is one.</summary>
    public Visibility DiskNoteVisibility => DiskIsNewer ? Visibility.Visible : Visibility.Collapsed;

    private static string Printable(string text, int max)
    {
        var builder = new StringBuilder(Math.Min(text.Length, max));
        foreach (var c in text)
        {
            if (builder.Length >= max) { builder.Append('…'); break; }
            builder.Append(char.IsControl(c) ? '?' : c);
        }
        return builder.ToString();
    }
}

/// <summary>
/// Offered at startup when the recovery folder is not empty. Deliberately not a yes/no prompt:
/// after a crash the useful question is which of these files you want back.
/// </summary>
public partial class RecoveryDialog : Window
{
    private IReadOnlyList<RecoverySnapshot>? _result;

    private RecoveryDialog(IReadOnlyList<RecoverySnapshot> snapshots)
    {
        InitializeComponent();
        foreach (var snapshot in snapshots) Items.Add(new RecoveryItemViewModel(snapshot));
        List.ItemsSource = Items;
        Loaded += (_, _) => RestoreButton.Focus();
    }

    /// <summary>The rows the dialog is showing.</summary>
    public ObservableCollection<RecoveryItemViewModel> Items { get; } = [];

    /// <summary>Shows the prompt modally.</summary>
    /// <param name="owner">Window to centre on, if one is loaded.</param>
    /// <param name="snapshots">The snapshots to offer, newest first.</param>
    /// <returns>
    /// The snapshots to restore (the rest are to be deleted; empty for Discard all), or null for
    /// Not now / closed, which leaves every copy for next time.
    /// </returns>
    public static IReadOnlyList<RecoverySnapshot>? Show(Window? owner, IReadOnlyList<RecoverySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var dialog = new RecoveryDialog(snapshots) { Owner = owner is { IsLoaded: true } ? owner : null };
        if (dialog.Owner is not null) dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        using (ModalScope.Enter()) dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>A non-modal instance for the diagnostic capture.</summary>
    internal static Window CreateForCapture(Window owner, IReadOnlyList<RecoverySnapshot> snapshots) =>
        new RecoveryDialog(snapshots) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        _result = [.. Items.Where(i => i.IsSelected).Select(i => i.Snapshot)];
        Close();
    }

    private void OnDiscardAll(object sender, RoutedEventArgs e)
    {
        _result = [];
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }
}
