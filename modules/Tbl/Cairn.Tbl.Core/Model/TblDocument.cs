using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Model;

/// <summary>
/// An immutable parse of one table's text: tokens, sections, entries, fields and values with their
/// spans. Built by <see cref="Parse"/>, which accepts any text and never throws.
/// </summary>
public sealed class TblDocument
{
    private LineMap? _lines;

    private TblDocument(string text, ImmutableArray<TblLexToken> tokens, TblTableSchema? schema)
    {
        Text = text; Tokens = tokens; Schema = schema;
    }

    public string Text { get; }
    /// <summary>Every token; their texts concatenated give <see cref="Text"/>.</summary>
    public ImmutableArray<TblLexToken> Tokens { get; }
    /// <summary>The schema the document was parsed against, or null.</summary>
    public TblTableSchema? Schema { get; }
    /// <summary>Sections in order (content before the first header forms a section without a header).</summary>
    public ImmutableArray<TblSection> Sections { get; private set; } = [];
    /// <summary>Every comment token (for folding <c>/* */</c> blocks).</summary>
    public ImmutableArray<TblLexToken> Comments { get; private set; } = [];
    public ImmutableArray<TblSyntaxIssue> Issues { get; private set; } = [];
    public LineMap Lines => _lines ??= new LineMap(Text);

    /// <summary>Every entry of every section, in order.</summary>
    public IEnumerable<TblEntry> Entries => Sections.SelectMany(s => s.Entries);

    /// <summary>Every field, depth first, in text order.</summary>
    public IEnumerable<TblFieldNode> AllFields => Sections.SelectMany(s => s.AllFields());

    /// <summary>Parses <paramref name="text"/>, binding fields to <paramref name="schema"/> when given.</summary>
    public static TblDocument Parse(string text, TblTableSchema? schema = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var doc = new TblDocument(text, TblLexer.Lex(text), schema);
        new Builder(doc).Run();
        return doc;
    }

