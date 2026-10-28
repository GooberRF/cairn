using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cairn.Ui.Modules;

namespace Cairn.Shell;

/// <summary>
/// Routes key presses to the shell's and the modules' <see cref="ShortcutInfo"/>s from a window's
/// <c>PreviewKeyDown</c>. Unlike <c>KeyBinding</c>s this accepts gestures without modifiers (Space, letters,
/// Delete...), lets two modules share a gesture (the active document decides) and gives way to text inputs.
/// </summary>
public sealed class ShortcutRouter(ShellViewModel shell)
{
    /// <summary>Routes the key presses of <paramref name="window"/> (the main window, or another window the shell owns).</summary>
    public void Attach(Window window) => window.PreviewKeyDown += OnPreviewKeyDown;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        var focused = e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject;
        if (Route(sender as Window, key, Keyboard.Modifiers, focused, out _)) e.Handled = true;
    }

    /// <summary>
    /// What a key press in <paramref name="window"/> does: true when a shortcut ran. False when none applies, or when
    /// <paramref name="blockedBy"/> names the guard that let the key through untouched (a modifier key on its own, an
    /// open menu or popup, a modal dialog, or movement keys claimed by the focused element, see
    /// <see cref="KeyboardClaims"/>).
    /// </summary>
    public bool Route(Window? window, Key key, ModifierKeys modifiers, DependencyObject? focused, out string? blockedBy)
    {
        blockedBy = IsModifierKey(key) ? "a modifier key on its own"
            : ComponentDispatcher.IsThreadModal ? "a modal dialog is running"
            : IsMenuOrPopupActive(window, focused) ? "a menu or popup holds the keyboard"
            : KeyboardClaims.ClaimsMovementKey(focused, key, modifiers) ? "the focused element claims its movement keys"
            : null;
        return blockedBy is null && TryExecute(key, modifiers, focused);
    }

    /// <summary>
    /// Runs the first shortcut for <paramref name="key"/>+<paramref name="modifiers"/> that applies to the active
    /// document, does not yield to the focused text input and can execute. Order: the shortcuts of the module that
    /// owns the active document's kind, then the shell's, then the other modules'.
    /// </summary>
    public bool TryExecute(Key key, ModifierKeys modifiers, DependencyObject? focused)
    {
        var textInput = IsInTextInput(focused);
        foreach (var s in Candidates())
        {
            if (s.Key != key || s.Modifiers != modifiers) continue;
            if (s.AppliesTo is { } applies && !applies(shell.ActiveDocument)) continue;
            if (textInput && YieldsToTextInput(s)) continue;
            if (!s.Command.CanExecute(null)) continue;
            s.Command.Execute(null);
            return true;
        }
        return false;
    }

    /// <summary>Every shortcut in routing order for the active document.</summary>
    public IEnumerable<ShortcutInfo> Candidates()
    {
        var kindId = shell.ActiveDocument?.Kind.Id;
        var owner = kindId is null ? null : shell.Modules.FirstOrDefault(m => m.DocumentKinds.Any(k => k.Id == kindId));
        return [.. owner?.Shortcuts ?? [], .. shell.ShellShortcuts, .. shell.Modules.Where(m => m != owner).SelectMany(m => m.Shortcuts)];
    }

    /// <summary>
    /// True when a shortcut must give way to a focused text input: gestures without modifiers (or Shift only) and
    /// the clipboard, undo, select-all and caret/editing gestures (Ctrl+C/X/V/Z/Y/A, Delete, Backspace, arrows,
    /// Home/End, Space, Enter...), unless the shortcut sets <see cref="ShortcutInfo.AllowInTextInput"/>.
    /// </summary>
    public static bool YieldsToTextInput(ShortcutInfo s)
    {
        if (s.AllowInTextInput) return false;
        if (s.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return true;
        if (s.Modifiers.HasFlag(ModifierKeys.Alt) || s.Modifiers.HasFlag(ModifierKeys.Windows)) return false;
        return s.Key is Key.C or Key.X or Key.V or Key.Z or Key.Y or Key.A or Key.Insert or Key.Delete or Key.Back
            or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Space or Key.Enter;
    }

    /// <summary>
    /// True when <paramref name="element"/> is, or sits inside, a control that takes text input: any
    /// <see cref="TextBoxBase"/>, <see cref="PasswordBox"/>, editable <see cref="ComboBox"/>, or a text editor
    /// control recognised by its type name (TextArea/TextEditor/TextView, e.g. a code editor) without referencing it.
    /// </summary>
    public static bool IsInTextInput(DependencyObject? element)
    {
        for (var d = element; d != null && d is not Window; d = Parent(d))
        {
            if (d is TextBoxBase or PasswordBox or ComboBox { IsEditable: true }) return true;
            for (var t = d.GetType(); t != null && t != typeof(FrameworkElement); t = t.BaseType)
                if (t.Name is "TextArea" or "TextEditor" or "TextView") return true;
        }
        return false;
    }

    /// <summary>True while a menu, context menu, popup or combo drop-down holds the keyboard, or a modal dialog runs.</summary>
    public static bool IsMenuOrPopupActive(Window? window, DependencyObject? focused)
    {
        if (ComponentDispatcher.IsThreadModal) return true;
        if (focused is Visual v && PresentationSource.FromVisual(v) is HwndSource { RootVisual: not Window }) return true;
        for (var d = focused; d != null; d = Parent(d))
            if (d is MenuBase or MenuItem or ContextMenu or Popup or ComboBox { IsDropDownOpen: true }) return true;
        return window is MainWindow main && main.MainMenu.Items.OfType<MenuItem>().Any(m => m.IsSubmenuOpen);
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None or Key.DeadCharProcessed;

    private static DependencyObject? Parent(DependencyObject d) => d switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d),
        FrameworkContentElement fce => fce.Parent,
        _ => LogicalTreeHelper.GetParent(d),
    };
}
