using Cairn.Formats.Tbl;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;
using EditDistance = Cairn.Formats.Text.EditDistance;

namespace Cairn.Tbl.Linting;

/// <summary>
/// Checks a parsed table: syntax always; field names, order, types, ranges, required fields and
/// duplicates when a schema describes the table; file names and cross-table names through the context.
/// Never throws on any document.
/// </summary>
public static class TblLinter
{
    /// <summary>Lints <paramref name="doc"/> against <paramref name="schema"/> (default: the schema it was parsed with).</summary>
    public static ImmutableArray<TblDiagnostic> Lint(TblDocument doc, TblTableSchema? schema = null, TblLintContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (schema is not null && !ReferenceEquals(schema, doc.Schema)) doc = TblDocument.Parse(doc.Text, schema);
        schema = doc.Schema;
        var run = new Run(doc, context ?? TblLintContext.Default);
        run.Syntax();
        if (schema is not null) run.Schema(schema);
        run.References();
        return [.. run.Results.OrderBy(d => d.Span.Start).ThenBy(d => d.Code, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The file and cross-table references <paramref name="doc"/> leaves unresolved under <paramref name="context"/>
    /// (keys for <see cref="TblLintContext.StockMissing"/>). Lint the stock table of a name with this to learn which
    /// missing references the game itself tolerates.
    /// </summary>
    public static IReadOnlySet<string> UnresolvedNames(TblDocument doc, TblLintContext context)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(context);
        var run = new Run(doc, context with { StockMissing = null });
        run.References();
        // File cells of rows/matrix sections and Alpine values are checked by the schema pass; skip it (the cost of a
        // whole lint) for tables that have neither.
        if (doc.Schema is { } schema && (schema.IsAlpineLines || schema.Sections.Any(s => s.Layout is TblSectionLayout.Rows or TblSectionLayout.Matrix)))
            run.Schema(schema);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in run.Results)
            if (d.Code is "TBL201" or "TBL202") names.Add(MissingKey(d.Code, d.Span.GetText(doc.Text)));
        return names;
    }

    private static string MissingKey(string code, string name) => code + ":" + name.Trim();

    private sealed class Run(TblDocument doc, TblLintContext context)
    {
        public List<TblDiagnostic> Results { get; } = [];
        private readonly string _nl = context.NewLine ?? LineEndings.Detect(doc.Text).ToText();

        // A missing reference the stock table of this name has too: information, since the game tolerates it.
        private void AddMissing(TblRule rule, TextSpan span, string message, string? help, params TblQuickFix[] fixes)
        {
            if (context.StockMissing is { } stock && stock.Contains(MissingKey(rule.Code, span.GetText(doc.Text))))
            {
                Results.Add(new TblDiagnostic(rule.Code, TblSeverity.Information,
                    message + " Also missing in the stock game, which the game tolerates.", help, span, [.. fixes]));
                return;
            }
            Add(rule, span, message, help, fixes);
        }

        private void Add(TblRule rule, TextSpan span, string message, string? help = null, params TblQuickFix[] fixes)
        {
            int start = Math.Clamp(span.Start, 0, doc.Text.Length);
            int end = Math.Clamp(span.End, start, doc.Text.Length);
            Results.Add(new TblDiagnostic(rule.Code, rule.Severity, message, help, TextSpan.FromBounds(start, end), [.. fixes]));
        }

        // ---- Syntax ----

        public void Syntax()
        {
            // Text blocks and free-text sections are read as raw lines: brackets and quotes mean nothing there.
            var rawText = new List<TextSpan>();
            foreach (var f in doc.AllFields)
                if (f.EndSpan is not null || f.Schema?.Type == TblValueType.Text) rawText.Add(f.Span);
            foreach (var s in doc.Sections)
                if ((s.FreeValues.Length > 0 && !s.AllFields().Any()) || s.IsIgnored || s.Schema is { IsFreeLayout: true }) rawText.Add(s.Span);
            bool Raw(TextSpan span) => rawText.Any(r => r.Start <= span.Start && span.End <= r.End);

            foreach (var issue in doc.Issues)
            {
                switch (issue.Kind)
                {
                    case TblSyntaxIssueKind.UnterminatedString when !Raw(issue.Span):
                        Add(TblRules.UnterminatedString, issue.Span, "This string has no closing quote.", "Add a \" at the end of the string.",
                            new TblQuickFix("Add the closing quote", [TextEdit.Insert(issue.Span.End, "\"")]));
                        break;
                    case TblSyntaxIssueKind.UnterminatedComment:
                        Add(TblRules.UnterminatedComment, TextSpan.FromBounds(issue.Span.Start, Math.Min(issue.Span.End, issue.Span.Start + 2)),
                            "This /* comment is never closed, so the rest of the file is commented out.", "Add */ where the comment should end.");
                        break;
                    case TblSyntaxIssueKind.UnclosedBracket when !Raw(issue.Span):
                        Add(TblRules.UnclosedBracket, issue.Span, $"This '{issue.Detail}' is never closed.", $"Add the matching '{Closing(issue.Detail)}'.");
                        break;
                    case TblSyntaxIssueKind.NestedTooDeeply:
                        Add(TblRules.UnclosedBracket, issue.Span, $"Brackets are nested too deeply here (more than {TblDocument.MaxNesting} levels).",
                            "No table needs this; remove the extra brackets. Cairn reads the deeper ones as plain text.");
                        break;
                    case TblSyntaxIssueKind.StrayCloser when !Raw(issue.Span):
                        Add(TblRules.StrayCloser, issue.Span, $"This '{issue.Detail}' has no opening bracket to close.", "Remove it or add the opening bracket.",
                            new TblQuickFix("Remove the bracket", [TextEdit.Delete(issue.Span)]));
                        break;
                    case TblSyntaxIssueKind.MissingEnd:
                    {
                        var section = doc.Sections.FirstOrDefault(s => s.HeaderSpan == issue.Span);
                        int at = section?.Span.End ?? doc.Text.Length;
                        Add(TblRules.MissingEnd, issue.Span, $"Section #{issue.Detail} has no #End.", "The game stops reading the table at the end of the section; add #End after its last entry.",
                            new TblQuickFix("Add #End", [TextEdit.Insert(at, _nl + _nl + "#End")]));
                        break;
                    }
                    case TblSyntaxIssueKind.StrayEnd:
                        Add(TblRules.StrayEnd, issue.Span, $"{issue.Detail} does not close any section.", "Remove it, or add the section header it was meant to close.",
                            new TblQuickFix("Remove " + issue.Detail, [TextEdit.Delete(issue.Span)]));
                        break;
                    case TblSyntaxIssueKind.MarkerWithoutColon when !Raw(issue.Span):
                        Add(TblRules.MarkerWithoutColon, issue.Span, $"{issue.Detail} looks like a field name but has no colon.", "Field names end with ':', for example $Name:.",
                            new TblQuickFix("Add the colon", [TextEdit.Insert(issue.Span.End, ":")]));
                        break;
                }
            }

            // The game's white space is space and 0x09-0x0D only: a non-breaking space (text pasted from the web) or
            // another Unicode space outside a string or comment is a character, so the marker or value after it
            // does not match and the game stops.
            if (doc.Schema?.IsAlpineLines != true)
            {
                foreach (var t in doc.Tokens)
                {
                    if (t.Kind is TblLexKind.String or TblLexKind.LineComment or TblLexKind.BlockComment || Raw(t.Span)) continue;
                    for (int i = t.Start; i < t.End; i++)
                    {
                        char ch = doc.Text[i];
                        if (ch < 0x80 || !char.IsWhiteSpace(ch)) continue;
                        Add(TblRules.StrayText, new TextSpan(i, 1),
                            $"This is a {(ch == ' ' ? "non-breaking space" : $"Unicode space (U+{(int)ch:X4})")}; the game does not treat it as white space, so the text after it does not match.",
                            "Replace it with an ordinary space.", new TblQuickFix("Replace with a space", [TextEdit.Replace(new TextSpan(i, 1), " ")]));
                        break;
                    }
                }
            }

            if (context.Encoding is TblFileEncoding.Utf8Bom or TblFileEncoding.Latin1Bom && doc.Schema?.IsAlpineLines != true)
                Add(TblRules.ByteOrderMark, TextSpan.At(0), "The file starts with a UTF-8 byte-order mark, so the game does not recognise the first marker and stops with an error.",
                    "Save the file without a byte-order mark (ANSI or plain UTF-8).");

            // The game ends a // comment only at a carriage return: after a bare LF it keeps skipping, up to the
            // next CR or the end of the file.
            if (doc.Schema?.IsAlpineLines != true)
            {
                // Linear: the start of the next significant token at or after each token, and the next CR found
                // once and reused by every comment before it.
                var tokens = doc.Tokens;
                var nextSignificant = new int[tokens.Length + 1];
                nextSignificant[tokens.Length] = int.MaxValue;
                for (int j = tokens.Length - 1; j >= 0; j--)
                    nextSignificant[j] = tokens[j].IsTrivia ? nextSignificant[j + 1] : tokens[j].Start;
                int cr = -2;
                for (int i = 0; i + 1 < tokens.Length; i++)
                {
                    var c = tokens[i];
                    if (c.Kind != TblLexKind.LineComment || tokens[i + 1] is not { Kind: TblLexKind.NewLine } nl || doc.Text[nl.Start] != '\n') continue;
                    if (cr == -2 || (cr >= 0 && cr < nl.End)) cr = doc.Text.IndexOf('\r', nl.End);
                    int until = cr < 0 ? doc.Text.Length : cr;
                    if (nextSignificant[i + 2] >= until) continue;
                    Add(TblRules.CommentWithoutCr, c.Span,
                        cr < 0 ? "This line ends with LF only, so the game treats everything after this // as a comment, to the end of the file."
                               : $"This line ends with LF only, so the game treats everything after this // as a comment, up to line {doc.Lines.LineOf(cr)}.",
                        "The game ends // comments only at a carriage return. Save the file with Windows (CRLF) line endings.",
                        new TblQuickFix("Convert line endings to CRLF", CrLfEdits(doc.Text)));
                    break;
                }
            }
            foreach (var s in doc.Sections)
            {
                if (s.FreeValues.IsEmpty || s.IsIgnored || s.Schema is { IsFreeLayout: true } or { EntrySearch: true }) continue;
                bool structured = s.Schema is { } ss ? ss.Fields.Length > 0 && ss.Fields.All(f => f.Type != TblValueType.Text) : s.AllFields().Any();
                if (!structured) continue;
                foreach (var run in Runs(s.FreeValues))
                    Add(TblRules.StrayText, run, "This text is not the value of any field.", "Put it after a field name, or turn it into a comment with //.",
                        new TblQuickFix("Comment it out", [TextEdit.Insert(run.Start, "// ")]));
            }
        }

        // Consecutive free values on the same line form one run.
        private IEnumerable<TextSpan> Runs(ImmutableArray<TblValueNode> values)
        {
            TextSpan? current = null;
            int line = -1;
            foreach (var v in values)
            {
                int l = doc.Lines.LineOf(v.Span.Start);
                if (current is { } c && l == line) { current = c.Union(v.Span); continue; }
                if (current is { } done) yield return done;
                current = v.Span;
                line = l;
            }
            if (current is { } last) yield return last;
        }

        // "Did you mean": table names are long and alike, so a third of the shorter name may differ (ATX allows half).
        private static string? Suggest(string value, IEnumerable<string> candidates) => EditDistance.Closest(value, candidates, 3);

        private static string Closing(string open) => open switch { "(" => ")", "<" => ">", "{" => "}", _ => "?" };

        // Converts every line break to CRLF in place: a CR before each bare LF, an LF after each bare CR; no other text moves.
        private static ImmutableArray<TextEdit> CrLfEdits(string text)
        {
            var edits = ImmutableArray.CreateBuilder<TextEdit>();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r')) edits.Add(TextEdit.Insert(i, "\r"));
                else if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')) edits.Add(TextEdit.Insert(i + 1, "\n"));
            }
            return edits.ToImmutable();
        }

        // ---- Schema ----

        public void Schema(TblTableSchema schema)
        {
            // Alpine matches its line tables exactly: no option is read before a line that is exactly #Start
            // (or after #End), and an option whose name differs only in case is not recognised.
            if (schema.IsAlpineLines)
            {
                foreach (var s in doc.Sections)
                {
                    if (s.Schema is { } sec && s.HeaderSpan is { } hs && hs.GetText(doc.Text) is var header && header != sec.Name)
                        Add(TblRules.AlpineValue, hs, $"Alpine Faction looks for a line that is exactly {sec.Name}; with {header} it reads nothing in this file.",
                            null, new TblQuickFix("Change to " + sec.Name, [TextEdit.Replace(hs, sec.Name)]));
                    foreach (var f in s.AllFields())
                        if (f.Schema is { } fs && f.Marker != fs.Name.Trim())
                            Add(TblRules.AlpineValue, f.MarkerSpan, $"Alpine Faction matches option names exactly; {f.Marker} is not {fs.Name.Trim()}, so it is ignored.",
                                null, new TblQuickFix("Change to " + fs.Name.Trim(), [TextEdit.Replace(f.MarkerSpan, fs.Name.Trim())]));
                }
            }

            var present = new HashSet<TblSectionSchema>();
            foreach (var s in doc.Sections)
            {
                if (s.IsIgnored) continue;
                if (s.Schema is null)
                {
                    if (s.HasHeader)
                    {
                        string? guess = Suggest(s.Name, schema.Sections.Where(x => !x.IsRoot).Select(x => x.BareName));
                        var fixes = guess is null ? [] : new[] { new TblQuickFix($"Change to #{guess}", [TextEdit.Replace(s.HeaderSpan!.Value, "#" + guess)]) };
                        Add(TblRules.UnknownSection, s.HeaderSpan!.Value, $"#{s.Name} is not a section of {TableName(schema)}." + (guess is null ? "" : $" Did you mean #{guess}?"),
                            "The game stops with an error at a section header it does not expect.", fixes);
                    }
                    else if (s.AllFields().Any() && schema.Sections.All(x => !x.IsRoot))
                    {
                        var first = s.AllFields().First();
                        Add(TblRules.StrayText, first.MarkerSpan, $"{first.Marker} is outside any section.", "Move it into the section it belongs to.");
                    }
                    continue;
                }
                present.Add(s.Schema);
                if (s.IsIgnored) continue;
                if (s.Schema.Layout == TblSectionLayout.Rows) CheckRows(s, s.Schema);
                else if (s.Schema.Layout == TblSectionLayout.Matrix) CheckMatrix(s, s.Schema);
                if (s.Schema.IsFreeLayout) continue;
                CheckSection(s, s.Schema);
            }
            foreach (var ss in schema.Sections.Where(x => x.Required && !x.IsRoot && !present.Contains(x)))
                Add(TblRules.MissingSection, TextSpan.At(Math.Max(0, doc.Text.Length)), $"{TableName(schema)} needs a {ss.Name} section.", ss.Doc);
        }

        private static string TableName(TblTableSchema schema) => schema.File ?? schema.Title;

        private void CheckSection(TblSection s, TblSectionSchema ss)
        {
            var names = new Dictionary<string, TblEntry>(StringComparer.OrdinalIgnoreCase);
            if (s.Entries.IsEmpty || ss.Entry is null)
            {
                CheckFieldList(s.Fields, ss.Fields, anchor: s.HeaderSpan ?? TextSpan.At(s.Span.Start), insertAfter: s.HeaderSpan?.End ?? s.Span.Start, s.Entries.IsEmpty, freeOrder: ss.FreeOrder);
            }
            else
            {
                foreach (var f in s.Fields)
                    CheckUnknownOrOrder(f, ss.Fields);
            }
            if (ss.MaxEntries is int max && s.Entries.Length > max)
                Add(TblRules.TooManyEntries, s.Entries[max].NameSpan, $"#{s.Name} has {s.Entries.Length} entries; the game keeps only the first {max}.", "Entries after that are ignored.");
            foreach (var e in s.Entries)
            {
                CheckCounts(e.AllFields().ToList());
                CheckFieldList(e.Fields, ss.Fields, anchor: e.EntryField?.MarkerSpan ?? e.HeaderSpan ?? e.Span, insertAfter: e.Span.End, true, skipTail: ss.EntrySearch, freeOrder: ss.FreeOrder);
                if (e.Name.Trim().Length == 0) continue;
                if (names.TryGetValue(e.Name.Trim(), out var first))
                {
                    Add(TblRules.DuplicateEntry, e.NameSpan, $"\"{e.Name}\" is already defined on line {doc.Lines.LineOf(first.NameSpan.Start)}.",
                        "When two entries share a name, only one of them is used. Rename or remove one.");
                }
                else names[e.Name.Trim()] = e;
            }
        }

        // A field read exactly N times, N being another field's value ($Sounds: then N x $Sound:).
        private void CheckCounts(List<TblFieldNode> fields)
        {
            foreach (var group in fields.Where(f => f.Schema?.CountFrom is not null).GroupBy(f => f.Schema!))
            {
                var counter = fields.FirstOrDefault(f => f.Is(group.Key.CountFrom!));
                if (counter?.Values.FirstOrDefault()?.Number is not double n) continue;
                int have = group.Count();
                // Where the game searches for the next entry, extra lines after the counted ones are skipped.
                bool searched = fields[0].Section.Schema?.EntrySearch == true;
                if (have < (int)n || (have > (int)n && !searched))
                    Add(TblRules.WrongCount, have > n ? group.ElementAt((int)Math.Max(0, n)).MarkerSpan : group.Last().MarkerSpan,
                        $"{counter.Marker} says {n}, but there are {have} {group.Key.Name} lines.", $"The game reads exactly that many {group.Key.Name} lines; make the two agree.");
            }
        }

        private void CheckUnknownOrOrder(TblFieldNode f, ImmutableArray<TblFieldSchema> schemaFields)
        {
            if (f.Schema is null) UnknownField(f, schemaFields);
            else CheckField(f);
        }

        // Walks fields in text order against the schema's read order.
        private enum Status { Ok, Unknown, Repeated, Order, Skipped }

        // True when a later field is one the game searches ahead for (at or after pos): it skips everything before it.
        private static bool SeekAhead(ImmutableArray<TblFieldNode> fields, int i, ImmutableArray<TblFieldSchema> schemaFields, int pos)
        {
            for (int j = i + 1; j < fields.Length; j++)
                if (fields[j].Schema is { Seek: true } s && schemaFields.IndexOf(s) >= pos) return true;
            return false;
        }

        // True when the game, having read up to schema position pos, would accept field f next.
        private static bool IsReadable(TblFieldNode f, ImmutableArray<TblFieldSchema> schemaFields, int pos, Dictionary<TblFieldSchema, TblFieldNode> seen)
        {
            if (f.Schema is null) return false;
            int idx = schemaFields.IndexOf(f.Schema);
            if (idx < 0) return false;
            if (idx >= pos) return !(seen.ContainsKey(f.Schema) && !f.Schema.Repeat);
            return idx == pos - 1 && f.Schema.Repeat;
        }

        private void CheckFieldList(ImmutableArray<TblFieldNode> fields, ImmutableArray<TblFieldSchema> schemaFields, TextSpan anchor, int insertAfter, bool checkMissing, bool skipTail = false, bool freeOrder = false)
        {
            // The "#" of numbered entries (strings.tbl) is the entry header, not a field.
            if (schemaFields.Any(x => x.Name.StartsWith('#'))) schemaFields = [.. schemaFields.Where(x => !x.Name.StartsWith('#'))];
            int pos = 0;
            var seen = new Dictionary<TblFieldSchema, TblFieldNode>();
            var status = new (Status Status, TblFieldNode? Earlier, TblFieldSchema? Next)[fields.Length];
            int cutoff = fields.Length;
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                // Where the game searches for the next entry, an unexpected field ends the entry quietly unless
                // a required field is still to be read: everything from it on is skipped.
                if (skipTail && i > 0 && !IsReadable(f, schemaFields, pos, seen)
                    && !schemaFields.Skip(pos).Any(x => x.Required && !seen.ContainsKey(x)))
                {
                    cutoff = i;
                    break;
                }
                var (idx, fs) = f.Schema is null ? (-1, null) : IndexOf(schemaFields, f.Schema);
                // Editor-only fields are skipped by the game; so is anything before a field it searches ahead for.
                if (fs is { EditorOnly: true } || (!IsReadable(f, schemaFields, pos, seen) && SeekAhead(fields, i, schemaFields, pos)))
                {
                    status[i] = (Status.Skipped, null, null);
                    continue;
                }
                if (fs is null) { status[i] = (Status.Unknown, null, null); continue; }
                if (freeOrder)
                {
                    status[i] = seen.TryGetValue(fs, out var dup) && !fs.Repeat ? (Status.Repeated, dup, null) : (Status.Ok, null, null);
                    seen.TryAdd(fs, f);
                    continue;
                }
                if (seen.TryGetValue(fs, out var earlier) && !fs.Repeat) status[i] = (Status.Repeated, earlier, null);
                else if (idx < pos - 1 || (idx == pos - 1 && !fs.Repeat && !seen.ContainsKey(fs))) status[i] = (Status.Order, null, schemaFields[pos - 1]);
                else
                {
                    if (idx >= pos) pos = idx + 1;
                    status[i] = (Status.Ok, null, null);
                }
                seen.TryAdd(fs, f);
            }
            int lastOk = cutoff - 1;
            for (int i = 0; i < cutoff; i++)
            {
                var f = fields[i];
                var (st, earlier, next) = status[i];
                switch (st)
                {
                    case Status.Skipped:
                        continue;
                    case Status.Unknown:
                        UnknownField(f, schemaFields);
                        continue;
                    case Status.Repeated:
                        Add(TblRules.RepeatedField, f.MarkerSpan, $"{f.Marker} appears more than once here (first on line {doc.Lines.LineOf(earlier!.MarkerSpan.Start)}).",
                            "The game reads it once and then stops with an error at the second one; remove the extra one.");
                        break;
                    case Status.Order:
                        Add(TblRules.FieldOrder, f.MarkerSpan, $"{f.Marker} must come before {next!.Name}.",
                            $"The game reads fields in a fixed order and stops with an error at a field it no longer expects. Move {f.Marker} above {next.Name}.");
                        break;
                }
                // Read only when another field has a value ($Cycle Position: only for player_wep weapons).
                if (f.Schema is { RequiredIf: { } condition, AbsentOtherwise: true } && TblConditions.Holds(condition, fields) == false)
                    NotReadHere(f, $"only when {condition}");
                CheckField(f, ignoreExtra: skipTail && i == lastOk);
            }
            if (!checkMissing) return;
            for (int k = 0; k < schemaFields.Length; k++)
            {
                var fs = schemaFields[k];
                if (seen.ContainsKey(fs)) continue;
                bool required = fs.Required;
                string because = "";
                if (fs.RequiredIf is { } rc)
                {
                    required = TblConditions.Holds(rc, fields) == true;
                    because = $" (because {rc})";
                }
                if (!required && fs.RequiredAfter is { } afterMarker && fields.Any(x => x.Is(afterMarker)))
                {
                    required = true;
                    because = $" (because {afterMarker} is present)";
                }
                if (!required) continue;
                // Insert after the last present field the game reads before this one.
                TblFieldNode? after = null;
                foreach (var f in fields)
                    if (f.Schema is { } x && IndexOf(schemaFields, x).Index < k) after = f;
                TblFieldNode? before = after is null ? fields.FirstOrDefault(f => f.Schema is not null) : null;
                TextEdit edit;
                if (after is not null) edit = TextEdit.Insert(after.FullSpan.End, _nl + IndentOf(after) + Template(fs));
                else if (before is not null) edit = TextEdit.Insert(LineStart(before.MarkerSpan.Start), IndentOf(before) + Template(fs) + _nl);
                else edit = TextEdit.Insert(insertAfter, _nl + Template(fs));
                Add(TblRules.MissingField, anchor, $"{fs.Name} is required here{because} but missing.", fs.Doc,
                    new TblQuickFix($"Insert {fs.Name}", [edit]));
            }
        }

