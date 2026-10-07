using System;
using System.Windows;
using System.Windows.Media;
using Cairn.Workspace;
using Microsoft.Win32;

namespace Cairn.Ui.Services;

/// <summary>
/// Owns the app's look: the .NET 9 Fluent <see cref="ThemeMode"/> for stock controls, plus the
/// colour dictionary from Cairn.Ui (Themes/Light.xaml or Themes/Dark.xaml) for everything the design
/// specifies itself — squiggles, syntax colours, badges, the checkerboard. The colour dictionary is
/// always the last entry in <c>Application.Resources.MergedDictionaries</c>, so it wins over the
/// Fluent one, and swapping it repaints every <c>DynamicResource</c> reference in place.
/// </summary>
public sealed class ThemeService
{
    private static readonly Uri LightUri = new("pack://application:,,,/Cairn.Ui;component/Themes/Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkUri = new("pack://application:,,,/Cairn.Ui;component/Themes/Dark.xaml", UriKind.Absolute);
    private static readonly Uri ControlsUri = new("pack://application:,,,/Cairn.Ui;component/Themes/Controls.xaml", UriKind.Absolute);

    private readonly Application _application;
    private ResourceDictionary? _colours;
    private ResourceDictionary? _controls;
    private AppTheme _requested = AppTheme.System;

    public ThemeService(Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Raised after the effective theme changes, so the editor can rebuild its colours.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>What the user asked for (System follows Windows).</summary>
    public AppTheme Requested => _requested;

    /// <summary>The theme actually in force: never <see cref="AppTheme.System"/>.</summary>
    public AppTheme Effective { get; private set; } = AppTheme.Light;

    /// <summary>True when the dark colour set is in force.</summary>
    public bool IsDark => Effective == AppTheme.Dark;

    /// <summary>Applies a theme choice, resolving System against the Windows setting.</summary>
    public void Apply(AppTheme theme)
    {
        _requested = theme;
        var effective = theme == AppTheme.System ? ReadWindowsTheme() : theme;
        bool changed = effective != Effective || _colours is null;
        Effective = effective;

        ApplyFluentThemeMode(theme);
        EnsureDictionaries(effective);
        if (changed) ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Looks a theme colour up, falling back to <paramref name="fallback"/> when absent.</summary>
    public Color Color(string key, Color fallback)
    {
        object? value = _application.TryFindResource(key);
        return value is Color c ? c : fallback;
    }

    /// <summary>Looks a theme brush up, falling back to <paramref name="fallback"/> when absent.</summary>
    public Brush Brush(string key, Brush fallback)
    {
        object? value = _application.TryFindResource(key);
        return value is Brush b ? b : fallback;
    }

    private void ApplyFluentThemeMode(AppTheme theme)
    {
        try
        {
            _application.ThemeMode = theme switch
            {
                AppTheme.Light => ThemeMode.Light,
                AppTheme.Dark => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Fluent theming is experimental; if the runtime refuses it, the app's own colour
            // dictionary still gives a complete, readable light or dark surface.
        }
    }

    private void EnsureDictionaries(AppTheme effective)
    {
        var merged = _application.Resources.MergedDictionaries;
        var wanted = effective == AppTheme.Dark ? DarkUri : LightUri;

        if (_colours is null)
        {
            _colours = new ResourceDictionary { Source = wanted };
            _controls = new ResourceDictionary { Source = ControlsUri };
        }
        else if (_colours.Source != wanted)
        {
            _colours.Source = wanted;
        }

        // Keep both at the end, in this order: Fluent inserts itself at the front on every
        // ThemeMode change, and the app's colours must stay on top of it.
        merged.Remove(_colours);
        merged.Remove(_controls);
        merged.Add(_colours);
        merged.Add(_controls!);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        if (_requested != AppTheme.System) return;
        _application.Dispatcher.BeginInvoke(new Action(() => Apply(AppTheme.System)));
    }

    /// <summary>Reads the Windows "app mode" preference; anything unreadable means light.</summary>
    private static AppTheme ReadWindowsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? AppTheme.Dark
                : AppTheme.Light;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
            or UnauthorizedAccessException or System.IO.IOException)
        {
            return AppTheme.Light;
        }
    }
}
