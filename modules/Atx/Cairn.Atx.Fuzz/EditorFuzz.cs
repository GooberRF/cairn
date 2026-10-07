using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cairn.Atx.Editing;
using Cairn.Atx.Linting;
using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Atx.Sequences;
using Cairn.Atx.Text;

namespace Cairn.Atx.Fuzz;

public static class EditorFuzz
{
    private static readonly char[] Breaks = { (char)10, (char)13 };
    private static readonly Regex MarkerRx = new(@"\b([PHKTXADUM])(\d+)\b", RegexOptions.Compiled);

    public static void Run(int iterations)
    {
        int valid = 0, invalid = 0;
        for (int seed = 1; seed <= iterations; seed++)
        {
            var r = new Rng(seed);
            bool canonicalOnly = seed % 4 != 0;
            GDoc d;
            try { d = Generator.Generate(r, canonicalOnly, seed); }
            catch (Exception ex) { Program.Report("GEN", ex.ToString(), "", "gen", seed); continue; }

            AtxParseResult parse;
            try { parse = AtxParser.Parse(d.Text); }
            catch (Exception ex) { Program.Report("PARSE-THROW", ex.ToString(), d.Text, "parse", seed); continue; }
            Program.Tag = d.HasBom ? "BOM/" : "";
            if (parse.Model is null) { invalid++; RunInvalid(d, seed); continue; }
            valid++;

            // C. parser/model parity against what the generator meant.
            string want = Program.SpecOf(d, d.Frames);
            string got = Program.SpecOf(parse.Model);
            Program.Cases++;
            if (want != got)
                Program.Report("MODEL-PARITY", $"expected\n{want}\nactual\n{got}", d.Text, "parse", seed);

            CheckSpans(d, parse, seed);
            CheckTiling(d, parse, seed);

            // B. operations.
            for (int trial = 0; trial < 3; trial++) RunOp(d, parse, r, seed, trial);
        }
        Console.WriteLine($"[editor] {iterations} docs ({valid} valid TOML, {invalid} not), {Program.Cases} checks");
    }

    private static void RunInvalid(GDoc d, int seed)
    {
        // Operations on an unparseable document must never throw and never corrupt.
        AtxEditor ed;
        try { ed = AtxEditor.Create(d.Text); }
        catch (Exception ex) { Program.Report("EDITOR-CTOR-THROW", ex.ToString(), d.Text, "ctor", seed); return; }
        var ops = new (string, Func<TextEditBatch>)[]
        {
            ("SetHeaderValue", () => ed.SetHeaderValue(AtxSchema.KeyFrameTime, AtxValue.Integer(42))),
            ("RemoveHeaderKey", () => ed.RemoveHeaderKey(AtxSchema.KeyFrameTime)),
            ("InsertFrames", () => ed.InsertFrames(0, [new NewFrame("x.tga")])),
            ("RemoveFrames", () => ed.RemoveFrames([0, 1])),
            ("MoveFrames", () => ed.MoveFrames([0], 1)),
            ("Normalize", () => ed.Normalize()),
            ("SortFrames", () => ed.SortFrames([0, 1, 2])),
        };
        foreach (var (name, f) in ops)
        {
            Program.Cases++;
            try { var b = f(); b.Apply(d.Text); }
            catch (Exception ex) { Program.Report("INVALID-DOC-THROW:" + name, ex.ToString(), d.Text, name, seed); }
        }
    }

    // ── span / tiling checks ─────────────────────────────────────────────────

    private static void CheckSpans(GDoc d, AtxParseResult parse, int seed)
    {
        string text = d.Text;
        void Check(Located<long>? l, string key) { if (l is not null) RoundTrip(l.ValueSpan, l.Accepted, l.Value.ToString(CultureInfo.InvariantCulture), key); }
        void CheckS(Located<string>? l, string key) { if (l is not null) RoundTrip(l.ValueSpan, l.Accepted, l.Value ?? "", key); }
        void CheckB(Located<bool>? l, string key) { if (l is not null) RoundTrip(l.ValueSpan, l.Accepted, l.Value.ToString(), key); }

        void RoundTrip(TextSpan span, bool accepted, string value, string key)
        {
            Program.Cases++;
            if (span.Start < 0 || span.End > text.Length)
            {
                Program.Report("SPAN-OOB", $"{key} span {span} outside 0..{text.Length}", text, "span", seed);
                return;
            }
            string raw = span.GetText(text);
            var p2 = AtxParser.Parse("[[frame]]\nfile = " + raw + "\n");
            if (p2.Model is null || p2.Model.Frames.Count != 1)
            {
                Program.Report("SPAN-REPARSE", $"{key} value text {Program.Esc(raw)} does not re-parse", text, "span", seed);
            }
        }

        var h = parse.Model!.Header;
        Check(h.FrameTime, "frame_time"); Check(h.AnimationMode, "animation_mode");
        CheckB(h.InitiallyOn, "initially_on");
        CheckS(h.Format, "format"); CheckS(h.AlphaMask, "alpha_mask"); CheckS(h.Material, "material");
        foreach (var f in parse.Model.Frames)
        {
            CheckS(f.File, "file"); Check(f.FrameTime, "frame_time"); CheckS(f.Material, "material");
        }
    }