        private static (int Index, TblFieldSchema? Field) IndexOf(ImmutableArray<TblFieldSchema> list, TblFieldSchema fs)
        {
            int i = list.IndexOf(fs);
            return i < 0 ? (-1, null) : (i, fs);
        }

        private void UnknownField(TblFieldNode f, ImmutableArray<TblFieldSchema> scope)
        {
            var parentChildren = f.Parent?.Schema?.Children ?? [];
            var candidates = scope.Concat(parentChildren).Concat(scope.SelectMany(x => x.Children)).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string? guess = Suggest(f.Marker, candidates);
            var fixes = guess is null ? [] : new[] { new TblQuickFix($"Change to {guess}", [TextEdit.Replace(f.MarkerSpan, guess)]) };
            string where = f.Parent is { } p ? $"under {p.Marker}" : f.Entry is not null ? "in this entry" : "here";
            Add(TblRules.UnknownField, f.MarkerSpan, $"{f.Marker} is not a field the game reads {where}." + (guess is null ? "" : $" Did you mean {guess}?"),
                "The game stops with an error when it meets a field it does not expect at this point.", fixes);
            foreach (var c in f.Children) CheckUnknownOrOrder(c, f.Schema?.Children ?? scope);
        }

        private void CheckField(TblFieldNode f, bool ignoreExtra = false)
        {
            var fs = f.Schema!;
            // parse_string holds at most 254 bytes (255 is fatal) for every field; the schema's maxLength names a
            // field's own buffer (smaller, or larger for parse_c_string readers such as strings.tbl's 1023). The
            // limit is in bytes of the file's encoding, not characters.
            int maxLength = fs.MaxLength ?? TblValueParser.MaxStringBytes;
            foreach (var v in f.Values.SelectMany(v => v.DescendantsAndSelf()))
            {
                if (v.Kind != TblValueKind.String || v.Text.Length * 3 <= maxLength) continue;
                int bytes = context.Encoding is TblFileEncoding.Utf8 or TblFileEncoding.Utf8Bom ? System.Text.Encoding.UTF8.GetByteCount(v.Text) : v.Text.Length;
                if (bytes <= maxLength) continue;
                Add(TblRules.TooLong, v.ContentSpan, $"This text is {bytes} bytes long; {f.Marker} holds at most {maxLength}.", "The game stops with an error (or cuts the text); shorten it.");
            }
            if (fs.MaxCount is int maxCount && f.Values.FirstOrDefault() is { Kind: TblValueKind.List or TblValueKind.Block } list && list.Items.Length > maxCount)
                Add(TblRules.TooMany, list.Span, $"{f.Marker} has {list.Items.Length} items; the game has room for {maxCount}.", "Extra items overwrite other memory in the game; remove them.");
            if (doc.Schema?.IsAlpineLines == true) CheckAlpineValue(f, fs);
            else if (f.Parsed is { } parsed)
            {
                foreach (var p in parsed.Problems)
                {
                    switch (p.Kind)
                    {
                        case TblValueProblemKind.Missing:
                            Add(TblRules.MissingValue, p.Span, p.Message, fs.Default is { } d ? $"For example: {f.Marker} {d}" : null,
                                new TblQuickFix("Add a value", [TextEdit.Insert(f.MarkerSpan.End, " " + DefaultValue(fs))]));
                            break;
                        case TblValueProblemKind.Extra when ignoreExtra:
                            break;
                        case TblValueProblemKind.Trailing:
                            Add(TblRules.TrailingText, p.Span, p.Message);
                            break;
                        case TblValueProblemKind.Range:
                            Add(TblRules.OutOfRange, p.Span, p.Message);
                            break;
                        case TblValueProblemKind.NotAllowed:
                        {
                            string text = p.Span.GetText(doc.Text);
                            var expected = p.Expected.IsDefault ? [] : p.Expected;
                            string? guess = Suggest(text, expected);
                            var fixes = guess is null ? [] : new[] { new TblQuickFix($"Change to \"{guess}\"", [TextEdit.Replace(p.Span, guess)]) };
                            Add(TblRules.NotAllowed, p.Span, p.Message + (guess is null ? "" : $" Did you mean \"{guess}\"?"),
                                expected.Length is > 0 and <= 30 ? "Accepted: " + string.Join(", ", expected.Select(x => "\"" + x + "\"")) + "." : null, fixes);
                            break;
                        }
                        default:
                            Add(TblRules.WrongType, p.Span, p.Message, fs.Default is { } d2 ? $"For example: {f.Marker} {d2}" : null);
                            break;
                    }
                }
            }
            bool? childrenRead = TblConditions.ChildrenRead(f);
            if (f.Children.Length > 0)
            {
                // The game reads the children only in one branch; in the other the first of them is an unexpected field.
                if (childrenRead == false) NotReadHere(f.Children[0], TblConditions.DescribeChildrenGate(fs) ?? "in another branch");
                else if (fs.Children.Length > 0) CheckFieldList(f.Children, fs.Children, f.MarkerSpan, f.Span.End, true);
                else foreach (var c in f.Children) CheckUnknownOrOrder(c, fs.Children);
            }
            else if (fs.Children.Any(c => c.Required) && childrenRead == true)
            {
                CheckFieldList([], fs.Children, f.MarkerSpan, f.Span.End, true);
            }
        }

