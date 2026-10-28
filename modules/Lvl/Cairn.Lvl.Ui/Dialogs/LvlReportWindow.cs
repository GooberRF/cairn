using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Lvl.Conversion;

namespace Cairn.Lvl.Ui.Dialogs;

/// <summary>One converted (or failed) source for the report.</summary>
/// <param name="SourceName">The level or group file.</param>
/// <param name="Report">The conversion's report, or null when it did not get that far.</param>
/// <param name="Written">The files or entries written, the main one first.</param>
/// <param name="Error">Why nothing was written, or null.</param>
public sealed record LvlConvertOutcome(string SourceName, LvlConvertReport? Report, IReadOnlyList<string> Written, string? Error)
{
    public bool Succeeded => Written.Count > 0;
}

/// <summary>
/// The report after a conversion: what was written where, the counts, and every entry of each source's report (errors
/// first: what was approximated, dropped or split, and the engine limits the output breaks), with "Copy report".
/// </summary>
public sealed class LvlReportWindow : Window
{
    /// <param name="summary">The one-line summary.</param>
    /// <param name="outcomes">Each source's outcome.</param>
    public LvlReportWindow(string summary, IReadOnlyList<LvlConvertOutcome> outcomes)
    {
        Text = ReportText(summary, outcomes);
        Title = outcomes.Count == 1 ? "Converted " + outcomes[0].SourceName : "Conversion report";
        Width = 720; Height = 560; MinWidth = 420; MinHeight = 300;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");

        var root = new DockPanel { Margin = new Thickness(14) };
        var head = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var copy = new Button { Content = "_Copy report", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Copy the whole report as text" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(Text); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
        };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 90, ToolTip = "Close the report" };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in new[] { copy, close }) { b.SetResourceReference(StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var body = new StackPanel();
        foreach (var outcome in outcomes) body.Children.Add(Section(outcome, outcomes.Count > 1));
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(scroll, "Report");
        root.Children.Add(scroll);
        Content = root;
    }

    /// <summary>The whole report as text (also what "Copy report" copies).</summary>
    public string Text { get; }

    private static FrameworkElement Section(LvlConvertOutcome outcome, bool heading)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        TextBlock Line(string text, string brush = "App.Text", bool bold = false)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            panel.Children.Add(t);
            return t;
        }
        if (heading) Line(outcome.SourceName, bold: true);
        if (outcome.Error is { } error) Line("Not converted: " + error, "Severity.Error");
        if (outcome.Written.Count > 0)
            Line(outcome.Written.Count == 1 ? "Wrote " + outcome.Written[0]
                : string.Format(CultureInfo.CurrentCulture, "Wrote {0} and {1:N0} more file(s)", outcome.Written[0], outcome.Written.Count - 1), "App.SecondaryText");
        if (outcome.Report is { } report)
        {
            Line(StatsText(report.Stats), "App.SecondaryText");
            foreach (var issue in report.Issues.OrderByDescending(i => i.Severity))
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var code = new TextBlock { Text = $"{issue.Code}  {SeverityText(issue.Severity)}", Width = 120, VerticalAlignment = VerticalAlignment.Top };
                code.SetResourceReference(TextBlock.ForegroundProperty, issue.Severity switch
                {
                    LvlIssueSeverity.Error => "Severity.Error",
                    LvlIssueSeverity.Warning => "Severity.Warning",
                    _ => "App.SecondaryText",
                });
                DockPanel.SetDock(code, Dock.Left);
                row.Children.Add(code);
                var message = new TextBox { Text = issue.Message, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap };
                message.SetResourceReference(ForegroundProperty, "App.Text");
                AutomationProperties.SetName(message, issue.Code);
                row.Children.Add(message);
                panel.Children.Add(row);
            }
        }
        return panel;
    }

    private static string SeverityText(LvlIssueSeverity severity) => severity switch
    {
        LvlIssueSeverity.Error => "error",
        LvlIssueSeverity.Warning => "warning",
        _ => "note",
    };

    internal static string StatsText(LvlConvertStats s) => string.Format(CultureInfo.CurrentCulture,
        "{0:N0} of {1:N0} faces, {2:N0} triangles, {3:N0} vertices, {4:N0} parts, {5:N0} materials, {6:N0} textures, {7:N0} objects",
        s.Faces, s.SourceFaces, s.Triangles, s.Vertices, s.Parts, s.Materials, s.Textures, s.Objects);

    /// <summary>The report as text: the summary, then each source with its files, counts and entries.</summary>
    public static string ReportText(string summary, IReadOnlyList<LvlConvertOutcome> outcomes)
    {
        var sb = new StringBuilder().AppendLine(summary);
        foreach (var o in outcomes)
        {
            sb.AppendLine().AppendLine(o.SourceName);
            if (o.Error is { } error) sb.Append("  Not converted: ").AppendLine(error);
            foreach (var file in o.Written) sb.Append("  Wrote ").AppendLine(file);
            if (o.Report is { } report)
            {
                sb.Append("  ").AppendLine(StatsText(report.Stats));
                foreach (var issue in report.Issues.OrderByDescending(i => i.Severity)) sb.Append("  ").AppendLine(issue.ToString());
            }
        }
        return sb.ToString();
    }
}