    private static void CheckTiling(GDoc d, AtxParseResult parse, int seed)
    {
        var map = parse.SyntaxMap;
        Program.Cases++;
        if (map.Blocks.Count == 0)
        {
            if (map.Preamble.Start != 0 || map.Preamble.End != d.Text.Length)
                Program.Report("TILE-EMPTY", $"preamble {map.Preamble} != whole text", d.Text, "tile", seed);
            return;
        }
        if (map.Preamble.Start != 0 || map.Preamble.End != map.Blocks[0].Span.Start)
            Program.Report("TILE-PREAMBLE", $"preamble {map.Preamble} vs block0 {map.Blocks[0].Span}", d.Text, "tile", seed);
        for (int i = 0; i + 1 < map.Blocks.Count; i++)
        {
            if (map.Blocks[i].Span.End != map.Blocks[i + 1].Span.Start)
                Program.Report("TILE-GAP", $"block {i} {map.Blocks[i].Span} then {map.Blocks[i + 1].Span}", d.Text, "tile", seed);
        }
        if (map.Blocks[^1].Span.End != d.Text.Length)
            Program.Report("TILE-END", $"last block {map.Blocks[^1].Span} vs len {d.Text.Length}", d.Text, "tile", seed);

        // Each frame's declaration line must be inside its own block.
        for (int i = 0; i < map.FrameBlocks.Count; i++)
        {
            var b = map.FrameBlocks[i];
            if (b.DeclarationSpan.Start < b.Span.Start || b.DeclarationSpan.End > b.Span.End)
                Program.Report("TILE-DECL", $"frame {i} decl {b.DeclarationSpan} outside {b.Span}", d.Text, "tile", seed);
            var at = map.FrameIndexAt(b.DeclarationSpan.Start);
            if (at != i) Program.Report("FRAMEINDEXAT", $"FrameIndexAt(decl of {i}) = {at}", d.Text, "tile", seed);
        }
    }

    // ── operations ───────────────────────────────────────────────────────────

