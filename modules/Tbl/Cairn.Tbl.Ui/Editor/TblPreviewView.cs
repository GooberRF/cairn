using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cairn.Tbl.Text;
using Cairn.Tbl.Ui.Documents;
using ICSharpCode.AvalonEdit;

namespace Cairn.Tbl.Ui.Editor;

/// <summary>
/// The read-only highlighted view of a table for other modules' preview panes (packfile entries). The text is
/// decoded at once; parsing and classification run on a pool thread, so creating and disposing previews in quick
/// succession stays cheap. No document, undo history or settings are touched.
/// </summary>
public sealed class TblPreviewView : Border, IDisposable
{
    private readonly TblEditorController _controller;
    private CancellationTokenSource? _cts = new();

    /// <summary>Whether table previews wrap long lines (on by default; remembered for the session).</summary>
    private static bool s_wrap = true;

    public TblPreviewView(TblModule module, byte[] bytes, string fileName)
    {
        var file = TblTextFiles.Decode(bytes);
        var editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        editor.SetResourceReference(Control.BackgroundProperty, "Editor.Background");
        editor.SetResourceReference(Control.ForegroundProperty, "Editor.Foreground");
        editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "Editor.LineNumber");
        System.Windows.Automation.AutomationProperties.SetName(editor, "Table preview of " + fileName);
        editor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument(file.Text) { UndoStack = { SizeLimit = 0 } };
        SetResourceReference(BackgroundProperty, "Editor.Background");
        Editor = editor;
        FileName = fileName;
        _controller = new TblEditorController(editor, module, null);

        // Previews wrap long lines by default (the pane is narrow); the toggle is shared by every table preview of
        // the session and is separate from the editor tabs' own word-wrap setting.
        editor.WordWrap = s_wrap;
        var wrap = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = "Wrap",
            IsChecked = s_wrap,
            ToolTip = "Wrap long lines to the width of the pane (off: scroll sideways)",
            Margin = new Thickness(4, 2, 4, 2),
            Padding = new Thickness(7, 0, 7, 0),
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        wrap.SetResourceReference(StyleProperty, "ToolToggle");
        System.Windows.Automation.AutomationProperties.SetName(wrap, "Wrap long lines");
        wrap.Click += (_, _) => { s_wrap = wrap.IsChecked == true; editor.WordWrap = s_wrap; };
        var bar = new Border { Child = wrap, BorderThickness = new Thickness(0, 0, 0, 1) };
        bar.SetResourceReference(Border.BackgroundProperty, "App.ChromeBackground");
        bar.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(editor);
        Child = dock;

        var token = _cts.Token;
        var schema = module.Schemas.Find(fileName);
        var dispatcher = Dispatcher;
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var parsed = Cairn.Tbl.Model.TblDocument.Parse(file.Text, schema);
            var classes = Cairn.Tbl.Model.TblClassifier.Classify(parsed);
            return new TblModel(0, file.Text, parsed, schema, classes, []);
        }, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (_cts is not null) _controller.SetReadOnlyModel(t.Result); });
            }
        }, TaskScheduler.Default);
    }

    /// <summary>The previewed file's name.</summary>
    public string FileName { get; }

    /// <summary>The read-only editor.</summary>
    public TextEditor Editor { get; }

    /// <summary>The controller (highlighting and folding).</summary>
    public TblEditorController Controller => _controller;

    public void Dispose()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        _controller.Dispose();
        Editor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
        Child = null;
    }
}
