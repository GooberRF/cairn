using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Cairn.Atx.Ui.ViewModels;
using Cairn.Atx.Editing;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace Cairn.Atx.Ui.Editor;

/// <summary>What part of the line a completion replaces when it is accepted.</summary>
public enum CompletionTarget
{
    /// <summary>The identifier being typed (a key name or a bare table name).</summary>
    Word,
    /// <summary>Everything to the right of <c>=</c>, up to a trailing comment.</summary>
    ValueRegion,
}

/// <summary>One completion entry, built from <c>AtxSchema</c> or from the ATX folder's file list.</summary>
public sealed class AtxCompletionData : ICompletionData
{
    private readonly string _insertText;
    private readonly int _replaceStart;
    private readonly CompletionTarget _target;

    /// <param name="text">What the list shows and filters on.</param>
    /// <param name="insertText">What is written into the document, e.g. a quoted token.</param>
    /// <param name="description">The documentation panel's text.</param>
    /// <param name="replaceStart">Offset the replacement starts at.</param>
    /// <param name="target">How far the replacement reaches.</param>
    /// <param name="priority">Sort weight; higher floats to the top.</param>
    public AtxCompletionData(
        string text, string insertText, string description,
        int replaceStart, CompletionTarget target, double priority = 0)
    {
        Text = text;
        _insertText = insertText;
        Description = description;
        _replaceStart = replaceStart;
        _target = target;
        Priority = priority;
    }

    public ImageSource? Image => null;

    public string Text { get; }

    public object Content => Text;

    public object Description { get; }

    public double Priority { get; }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        ArgumentNullException.ThrowIfNull(textArea);
        var document = textArea.Document;
        int start = Math.Clamp(_replaceStart, 0, document.TextLength);
        int end = _target == CompletionTarget.Word
            ? WordEnd(document, Math.Max(start, completionSegment?.EndOffset ?? start))
            : ValueEnd(document, start);
        end = Math.Clamp(end, start, document.TextLength);
        document.Replace(start, end - start, _insertText);
    }

    private static int WordEnd(ITextSource document, int offset)
    {
        int i = offset;
        while (i < document.TextLength && (char.IsLetterOrDigit(document.GetCharAt(i)) || document.GetCharAt(i) == '_'))
            i++;
        return i;
    }

    /// <summary>End of the value region: end of line, or the start of a trailing comment.</summary>
    private static int ValueEnd(ITextSource document, int start)
    {
        int i = start;
        bool inString = false;
        char quote = '"';
        while (i < document.TextLength)
        {
            char c = document.GetCharAt(i);
            if (c is '\r' or '\n') break;
            if (inString)
            {
                if (c == '\\') { i += 2; continue; }
                if (c == quote) inString = false;
            }
            else if (c is '"' or '\'') { inString = true; quote = c; }
            else if (c == '#') break;
            i++;
        }
        while (i > start && char.IsWhiteSpace(document.GetCharAt(i - 1))) i--;
        return i;
    }
}