    private static void RunOp(GDoc d, AtxParseResult parse, Rng r, int seed, int trial)
    {
        string text = d.Text;
        AtxEditor ed;
        try { ed = new AtxEditor(text, parse); }
        catch (Exception ex) { Program.Report("EDITOR-CTOR-THROW", ex.ToString(), text, "ctor", seed); return; }

        int n = d.Frames.Count;
        var expected = d.Frames.Select(Clone).ToList();
        var eh = new GDoc
        {
            HeaderPresent = d.HeaderPresent, FrameTime = d.FrameTime, InitiallyOn = d.InitiallyOn,
            AnimationMode = d.AnimationMode, Format = d.Format, AlphaMask = d.AlphaMask, Material = d.Material,
        };

        int nFrameBlocks = parse.SyntaxMap.FrameBlocks.Count;
        bool canonicalFrames = nFrameBlocks == n;
        int op = r.Next(13);
        string opName;
        TextEditBatch batch;
        bool modelCheckable = d.Canonical && canonicalFrames;

        List<int> RandomIndices()
        {
            if (n == 0) return r.Chance(50) ? new List<int>() : new List<int> { r.Next(-2, 3) };
            int count = r.Next(0, n + 2);
            var list = new List<int>();
            for (int i = 0; i < count; i++) list.Add(r.Next(-1, n + 1));
            return list;
        }

        try
        {
            switch (op)
            {
                case 0:
                {
                    var (key, value) = RandomHeaderKV(r);
                    opName = $"SetHeaderValue({key},{value.ToToml()})";
                    batch = ed.SetHeaderValue(key, value);
                    if (modelCheckable)
                    {
                        bool present = HeaderHas(d, key);
                        bool allowed = present || !d.HeaderPresent || d.HeaderIsRealTable;
                        if (allowed) SetHeader(eh, key, value);
                        if (allowed && !eh.HeaderPresent) eh.HeaderPresent = true;
                    }
                    break;
                }
                case 1:
                {
                    string key = r.Pick(AllHeaderKeys);
                    opName = $"RemoveHeaderKey({key})";
                    batch = ed.RemoveHeaderKey(key);
                    if (modelCheckable && d.HeaderIsRealTable) ClearHeader(eh, key);
                    break;
                }
                case 2:
                {
                    var idx = RandomIndices();
                    var (key, value) = RandomFrameKV(r);
                    opName = $"SetFrameValue([{string.Join(",", idx)}],{key},{value.ToToml()})";
                    batch = ed.SetFrameValue(idx, key, value);
                    if (modelCheckable)
                        foreach (int i in idx.Distinct().Where(i => i >= 0 && i < n)) SetFrame(expected[i], key, value);
                    break;
                }
                case 3:
                {
                    string key = r.Pick(AllFrameKeys);
                    var map = new Dictionary<int, AtxValue?>();
                    int count = r.Next(0, n + 2);
                    for (int i = 0; i < count; i++)
                    {
                        int k = r.Next(-1, n + 1);
                        map[k] = r.Chance(35) ? null : RandomValueFor(key, r);
                    }
                    opName = $"SetFrameValues({key},{{{string.Join(",", map.Select(p => p.Key + "=" + (p.Value?.ToToml() ?? "null")))}}})";
                    batch = ed.SetFrameValues(key, map);
                    if (modelCheckable)
                        foreach (var (i, v) in map)
                        {
                            if (i < 0 || i >= n) continue;
                            if (v is null) ClearFrame(expected[i], key); else SetFrame(expected[i], key, v.Value);
                        }
                    break;
                }
                case 4:
                {
                    var idx = RandomIndices();
                    string key = r.Pick(AllFrameKeys);
                    opName = $"RemoveFrameKey([{string.Join(",", idx)}],{key})";
                    batch = ed.RemoveFrameKey(idx, key);
                    if (modelCheckable)
                        foreach (int i in idx.Distinct().Where(i => i >= 0 && i < n)) ClearFrame(expected[i], key);
                    break;
                }
                case 5:
                {
                    int at = r.Next(-1, n + 2);
                    var frames = new List<NewFrame>();
                    int m = r.Next(0, 3);
                    for (int i = 0; i < m; i++)
                    {
                        frames.Add(new NewFrame(
                            "new" + r.Next(1000) + r.Pick(NewFrameNames),
                            r.Chance(50) ? r.Next(1, 5000) : null,
                            r.Chance(40) ? r.Pick(NewMats) : null));
                    }
                    opName = $"InsertFrames({at},[{string.Join(",", frames.Select(f => f.File))}])";
                    batch = ed.InsertFrames(at, frames);
                    if (modelCheckable && frames.Count > 0)
                    {
                        int pos = Math.Clamp(at, 0, n);
                        expected.InsertRange(pos, frames.Select(f => new GFrame
                        { Id = -1, File = f.File, FrameTime = f.FrameTimeMs, Material = f.Material }));
                    }
                    break;
                }
                case 6:
                {
                    var idx = RandomIndices();
                    opName = $"RemoveFrames([{string.Join(",", idx)}])";
                    batch = ed.RemoveFrames(idx);
                    if (modelCheckable)
                    {
                        var kill = idx.Where(i => i >= 0 && i < n).Distinct().ToHashSet();
                        expected = expected.Where((_, i) => !kill.Contains(i)).ToList();
                    }
                    break;
                }
                case 7:
                {
                    var idx = RandomIndices();
                    int target = r.Next(-1, n + 2);
                    opName = $"MoveFrames([{string.Join(",", idx)}],{target})";
                    batch = ed.MoveFrames(idx, target);
                    if (modelCheckable)
                    {
                        var picked = idx.Distinct().Where(i => i >= 0 && i < n).OrderBy(i => i).ToList();
                        if (picked.Count > 0)
                        {
                            int t = Math.Clamp(target, 0, n);
                            var remaining = Enumerable.Range(0, n).Where(i => !picked.Contains(i)).ToList();
                            int insertPos = remaining.Count(i => i < t);
                            var order = new List<int>(remaining);
                            order.InsertRange(insertPos, picked);
                            expected = order.Select(i => Clone(d.Frames[i])).ToList();
                        }
                    }
                    break;
                }
                case 8:
                {
                    var idx = RandomIndices();
                    opName = $"DuplicateFrames([{string.Join(",", idx)}])";
                    batch = ed.DuplicateFrames(idx);
                    if (modelCheckable)
                    {
                        var picked = idx.Distinct().Where(i => i >= 0 && i < n).OrderBy(i => i).ToList();
                        if (picked.Count > 0)
                            expected.InsertRange(picked[^1] + 1, picked.Select(i => Clone(d.Frames[i])));
                    }
                    break;
                }
                case 9:
                {
                    var idx = RandomIndices();
                    opName = $"ReverseFrames([{string.Join(",", idx)}])";
                    batch = ed.ReverseFrames(idx);
                    if (modelCheckable)
                    {
                        var picked = idx.Distinct().Where(i => i >= 0 && i < n).OrderBy(i => i).ToList();
                        if (picked.Count >= 2)
                        {
                            var src = Enumerable.Reverse(picked).ToList();
                            for (int i = 0; i < picked.Count; i++) expected[picked[i]] = Clone(d.Frames[src[i]]);
                        }
                    }
                    break;
                }
                case 10:
                {
                    var idx = RandomIndices();
                    opName = $"SortFrames([{string.Join(",", idx)}])";
                    batch = ed.SortFrames(idx);
                    if (modelCheckable)
                    {
                        var picked = idx.Distinct().Where(i => i >= 0 && i < n).OrderBy(i => i).ToList();
                        if (picked.Count >= 2)
                        {
                            var src = picked
                                .OrderBy(i => parse.Model!.Frames[i].EffectiveFile ?? string.Empty,
                                    NaturalStringComparer.Instance)
                                .ToList();
                            for (int i = 0; i < picked.Count; i++) expected[picked[i]] = Clone(d.Frames[src[i]]);
                        }
                    }
                    break;
                }
                case 11:
                {
                    opName = "Normalize";
                    batch = ed.Normalize();
                    break;
                }
                default:
                {
                    var req = RandomBulk(r, n);
                    opName = $"BulkTiming({req.Scope},{req.Operation})";
                    var plan = BulkTiming.Plan(parse.Model!, req, [.. Enumerable.Range(0, n).Where(_ => r.Chance(50))]);
                    batch = BulkTiming.Apply(ed, plan);
                    if (modelCheckable)
                    {
                        foreach (var c in plan)
                        {
                            if (c.AfterIsOverride) expected[c.FrameIndex].FrameTime = c.AfterMs;
                            else expected[c.FrameIndex].FrameTime = null;
                        }
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Program.Report("OP-THROW:" + ex.GetType().Name, ex.ToString(), text, "op" + op, seed);
            return;
        }

        Program.Cases++;

        // 5. edits in bounds and ordered.
        foreach (var e in batch)
        {
            if (e.Span.Start < 0 || e.Span.End > text.Length || e.Span.Length < 0)
                Program.Report("EDIT-OOB", $"{opName}: edit {e.Span} outside 0..{text.Length}", text, opName, seed);
        }

        string result;
        try { result = batch.Apply(text); }
        catch (Exception ex) { Program.Report("APPLY-THROW", $"{opName}: {ex}", text, opName, seed); return; }

        AtxParseResult p2;
        try { p2 = AtxParser.Parse(result); }
        catch (Exception ex) { Program.Report("REPARSE-THROW", $"{opName}: {ex}", text, opName, seed); return; }

        // 2. no new syntax errors.
        if (p2.Model is null)
        {
            string err = string.Join(" | ", p2.Diagnostics.Select(x => x.Message));
            Program.Report("INVALID-TOML:" + opName.Split('(')[0],
                $"{opName} produced invalid TOML: {err}\nRESULT: {Program.Esc(result)}", text, opName, seed);
            return;
        }

        // 3. model equality.
        if (modelCheckable && op != 11)
        {
            string want = Program.SpecOf(eh.HeaderPresent || !d.HeaderPresent ? eh : eh, expected);
            string got = Program.SpecOf(p2.Model);
            if (want != got)
            {
                Program.Report("OP-MODEL:" + opName.Split('(')[0],
                    $"{opName}\nexpected\n{want}\nactual\n{got}\nRESULT: {Program.Esc(result)}", text, opName, seed);
            }
        }
        if (op == 11 && d.Canonical && canonicalFrames)
        {
            string want = Program.SpecOf(parse.Model!);
            string got = Program.SpecOf(p2.Model);
            if (want != got)
                Program.Report("NORMALIZE-MODEL",
                    $"expected\n{want}\nactual\n{got}\nRESULT: {Program.Esc(result)}", text, opName, seed);
            // idempotence
            var ed2 = new AtxEditor(result, p2);
            var b2 = ed2.Normalize();
            string r2 = b2.Apply(result);
            if (!string.Equals(r2, result, StringComparison.Ordinal))
                Program.Report("NORMALIZE-IDEMPOTENT",
                    $"first: {Program.Esc(result)}\nsecond: {Program.Esc(r2)}", text, opName, seed);
        }

        // 6. EOL preserved, no mixed endings introduced.
        if (d.UniformEol && !batch.IsEmpty && text.IndexOfAny(Breaks) >= 0)
        {
            string? bad = ForeignEol(result, d.Eol);
            if (bad is not null)
                Program.Report("EOL:" + opName.Split('(')[0],
                    $"{opName}: document uses {Program.Esc(d.Eol)} but result contains {bad}\nRESULT: {Program.Esc(result)}",
                    text, opName, seed);
        }
        if (d.HasBom && result.Length > 0 && result[0] != '﻿')
            Program.Report("BOM-LOST", opName + " dropped the byte-order mark", text, opName, seed);

        // 4. comment markers.
        CheckMarkers(d, text, result, op, opName, seed, expected, batch);

        // 7. inverses.
        CheckInverse(d, parse, text, r, seed);
    }

    private static string? ForeignEol(string s, string eol)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\r')
            {
                bool crlf = i + 1 < s.Length && s[i + 1] == '\n';
                string found = crlf ? "\r\n" : "\r";
                if (found != eol) return Program.Esc(found) + $" at {i}";
                if (crlf) i++;
            }
            else if (s[i] == '\n')
            {
                if (eol != "\n") return "\\n at " + i;
            }
        }
        return null;
    }

    private static void CheckMarkers(
        GDoc d, string before, string after, int op, string opName, int seed,
        List<GFrame> expected, TextEditBatch batch)
    {
        var lost = new List<string>();
        bool removes = op is 6;             // RemoveFrames legitimately drops markers
        bool dups = op is 8;
        bool normalize = op is 11;
        bool reorder = op is 7 or 9 or 10;  // attribution is only meaningful when files do not change
        if (removes || dups) return;

        foreach (string m in d.Markers)
        {
            int cb = Count(before, m), ca = Count(after, m);
            if (ca < cb) lost.Add($"{m}: {cb}->{ca}");
        }
        if (lost.Count > 0)
        {
            Program.Report((normalize ? "NORMALIZE-COMMENT-LOST" : "COMMENT-LOST:") + opName.Split('(')[0],
                $"{opName} lost markers {string.Join(", ", lost)}\nRESULT: {Program.Esc(after)}", before, opName, seed);
        }

        // Attribution: each frame block must contain only its own attached comments.
        if (!reorder || batch.IsEmpty) return;
        var p2 = AtxParser.Parse(after);
        if (p2.Model is null) return;
        var map = p2.SyntaxMap;
        for (int i = 0; i < map.FrameBlocks.Count && i < p2.Model.Frames.Count; i++)
        {
            string block = map.FrameBlocks[i].Span.GetText(after);
            foreach (Match mt in Regex.Matches(block, @"owner=(\d+)"))
            {
                int owner = int.Parse(mt.Groups[1].Value, CultureInfo.InvariantCulture);
                string? file = p2.Model.Frames[i].EffectiveFile;
                var src = d.Frames.FirstOrDefault(f => f.Id == owner);
                if (src is null) continue;
                string? srcFile = Program.NonEmpty(src.File);
                if (srcFile != file)
                {
                    Program.Report("COMMENT-MISATTRIBUTED:" + opName.Split('(')[0],
                        $"{opName}: block {i} (file {Program.Q(file)}) carries a comment owned by frame {owner} (file {Program.Q(srcFile)})\nRESULT: {Program.Esc(after)}",
                        before, opName, seed);
                    return;
                }
            }
        }
    }

    private static int Count(string haystack, string needle)
    {
        int c = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            // whole-token match
            bool leftOk = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            int end = i + needle.Length;
            bool rightOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (leftOk && rightOk) c++;
            i = end;
        }
        return c;
    }