        // A field the game does not read in this case: it stops with an error at it, unless the section is one where
        // the game searches for the next entry and no required field is left to read (then the rest of the entry is skipped).
        private void NotReadHere(TblFieldNode f, string when)
        {
            bool fatal = true;
            if (f.Section.Schema is { EntrySearch: true } ss)
            {
                fatal = false;
                for (var node = f; node is not null && !fatal; node = node.Parent)
                {
                    var scope = node.Parent?.Schema?.Children ?? ss.Fields;
                    int idx = node.Schema is null ? -1 : scope.IndexOf(node.Schema);
                    if (idx >= 0 && scope.Skip(idx + 1).Any(x => x.Required && !x.EditorOnly)) fatal = true;
                }
            }
            if (fatal)
                Add(TblRules.ConditionalField, f.MarkerSpan, $"The game reads {f.Marker} {when}, so here it stops with an error at it.",
                    "Remove the field, or change the value it depends on.", new TblQuickFix($"Remove {f.Marker}", [TextEdit.Delete(LineSpanOf(f))]));
            else
                Add(TblRules.SkippedField, f.MarkerSpan, $"The game reads {f.Marker} {when}, so here it skips it and the rest of the entry.",
                    "Remove the field, or change the value it depends on.", new TblQuickFix($"Remove {f.Marker}", [TextEdit.Delete(LineSpanOf(f))]));
        }

