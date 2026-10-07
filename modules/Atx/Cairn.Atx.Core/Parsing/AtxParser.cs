using Cairn.Atx.Linting;
using Cairn.Atx.Model;
using Cairn.Atx.Schema;
using Cairn.Atx.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Cairn.Atx.Parsing;

/// <summary>
/// Turns .atx text into an <see cref="AtxModel"/> plus a <see cref="SyntaxMap"/>.
/// Reading mirrors <c>parse_atx</c> in <c>common/include/common/atx/parse.h</c>: the header is
/// optional, values of the wrong TOML type are ignored, integers are clamped and empty strings
/// count as unset.
/// </summary>
public static class AtxParser
{
    /// <summary>Parses <paramref name="text"/>. Never throws for malformed input.</summary>
    /// <param name="text">The document text (without a byte-order mark).</param>
    /// <param name="sourceName">Filename used in TOML error messages.</param>
    public static AtxParseResult Parse(string text, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new LineMap(text);
        var lineEnding = LineEndings.Detect(text);

        DocumentSyntax doc;
        try
        {
            // validate: true also reports duplicate keys and duplicate tables, which toml++
            // rejects too, so the game would refuse those files as well.
            doc = SyntaxParser.Parse(text, sourceName ?? "document.atx", validate: true);
        }
        catch (Exception ex)
        {
            return new AtxParseResult
            {
                Text = text,
                SyntaxMap = new SyntaxMap(text, lines, lineEnding, [], new TextSpan(0, text.Length),
                    EmptyKeys, []),
                Model = null,
                Diagnostics = [SyntaxDiagnostic(lines, new TextSpan(0, 0), ex.Message)],
            };
        }

        var diagnostics = new List<Diagnostic>();
        foreach (var d in doc.Diagnostics)
        {
            if (d.Kind != DiagnosticMessageKind.Error) continue;
            diagnostics.Add(SyntaxDiagnostic(lines, ToSpan(d.Span, text), d.Message));
        }

        // A '#' or an empty line inside a multi-line string is part of that string's text, not a
        // comment or a blank line. Work out which lines those are before anything asks the line map
        // structural questions about them.
        lines.MarkValueInterior(ValueInteriorLines(doc, text, lines));

        var layout = ReadLayout(doc, text, lines);
        var (blocks, preamble) = BuildBlocks(text, lines, layout.Declarations);

        if (doc.HasErrors)
        {
            return new AtxParseResult
            {
                Text = text,
                SyntaxMap = new SyntaxMap(text, lines, lineEnding, blocks, preamble, EmptyKeys, []),
                Model = null,
                Diagnostics = diagnostics,
                IsCanonical = layout.NonCanonical.Count == 0,
                NonCanonicalReasons = [.. layout.NonCanonical],
            };
        }

        // Tomlyn reads a few things the game's TOML parser refuses outright. Report those here, so
        // a file the game will not load never looks clean.
        TomlStrictness.Check(doc, text, lines, node => ToSpan(node.Span, text), diagnostics);

        diagnostics.AddRange(StructureDiagnostics(layout));

        var headerEntries = new Dictionary<string, AtxKeyEntry>(StringComparer.Ordinal);
        var frameEntries = new List<IReadOnlyDictionary<string, AtxKeyEntry>>();
        var model = BuildModel(text, blocks, layout, headerEntries, frameEntries);

        return new AtxParseResult
        {
            Text = text,
            SyntaxMap = new SyntaxMap(text, lines, lineEnding, blocks, preamble, headerEntries, frameEntries),
            Model = model,
            Diagnostics = diagnostics,
            IsCanonical = layout.NonCanonical.Count == 0,
            NonCanonicalReasons = [.. layout.NonCanonical],
        };
    }