/// <summary>
/// Context-aware completion for the source editor: the keys that are valid in the table the caret
/// is in and not already present, the tokens a given key accepts, and — for <c>file</c> and
/// <c>alpha_mask</c> — the image files sitting next to the .atx.
/// </summary>
public static class AtxCompletion
{
    private static readonly Regex KeyContext = new(@"^\s*(?<word>[A-Za-z_][A-Za-z0-9_\-]*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ValueContext =
        new(@"^\s*(?<key>[A-Za-z_][A-Za-z0-9_\-]*)\s*=\s*(?<value>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds the completion list for the caret position, or an empty list when completion does not
    /// apply there (inside a comment, inside a string that is not a value, and so on).
    /// </summary>
    /// <param name="document">The document the caret is in.</param>
    /// <param name="offset">Caret offset.</param>
    /// <param name="filterStart">Receives the offset the completion window filters from.</param>
    public static IReadOnlyList<ICompletionData> Suggest(
        DocumentViewModel document, int offset, out int filterStart)
    {
        ArgumentNullException.ThrowIfNull(document);
        filterStart = offset;

        string text = document.Document.Text;
        offset = Math.Clamp(offset, 0, text.Length);
        int lineStart = LineStart(text, offset);
        string prefix = text[lineStart..offset];
        if (prefix.Contains('#', StringComparison.Ordinal)) return [];

        var map = document.Parse.SyntaxMap;
        var block = map.BlockAt(Math.Max(0, offset - 1));
        var scope = block?.Kind == AtxBlockKind.Frame ? AtxKeyScope.Frame : AtxKeyScope.Header;

        var keyMatch = KeyContext.Match(prefix);
        if (keyMatch.Success)
        {
            var word = keyMatch.Groups["word"];
            filterStart = word.Success ? lineStart + word.Index : offset;
            return KeyCompletions(text, block, scope, filterStart, lineStart);
        }

        var valueMatch = ValueContext.Match(prefix);
        if (!valueMatch.Success) return [];

        string key = valueMatch.Groups["key"].Value;
        var valueGroup = valueMatch.Groups["value"];
        int valueStart = lineStart + valueGroup.Index;
        string typed = valueGroup.Value;
        // Filter on what has been typed after any opening quote.
        int quoteSkip = typed.StartsWith('"') || typed.StartsWith('\'') ? 1 : 0;
        filterStart = valueStart + quoteSkip;

        return ValueCompletions(document, scope, key, valueStart);
    }

    private static IReadOnlyList<ICompletionData> KeyCompletions(
        string text, AtxBlock? block, AtxKeyScope scope, int start, int caretLineStart)
    {
        var present = PresentKeys(text, block, caretLineStart);
        var results = new List<ICompletionData>();
        foreach (var info in scope == AtxKeyScope.Frame ? AtxSchema.FrameKeys : AtxSchema.HeaderKeys)
        {
            if (present.Contains(info.Name)) continue;
            results.Add(new AtxCompletionData(
                info.Name, info.Name + " = ", Describe(info), start, CompletionTarget.Word,
                priority: 100 - info.Order));
        }
        return results;
    }

    /// <summary>
    /// The keys already written in the block the caret is in, read straight from the text rather
    /// than from the syntax map. A half-typed key makes the document invalid TOML for a moment, so
    /// the map has nothing to say — but the completion list still has to know what is already there.
    /// The caret's own line is skipped, because that is the key being typed.
    /// </summary>
    private static HashSet<string> PresentKeys(string text, AtxBlock? block, int caretLineStart)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        if (block is null) return present;

        int start = Math.Clamp(block.Span.Start, 0, text.Length);
        int end = Math.Clamp(block.Span.End, start, text.Length);
        int i = start;
        while (i < end)
        {
            int lineEnd = text.IndexOfAny(['\r', '\n'], i);
            if (lineEnd < 0 || lineEnd > end) lineEnd = end;
            if (i != caretLineStart)
            {
                var match = Assignment.Match(text[i..lineEnd]);
                if (match.Success) present.Add(match.Groups["key"].Value);
            }
            i = lineEnd;
            while (i < end && text[i] is '\r' or '\n') i++;
        }
        return present;
    }

    private static readonly Regex Assignment = new(@"^\s*(?<key>[A-Za-z_][A-Za-z0-9_\-]*)\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyList<ICompletionData> ValueCompletions(
        DocumentViewModel document, AtxKeyScope scope, string key, int valueStart)
    {
        var results = new List<ICompletionData>();
        switch (key)
        {
            case AtxSchema.KeyFormat:
                foreach (var token in AtxSchema.FormatTokens)
                {
                    foreach (string spelling in token.AllSpellings)
                    {
                        results.Add(new AtxCompletionData(
                            spelling, TomlText.QuoteBasicString(spelling), token.Description,
                            valueStart, CompletionTarget.ValueRegion,
                            priority: spelling == token.Token ? 10 : 0));
                    }
                }
                break;

            case AtxSchema.KeyMaterial:
                foreach (var material in AtxSchema.Materials)
                {
                    results.Add(new AtxCompletionData(
                        material.Token, TomlText.QuoteBasicString(material.Token), material.Description,
                        valueStart, CompletionTarget.ValueRegion));
                }
                break;

            case AtxSchema.KeyAnimationMode:
                foreach (var mode in AtxSchema.AnimationModes)
                {
                    int value = (int)mode.Mode;
                    results.Add(new AtxCompletionData(
                        value.ToString(CultureInfo.InvariantCulture),
                        value.ToString(CultureInfo.InvariantCulture),
                        $"{mode.Label} — {mode.Description}",
                        valueStart, CompletionTarget.ValueRegion,
                        priority: 10 - value));
                }
                break;

            case AtxSchema.KeyInitiallyOn:
                results.Add(new AtxCompletionData("true", "true",
                    "The animation is already playing when the level loads.",
                    valueStart, CompletionTarget.ValueRegion, 1));
                results.Add(new AtxCompletionData("false", "false",
                    "The animation waits for an ATX_Play event.",
                    valueStart, CompletionTarget.ValueRegion));
                break;

            case AtxSchema.KeyFile when scope == AtxKeyScope.Frame:
            case AtxSchema.KeyAlphaMask when scope == AtxKeyScope.Header:
                foreach (string name in ImageNames(document.AtxFolder))
                {
                    results.Add(new AtxCompletionData(
                        name, TomlText.QuoteBasicString(name), "An image file next to this .atx.",
                        valueStart, CompletionTarget.ValueRegion));
                }
                break;
        }
        return results;
    }

    /// <summary>Image files sitting next to the .atx, naturally sorted.</summary>
    private static IReadOnlyList<string> ImageNames(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return [];
        try
        {
            if (!Directory.Exists(folder)) return [];
            return
            [
                .. Directory.EnumerateFiles(folder)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null && AtxSchema.ReadableExtensions.Any(
                        e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .Select(n => n!)
                    .OrderBy(n => n, Cairn.Formats.Text.NaturalStringComparer.Instance),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The hover/completion documentation for a key, straight from the schema.</summary>
    public static string Describe(AtxKeyInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var parts = new List<string> { info.Summary, info.Details };
        if (info.DefaultText is { Length: > 0 } d) parts.Add($"Default: {d}.");
        if (info.RuntimeEvent is { Length: > 0 } e) parts.Add($"Level event: {e}.");
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private static int LineStart(string text, int offset)
    {
        int i = offset;
        while (i > 0 && text[i - 1] is not ('\n' or '\r')) i--;
        return i;
    }
}