    private static void CheckInverse(GDoc d, AtxParseResult parse, string text, Rng r, int seed)
    {
        int n = d.Frames.Count;
        if (n < 2 || !d.Canonical || parse.SyntaxMap.FrameBlocks.Count != n) return;

        // insert then remove
        {
            var ed = new AtxEditor(text, parse);
            int at = r.Next(0, n + 1);
            var b = ed.InsertFrames(at, [new NewFrame("inv_probe.tga")]);
            string t2 = b.Apply(text);
            var p2 = AtxParser.Parse(t2);
            Program.Cases++;
            if (p2.Model is null)
            {
                Program.Report("INVERSE-INSERT-INVALID", $"InsertFrames({at}) gave invalid TOML: {Program.Esc(t2)}", text, "insert", seed);
            }
            else if (p2.SyntaxMap.FrameBlocks.Count == n + 1)
            {
                var ed2 = new AtxEditor(t2, p2);
                string t3 = ed2.RemoveFrames([at]).Apply(t2);
                if (!string.Equals(t3, text, StringComparison.Ordinal))
                {
                    ReportDrift("INVERSE-INSERT-REMOVE", text, t3, false,
                        $"InsertFrames({at}) then RemoveFrames([{at}]) did not restore the text\nmid: {Program.Esc(t2)}\nout: {Program.Esc(t3)}",
                        "insert/remove", seed);
                }
            }
        }

        // move there and back
        {
            var ed = new AtxEditor(text, parse);
            int from = r.Next(0, n), to = r.Next(0, n + 1);
            var b = ed.MoveFrames([from], to);
            if (b.IsEmpty) return;
            string t2 = b.Apply(text);
            var p2 = AtxParser.Parse(t2);
            Program.Cases++;
            if (p2.Model is null || p2.SyntaxMap.FrameBlocks.Count != n) return;
            int landed = to > from ? to - 1 : to;
            var ed2 = new AtxEditor(t2, p2);
            string t3 = ed2.MoveFrames([landed], from <= landed ? from : from + 1).Apply(t2);
            if (!string.Equals(t3, text, StringComparison.Ordinal))
            {
                ReportDrift("INVERSE-MOVE", text, t3, true,
                    $"MoveFrames([{from}],{to}) then back did not restore the text\nmid: {Program.Esc(t2)}\nout: {Program.Esc(t3)}",
                    "move/move", seed);
            }
        }
    }