        // The field's whole lines (marker to the end of its last value, with the line break).
        private TextSpan LineSpanOf(TblFieldNode f)
        {
            int start = LineStart(f.MarkerSpan.Start);
            int end = f.FullSpan.End;
            int line = doc.Lines.LineOf(Math.Max(start, end - 1));
            end = line < doc.Lines.LineCount ? doc.Lines.StartOf(line + 1) : doc.Text.Length;
            return TextSpan.FromBounds(start, Math.Max(start, end));
        }

        private int LineStart(int offset) => doc.Lines.StartOf(doc.Lines.LineOf(offset));

        private string IndentOf(TblFieldNode f)
        {
            int start = LineStart(f.MarkerSpan.Start);
            return doc.Text[start..f.MarkerSpan.Start] is var lead && lead.All(TblLexer.IsSpace) ? lead : string.Empty;
        }

        private static string Template(TblFieldSchema fs) => fs.Name + " " + DefaultValue(fs);

        private static string DefaultValue(TblFieldSchema fs) => TblTemplates.DefaultValue(fs);

        // ---- Rows, matrices, Alpine option lines ----

        // Unmarked rows (sounds.tbl, hud.tbl): the game reads the column values in order, one row after another.
        private void CheckRows(TblSection s, TblSectionSchema ss)
        {
            if (ss.Columns.IsEmpty || s.AllFields().Any()) return;
            var rows = s.FreeValues.GroupBy(v => doc.Lines.LineOf(v.Span.Start)).Select(g => g.ToList()).ToList();
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                var span = TextSpan.FromBounds(row[0].Span.Start, row[^1].Span.End);
                if (ss.MaxEntries is int max && r == max)
                    Add(TblRules.TooManyEntries, span, $"#{s.Name} has {rows.Count} rows; the game reads only the first {max}.", "Rows after that are ignored.");
                for (int c = 0; c < row.Count && c < ss.Columns.Length; c++)
                    CheckCell(row[c], ss.Columns[c], $"Column {c + 1}");
                if (row.Count != ss.Columns.Length)
                    Add(TblRules.RowShape, span, $"This row has {row.Count} value{(row.Count == 1 ? "" : "s")}; each row of #{s.Name} has {ss.Columns.Length}.",
                        "The game reads the values one after another, so a missing or extra value shifts every row after it. Expected: "
                        + string.Join(", ", ss.Columns.Select(x => x.Doc ?? x.TypeName)) + ".");
            }
        }

        // A matrix (materials.tbl hit sounds): N names, then N rows of a name and N numbers, read value after value.
        private void CheckMatrix(TblSection s, TblSectionSchema ss)
        {
            if (ss.MatrixSize is not int n || n <= 0 || s.AllFields().Any()) return;
            var values = s.FreeValues;
            int expected = n + n * (n + 1);
            for (int i = 0; i < values.Length && i < expected; i++)
            {
                if (i < n)
                {
                    if (ss.MatrixHeader is { } h) CheckCell(values[i], h, $"Header name {i + 1}");
                    continue;
                }
                int row = (i - n) / (n + 1), col = (i - n) % (n + 1);
                var type = col == 0 ? ss.MatrixHeader : ss.MatrixCell;
                if (type is not null) CheckCell(values[i], type, col == 0 ? $"The name of row {row + 1}" : $"Row {row + 1}, column {col}");
            }
            if (values.Length < expected)
                Add(TblRules.RowShape, values.Length > 0 ? values[^1].Span : s.HeaderSpan ?? TextSpan.At(s.Span.Start),
                    $"#{s.Name} has {values.Length} values; the game reads {n} names, then {n} rows of a name and {n} numbers ({expected} values).",
                    "The game stops with an error when the values run out.");
            else if (values.Length > expected)
                Add(TblRules.TrailingText, TextSpan.FromBounds(values[expected].Span.Start, values[^1].Span.End),
                    $"The game reads {expected} values in #{s.Name} and ignores this text.");
        }

        private void CheckCell(TblValueNode v, TblFieldSchema type, string label)
        {
            var (_, problems) = TblValueParser.CheckValue(v, type, label);
            foreach (var p in problems)
            {
                switch (p.Kind)
                {
                    case TblValueProblemKind.Range:
                        Add(TblRules.OutOfRange, p.Span, p.Message);
                        break;
                    case TblValueProblemKind.Trailing:
                        Add(TblRules.TrailingText, p.Span, p.Message);
                        break;
                    case TblValueProblemKind.NotAllowed:
                        Add(TblRules.NotAllowed, p.Span, p.Message);
                        break;
                    default:
                        Add(TblRules.WrongType, p.Span, p.Message, type.Doc is { } d ? $"{label}: {d}." : null);
                        break;
                }
            }
            if (type.Type == TblValueType.File && TblValueParser.StringOf(v) is { Length: > 0 } name && !FileFound(name))
                AddMissing(TblRules.FileNotFound, v.ContentSpan, $"\"{name}\" was not found in the game data, the search folders or next to the table.",
                    "Check the spelling, or add the file to a search folder or packfile.");
        }

        private readonly Dictionary<string, bool> _files = new(StringComparer.OrdinalIgnoreCase);

        // True when the file exists, or when there is no way to tell.
        private bool FileFound(string name)
        {
            name = name.Trim();
            if (context.FileExists is null || name.Length == 0) return true;
            if (_files.TryGetValue(name, out bool found)) return found;
            try { found = TblFileName.Normalize(name).Any(c => context.FileExists(c)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { found = true; }
            return _files[name] = found;
        }

        // Alpine reads the rest of the line after the colon (trimmed; outer quotes stripped; a // there is part of it).
        private void CheckAlpineValue(TblFieldNode f, TblFieldSchema fs)
        {
            int start = f.MarkerSpan.End, end = start;
            while (end < doc.Text.Length && doc.Text[end] is not ('\r' or '\n')) end++;
            while (start < end && TblLexer.IsSpace(doc.Text[start])) start++;
            while (end > start && TblLexer.IsSpace(doc.Text[end - 1])) end--;
            var span = TextSpan.FromBounds(start, end);
            string raw = doc.Text[start..end];
            string value = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
            string syntax = fs.Syntax ?? "";
            void Ignored(string why) => Add(TblRules.AlpineValue, span.IsEmpty ? f.MarkerSpan : span, why.EndsWith('.') ? why : why + ".", fs.Default is { } d ? $"For example: {f.Marker} {d}" : null);

            switch (fs.Type)
            {
                case TblValueType.Int:
                case TblValueType.Float:
                {
                    // std::stoi / std::stof: a leading number; the rest of the text is ignored.
                    var m = System.Text.RegularExpressions.Regex.Match(value, fs.Type == TblValueType.Int ? @"^\s*[+-]?\d+" : @"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?");
                    if (!m.Success || !double.TryParse(m.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                    {
                        Ignored($"Alpine Faction cannot read {(fs.Type == TblValueType.Int ? "a whole number" : "a number")} from \"{value}\", so it ignores {f.Marker}");
                        return;
                    }
                    if (m.Length < value.Length)
                        Add(TblRules.TrailingText, TextSpan.FromBounds(start + (value.Length < raw.Length ? 1 : 0) + m.Length, end - (value.Length < raw.Length ? 1 : 0)),
                            $"Alpine Faction reads {m.Value.Trim()} here and ignores the rest.");
                    if (fs.Min is double min && d < min || fs.Max is double max && d > max)
                        Add(TblRules.OutOfRange, span, $"{m.Value.Trim()} is out of range for {f.Marker}: {Fmt(fs.Min)} to {Fmt(fs.Max)}.");
                    else if (!fs.Values.IsEmpty && !fs.Values.Any(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a) && a == d))
                        Add(TblRules.NotAllowed, span, $"{m.Value.Trim()} is not a value {f.Marker} accepts.", "Accepted: " + string.Join(", ", fs.Values) + ".");
                    return;
                }
                case TblValueType.Bool:
                    if (value is not ("1" or "0" or "true" or "True" or "false" or "False"))
                        Ignored($"Alpine Faction reads only 1, true or True as true, so \"{value}\" counts as false.");
                    return;
                case TblValueType.Color:
                    if (!IsAlpineColor(value))
                        Ignored($"\"{value}\" is not a colour Alpine Faction can read (RRGGBB, RRGGBBAA or r,g,b), so it ignores {f.Marker}.");
                    return;
                case TblValueType.File:
                    if (value.Length > 0 && !FileFound(value))
                        AddMissing(TblRules.FileNotFound, span, $"\"{value}\" was not found in the game data, the search folders or next to the table.",
                            "Check the spelling, or add the file to a search folder or packfile.");
                    return;
                case TblValueType.Enum when fs.Strict && !fs.Values.IsEmpty && !fs.Values.Contains(value, StringComparer.OrdinalIgnoreCase):
                    Add(TblRules.NotAllowed, span, $"\"{value}\" is not a value {f.Marker} accepts.", "Accepted: " + string.Join(", ", fs.Values) + ".");
                    return;
            }
            if (syntax.Equals("alpine-pair", StringComparison.OrdinalIgnoreCase))
            {
                var m = System.Text.RegularExpressions.Regex.Match(raw, "^\\{\\s*\"([^\"]*)\"\\s*,\\s*\"([^\"]*)\"\\s*\\}$");
                if (!m.Success)
                {
                    Ignored($"Alpine Faction expects {{\"original.v3m\", \"replacement.v3m\"}} here, so it ignores this {f.Marker}");
                    return;
                }
                if (m.Groups[2].Length > 31)
                    Ignored($"The replacement name is {m.Groups[2].Length} characters long; Alpine Faction takes at most 31, so it ignores this {f.Marker}");
                foreach (var g in new[] { m.Groups[1], m.Groups[2] })
                    if (g.Length > 0 && !FileFound(g.Value))
                        AddMissing(TblRules.FileNotFound, new TextSpan(start + g.Index, g.Length), $"\"{g.Value}\" was not found in the game data, the search folders or next to the table.",
                            "Check the spelling, or add the file to a search folder or packfile.");
            }
        }

        // RRGGBB / RRGGBBAA, or exactly three comma-separated numbers, optionally in {} or <>.
        private static bool IsAlpineColor(string value)
        {
            string v = value.Trim();
            if (v.Length >= 2 && (v[0], v[^1]) is ('{', '}') or ('<', '>')) v = v[1..^1].Trim();
            if (v.Contains(','))
            {
                var parts = v.Split(',');
                return parts.Length == 3 && parts.All(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^\s*[+-]?\d+"));
            }
            return v.Length is 6 or 8 && v.All(char.IsAsciiHexDigit);
        }

        private static string Fmt(double? d) => d is double x ? x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "any";

        // ---- References ----

        public void References()
        {
            bool files = context.FileExists is not null;
            bool names = context.Index is not null;
            if (!files && !names) return;
            var fileCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in TblValueRoles.All(doc))
            {
                // Alpine line tables: file names are checked from the raw line (CheckAlpineValue), as Alpine reads them.
                if (r.Role == TblValueRole.File && doc.Schema?.IsAlpineLines == true) continue;
                if (r.Role == TblValueRole.File && files)
                {
                    string name = r.Name.Trim();
                    if (name.Length == 0) continue;
                    string? suffix = r.Field.Schema?.NameSuffix;
                    if (!fileCache.TryGetValue(suffix + "|" + name, out bool found))
                    {
                        found = false;
                        try { found = (r.Field.Schema?.EngineFileNames(name) ?? TblFileName.Normalize(name)).Any(c => context.FileExists!(c)); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { found = true; }
                        fileCache[suffix + "|" + name] = found;
                    }
                    if (!found)
                        AddMissing(TblRules.FileNotFound, r.Span, $"\"{name}\" was not found in the game data, the search folders or next to the table.",
                            "Check the spelling, or add the file to a search folder or packfile.");
                }
                else if (r.Role == TblValueRole.Ref && names)
                {
                    var index = context.Index!;
                    string name = r.Name.Trim();
                    if (name.Length == 0 || !index.HasKind(r.Kind)) continue;
                    if (!index.Define(r.Kind, name).IsEmpty) continue;
                    string? guess = Suggest(name, index.Names(r.Kind));
                    var fixes = guess is null ? [] : new[] { new TblQuickFix($"Change to \"{guess}\"", [TextEdit.Replace(r.Span, guess)]) };
                    AddMissing(TblRules.NameNotDefined, r.Span, $"No {r.Kind} named \"{name}\" is defined in the indexed tables." + (guess is null ? "" : $" Did you mean \"{guess}\"?"),
                        "Names must match an entry of the table that defines them.", fixes);
                }
            }
        }
    }
}
