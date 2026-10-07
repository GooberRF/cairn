using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Atx.Sequences;
using Cairn.Atx.Text;

namespace Cairn.Atx.Editing;

/// <summary>A frame to write into a document.</summary>
/// <param name="File">The bare image filename.</param>
/// <param name="FrameTimeMs">Optional per-frame time override, in milliseconds.</param>
/// <param name="Material">Optional per-frame material token.</param>
public sealed record NewFrame(string File, int? FrameTimeMs = null, string? Material = null);

/// <summary>
/// Produces <see cref="TextEditBatch"/>es for structural changes to an .atx document. Nothing here
/// mutates a model and nothing reserialises the file except <see cref="Normalize"/>: every
/// operation is the smallest text change that does the job, so comments, blank lines and the
/// file's own formatting survive.
/// </summary>
public sealed class AtxEditor
{
    private readonly string _text;
    private readonly AtxParseResult _parse;
    private readonly SyntaxMap _map;
    private readonly LineMap _lines;
    private readonly string _eol;

    /// <param name="text">The document text the parse result describes.</param>
    /// <param name="parse">The parse of that exact text.</param>
    public AtxEditor(string text, AtxParseResult parse)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(parse);
        if (!ReferenceEquals(text, parse.Text) && !string.Equals(text, parse.Text, StringComparison.Ordinal))
            throw new ArgumentException("The parse result does not describe this text.", nameof(parse));
        _text = text;
        _parse = parse;
        _map = parse.SyntaxMap;
        _lines = _map.Lines;
        _eol = _map.LineEnding.ToText();
    }

    /// <summary>Parses <paramref name="text"/> and returns an editor for it.</summary>
    public static AtxEditor Create(string text) => new(text, AtxParser.Parse(text));

    /// <summary>The model this editor is editing against, or null when the TOML is invalid.</summary>
    public AtxModel? Model => _parse.Model;

    /// <summary>The document's line ending, which generated text matches.</summary>
    public string LineEnding => _eol;

    private int FrameCount => _map.FrameBlocks.Count;

    // ── Header keys ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sets a key in <c>[header]</c>, creating the key (and the section) if needed. Setting a key
    /// that is absent to its own default is a no-op — the file already means that.
    /// </summary>
    public TextEditBatch SetHeaderValue(string key, AtxValue value)
    {
        string label = $"Set {key}";
        if (_map.HeaderKeys.TryGetValue(key, out var entry))
        {
            return ReplaceValue(entry, value, label);
        }
        if (IsSchemaDefault(key, value)) return TextEditBatch.Empty;

        string line = $"{key} = {value.ToToml()}{_eol}";
        var header = _map.HeaderBlock;
        if (header is null) return CreateHeaderWith(line, label);
        // A header expressed as dotted keys or an inline table cannot take a new plain key line;
        // the UI offers "Convert to standard layout" for those files instead.
        if (!IsRealHeaderTable(header)) return TextEditBatch.Empty;
        return Batch(label, InsertLine(HeaderKeyInsertOffset(key, header), line));
    }

    /// <summary>
    /// An insertion that first closes the previous line when the file ends without a line break.
    /// </summary>
    private TextEdit InsertLine(int offset, string line) =>
        TextEdit.Insert(offset, NeedsLeadingBreak(offset) ? _eol + line : line);

    private bool IsRealHeaderTable(AtxBlock header)
    {
        string decl = header.DeclarationSpan.GetText(_text).TrimStart();
        return decl.StartsWith('[') && !decl.StartsWith("[[", StringComparison.Ordinal);
    }

    /// <summary>Removes a key from <c>[header]</c>, which restores the game's default for it.</summary>
    public TextEditBatch RemoveHeaderKey(string key)
    {
        if (!_map.HeaderKeys.TryGetValue(key, out var entry)) return TextEditBatch.Empty;
        // Removing means deleting the key's whole line, which is only the key's own text when it
        // actually has a line to itself. In a header written as an inline table the "line" is the
        // entire `header = { ... }`, so deleting it would take every other setting with it — and
        // in a dotted-key header the deletion is fine but a member of a shared line is not. The UI
        // offers "Convert to standard layout" for those files; until then this is a no-op.
        if (!OwnsItsLine(entry)) return TextEditBatch.Empty;
        return Batch($"Remove {key}", TextEdit.Delete(entry.LineSpan));
    }

    private bool IsSchemaDefault(string key, AtxValue value) => key switch
    {
        AtxSchema.KeyFrameTime => value.Kind == AtxValueKind.Integer
            && value.Number == AtxSchema.DefaultFrameTimeMs,
        AtxSchema.KeyInitiallyOn => value.Kind == AtxValueKind.Boolean
            && value.Flag == AtxSchema.DefaultInitiallyOn,
        AtxSchema.KeyAnimationMode => value.Kind == AtxValueKind.Integer
            && value.Number == (int)AtxSchema.DefaultAnimationMode,
        _ => false,
    };

    private TextEditBatch CreateHeaderWith(string keyLine, string label)
    {
        int offset = _map.Blocks.Count > 0 ? _map.Blocks[0].Span.Start : _text.Length;
        string prefix = NeedsLeadingBreak(offset) ? _eol : string.Empty;
        string suffix = offset < _text.Length ? _eol : string.Empty;
        string insert = $"{prefix}[{AtxSchema.HeaderTable}]{_eol}{keyLine}{suffix}";
        return Batch(label, TextEdit.Insert(offset, insert));
    }

    private int HeaderKeyInsertOffset(string key, AtxBlock header)
    {
        int order = AtxSchema.KeyOrder(AtxKeyScope.Header, key);
        var present = AtxSchema.HeaderKeys
            .Where(k => _map.HeaderKeys.ContainsKey(k.Name))
            .Select(k => (k.Order, Entry: _map.HeaderKeys[k.Name]))
            .ToList();

        int declLine = _lines.LineOf(header.DeclarationSpan.Start);
        var after = present.Where(p => p.Order < order).OrderBy(p => p.Order).LastOrDefault();
        var before = present.Where(p => p.Order > order).OrderBy(p => p.Order).FirstOrDefault();

        if (before.Entry is not null) return AttachedCommentStart(before.Entry.LineSpan.Start, declLine + 1);
        if (after.Entry is not null) return after.Entry.LineSpan.End;
        return _lines.LineEndWithBreak(declLine);
    }

    // ── Frame keys ────────────────────────────────────────────────────────────

    /// <summary>Sets the same value on <paramref name="key"/> for every listed frame.</summary>
    public TextEditBatch SetFrameValue(IReadOnlyList<int> indices, string key, AtxValue value)
    {
        var map = new Dictionary<int, AtxValue?>();
        foreach (int i in indices) map[i] = value;
        return SetFrameValues(key, map);
    }

    /// <summary>
    /// Sets (or, for a null value, removes) <paramref name="key"/> on each listed frame in one
    /// undo step. This is what bulk timing and multi-selection editing use.
    /// </summary>
    public TextEditBatch SetFrameValues(string key, IReadOnlyDictionary<int, AtxValue?> values)
    {
        var edits = new List<TextEdit>();
        foreach (var (index, value) in values.OrderBy(p => p.Key))
        {
            if (index < 0 || index >= FrameCount) continue;
            var keys = _map.FrameKeys[index];
            if (value is null)
            {
                // Same rule as RemoveHeaderKey: only delete a line that belongs to this key alone.
                if (keys.TryGetValue(key, out var present) && OwnsItsLine(present))
                    edits.Add(TextEdit.Delete(present.LineSpan));
                continue;
            }
            if (keys.TryGetValue(key, out var entry))
            {
                string replacement = value.Value.ToToml();
                if (!string.Equals(entry.ValueSpan.GetText(_text), replacement, StringComparison.Ordinal))
                    edits.Add(TextEdit.Replace(entry.ValueSpan, replacement));
            }
            else
            {
                edits.Add(InsertLine(
                    FrameKeyInsertOffset(index, key),
                    $"{key} = {value.Value.ToToml()}{_eol}"));
            }
        }
        return new TextEditBatch(edits, $"Set {key}");
    }

    /// <summary>Removes <paramref name="key"/> from every listed frame, so it inherits again.</summary>
    public TextEditBatch RemoveFrameKey(IReadOnlyList<int> indices, string key)
    {
        var edits = new List<TextEdit>();
        foreach (int index in indices.Distinct().OrderBy(i => i))
        {
            if (index < 0 || index >= FrameCount) continue;
            if (_map.FrameKeys[index].TryGetValue(key, out var entry) && OwnsItsLine(entry))
                edits.Add(TextEdit.Delete(entry.LineSpan));
        }
        return new TextEditBatch(edits, $"Remove {key}");
    }

    private int FrameKeyInsertOffset(int index, string key)
    {
        int order = AtxSchema.KeyOrder(AtxKeyScope.Frame, key);
        var keys = _map.FrameKeys[index];
        var block = _map.FrameBlocks[index];
        int declLine = _lines.LineOf(block.DeclarationSpan.Start);

        var present = AtxSchema.FrameKeys
            .Where(k => keys.ContainsKey(k.Name))
            .Select(k => (k.Order, Entry: keys[k.Name]))
            .ToList();

        var before = present.Where(p => p.Order > order).OrderBy(p => p.Order).FirstOrDefault();
        var after = present.Where(p => p.Order < order).OrderBy(p => p.Order).LastOrDefault();

        if (before.Entry is not null) return AttachedCommentStart(before.Entry.LineSpan.Start, declLine + 1);
        if (after.Entry is not null) return after.Entry.LineSpan.End;
        return _lines.LineEndWithBreak(declLine);
    }

    // ── Frame blocks ──────────────────────────────────────────────────────────

    /// <summary>Inserts new <c>[[frame]]</c> blocks before the frame at <paramref name="index"/>.</summary>
    /// <param name="index">0..frame count; the count appends at the end.</param>
    /// <param name="frames">The frames to write.</param>
    public TextEditBatch InsertFrames(int index, IReadOnlyList<NewFrame> frames)
    {
        if (frames.Count == 0) return TextEditBatch.Empty;
        index = Math.Clamp(index, 0, FrameCount);
        string body = RenderFrames(frames);
        string label = frames.Count == 1 ? "Add frame" : $"Add {frames.Count} frames";

        if (index < FrameCount)
        {
            int offset = _map.FrameBlocks[index].Span.Start;
            return Batch(label, TextEdit.Insert(offset, body + _eol));
        }

        // When another block follows the last frame — a [header] written at the bottom of the file —
        // go in directly above it, exactly as inserting before any other frame does. The new block
        // then owns the blank line that separates it from what follows, so removing these frames
        // again takes that blank with them instead of leaving one behind every time.
        if (_map.FrameBlocks.Count > 0 && _map.FrameBlocks[^1].Span.End < _text.Length)
        {
            return Batch(label, TextEdit.Insert(_map.FrameBlocks[^1].Span.End, body + _eol));
        }

        int appendAt = AppendOffset();
        string prefix = NeedsLeadingBreak(appendAt) ? _eol : string.Empty;
        if (appendAt > ContentStart) prefix += _eol; // blank line between blocks
        return Batch(label, TextEdit.Insert(appendAt, prefix + body));
    }

    /// <summary>Removes whole frame blocks, attached comments included.</summary>
    public TextEditBatch RemoveFrames(IReadOnlyList<int> indices)
    {
        var picked = Normalise(indices);
        if (picked.Count == 0) return TextEditBatch.Empty;

        var edits = new List<TextEdit>(picked.Count);
        int floor = Math.Max(0, _map.Preamble.End);
        foreach (int i in picked)
        {
            var span = _map.FrameBlocks[i].Span;
            // The blank line that separates a block from the one above belongs to the block above,
            // so removing the block that ends the file would leave that separator dangling at the
            // end — and adding and removing a frame repeatedly would pile them up. Take it with the
            // block. Only blank lines go, never a comment the designer wrote.
            if (span.End >= _text.Length)
            {
                span = TextSpan.FromBounds(BlankLineRunStart(span.Start, floor), span.End);
            }
            edits.Add(TextEdit.Delete(span));
            floor = span.End;
        }
        return new TextEditBatch(edits, picked.Count == 1 ? "Remove frame" : "Remove frames");
    }

    /// <summary>
    /// Walks back from <paramref name="offset"/> over whole blank lines, stopping at the first line
    /// with anything on it and never going below <paramref name="floor"/>.
    /// </summary>
    private int BlankLineRunStart(int offset, int floor)
    {
        int line = _lines.LineOf(offset);
        while (line > 0 && _lines.LineStart(line - 1) >= floor && _lines.IsBlank(line - 1)) line--;
        return Math.Max(floor, _lines.LineStart(line));
    }

    /// <summary>Moves whole frame blocks so they sit before the frame at <paramref name="targetIndex"/>.</summary>
    /// <param name="indices">Frames to move.</param>
    /// <param name="targetIndex">0..frame count, interpreted against the current order.</param>
    public TextEditBatch MoveFrames(IReadOnlyList<int> indices, int targetIndex)
    {
        var picked = Normalise(indices);
        if (picked.Count == 0) return TextEditBatch.Empty;
        targetIndex = Math.Clamp(targetIndex, 0, FrameCount);

        // Already in place? Then there is nothing to do.
        var remaining = Enumerable.Range(0, FrameCount).Where(i => !picked.Contains(i)).ToList();
        int insertPos = remaining.Count(i => i < targetIndex);
        var resulting = new List<int>(remaining);
        resulting.InsertRange(insertPos, picked);
        if (resulting.SequenceEqual(Enumerable.Range(0, FrameCount))) return TextEditBatch.Empty;

        string body = string.Concat(picked.Select(i => BlockText(i)));
        int offset = targetIndex < FrameCount
            ? _map.FrameBlocks[targetIndex].Span.Start
            : _map.FrameBlocks[FrameCount - 1].Span.End;

        // Never land inside a block that this batch deletes.
        foreach (int i in picked)
        {
            var span = _map.FrameBlocks[i].Span;
            if (offset > span.Start && offset < span.End) { offset = span.Start; break; }
        }

        // Moving to the end lands at the very end of a file that may not finish with a line break,
        // which would run the moved [[frame]] onto the previous line and break the TOML.
        if (NeedsLeadingBreak(offset)) body = _eol + body;
        // A file that ends with a comment needs a blank line before the moved block as well:
        // without one that comment sits directly above the moved [[frame]], which makes it that
        // frame's own attached comment, and the next move would carry it off. Nothing else needs
        // separating, so a file written without blank lines between its frames keeps that style.
        if (offset >= _text.Length && LastLineIsComment()) body = _eol + body;
        // The same in reverse: a block that finishes on a comment must not land straight on top of
        // the next one, or that comment would become the next block's.
        else if (offset < _text.Length && EndsOnAttachingComment(picked[^1])) body += _eol;

        var edits = new List<TextEdit> { TextEdit.Insert(offset, body) };
        edits.AddRange(picked.Select(i => TextEdit.Delete(_map.FrameBlocks[i].Span)));
        return new TextEditBatch(edits, "Move frames");
    }

    /// <summary>Copies the selected frame blocks in directly after the selection.</summary>
    public TextEditBatch DuplicateFrames(IReadOnlyList<int> indices)
    {
        var picked = Normalise(indices);
        if (picked.Count == 0) return TextEditBatch.Empty;
        string body = string.Concat(picked.Select(i => BlockText(i)));
        int offset = _map.FrameBlocks[picked[^1]].Span.End;
        string prefix = NeedsLeadingBreak(offset) ? _eol : string.Empty;
        return Batch("Duplicate frames", TextEdit.Insert(offset, prefix + body));
    }

    /// <summary>Reverses the order of the selected frames, leaving other frames where they are.</summary>
    public TextEditBatch ReverseFrames(IReadOnlyList<int> indices)
    {
        var picked = Normalise(indices);
        if (picked.Count < 2) return TextEditBatch.Empty;
        return Rearrange(picked, [.. Enumerable.Reverse(picked)], "Reverse frames");
    }

    /// <summary>Sorts the selected frames by filename using natural (human) ordering.</summary>
    public TextEditBatch SortFrames(IReadOnlyList<int> indices)
    {
        var picked = Normalise(indices);
        if (picked.Count < 2) return TextEditBatch.Empty;
        var model = _parse.Model;
        var order = picked
            .OrderBy(i => model is not null && i < model.Frames.Count
                ? model.Frames[i].EffectiveFile ?? string.Empty
                : string.Empty, NaturalStringComparer.Instance)
            .ToList();
        return Rearrange(picked, order, "Sort frames");
    }

    private TextEditBatch Rearrange(List<int> positions, List<int> sourceOrder, string label)
    {
        if (positions.SequenceEqual(sourceOrder)) return TextEditBatch.Empty;
        var edits = new List<TextEdit>();
        for (int i = 0; i < positions.Count; i++)
        {
            var span = _map.FrameBlocks[positions[i]].Span;
            string replacement = BlockText(sourceOrder[i]);
            bool atEof = span.End >= _text.Length;
            if (atEof && !EndsWithBreak(_text)) replacement = TrimOneBreak(replacement);
            if (!string.Equals(span.GetText(_text), replacement, StringComparison.Ordinal))
                edits.Add(TextEdit.Replace(span, replacement));
        }
        return new TextEditBatch(edits, label);
    }

    // ── Normalize ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Rewrites the whole file in the standard <c>[header]</c> + <c>[[frame]]</c> layout, keeping
    /// comments where they can be attributed to a key, a frame, or the top of the file. This is
    /// the only operation that reserialises the document.
    /// </summary>
    public TextEditBatch Normalize()
    {
        var model = _parse.Model;
        if (model is null) return TextEditBatch.Empty;

        var sb = new System.Text.StringBuilder();
        // Every line the rewrite has already accounted for, so the leftover pass can pick up the
        // comments and unknown keys that belong to a block but to no particular setting.
        var taken = new bool[_lines.LineCount];

        string preambleText = _map.Preamble.GetText(_text);
        if (preambleText.StartsWith('﻿'))
        {
            // The byte-order mark stays exactly where it is: first, and nowhere else.
            sb.Append('﻿');
            preambleText = preambleText[1..];
        }
        string preamble = preambleText.TrimEnd('\r', '\n', ' ', '\t');
        if (preamble.Length > 0)
        {
            sb.Append(LineEndings.Normalize(preamble, _map.LineEnding)).Append(_eol).Append(_eol);
        }

        if (model.Header.IsPresent)
        {
            var headerBlock = _map.HeaderBlock;
            int bound = headerBlock is null ? 0 : _lines.LineOf(headerBlock.DeclarationSpan.Start) + 1;
            // Comment lines attached directly above the header travel with it, exactly as a frame
            // block's attached comments do. Without this they would be dropped, because they belong
            // to the header block rather than to the file's preamble.
            if (headerBlock is not null)
            {
                int declLine = _lines.LineOf(headerBlock.DeclarationSpan.Start);
                int startLine = _lines.LineOf(headerBlock.Span.Start);
                for (int l = startLine; l < declLine; l++)
                {
                    sb.Append(_lines.GetContentLine(l).TrimEnd()).Append(_eol);
                    taken[l] = true;
                }
                taken[declLine] = true;
            }
            sb.Append('[').Append(AtxSchema.HeaderTable).Append(']')
              .Append(headerBlock is not null && IsRealHeaderTable(headerBlock)
                  ? DeclarationComment(headerBlock) : string.Empty)
              .Append(_eol);
            foreach (var key in AtxSchema.HeaderKeys)
            {
                if (!_map.HeaderKeys.TryGetValue(key.Name, out var entry)) continue;
                AppendKeyWithComments(sb, entry, bound, taken);
            }
            foreach (var unknown in model.Header.UnknownKeys)
            {
                if (_map.HeaderKeys.TryGetValue(unknown.Name, out var entry))
                    AppendKeyWithComments(sb, entry, bound, taken);
            }
            foreach (var block in _map.Blocks)
            {
                if (block.Kind == AtxBlockKind.Header) AppendLeftoverLines(sb, block, taken);
            }
            sb.Append(_eol);
        }

        for (int i = 0; i < model.Frames.Count; i++)
        {
            var block = i < _map.FrameBlocks.Count ? _map.FrameBlocks[i] : null;
            if (block is not null)
            {
                int declLine = _lines.LineOf(block.DeclarationSpan.Start);
                int startLine = _lines.LineOf(block.Span.Start);
                for (int l = startLine; l < declLine; l++)
                {
                    sb.Append(_lines.GetContentLine(l).TrimEnd()).Append(_eol);
                    taken[l] = true;
                }
                taken[declLine] = true;
            }
            sb.Append("[[").Append(AtxSchema.FrameArray).Append("]]")
              .Append(block is null ? string.Empty : DeclarationComment(block))
              .Append(_eol);

            var keys = i < _map.FrameKeys.Count ? _map.FrameKeys[i] : null;
            int keyBound = block is null ? 0 : _lines.LineOf(block.DeclarationSpan.Start) + 1;
            foreach (var key in AtxSchema.FrameKeys)
            {
                if (keys is not null && keys.TryGetValue(key.Name, out var entry))
                {
                    AppendKeyWithComments(sb, entry, keyBound, taken);
                }
                else if (key.Name == AtxSchema.KeyFile && model.Frames[i].EffectiveFile is { } file)
                {
                    sb.Append(AtxSchema.KeyFile).Append(" = ")
                      .Append(TomlText.QuoteBasicString(file)).Append(_eol);
                }
            }
            if (block is not null) AppendLeftoverLines(sb, block, taken);
            if (i + 1 < model.Frames.Count) sb.Append(_eol);
        }

        // Top-level tables and keys the ATX format does not define are still the designer's work,
        // and a layout conversion is no place to delete them. They go after the frames, where they
        // cannot be mistaken for part of one.
        foreach (var block in _map.Blocks)
        {
            if (block.Kind != AtxBlockKind.Other) continue;
            int before = sb.Length;
            AppendLeftoverLines(sb, block, taken);
            if (sb.Length > before) sb.Insert(before, _eol);
        }

        string result = sb.ToString();
        if (!EndsWithBreak(result) && result.Length > 0) result += _eol;
        if (string.Equals(result, _text, StringComparison.Ordinal)) return TextEditBatch.Empty;
        return Batch("Convert to standard layout", TextEdit.Replace(new TextSpan(0, _text.Length), result));
    }

    private void AppendKeyWithComments(
        System.Text.StringBuilder sb, AtxKeyEntry entry, int lowerBoundLine, bool[] taken)
    {
        if (!OwnsItsLine(entry))
        {
            // A member of an inline table: its line carries every other setting in the braces too,
            // so only the value itself can come across — there is no comment that belongs to it
            // alone, and no line to take.
            sb.Append(entry.Key).Append(" = ");
            AppendSpanAsLine(sb, entry.ValueSpan);
            return;
        }

        int line = _lines.LineOf(entry.LineSpan.Start);
        int last = _lines.LineOf(Math.Max(entry.LineSpan.Start, entry.LineSpan.End - 1));
        int start = line;
        while (start - 1 >= lowerBoundLine && _lines.IsComment(start - 1)) start--;
        for (int l = start; l < line; l++) sb.Append(_lines.GetContentLine(l).TrimEnd()).Append(_eol);
        for (int l = start; l <= last && l < taken.Length; l++) taken[l] = true;

        if (IsOwnLineAssignment(entry))
        {
            // The whole span, not just its first physical line: a value written as a """…""" string
            // covers several lines, and keeping only the first leaves an unterminated string.
            AppendSpanAsLine(sb, entry.LineSpan);
            return;
        }

        // A quoted or dotted key — "frame_time" = 20, or header.material = "rock". The key token is
        // rewritten as the plain name; the rest of the line, trailing comment included, is carried
        // across as it stands.
        sb.Append(entry.Key).Append(" = ");
        AppendSpanAsLine(sb, entry.ValueSpan.Length > 0 && entry.ValueSpan.Start >= entry.LineSpan.Start
            ? TextSpan.FromBounds(entry.ValueSpan.Start, entry.LineSpan.End)
            : entry.ValueSpan);
    }

    /// <summary>
    /// Appends the lines of <paramref name="block"/> the rewrite has not already written out:
    /// comments that sit under a block rather than above one of its settings, and keys the ATX
    /// format does not define. They are the designer's work too, and a layout conversion has no
    /// business throwing them away.
    /// </summary>
    private void AppendLeftoverLines(System.Text.StringBuilder sb, AtxBlock block, bool[] taken)
    {
        int first = _lines.LineOf(block.Span.Start);
        int last = _lines.LineOf(Math.Max(block.Span.Start, block.ContentSpan.End - 1));
        for (int l = first; l <= last && l < taken.Length; l++)
        {
            if (taken[l]) continue;
            taken[l] = true;
            // A blank line carries nothing, and the layout supplies its own spacing. A line inside
            // a multi-line value is never blank, so this cannot cut a string in half.
            if (_lines.IsBlank(l)) continue;
            sb.Append(_lines.GetContentLine(l).TrimEnd()).Append(_eol);
        }
    }

    /// <summary>
    /// Appends a span's text as one logical line: line breaks inside it become the document's own,
    /// and exactly one line break closes it.
    /// </summary>
    private void AppendSpanAsLine(System.Text.StringBuilder sb, TextSpan span)
    {
        string raw = LineEndings.Normalize(span.GetText(_text), _map.LineEnding);
        sb.Append(raw.TrimEnd()).Append(_eol);
    }

    /// <summary>
    /// The trailing comment on a block's declaration line — the "# the flash" of
    /// <c>[[frame]]  # the flash</c> — ready to append, or empty when there is none.
    /// <see cref="Normalize"/> writes the declaration itself out fresh, so without this the note
    /// the designer wrote beside it would simply disappear.
    /// </summary>
    private string DeclarationComment(AtxBlock block)
    {
        string decl = block.DeclarationSpan.GetText(_text);
        int hash = CommentStart(decl);
        if (hash < 0) return string.Empty;
        string comment = decl[hash..].TrimEnd();
        return comment.Length == 0 ? string.Empty : "  " + comment;
    }

    /// <summary>
    /// Where the comment starts on a single line of TOML, or -1. A '#' inside quotes is part of a
    /// name (<c>["a#b"]</c>) rather than the start of a comment.
    /// </summary>
    private static int CommentStart(string line)
    {
        bool basic = false, literal = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (basic)
            {
                if (c == '\\') i++;
                else if (c == '"') basic = false;
            }
            else if (literal)
            {
                if (c == '\'') literal = false;
            }
            else if (c == '"') basic = true;
            else if (c == '\'') literal = true;
            else if (c == '#') return i;
        }
        return -1;
    }

    /// <summary>True when the entry is written as its own <c>key = value</c> line under a table.</summary>
    private bool IsOwnLineAssignment(AtxKeyEntry entry) =>
        string.Equals(entry.KeySpan.GetText(_text), entry.Key, StringComparison.Ordinal)
        && OwnsItsLine(entry);

    /// <summary>
    /// True when the entry's key starts its line, so deleting the line deletes nothing but this
    /// key. A dotted key (<c>header.material = …</c>) qualifies even though its key token is not
    /// the bare name; a member of an inline table does not, because its line belongs to the table.
    /// </summary>
    private bool OwnsItsLine(AtxKeyEntry entry)
    {
        int line = _lines.LineOf(entry.KeySpan.Start);
        int firstNonSpace = _lines.LineStart(line);
        while (firstNonSpace < entry.KeySpan.Start && char.IsWhiteSpace(_text[firstNonSpace])) firstNonSpace++;
        return firstNonSpace == entry.KeySpan.Start;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private TextEditBatch ReplaceValue(AtxKeyEntry entry, AtxValue value, string label)
    {
        string replacement = value.ToToml();
        if (string.Equals(entry.ValueSpan.GetText(_text), replacement, StringComparison.Ordinal))
            return TextEditBatch.Empty;
        return Batch(label, TextEdit.Replace(entry.ValueSpan, replacement));
    }

    private static TextEditBatch Batch(string label, params TextEdit[] edits) => new(edits, label);

    private List<int> Normalise(IReadOnlyList<int> indices) =>
        [.. indices.Distinct().Where(i => i >= 0 && i < FrameCount).OrderBy(i => i)];

    /// <summary>The block's raw text, guaranteed to end with a line break so it can be re-placed.</summary>
    private string BlockText(int index)
    {
        string s = _map.FrameBlocks[index].Span.GetText(_text);
        return EndsWithBreak(s) ? s : s + _eol;
    }

    /// <summary>Where new frames go when appending: after the last block's real content.</summary>
    private int AppendOffset()
    {
        if (_map.FrameBlocks.Count > 0) return _map.FrameBlocks[^1].ContentSpan.End;
        if (_map.Blocks.Count > 0) return _map.Blocks[^1].ContentSpan.End;
        int end = _map.Preamble.End;
        return Math.Min(end, _text.Length);
    }

    /// <summary>
    /// The first offset that is really document text. A byte-order mark sits in front of the first
    /// line rather than on it, so nothing counts as coming "after" it on the same line.
    /// </summary>
    private int ContentStart => _text.Length > 0 && _text[0] == '﻿' ? 1 : 0;

    private bool NeedsLeadingBreak(int offset) => offset > ContentStart && offset <= _text.Length
        && _text[offset - 1] is not ('\n' or '\r');

    /// <summary>Start of the comment lines attached directly above <paramref name="offset"/>'s line.</summary>
    private int AttachedCommentStart(int offset, int lowerBoundLine)
    {
        int line = _lines.LineOf(offset);
        int start = line;
        while (start - 1 >= lowerBoundLine && _lines.IsComment(start - 1)) start--;
        return _lines.LineStart(start);
    }

    private string RenderFrames(IReadOnlyList<NewFrame> frames) =>
        string.Join(_eol, frames.Select(RenderFrame));

    private string RenderFrame(NewFrame frame)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[[").Append(AtxSchema.FrameArray).Append("]]").Append(_eol);
        sb.Append(AtxSchema.KeyFile).Append(" = ")
          .Append(TomlText.QuoteBasicString(frame.File)).Append(_eol);
        if (frame.FrameTimeMs is { } ms)
            sb.Append(AtxSchema.KeyFrameTime).Append(" = ").Append(ms).Append(_eol);
        if (!string.IsNullOrEmpty(frame.Material))
            sb.Append(AtxSchema.KeyMaterial).Append(" = ")
              .Append(TomlText.QuoteBasicString(frame.Material)).Append(_eol);
        return sb.ToString();
    }

    private static bool EndsWithBreak(string s) => s.Length > 0 && s[^1] is '\n' or '\r';

    /// <summary>True when the last line of the document is a comment.</summary>
    private bool LastLineIsComment() =>
        _text.Length > 0 && _lines.IsComment(_lines.LineOf(_text.Length - 1));

    /// <summary>
    /// True when a frame block's last line is a comment with no blank line after it, so whatever
    /// this block is placed above would take that comment over as its own.
    /// </summary>
    private bool EndsOnAttachingComment(int frameIndex)
    {
        var block = _map.FrameBlocks[frameIndex];
        if (block.ContentSpan.IsEmpty || block.ContentSpan.End < block.Span.End) return false;
        return _lines.IsComment(_lines.LineOf(block.ContentSpan.End - 1));
    }

    private static string TrimOneBreak(string s)
    {
        if (s.EndsWith("\r\n", StringComparison.Ordinal)) return s[..^2];
        if (EndsWithBreak(s)) return s[..^1];
        return s;
    }
}