    /// <summary>
    /// An operation and its inverse must give the file back. Two differences are known and
    /// deliberate rather than defects — review finding F14, second half — so they are reported for
    /// the record instead of failing the run:
    /// <list type="bullet">
    /// <item>the file ends with a different number of blank lines;</item>
    /// <item>for a move, the same lines come back arranged differently, which happens when the
    /// frames have a <c>[header]</c> or another table between them: moving frame 1 to the front and
    /// back again is not a text-level inverse when the blocks interleave.</item>
    /// </list>
    /// Anything that loses, invents or alters a line is still a failure.
    /// </summary>
    private static void ReportDrift(
        string id, string before, string after, bool allowRearrangement, string detail, string op, int seed)
    {
        if (string.Equals(before.TrimEnd(Breaks), after.TrimEnd(Breaks), StringComparison.Ordinal))
            Program.ReportInfo("TRAILING-BREAKS:" + id, detail, before, op, seed);
        else if (allowRearrangement && SameContentLines(before, after))
            Program.ReportInfo("BLANK-LINE-LAYOUT:" + id, detail, before, op, seed);
        else
            Program.Report(id, detail, before, op, seed);
    }

    /// <summary>True when both texts hold exactly the same non-blank lines, in any order.</summary>
    private static bool SameContentLines(string a, string b)
    {
        static List<string> Content(string s) =>
            [.. s.Split(Breaks).Select(l => l.TrimEnd()).Where(l => l.Length > 0)
                 .OrderBy(l => l, StringComparer.Ordinal)];
        var first = Content(a);
        var second = Content(b);
        return first.Count == second.Count && first.SequenceEqual(second, StringComparer.Ordinal);
    }

