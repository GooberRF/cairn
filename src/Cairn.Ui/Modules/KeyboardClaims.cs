using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Cairn.Ui.Modules;

/// <summary>
/// Keys an element takes for itself while it (or an element inside it) has the keyboard focus, so the shell's
/// shortcut routing gives way to it. A fly camera sets <see cref="MovementKeysProperty"/> on its surface while flying:
/// W A S D Q E with Shift or Ctrl (faster, slower) then move the camera instead of running Ctrl+W (close the tab) or
/// Ctrl+Shift+E. Gestures with Alt or the Windows key are never claimed.
/// </summary>
public static class KeyboardClaims
{
    /// <summary>True on an element that takes the movement keys W A S D Q E, with or without Shift and Ctrl.</summary>
    public static readonly DependencyProperty MovementKeysProperty = DependencyProperty.RegisterAttached(
        "MovementKeys", typeof(bool), typeof(KeyboardClaims), new PropertyMetadata(false));

    public static bool GetMovementKeys(DependencyObject element) => (bool)element.GetValue(MovementKeysProperty);

    public static void SetMovementKeys(DependencyObject element, bool value) => element.SetValue(MovementKeysProperty, value);

    /// <summary>The keys <see cref="MovementKeysProperty"/> claims.</summary>
    public static bool IsMovementKey(Key key) => key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E;

    /// <summary>
    /// True when <paramref name="key"/> with <paramref name="modifiers"/> is a movement key claimed by
    /// <paramref name="focused"/> or one of its ancestors.
    /// </summary>
    public static bool ClaimsMovementKey(DependencyObject? focused, Key key, ModifierKeys modifiers)
    {
        if (!IsMovementKey(key) || (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        for (var d = focused; d != null && d is not Window; d = Parent(d))
            if (GetMovementKeys(d)) return true;
        return false;
    }

    private static DependencyObject? Parent(DependencyObject d) => d switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d),
        FrameworkContentElement fce => fce.Parent,
        _ => LogicalTreeHelper.GetParent(d),
    };
}
