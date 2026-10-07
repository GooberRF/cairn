using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Cairn.Ui.Controls;

/// <summary>One button offered by a <see cref="ChoiceDialog"/>.</summary>
/// <param name="Text">Button label, with an <c>_</c> before its access key.</param>
/// <param name="IsDefault">True for the button Enter activates.</param>
/// <param name="IsCancel">True for the button Esc activates.</param>
public sealed record ChoiceButton(string Text, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// The app's only modal dialog shape: a heading, a sentence of explanation, an optional list of
/// affected files, optional technical details, and up to a handful of buttons. Every prompt the
/// design calls for (unsaved changes, save with errors, copy-or-reference, the crash report) is one
/// of these, so they all look and behave the same.
/// </summary>
public partial class ChoiceDialog : Window
{
    private int _result = -1;

    private ChoiceDialog() => InitializeComponent();

    /// <summary>
    /// Shows the dialog and returns the 0-based index of the button pressed, or -1 when the dialog
    /// was dismissed without choosing (Esc, or the title-bar close button).
    /// </summary>
    /// <param name="owner">Window to centre on.</param>
    /// <param name="title">Title-bar text.</param>
    /// <param name="heading">The question, in one short line.</param>
    /// <param name="body">One or two sentences of explanation.</param>
    /// <param name="buttons">The choices, left to right.</param>
    /// <param name="items">Optional list of file names the choice affects.</param>
    /// <param name="details">Optional technical text behind an expander.</param>
    public static int Show(
        Window? owner,
        string title,
        string heading,
        string? body,
        IReadOnlyList<ChoiceButton> buttons,
        IReadOnlyList<string>? items = null,
        string? details = null)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        var dialog = new ChoiceDialog
        {
            Title = title,
            Owner = owner is { IsLoaded: true } ? owner : null,
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.HeadingText.Text = heading;
        dialog.BodyText.Text = body ?? string.Empty;
        dialog.BodyText.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;

        if (items is { Count: > 0 })
        {
            dialog.ItemsList.ItemsSource = items;
            dialog.ItemsBox.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrEmpty(details))
        {
            dialog.DetailsText.Text = details;
            dialog.DetailsBox.Visibility = Visibility.Visible;
        }

        for (int i = 0; i < buttons.Count; i++)
        {
            var spec = buttons[i];
            int index = i;
            var button = new Button
            {
                // AccessText turns the '_' into an access key; a bare string would show it.
                Content = new AccessText { Text = spec.Text },
                MinWidth = 96,
                Height = 30,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = spec.IsDefault,
                IsCancel = spec.IsCancel,
            };
            AutomationProperties.SetName(button, spec.Text.Replace("_", string.Empty, StringComparison.Ordinal));
            button.Click += (_, _) => { dialog._result = index; dialog.DialogResult = true; };
            dialog.ButtonRow.Children.Add(button);
            if (spec.IsDefault) dialog.Loaded += (_, _) => button.Focus();
        }

        using (Services.ModalScope.Enter()) dialog.ShowDialog();
        return dialog._result;
    }
}