    private static readonly Dictionary<string, AtxKeyEntry> EmptyKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Tomlyn names the tokens it was reading — "(token: `closebracketdouble`)" — which means
    /// nothing to a level designer, and writes the characters in backticks. Strip the parser's
    /// internals and quote the characters normally, so what is left reads as a sentence about
    /// the file rather than about the parser.
    /// </summary>
    private static string Humanise(string message)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(
            message, @"\s*\(token:\s*`[^`]*`\)", string.Empty).Replace('`', '\'');
        text = text.Trim();
        if (text.Length > 0 && text[^1] is not ('.' or '!' or '?')) text += ".";
        return text;
    }

    private static Diagnostic SyntaxDiagnostic(LineMap lines, TextSpan span, string message)
    {
        var (line, col) = lines.LineColumn(span.Start);
        return new Diagnostic(
            AtxRules.Syntax,
            DiagnosticSeverity.Error,
            $"Line {line}, column {col}: {Humanise(message)}",
            "The file is not valid TOML, so the game will not read it at all. Fix the highlighted "
            + "text — the usual causes are a missing quote around a filename, a missing = sign, or "
            + "the same key written twice in one section.",
            span);
    }

    // ── Reading the syntax tree ───────────────────────────────────────────────

    private sealed record RawKeyValue(
        string Key,
        bool IsDotted,
        TextSpan KeySpan,
        ValueSyntax? Value,
        TextSpan ValueSpan,
        TextSpan LineSpan);

    private sealed record RawFrame(IReadOnlyList<RawKeyValue> Items, TextSpan DeclSpan);

    private sealed class Layout
    {
        public List<(AtxBlockKind Kind, int FrameIndex, TextSpan Decl)> Declarations { get; } = [];
        public List<RawKeyValue>? Header { get; set; }
        public TextSpan? HeaderDecl { get; set; }
        public List<RawFrame> Frames { get; } = [];
        public List<UnknownKey> UnknownTopLevel { get; } = [];
        public HashSet<NonCanonicalReason> NonCanonical { get; } = [];
        public TextSpan? FrameNotTablesSpan { get; set; }
        public TextSpan? HeaderNotTableSpan { get; set; }
    }

    private static Layout ReadLayout(DocumentSyntax doc, string text, LineMap lines)
    {
        var layout = new Layout();

        // Top-level key/values: header = {...}, header.x = ..., frame = [{...}], or unknown keys.
        foreach (var kv in doc.KeyValues)
        {
            if (kv.Key is null) continue;
            var (name, dotted, keySpan) = KeyNameAndSpan(kv.Key, text);
            string root = dotted ? name[..name.IndexOf('.')] : name;
            var lineSpan = LineSpanOf(lines, kv);

            if (root == AtxSchema.HeaderTable)
            {
                if (dotted)
                {
                    layout.NonCanonical.Add(NonCanonicalReason.HeaderAsDottedKeys);
                    layout.Header ??= [];
                    layout.Header.Add(new RawKeyValue(name[(root.Length + 1)..], true, keySpan,
                        kv.Value, SpanOfValue(kv.Value, text), lineSpan));
                    layout.HeaderDecl ??= lineSpan;
                    layout.Declarations.Add((AtxBlockKind.Header, -1, keySpan));
                }
                else if (kv.Value is InlineTableSyntax inline)
                {
                    layout.NonCanonical.Add(NonCanonicalReason.HeaderAsInlineTable);
                    layout.Header ??= [];
                    layout.Header.AddRange(ReadInlineTable(inline, text, lines, layout));
                    layout.HeaderDecl ??= lineSpan;
                    layout.Declarations.Add((AtxBlockKind.Header, -1, keySpan));
                }
                else
                {
                    layout.HeaderNotTableSpan ??= lineSpan;
                    layout.Declarations.Add((AtxBlockKind.Other, -1, keySpan));
                }
                continue;
            }

            if (root == AtxSchema.FrameArray && !dotted)
            {
                layout.Declarations.Add((AtxBlockKind.Other, -1, keySpan));
                if (kv.Value is ArraySyntax arr)
                {
                    layout.NonCanonical.Add(NonCanonicalReason.FramesAsInlineTableArray);
                    foreach (var item in arr.Items)
                    {
                        if (item.Value is InlineTableSyntax it)
                        {
                            layout.Frames.Add(new RawFrame(
                                ReadInlineTable(it, text, lines, layout), SpanOfNode(it, text)));
                        }
                        else
                        {
                            layout.FrameNotTablesSpan ??= SpanOfValue(item.Value, text);
                        }
                    }
                }
                else
                {
                    layout.FrameNotTablesSpan ??= lineSpan;
                }
                continue;
            }

            layout.UnknownTopLevel.Add(new UnknownKey(name, keySpan, lineSpan));
            layout.Declarations.Add((AtxBlockKind.Other, -1, keySpan));
        }

        // Tables: [header], [[frame]], anything else.
        foreach (var table in doc.Tables)
        {
            if (table.Name is null) continue;
            var (name, dotted, _) = KeyNameAndSpan(table.Name, text);
            var declSpan = DeclarationSpan(table, text, lines);

            if (!dotted && name == AtxSchema.HeaderTable && table is TableSyntax)
            {
                layout.Header ??= [];
                layout.Header.AddRange(ReadTableItems(table, text, lines, layout));
                layout.HeaderDecl = declSpan;
                layout.Declarations.Add((AtxBlockKind.Header, -1, declSpan));
            }
            else if (!dotted && name == AtxSchema.FrameArray && table is TableArraySyntax)
            {
                layout.Frames.Add(new RawFrame(ReadTableItems(table, text, lines, layout), declSpan));
                layout.Declarations.Add((AtxBlockKind.Frame, layout.Frames.Count - 1, declSpan));
            }
            else if (!dotted && name == AtxSchema.HeaderTable)
            {
                // [[header]] — an array of tables, which the game does not read as [header].
                layout.HeaderNotTableSpan ??= declSpan;
                layout.Declarations.Add((AtxBlockKind.Other, -1, declSpan));
            }
            else if (!dotted && name == AtxSchema.FrameArray)
            {
                // [frame] — a plain table, not an array of tables.
                layout.FrameNotTablesSpan ??= declSpan;
                layout.Declarations.Add((AtxBlockKind.Other, -1, declSpan));
            }
            else
            {
                if (dotted && (name.StartsWith(AtxSchema.HeaderTable + ".", StringComparison.Ordinal)
                    || name.StartsWith(AtxSchema.FrameArray + ".", StringComparison.Ordinal)))
                {
                    layout.NonCanonical.Add(NonCanonicalReason.SubTable);
                }
                layout.UnknownTopLevel.Add(new UnknownKey(name, declSpan, declSpan));
                layout.Declarations.Add((AtxBlockKind.Other, -1, declSpan));
            }
        }

        layout.Declarations.Sort((a, b) => a.Decl.Start.CompareTo(b.Decl.Start));

        // Frames are numbered in document order — a [[frame]] can legally sit after another table.
        int frameNo = 0;
        for (int i = 0; i < layout.Declarations.Count; i++)
        {
            if (layout.Declarations[i].Kind == AtxBlockKind.Frame)
            {
                layout.Declarations[i] = (AtxBlockKind.Frame, frameNo++, layout.Declarations[i].Decl);
            }
        }

        return layout;
    }

    private static IEnumerable<Diagnostic> StructureDiagnostics(Layout layout)
    {
        if (layout.HeaderNotTableSpan is { } hs)
        {
            // Deviation from the design table's severity: the game simply ignores a non-table
            // 'header' and loads with defaults, so this cannot be an Error by our own definition.
            yield return new Diagnostic(
                AtxRules.BadStructure, DiagnosticSeverity.Warning,
                "'header' is not written as a [header] section, so all of its settings are ignored.",
                "Write the texture settings as a [header] section with one key = value per line. "
                + "Until then the game uses the defaults: 100 ms per frame, Static, playing.",
                hs, null, AtxSchema.HeaderTable,
                [new QuickFix("Convert to standard layout", QuickFixKind.ConvertToStandardLayout)]);
        }
        if (layout.FrameNotTablesSpan is { } fs)
        {
            yield return new Diagnostic(
                AtxRules.BadStructure, DiagnosticSeverity.Error,
                "'frame' must be a list of frame entries, and at least one entry here is not one.",
                "Write each frame as its own [[frame]] section containing a file = \"name.tga\" "
                + "line. The game refuses to load the texture otherwise.",
                fs, null, AtxSchema.FrameArray,
                [new QuickFix("Convert to standard layout", QuickFixKind.ConvertToStandardLayout)]);
        }
    }

    private static List<RawKeyValue> ReadTableItems(TableSyntaxBase table, string text, LineMap lines, Layout layout)
    {
        var result = new List<RawKeyValue>();
        foreach (var kv in table.Items)
        {
            if (kv.Key is null) continue;
            var (name, dotted, keySpan) = KeyNameAndSpan(kv.Key, text);
            if (dotted) layout.NonCanonical.Add(NonCanonicalReason.DottedKeyInTable);
            result.Add(new RawKeyValue(name, dotted, keySpan, kv.Value,
                SpanOfValue(kv.Value, text), LineSpanOf(lines, kv)));
        }
        return result;
    }

    private static List<RawKeyValue> ReadInlineTable(InlineTableSyntax inline, string text, LineMap lines, Layout layout)
    {
        var result = new List<RawKeyValue>();
        foreach (var item in inline.Items)
        {
            var kv = item.KeyValue;
            if (kv?.Key is null) continue;
            var (name, dotted, keySpan) = KeyNameAndSpan(kv.Key, text);
            if (dotted) layout.NonCanonical.Add(NonCanonicalReason.DottedKeyInTable);
            result.Add(new RawKeyValue(name, dotted, keySpan, kv.Value,
                SpanOfValue(kv.Value, text), LineSpanOf(lines, kv)));
        }
        return result;
    }

    /// <summary>
    /// Flags every line that sits inside a value written across more than one line: the second and
    /// later lines of a <c>"""…"""</c> or <c>'''…'''</c> string, and of a bracketed value split
    /// over several lines. Those lines carry the value's own characters, so the block map must not
    /// read a leading <c>#</c> there as a comment it may detach and move.
    /// </summary>
    private static bool[] ValueInteriorLines(DocumentSyntax doc, string text, LineMap lines)
    {
        var mask = new bool[lines.LineCount];
        Walk(doc);
        return mask;

        void Walk(SyntaxNode? node)
        {
            if (node is null) return;
            if (node is ValueSyntax)
            {
                var span = ToSpan(node.Span, text);
                if (span.Length > 0)
                {
                    int first = lines.LineOf(span.Start);
                    int last = lines.LineOf(span.End - 1);
                    for (int l = first + 1; l <= last && l < mask.Length; l++) mask[l] = true;
                }
            }
            for (int i = 0; i < node.ChildrenCount; i++) Walk(node.GetChild(i));
        }
    }

    // ── Spans ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far Tomlyn's offsets sit behind this text's own. Tomlyn reports positions as if a
    /// leading byte-order mark were not there, so a document that still carries one — clipboard
    /// text, say; files are stripped on load — needs every span moved along by that character.
    /// </summary>
    private static int BomShift(string text) => text.Length > 0 && text[0] == '﻿' ? 1 : 0;

    private static TextSpan ToSpan(SourceSpan span, string text)
    {
        int shift = BomShift(text);
        int start = Math.Clamp(span.Offset + shift, 0, text.Length);
        int len = Math.Clamp(span.Length, 0, text.Length - start);
        return new TextSpan(start, len);
    }

    private static TextSpan SpanOfNode(SyntaxNode node, string text) => ToSpan(node.Span, text);

    private static TextSpan SpanOfValue(ValueSyntax? value, string text) =>
        value is null ? new TextSpan(0, 0) : ToSpan(value.Span, text);

    /// <summary>
    /// Where <paramref name="line"/> starts as far as the document is concerned. A byte-order mark
    /// is not part of the text the file says, so no line, block or key span may reach back over it
    /// — otherwise removing the first key, or moving the first frame, would carry the mark along
    /// into the middle of the file, where it is not valid TOML at all.
    /// </summary>
    private static int ContentLineStart(LineMap lines, string text, int line) =>
        Math.Max(lines.LineStart(line), BomShift(text));

    private static TextSpan LineSpanOf(LineMap lines, SyntaxNode node)
    {
        var span = ToSpan(node.Span, lines.Text);
        int firstLine = lines.LineOf(span.Start);
        int lastLine = lines.LineOf(Math.Max(span.Start, span.End - 1));
        return TextSpan.FromBounds(
            ContentLineStart(lines, lines.Text, firstLine), lines.LineEndWithBreak(lastLine));
    }

    private static TextSpan DeclarationSpan(TableSyntaxBase table, string text, LineMap lines)
    {
        int start = Math.Clamp(table.Span.Offset + BomShift(text), 0, text.Length);
        int line = lines.LineOf(start);
        return TextSpan.FromBounds(ContentLineStart(lines, text, line), lines.LineEnd(line));
    }

    /// <summary>
    /// How many parts of a dotted key are spelled out. The schema is two deep at most
    /// (<c>header.frame_time</c>), so anything past this is an unknown key whatever the rest says —
    /// and a key with a hundred thousand parts is a denial-of-service, not a setting.
    /// </summary>
    private const int MaxDottedParts = 8;

    private static (string Name, bool Dotted, TextSpan Span) KeyNameAndSpan(KeySyntax key, string text)
    {
        string first = KeyPartName(key.Key);
        bool dotted = key.DotKeys is { ChildrenCount: > 0 };
        if (!dotted) return (first, false, ToSpan(key.Span, text));

        // A StringBuilder, not `name += "."`: joining a long dotted key one part at a time copies
        // the whole name each round, which turned a 200 KB `a.b.b.b…` line into a 15-second freeze.
        var sb = new System.Text.StringBuilder(first);
        int parts = 1;
        foreach (var dot in key.DotKeys!)
        {
            if (parts++ >= MaxDottedParts) { sb.Append(".…"); break; }
            sb.Append('.').Append(KeyPartName(dot.Key));
        }
        return (sb.ToString(), true, ToSpan(key.Span, text));
    }

    private static string KeyPartName(BareKeyOrStringValueSyntax? part) => part switch
    {
        BareKeySyntax bare => bare.Key?.Text ?? string.Empty,
        StringValueSyntax s => s.Value ?? string.Empty,
        _ => string.Empty,
    };

    // ── Block map ─────────────────────────────────────────────────────────────

    private static (List<AtxBlock> Blocks, TextSpan Preamble) BuildBlocks(
        string text, LineMap lines,
        IReadOnlyList<(AtxBlockKind Kind, int FrameIndex, TextSpan Decl)> declarations)
    {
        var blocks = new List<AtxBlock>();
        if (declarations.Count == 0) return (blocks, new TextSpan(0, text.Length));

        // A block starts at the comment lines directly above its declaration with no blank line
        // between, but never reaches back into the previous block.
        var startLines = new int[declarations.Count];
        int minLine = 0;
        for (int i = 0; i < declarations.Count; i++)
        {
            int declLine = lines.LineOf(declarations[i].Decl.Start);
            int start = declLine;
            while (start - 1 >= minLine && lines.IsComment(start - 1)) start--;
            startLines[i] = start;
            minLine = declLine + 1;
        }

        for (int i = 0; i < declarations.Count; i++)
        {
            int blockStart = ContentLineStart(lines, text, startLines[i]);
            // Two declarations can land on the same line in a file that is already broken
            // ("[a] [b]"), which would otherwise give a block that ends before it starts.
            int blockEnd = Math.Max(blockStart,
                i + 1 < declarations.Count ? lines.LineStart(startLines[i + 1]) : text.Length);

            int lastLine = lines.LineOf(Math.Max(blockStart, blockEnd - 1));
            while (lastLine > startLines[i] && lines.IsBlank(lastLine)) lastLine--;
            int contentEnd = Math.Clamp(lines.LineEndWithBreak(lastLine), blockStart, blockEnd);

            int declLineIdx = lines.LineOf(declarations[i].Decl.Start);
            blocks.Add(new AtxBlock(
                declarations[i].Kind,
                declarations[i].FrameIndex,
                TextSpan.FromBounds(blockStart, blockEnd),
                TextSpan.FromBounds(blockStart, contentEnd),
                TextSpan.FromBounds(ContentLineStart(lines, text, declLineIdx), lines.LineEnd(declLineIdx))));
        }

        return (blocks, TextSpan.FromBounds(0, blocks[0].Span.Start));
    }

    // ── Model ─────────────────────────────────────────────────────────────────

    private static AtxModel BuildModel(
        string text,
        IReadOnlyList<AtxBlock> blocks,
        Layout layout,
        Dictionary<string, AtxKeyEntry> headerEntries,
        List<IReadOnlyDictionary<string, AtxKeyEntry>> frameEntries)
    {
        var headerBlock = blocks.FirstOrDefault(b => b.Kind == AtxBlockKind.Header);
        var header = ReadHeader(text, layout, headerBlock, headerEntries);

        var frameBlocks = blocks.Where(b => b.Kind == AtxBlockKind.Frame).ToList();
        var frames = new List<AtxFrame>();
        for (int i = 0; i < layout.Frames.Count; i++)
        {
            var entries = new Dictionary<string, AtxKeyEntry>(StringComparer.Ordinal);
            frames.Add(ReadFrame(text, layout.Frames[i], i,
                i < frameBlocks.Count ? frameBlocks[i] : null, entries));
            frameEntries.Add(entries);
        }

        return new AtxModel
        {
            Header = header,
            Frames = frames,
            UnknownTopLevel = layout.UnknownTopLevel,
        };
    }

    private static AtxHeader ReadHeader(
        string text, Layout layout, AtxBlock? block, Dictionary<string, AtxKeyEntry> entries)
    {
        if (layout.Header is null) return new AtxHeader { IsPresent = false };

        var unknown = new List<UnknownKey>();
        Located<long>? frameTime = null, animationMode = null;
        Located<bool>? initiallyOn = null;
        Located<string>? format = null, alphaMask = null, material = null;

        foreach (var item in layout.Header)
        {
            entries[item.Key] = new AtxKeyEntry(item.Key, item.KeySpan, item.ValueSpan, item.LineSpan);
            switch (item.Key)
            {
                case AtxSchema.KeyFrameTime: frameTime ??= ReadInteger(text, item); break;
                case AtxSchema.KeyInitiallyOn: initiallyOn ??= ReadBoolean(text, item); break;
                case AtxSchema.KeyAnimationMode: animationMode ??= ReadInteger(text, item); break;
                case AtxSchema.KeyFormat: format ??= ReadString(text, item); break;
                case AtxSchema.KeyAlphaMask: alphaMask ??= ReadString(text, item); break;
                case AtxSchema.KeyMaterial: material ??= ReadString(text, item); break;
                default: unknown.Add(new UnknownKey(item.Key, item.KeySpan, item.LineSpan)); break;
            }
        }

        return new AtxHeader
        {
            IsPresent = true,
            TableHeaderSpan = block?.DeclarationSpan ?? layout.HeaderDecl,
            BlockSpan = block?.Span,
            FrameTime = frameTime,
            InitiallyOn = initiallyOn,
            AnimationMode = animationMode,
            Format = format,
            AlphaMask = alphaMask,
            Material = material,
            UnknownKeys = unknown,
        };
    }

    private static AtxFrame ReadFrame(
        string text, RawFrame raw, int index, AtxBlock? block, Dictionary<string, AtxKeyEntry> entries)
    {
        var unknown = new List<UnknownKey>();
        Located<string>? file = null, material = null;
        Located<long>? frameTime = null;

        foreach (var item in raw.Items)
        {
            entries[item.Key] = new AtxKeyEntry(item.Key, item.KeySpan, item.ValueSpan, item.LineSpan);
            switch (item.Key)
            {
                case AtxSchema.KeyFile: file ??= ReadString(text, item); break;
                case AtxSchema.KeyFrameTime: frameTime ??= ReadInteger(text, item); break;
                case AtxSchema.KeyMaterial: material ??= ReadString(text, item); break;
                default: unknown.Add(new UnknownKey(item.Key, item.KeySpan, item.LineSpan)); break;
            }
        }

        return new AtxFrame
        {
            Index = index,
            HeaderSpan = block?.DeclarationSpan ?? raw.DeclSpan,
            BlockSpan = block?.Span ?? raw.DeclSpan,
            File = file,
            FrameTime = frameTime,
            Material = material,
            UnknownKeys = unknown,
        };
    }

    // ── Value reading (mirrors toml++ node::value<T>()) ───────────────────────

    private static TomlValueKind KindOf(ValueSyntax? v) => v switch
    {
        StringValueSyntax => TomlValueKind.String,
        IntegerValueSyntax => TomlValueKind.Integer,
        FloatValueSyntax => TomlValueKind.Float,
        BooleanValueSyntax => TomlValueKind.Boolean,
        ArraySyntax => TomlValueKind.Array,
        InlineTableSyntax => TomlValueKind.InlineTable,
        DateTimeValueSyntax => TomlValueKind.DateTime,
        _ => TomlValueKind.Unknown,
    };

    /// <summary>
    /// Mirrors <c>toml::node::value&lt;int64_t&gt;()</c>: an integer node is taken as-is, a
    /// floating-point node only when it is finite and exactly a whole number, and a boolean node
    /// converts to 1 or 0. Everything else (strings, dates, arrays, tables) is ignored.
    /// </summary>
    private static Located<long> ReadInteger(string text, RawKeyValue item)
    {
        long value = 0;
        bool accepted = false;
        switch (item.Value)
        {
            case IntegerValueSyntax i:
                value = i.Value;
                accepted = true;
                break;
            case FloatValueSyntax f:
                // toml++ converts between integer and float only when the conversion is lossless,
                // which it checks by converting back. Comparing against long.MaxValue instead would
                // let 9223372036854775808.0 through: that constant rounds *up* to 2^63 as a double,
                // so the comparison says "in range" while the value is one past the last long.
                if (!double.IsNaN(f.Value) && !double.IsInfinity(f.Value)
                    && Math.Floor(f.Value) == f.Value
                    && f.Value >= -9.2233720368547758E18 && f.Value < 9.2233720368547758E18)
                {
                    long narrowed = (long)f.Value;
                    if ((double)narrowed == f.Value)
                    {
                        value = narrowed;
                        accepted = true;
                    }
                }
                break;
            case BooleanValueSyntax b:
                // toml++: `static_cast<int64_t>(*ref_cast<bool>())`, so true reads as 1, false as 0.
                value = b.Value ? 1 : 0;
                accepted = true;
                break;
        }
        return new Located<long>(value, accepted, item.KeySpan, item.ValueSpan, item.LineSpan,
            KindOf(item.Value), item.ValueSpan.GetText(text));
    }

    /// <summary>
    /// Mirrors <c>toml::node::value&lt;bool&gt;()</c>: a boolean node is taken as-is and an integer
    /// node converts with C's truthiness (0 is false, anything else is true). Floats and strings
    /// are ignored — toml++ has no float-to-bool conversion.
    /// </summary>
    private static Located<bool> ReadBoolean(string text, RawKeyValue item)
    {
        bool value = false, accepted = false;
        switch (item.Value)
        {
            case BooleanValueSyntax b:
                value = b.Value;
                accepted = true;
                break;
            case IntegerValueSyntax i:
                // toml++: `static_cast<bool>(*ref_cast<int64_t>())`.
                value = i.Value != 0;
                accepted = true;
                break;
        }
        return new Located<bool>(value, accepted, item.KeySpan, item.ValueSpan, item.LineSpan,
            KindOf(item.Value), item.ValueSpan.GetText(text));
    }

    private static Located<string> ReadString(string text, RawKeyValue item)
    {
        string value = string.Empty;
        bool accepted = false;
        if (item.Value is StringValueSyntax s)
        {
            value = s.Value ?? string.Empty;
            accepted = true;
        }
        return new Located<string>(value, accepted, item.KeySpan, item.ValueSpan, item.LineSpan,
            KindOf(item.Value), item.ValueSpan.GetText(text));
    }
}
