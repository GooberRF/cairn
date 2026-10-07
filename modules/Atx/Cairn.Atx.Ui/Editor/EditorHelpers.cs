using System;
using System.Collections.Generic;
using System.Linq;
using Cairn.Atx.Ui.ViewModels;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;

namespace Cairn.Atx.Ui.Editor;

/// <summary>Collapsible regions: one per <c>[[frame]]</c> block, keeping its declaration visible.</summary>
public static class FrameFolding
{
    /// <summary>Rebuilds the foldings from the document's syntax map.</summary>
    public static void Update(FoldingManager manager, DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(document);

        var text = document.Document;
        var foldings = new List<NewFolding>();
        foreach (var block in document.Parse.SyntaxMap.FrameBlocks)
        {
            int start = Math.Clamp(block.DeclarationSpan.End, 0, text.TextLength);
            int end = Math.Clamp(block.ContentSpan.End, 0, text.TextLength);
            if (end <= start) continue;
            if (text.GetLineByOffset(start).LineNumber == text.GetLineByOffset(end).LineNumber) continue;
            foldings.Add(new NewFolding(start, end) { Name = " …" });
        }
        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        manager.UpdateFoldings(foldings, -1);
    }
}

/// <summary>Ctrl+/ : comments or uncomments the selected lines.</summary>
public static class CommentToggle
{
    /// <summary>
    /// Toggles <c># </c> on every line the selection touches. When every non-blank line is already
    /// commented the comment markers come off; otherwise they go on, aligned to the shallowest
    /// indent so a block stays readable.
    /// </summary>
    public static void Toggle(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var document = editor.Document;
        var area = editor.TextArea;

        int startLine = document.GetLineByOffset(area.Selection.IsEmpty
            ? area.Caret.Offset
            : area.Selection.SurroundingSegment.Offset).LineNumber;
        int endLine = document.GetLineByOffset(area.Selection.IsEmpty
            ? area.Caret.Offset
            : Math.Max(area.Selection.SurroundingSegment.Offset,
                       area.Selection.SurroundingSegment.EndOffset - 1)).LineNumber;

        var lines = Enumerable.Range(startLine, endLine - startLine + 1)
            .Select(document.GetLineByNumber)
            .ToList();
        var content = lines.Select(l => document.GetText(l)).ToList();
        bool anyContent = content.Any(t => t.Trim().Length > 0);
        if (!anyContent) return;

        bool allCommented = content
            .Where(t => t.Trim().Length > 0)
            .All(t => t.TrimStart().StartsWith('#'));

        int indent = content
            .Where(t => t.Trim().Length > 0)
            .Min(t => t.Length - t.TrimStart().Length);

        document.BeginUpdate();
        try
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                string line = content[i];
                if (line.Trim().Length == 0) continue;
                if (allCommented)
                {
                    int hash = line.IndexOf('#', StringComparison.Ordinal);
                    if (hash < 0) continue;
                    int remove = hash + 1 < line.Length && line[hash + 1] == ' ' ? 2 : 1;
                    document.Remove(lines[i].Offset + hash, remove);
                }
                else
                {
                    document.Insert(lines[i].Offset + indent, "# ");
                }
            }
        }
        finally { document.EndUpdate(); }
    }
}