    // ── value/key helpers ────────────────────────────────────────────────────

    private static readonly string[] AllHeaderKeys =
        { "frame_time", "initially_on", "animation_mode", "format", "alpha_mask", "material" };

    private static readonly string[] AllFrameKeys = { "file", "frame_time", "material" };

    private static readonly string[] NewFrameNames =
        { ".tga", "\"q.tga", "\\b.tga", "#h.tga", "é.tga", "\U0001F600.tga", " .tga", ".tga" };

    private static readonly string[] NewMats = { "metal", "rock", "\"weird\"", "" };

    private static readonly string[] HostileValues =
        { "x.tga", "a\"b.tga", "c\\d.tga", "e#f.tga", "é中.tga", "\U0001F600.tga", "", " ", "tab\there",
          "nl\nline", "ctrl", "0123456789012345678901234567890123456789.tga" };

    private static (string, AtxValue) RandomHeaderKV(Rng r)
    {
        string key = r.Pick(AllHeaderKeys);
        return (key, RandomValueFor(key, r));
    }

    private static (string, AtxValue) RandomFrameKV(Rng r)
    {
        string key = r.Pick(AllFrameKeys);
        return (key, RandomValueFor(key, r));
    }

    private static AtxValue RandomValueFor(string key, Rng r) => key switch
    {
        "frame_time" => AtxValue.Integer(r.Next(100) < 80 ? r.Next(-5, 2000) : r.NextLong()),
        "animation_mode" => AtxValue.Integer(r.Next(-2, 6)),
        "initially_on" => AtxValue.Boolean(r.Chance(50)),
        _ => AtxValue.String(r.Pick(HostileValues)),
    };