    /// <summary>The index of the token containing <paramref name="offset"/> (the last token at the end of the text), or -1 for empty text.</summary>
    public int TokenIndexAt(int offset)
    {
        if (Tokens.IsEmpty) return -1;
        int lo = 0, hi = Tokens.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Tokens[mid].Start <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The section containing <paramref name="offset"/>, or null.</summary>
    public TblSection? SectionAt(int offset)
    {
        TblSection? best = null;
        foreach (var s in Sections)
        {
            if (s.Span.Start <= offset) best = s; else break;
        }
        return best;
    }

    /// <summary>The entry whose span contains <paramref name="offset"/> (end inclusive), or null.</summary>
    public TblEntry? EntryAt(int offset)
    {
        var section = SectionAt(offset);
        if (section is null) return null;
        TblEntry? best = null;
        foreach (var e in section.Entries)
        {
            if (e.Span.Start <= offset) best = e; else break;
        }
        return best is not null && offset <= Math.Max(best.Span.End, NextStart(section, best)) ? best : null;
    }

    private static int NextStart(TblSection section, TblEntry entry) =>
        entry.Index + 1 < section.Entries.Length ? section.Entries[entry.Index + 1].Span.Start - 1 : section.Span.End;

    /// <summary>The innermost field whose marker or values contain <paramref name="offset"/> (end inclusive), or null.</summary>
    public TblFieldNode? FieldAt(int offset)
    {
        TblFieldNode? best = null;
        foreach (var f in AllFields)
        {
            if (f.MarkerSpan.Start > offset) break;
            if (f.Span.ContainsInclusive(offset)) best = f;
        }
        return best;
    }

    /// <summary>The innermost value containing <paramref name="offset"/> (end inclusive), with its field; or nulls.</summary>
    public (TblFieldNode? Field, TblValueNode? Value) ValueAt(int offset)
    {
        var field = FieldAt(offset);
        if (field is null) return (null, null);
        TblValueNode? best = null;
        foreach (var v in field.Values.SelectMany(v => v.DescendantsAndSelf()))
            if (v.Span.ContainsInclusive(offset) && (best is null || v.Span.Length <= best.Span.Length)) best = v;
        return (field, best);
    }

    /// <summary>The last field whose marker starts before <paramref name="offset"/>, or null.</summary>
    public TblFieldNode? FieldBefore(int offset)
    {
        TblFieldNode? best = null;
        foreach (var f in AllFields)
        {
            if (f.MarkerSpan.Start >= offset) break;
            best = f;
        }
        return best;
    }

    /// <summary>True when the header text (without <c>#</c>) ends a section or text block: <c>End</c>, <c>Sounds End</c>.</summary>
    public static bool IsEndHeader(string name)
    {
        name = name.Trim();
        return name.Equals("end", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" end", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a numbered header (<c>#12</c>), which starts an entry rather than a section.</summary>
    public static bool IsNumberedHeader(string name) => name.Length > 0 && name.All(char.IsAsciiDigit);

    /// <summary>The deepest bracket nesting read as structure; deeper openers become plain words, so no walk over
    /// the value tree (here, in the linter or in the editor) can recurse deeper than this, whatever the input.</summary>
    public const int MaxNesting = 64;

    private enum ItemKind { Header, Field, Free }

    private readonly record struct Item(ItemKind Kind, int Token, ImmutableArray<TblValueNode> Values, string Text, TextSpan Marker = default);

    private sealed class Builder(TblDocument doc)
    {
        private readonly string _text = doc.Text;
        private readonly ImmutableArray<TblLexToken> _tokens = doc.Tokens;
        private readonly List<TblSyntaxIssue> _issues = [];
        private readonly List<TblSection> _sections = [];
        private List<Item> _items = [];

        // Building state.
        private SectionBuilder? _section;
        private EntryBuilder? _entry;
        private readonly List<(TblFieldNode Node, List<TblFieldNode> Children)> _stack = [];
        private readonly Dictionary<TblFieldNode, List<TblFieldNode>> _children = [];

        private sealed class SectionBuilder(TblSection node)
        {
            public TblSection Node { get; } = node;
            public List<TblFieldNode> Fields { get; } = [];
            public List<EntryBuilder> Entries { get; } = [];
            public List<TblValueNode> Free { get; } = [];
            public List<TextSpan> Ignored { get; } = [];
            public int Start = -1, End = -1;
            public void Touch(TextSpan span)
            {
                if (Start < 0) Start = span.Start;
                End = Math.Max(End, span.End);
            }
        }

        private sealed class EntryBuilder(TblEntry node)
        {
            public TblEntry Node { get; } = node;
            public List<TblFieldNode> Fields { get; } = [];
            public int End;
        }

        public void Run()
        {
            CollectItems();
            for (int i = 0; i < _items.Count; i++) Handle(i);
            FlushLines();
            CloseSection(null);
            for (int i = 0; i < _sections.Count; i++) _sections[i].Index = i;
            doc.Sections = [.. _sections];
            doc.Comments = [.. _tokens.Where(t => t.IsComment)];
            doc.Issues = [.. _issues.OrderBy(i => i.Span.Start)];
        }

        // Pass 1: headers, field markers with their values, and runs of free values.
        private void CollectItems()
        {
            var items = new List<Item>();
            var sig = new List<int>(_tokens.Length / 2);
            for (int i = 0; i < _tokens.Length; i++)
            {
                var t = _tokens[i];
                if (t.Flags.HasFlag(TblLexFlags.Unterminated))
                {
                    _issues.Add(new(t.Kind == TblLexKind.String ? TblSyntaxIssueKind.UnterminatedString : TblSyntaxIssueKind.UnterminatedComment, t.Span, ""));
                }
                if (t.Flags.HasFlag(TblLexFlags.MarkerWithoutColon))
                    _issues.Add(new(TblSyntaxIssueKind.MarkerWithoutColon, t.Span, t.GetText(_text)));
                if (!t.IsTrivia) sig.Add(i);
            }
            int k = 0;
            while (k < sig.Count)
            {
                var t = _tokens[sig[k]];
                if (t.Kind == TblLexKind.Header)
                {
                    items.Add(new(ItemKind.Header, sig[k], [], EngineTrim(t.GetText(_text)[1..])));
                    k++;
                    continue;
                }
                bool isMarker = t.Kind is TblLexKind.FieldName or TblLexKind.BareKey;
                int start = isMarker ? k + 1 : k;
                var markerSpan = t.Span;
                // A schema marker without a colon (game.tbl's +No Geomod Limit) spans several words.
                if (!isMarker && ValuelessAt(t.Start) is { } vm)
                {
                    isMarker = true;
                    markerSpan = new TextSpan(t.Start, vm.Length);
                    start = k;
                    while (start < sig.Count && _tokens[sig[start]].End <= markerSpan.End) start++;
                    _issues.RemoveAll(i => i.Kind == TblSyntaxIssueKind.MarkerWithoutColon && markerSpan.Contains(i.Span.Start));
                }
                int end = start;
                while (end < sig.Count && _tokens[sig[end]].Kind is not (TblLexKind.Header or TblLexKind.FieldName or TblLexKind.BareKey)) end++;
                var values = BuildValues(sig, start, end);
                if (isMarker) items.Add(new(ItemKind.Field, sig[k], values, EngineTrim(markerSpan.GetText(_text)), markerSpan));
                else items.Add(new(ItemKind.Free, sig[k], values, ""));
                k = end;
            }
            _items = items;
        }

        // Trims only what the game treats as white: a non-breaking space stays part of the marker and so fails to match.
        private static string EngineTrim(string s) => s.Trim(' ', '\t', '\n', '\v', '\f', '\r');

        private List<string>? _valueless;

        private string? ValuelessAt(int offset)
        {
            if (doc.Schema is null) return null;
            _valueless ??= [.. doc.Schema.Sections.SelectMany(s => s.Fields).SelectMany(Flatten)
                .Select(f => f.Name.Trim()).Where(n => n.Length > 1 && n[0] is '$' or '+' && !n.EndsWith(':')).Distinct(StringComparer.OrdinalIgnoreCase)];
            foreach (string m in _valueless)
            {
                if (offset + m.Length > _text.Length || !_text.AsSpan(offset, m.Length).Equals(m, StringComparison.OrdinalIgnoreCase)) continue;
                if (offset + m.Length < _text.Length && char.IsLetterOrDigit(_text[offset + m.Length])) continue;
                return m;
            }
            return null;
        }

        private static IEnumerable<TblFieldSchema> Flatten(TblFieldSchema f) => f.Children.SelectMany(Flatten).Prepend(f);

        private ImmutableArray<TblValueNode> BuildValues(List<int> sig, int start, int end)
        {
            var result = ImmutableArray.CreateBuilder<TblValueNode>();
            int pos = start;
            while (pos < end)
            {
                var v = ParseValue(sig, ref pos, end, closer: null);
                if (v is not null) result.Add(v);
            }
            return result.ToImmutable();
        }

        private static TblLexKind? CloserOf(TblLexKind open) => open switch
        {
            TblLexKind.OpenParen => TblLexKind.CloseParen,
            TblLexKind.OpenAngle => TblLexKind.CloseAngle,
            TblLexKind.OpenBrace => TblLexKind.CloseBrace,
            _ => null,
        };

        // Parses one value at sig[pos]; returns null for a comma. Groups never cross a marker (end).
        private TblValueNode? ParseValue(List<int> sig, ref int pos, int end, List<TblLexKind>? closer)
        {
            var t = _tokens[sig[pos]];
            switch (t.Kind)
            {
                case TblLexKind.Comma:
                    pos++;
                    return null;
                case TblLexKind.String:
                {
                    pos++;
                    bool closed = !t.Flags.HasFlag(TblLexFlags.Unterminated);
                    string content = _text.Substring(t.Start + 1, Math.Max(0, t.Length - (closed ? 2 : 1)));
                    return new TblValueNode(TblValueKind.String, t.Span, content, [], false, !closed);
                }
                case TblLexKind.Number:
                    pos++;
                    return new TblValueNode(TblValueKind.Number, t.Span, t.GetText(_text), [], false, false);
                case TblLexKind.Boolean:
                    pos++;
                    return new TblValueNode(TblValueKind.Boolean, t.Span, t.GetText(_text), [], false, false);
                case TblLexKind.OpenParen or TblLexKind.OpenAngle or TblLexKind.OpenBrace when _depth >= MaxNesting:
                    pos++;
                    if (!_depthReported)
                    {
                        _depthReported = true;
                        _issues.Add(new(TblSyntaxIssueKind.NestedTooDeeply, t.Span, t.GetText(_text)));
                    }
                    return new TblValueNode(TblValueKind.Word, t.Span, t.GetText(_text), [], false, false);
                case TblLexKind.OpenParen or TblLexKind.OpenAngle or TblLexKind.OpenBrace:
                {
                    var kind = t.Kind switch { TblLexKind.OpenParen => TblValueKind.List, TblLexKind.OpenAngle => TblValueKind.Vector, _ => TblValueKind.Block };
                    return ParseGroup(sig, ref pos, end, closer, kind, t.Start, string.Empty);
                }
                case TblLexKind.CloseParen or TblLexKind.CloseAngle or TblLexKind.CloseBrace:
                    pos++;
                    _issues.Add(new(TblSyntaxIssueKind.StrayCloser, t.Span, t.GetText(_text)));
                    return new TblValueNode(TblValueKind.StrayCloser, t.Span, t.GetText(_text), [], false, false);
                default:
                {
                    // A word directly followed by "(" is a call such as XSTR(296, "text").
                    if (_depth < MaxNesting && pos + 1 < end && _tokens[sig[pos + 1]].Kind == TblLexKind.OpenParen && _tokens[sig[pos + 1]].Start == t.End)
                    {
                        string name = t.GetText(_text);
                        pos++;
                        return ParseGroup(sig, ref pos, end, closer, TblValueKind.Call, t.Start, name);
                    }
                    pos++;
                    return new TblValueNode(TblValueKind.Word, t.Span, t.GetText(_text), [], false, false);
                }
            }
        }

        private int _depth;
        private bool _depthReported;

        private TblValueNode ParseGroup(List<int> sig, ref int pos, int end, List<TblLexKind>? outer, TblValueKind kind, int start, string name)
        {
            _depth++;
            try { return ParseGroupCore(sig, ref pos, end, outer, kind, start, name); }
            finally { _depth--; }
        }

        private TblValueNode ParseGroupCore(List<int> sig, ref int pos, int end, List<TblLexKind>? outer, TblValueKind kind, int start, string name)
        {
            var open = _tokens[sig[pos]];
            var want = CloserOf(open.Kind)!.Value;
            var closers = new List<TblLexKind>(outer ?? []) { want };
            pos++;
            var items = ImmutableArray.CreateBuilder<TblValueNode>();
            int last = open.End;
            bool commas = false;
            int gaps = 0;
            while (pos < end)
            {
                var t = _tokens[sig[pos]];
                if (t.Kind == want)
                {
                    pos++;
                    return new TblValueNode(kind, TextSpan.FromBounds(start, t.End), name, items.ToImmutable(), false, false) { HasCommas = commas, CommaGaps = gaps };
                }
                // A closer belonging to an enclosing group: this group is unclosed.
                if (t.Kind is TblLexKind.CloseParen or TblLexKind.CloseAngle or TblLexKind.CloseBrace && closers.Contains(t.Kind)) break;
                if (t.Kind == TblLexKind.Comma)
                {
                    commas = true;
                    if (items.Count is > 0 and <= 31) gaps |= 1 << (items.Count - 1);
                }
                var v = ParseValue(sig, ref pos, end, closers);
                if (v is not null) { items.Add(v); last = v.Span.End; }
                else last = _tokens[sig[pos - 1]].End;
            }
            _issues.Add(new(TblSyntaxIssueKind.UnclosedBracket, open.Span, _text.Substring(open.Start, 1)));
            return new TblValueNode(kind, TextSpan.FromBounds(start, last), name, items.ToImmutable(), true, false) { HasCommas = commas, CommaGaps = gaps };
        }

        // Pass 2: structure.
        private TblFieldNode? _linesBlock;
        private readonly List<TblValueNode> _linesValues = [];

        // Stores the values collected for the open "lines" block.
        private void FlushLines()
        {
            if (_linesBlock is null) return;
            _linesBlock.Values = [.. _linesValues];
            _linesBlock = null;
            _linesValues.Clear();
        }

        private void Handle(int index)
        {
            var item = _items[index];
            var tok = _tokens[item.Token];
            if (item.Kind != ItemKind.Field) FlushLines();
            switch (item.Kind)
            {
                case ItemKind.Header:
                    HandleHeader(index, item, tok);
                    break;
                case ItemKind.Field:
                    HandleField(index, item, tok);
                    break;
                default:
                    var section = EnsureSection(index);
                    section.Free.AddRange(item.Values);
                    foreach (var v in item.Values) section.Touch(v.Span);
                    break;
            }
        }

        private void HandleHeader(int index, Item item, TblLexToken tok)
        {
            string name = item.Text;
            var currentSchema = _section?.Node.Schema;
            bool known = doc.Schema?.FindSection(name) is not null;
            bool schemaEnd = currentSchema?.End is { } endMarker && TblSchemaNames.Same(endMarker, "#" + name);
            // Inside a section whose entries the game finds by searching, other headers (folder names in
            // events.tbl, a #End the schema does not ask for) are skipped by the game.
            if (currentSchema is { EntrySearch: true } && !known && !schemaEnd && !IsNumberedHeader(name)
                && !(IsEndHeader(name) && currentSchema.End is null && _stack.Count > 0 && _stack[^1].Node.Schema?.Type == TblValueType.Text))
            {
                if (!IsEndHeader(name) || currentSchema.End is null)
                {
                    _section!.Ignored.Add(tok.Span);
                    _section.Touch(tok.Span);
                    return;
                }
            }
            var top = _stack.Count > 0 ? _stack[^1].Node : null;
            bool textBlock = top is not null && (top.Schema?.Type == TblValueType.Text || (top.Prefix == '\0' && _section?.Node.HasHeader != true));
            // A schema section's entry list ends only at parse_optional("#End"), a case-insensitive prefix match
            // ("#Ammo End" does not end it), or at the schema's own end marker. "#Sounds End" style headers end
            // text blocks and sections Cairn has no schema for.
            bool endsList = currentSchema is null || doc.Schema?.IsAlpineLines == true
                ? IsEndHeader(name)
                : name.StartsWith("End", StringComparison.OrdinalIgnoreCase);
            if (schemaEnd || (textBlock ? IsEndHeader(name) : endsList))
            {
                // Ends a text block (a field whose schema says text, or a bare-key field outside any
                // headed section, as in endgame.tbl), else the open section.
                if (textBlock)
                {
                    top!.EndSpan = tok.Span;
                    if (_entry is not null) _entry.End = Math.Max(_entry.End, tok.End);
                    _section?.Touch(tok.Span);
                    _stack.Clear();
                    return;
                }
                if (_section is not null && (_section.Node.HasHeader || _section.Node.Schema is not null) && _section.Node.EndSpan is null)
                {
                    _section.Node.EndSpan = tok.Span;
                    _section.Touch(tok.Span);
                    CloseSection(null);
                    return;
                }
                _issues.Add(new(TblSyntaxIssueKind.StrayEnd, tok.Span, "#" + name));
                return;
            }
            var current = _section;
            bool numbered = IsNumberedHeader(name)
                || (current?.Node.EntryMarker is { } em && em.StartsWith('#') && current.Node.Schema?.Entry is not null && doc.Schema?.FindSection(name) is null);
            if (numbered && current is not null)
            {
                StartEntry(current, null, tok.Span, name, tok.Span);
                return;
            }
            CloseSection(tok.Span);
            OpenSection(index, name, tok.Span);
            // A section the game never looks for (it finds the ones it wants by searching): skipped.
            if (!known && doc.Schema is not null && doc.Schema.Sections.Any(s => s.LocateBySearch)) _section!.Node.IsIgnored = true;
        }

        private void HandleField(int index, Item item, TblLexToken tok)
        {
            var section = EnsureSection(index);
            // A text block read line by line (endgame.tbl) takes everything up to its #End, words ending in ':' included.
            if (_stack.Count > 0 && _stack[^1].Node is { EndSpan: null, Schema.Syntax: "lines" } block)
            {
                // Collected in a list and frozen once (rebuilding the array per line was quadratic).
                if (!ReferenceEquals(_linesBlock, block)) { FlushLines(); _linesBlock = block; _linesValues.AddRange(block.Values); }
                _linesValues.Add(new TblValueNode(TblValueKind.Word, item.Marker, item.Text, [], false, false));
                _linesValues.AddRange(item.Values);
                var blockSpan = TextSpan.FromBounds(block.MarkerSpan.Start, Math.Max(block.Span.End, _linesValues[^1].Span.End));
                section.Touch(blockSpan);
                if (_entry is not null) _entry.End = Math.Max(_entry.End, blockSpan.End);
                return;
            }
            FlushLines();
            char prefix = tok.Kind == TblLexKind.BareKey ? '\0' : _text[item.Marker.Start];
            var field = new TblFieldNode(item.Text, prefix, item.Marker, item.Values) { Section = section.Node };
            _children[field] = [];

            bool startsEntry = section.Node.EntryMarker is { } marker && !marker.StartsWith('#')
                && (section.Node.Schema?.EntrySearchCaseSensitive == true ? marker.Trim() == item.Text : TblSchemaNames.Same(marker, item.Text));
            if (startsEntry)
            {
                StartEntry(section, field, null, field.FirstText ?? string.Empty, NameSpanOf(field));
                AttachTop(section, field);
            }
            else if (prefix == '$' && doc.Schema is null)
            {
                AttachTop(section, field);
            }
            else
            {
                AttachSub(section, field);
            }
            var span = field.Span;
            section.Touch(span);
            if (_entry is not null) _entry.End = Math.Max(_entry.End, span.End);
        }

        private static TextSpan NameSpanOf(TblFieldNode field)
        {
            if (field.Values.Length == 0) return field.MarkerSpan;
            var v = field.Values[0];
            if (v.Kind == TblValueKind.Call && v.Items.FirstOrDefault(i => i.Kind == TblValueKind.String) is { } s) return s.ContentSpan;
            return v.ContentSpan;
        }

        private ImmutableArray<TblFieldSchema> TopSchemaFields(SectionBuilder section) => section.Node.Schema?.Fields ?? [];

        private void AttachTop(SectionBuilder section, TblFieldNode field)
        {
            field.Schema = TblSchemaNames.Find(TopSchemaFields(section), field.Marker).Field;
            field.Entry = _entry?.Node;
            if (_entry is not null) _entry.Fields.Add(field); else section.Fields.Add(field);
            _stack.Clear();
            _stack.Add((field, _children[field]));
        }

        private void AttachSub(SectionBuilder section, TblFieldNode field)
        {
            // With a schema: the innermost open field that lists this marker as a child; else a top-level
            // field the section knows; else (and always without a schema) a child of the open $ field.
            if (doc.Schema is not null)
            {
                for (int d = _stack.Count - 1; d >= 0; d--)
                {
                    var parentSchema = _stack[d].Node.Schema;
                    if (parentSchema is null) continue;
                    var (_, child) = TblSchemaNames.Find(parentSchema.Children, field.Marker);
                    if (child is null) continue;
                    field.Schema = child;
                    AttachChild(d, field);
                    return;
                }
                if (TblSchemaNames.Find(TopSchemaFields(section), field.Marker).Field is not null)
                {
                    AttachTop(section, field);
                    return;
                }
            }
            if (field.Prefix is '\0' or '$' || _stack.Count == 0)
            {
                AttachTop(section, field);
                return;
            }
            AttachChild(0, field);
        }

        private void AttachChild(int depth, TblFieldNode field)
        {
            var parent = _stack[depth];
            field.Parent = parent.Node;
            field.Entry = _entry?.Node;
            parent.Children.Add(field);
            _stack.RemoveRange(depth + 1, _stack.Count - depth - 1);
            _stack.Add((field, _children[field]));
        }

        private void StartEntry(SectionBuilder section, TblFieldNode? field, TextSpan? header, string name, TextSpan nameSpan)
        {
            FinishEntry();
            var entry = new TblEntry(section.Node) { EntryField = field, HeaderSpan = header, Name = name, NameSpan = nameSpan };
            _entry = new EntryBuilder(entry) { End = (field?.Span ?? header!.Value).End };
            section.Entries.Add(_entry);
            _stack.Clear();
            section.Touch(header ?? field!.MarkerSpan);
        }

        private void FinishEntry()
        {
            if (_entry is null) return;
            var e = _entry.Node;
            foreach (var f in _entry.Fields) Freeze(f);
            e.Fields = [.. _entry.Fields];
            int start = e.HeaderSpan?.Start ?? e.EntryField!.MarkerSpan.Start;
            int end = _entry.End;
            foreach (var f in e.Fields) end = Math.Max(end, f.FullSpan.End);
            e.Span = TextSpan.FromBounds(start, end);
            _entry = null;
        }

        private void Freeze(TblFieldNode field)
        {
            if (!_children.Remove(field, out var list)) return;
            field.Children = [.. list];
            foreach (var c in list) Freeze(c);
        }

        private SectionBuilder EnsureSection(int itemIndex)
        {
            if (_section is null) OpenSection(itemIndex, string.Empty, null);
            return _section!;
        }

        private void OpenSection(int itemIndex, string name, TextSpan? header)
        {
            var node = new TblSection { Name = name, HeaderSpan = header };
            node.Schema = doc.Schema?.FindSection(name);
            if (node.Schema is null && doc.Schema is not null && name.Length == 0) node.Schema = doc.Schema.Sections.FirstOrDefault(s => s.IsRoot);
            node.EntryMarker = node.Schema is not null ? node.Schema.Entry : InferEntryMarker(itemIndex, header is not null);
            _section = new SectionBuilder(node);
            if (header is { } h) _section.Touch(h);
            _stack.Clear();
        }

        // Without a schema: the first $ field that occurs more than once in the section starts entries;
        // numbered headers (#0, #1 in strings.tbl) start entries too.
        private string? InferEntryMarker(int itemIndex, bool headed)
        {
            int start = itemIndex < 0 ? 0 : (_items[itemIndex].Kind == ItemKind.Header ? itemIndex + 1 : itemIndex);
            string? first = null;
            int count = 0;
            for (int i = start; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Kind == ItemKind.Header)
                {
                    if (IsNumberedHeader(it.Text)) return "#";
                    // A headed section ends at its #End; outside sections #End only ends text blocks.
                    if (IsEndHeader(it.Text) && !headed) continue;
                    break;
                }
                if (it.Kind != ItemKind.Field || !it.Text.StartsWith('$')) continue;
                if (first is null) { first = it.Text; count = 1; }
                else if (TblSchemaNames.Same(first, it.Text) && ++count > 1) return first;
            }
            return null;
        }

        private void CloseSection(TextSpan? nextHeader)
        {
            FinishEntry();
            if (_section is null) return;
            var s = _section.Node;
            foreach (var f in _section.Fields) Freeze(f);
            s.Fields = [.. _section.Fields];
            s.Entries = [.. _section.Entries.Select((e, i) => { e.Node.Index = i; return e.Node; })];
            s.FreeValues = [.. _section.Free];
            s.IgnoredHeaders = [.. _section.Ignored];
            int start = _section.Start < 0 ? (nextHeader?.Start ?? _text.Length) : _section.Start;
            int end = Math.Max(_section.End, start);
            foreach (var e in s.Entries) end = Math.Max(end, e.Span.End);
            foreach (var f in s.Fields) end = Math.Max(end, f.FullSpan.End);
            s.Span = TextSpan.FromBounds(start, end);
            if (s.HasHeader && s.EndSpan is null)
            {
                if (nextHeader is not null) s.ClosedByNextHeader = true;
                // Without a schema only a section left open at the end of the text is reported; with one,
                // any section whose end marker the game reads.
                var ss = s.Schema;
                bool needsEnd = s.IsIgnored ? false
                    : ss is null ? nextHeader is null
                    : ss.End is not null || (ss.Layout == TblSectionLayout.Fields && ss.Entry is not null && !ss.EntrySearch);
                if (needsEnd) _issues.Add(new(TblSyntaxIssueKind.MissingEnd, s.HeaderSpan!.Value, s.Name));
            }
            bool empty = !s.HasHeader && s.Fields.IsEmpty && s.Entries.IsEmpty && s.FreeValues.IsEmpty;
            if (!empty) _sections.Add(s);
            _section = null;
            _stack.Clear();
        }
    }
}