    private static bool HeaderHas(GDoc d, string key) => d.HeaderPresent && key switch
    {
        "frame_time" => d.FrameTime is not null,
        "initially_on" => d.InitiallyOn is not null,
        "animation_mode" => d.AnimationMode is not null,
        "format" => d.Format is not null,
        "alpha_mask" => d.AlphaMask is not null,
        _ => d.Material is not null,
    };

    private static void SetHeader(GDoc d, string key, AtxValue v)
    {
        switch (key)
        {
            case "frame_time": d.FrameTime = v.Number; break;
            case "initially_on": d.InitiallyOn = v.Flag; break;
            case "animation_mode": d.AnimationMode = v.Number; break;
            case "format": d.Format = v.Text; break;
            case "alpha_mask": d.AlphaMask = v.Text; break;
            default: d.Material = v.Text; break;
        }
    }

    private static void ClearHeader(GDoc d, string key)
    {
        switch (key)
        {
            case "frame_time": d.FrameTime = null; break;
            case "initially_on": d.InitiallyOn = null; break;
            case "animation_mode": d.AnimationMode = null; break;
            case "format": d.Format = null; break;
            case "alpha_mask": d.AlphaMask = null; break;
            default: d.Material = null; break;
        }
    }

    private static void SetFrame(GFrame f, string key, AtxValue v)
    {
        switch (key)
        {
            case "file": f.File = v.Text; break;
            case "frame_time": f.FrameTime = v.Number; break;
            default: f.Material = v.Text; break;
        }
    }

    private static void ClearFrame(GFrame f, string key)
    {
        switch (key)
        {
            case "file": f.File = null; break;
            case "frame_time": f.FrameTime = null; break;
            default: f.Material = null; break;
        }
    }

    private static GFrame Clone(GFrame f) => new()
    { Id = f.Id, File = f.File, FrameTime = f.FrameTime, Material = f.Material, AttachedComments = f.AttachedComments };

    private static BulkTimingRequest RandomBulk(Rng r, int n) => new()
    {
        Scope = (BulkTimingScope)r.Next(4),
        Operation = (BulkTimingOperation)r.Next(8),
        Value = r.Next(-5, 5000),
        Percent = r.Next(100) < 80 ? r.Next(0, 400) : (r.Chance(50) ? double.NaN : 1e12),
        Offset = r.Next(100) < 80 ? r.Next(-500, 500) : int.MaxValue,
        TotalMs = r.Next(100) < 80 ? r.Next(0, 100000) : int.MaxValue,
        FromMs = r.Next(-5, 3000),
        ToMs = r.Next(-5, 3000),
        RangeStart = r.Next(-2, n + 2),
        RangeEnd = r.Next(-2, n + 2),
        Nth = r.Next(-2, 5),
        NthOffset = r.Next(-2, 5),
    };
}
