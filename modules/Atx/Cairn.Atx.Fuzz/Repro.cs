using Cairn.Atx.Editing;
using Cairn.Atx.Linting;
using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Playback;
using Cairn.Atx.Schema;
using Cairn.Atx.Sequences;
using Cairn.Atx.Text;

namespace Cairn.Atx.Fuzz;

public static class Repro
{
    public static void Run()
    {
        Section("BOM shifts every span by one");
        Dump("file = \"a.tga\"\n");
        Dump("﻿[[frame]]\nfile = \"a.tga\"\n");
        Dump("[[frame]]\nfile=\"a.tga\"\n");

        Section("Normalize truncates a multi-line string value");
        Show("[[frame]]\nfile = \"\"\"\nab.tga\"\"\"\n", e => e.Normalize());

        Section("Normalize drops comments around a quoted key");
        Show("[header]\n# note\n\"frame_time\" = 20  # trailing\n", e => e.Normalize());

        Section("Normalize drops a trailing comment on a quoted key in a frame");
        Show("[[frame]]\n\"file\" = \"a.tga\"  # keep me\n", e => e.Normalize());

        Section("Multi-line string ending in a comment-looking line steals the next frame's block");
        string t = "[[frame]]\nfile = \"a.tga\"\nnote = \"\"\"\nx\n# looks like a comment\"\"\"\n[[frame]]\nfile = \"b.tga\"\n";
        DumpBlocks(t);
        Show(t, e => e.RemoveFrames([1]));
        Show(t, e => e.MoveFrames([1], 0));

        Section("FrameIndexAt at a block boundary");
        string t2 = "[[frame]]\nfile = \"a.tga\"\n[[frame]]\nfile = \"b.tga\"\n";
        var p2 = AtxParser.Parse(t2);
        int declStart = p2.SyntaxMap.FrameBlocks[1].DeclarationSpan.Start;
        Console.WriteLine($"  FrameIndexAt(start of frame 1 decl = {declStart}) = {p2.SyntaxMap.FrameIndexAt(declStart)}");
        Console.WriteLine($"  BlockAt(same)                                   = {p2.SyntaxMap.BlockAt(declStart)?.Kind} idx {p2.SyntaxMap.BlockAt(declStart)?.FrameIndex}");

        Section("Rename-key quick fix can create a duplicate key");
        string t3 = "[[frame]]\nfile = \"a.tga\"\nfil = \"b.tga\"\n";
        var p3 = AtxParser.Parse(t3);
        foreach (var d in AtxLinter.Analyze(p3))
        {
            foreach (var f in d.QuickFixes.Where(x => x.Kind == QuickFixKind.Edit))
            {
                string after = f.Apply().Apply(t3);
                var pa = AtxParser.Parse(after);
                Console.WriteLine($"  {d.Code} '{f.Title}' -> {Program.Esc(after)} valid={pa.Model is not null}");
                if (pa.Model is null) Console.WriteLine("     " + string.Join(" | ", pa.Diagnostics.Select(x => x.Message)));
            }
        }

        Section("float -> int64 boundary vs toml++");
        foreach (string v in new[] { "9223372036854775808.0", "-9223372036854775808.0", "1e19", "1e2", "-0.0", "inf", "nan" })
        {
            var p = AtxParser.Parse($"[header]\nframe_time = {v}\n");
            var l = p.Model?.Header.FrameTime;
            Console.WriteLine($"  frame_time = {v,-24} accepted={l?.Accepted} value={l?.Value} effective={p.Model?.Header.EffectiveFrameTimeMs}");
        }

        Section("integer edge cases");
        foreach (string v in new[] { "9223372036854775807", "9223372036854775808", "0xFFFFFFFFFFFFFFFF",
                                     "0x7FFFFFFFFFFFFFFF", "+100", "1_0_0", "0o144", "0b1100100", "-0" })
        {
            var p = AtxParser.Parse($"[header]\nframe_time = {v}\n");
            Console.WriteLine($"  frame_time = {v,-22} model={(p.Model is null ? "SYNTAX ERROR" : p.Model.Header.FrameTime?.Value.ToString())}");
        }

        Section("key spelling equivalence");
        foreach (string k in new[] { "frame_time", "\"frame_time\"", "'frame_time'", "\"frame\\u005Ftime\"", "File", "\"file\"" })
        {
            var p = AtxParser.Parse($"[[frame]]\nfile = \"a.tga\"\n[header]\n{k} = 5\n");
            Console.WriteLine($"  {k,-22} header.frame_time={p.Model?.Header.FrameTime?.Value.ToString() ?? "-"} effective={p.Model?.Header.EffectiveFrameTimeMs}");
        }

        Section("Tomlyn vs toml++ acceptance (workbench green / game refuses, or vice versa)");
        foreach (var (v, tomlpp) in new (string, string)[]
        {
            ("9223372036854775808", "ERROR: not representable as a signed 64-bit integer"),
            ("-9223372036854775809", "ERROR: not representable"),
            ("0xFFFFFFFFFFFFFFFF", "ERROR: >16 hex digits value out of range"),
            ("0x10000000000000000", "ERROR: too many digits"),
            ("007", "ERROR: leading zeroes are prohibited"),
            ("+007", "ERROR: leading zeroes are prohibited"),
            ("1_", "ERROR: underscores must be followed by digits"),
            ("_1", "ERROR"),
            ("0b111111111111111111111111111111111111111111111111111111111111111111", "ERROR: >63 binary digits"),
            ("1.", "ERROR: expected decimal digit"),
            (".5", "ERROR"),
            ("01.5", "ERROR: leading zeroes"),
            ("1e999", "OK (inf)"),
            ("2026-09-18", "OK (date)"),
            ("07:32:00", "OK (time)"),
        })
        {
            var p = AtxParser.Parse($"[header]\nframe_time = {v}\n");
            Console.WriteLine($"  {v,-68} tomlyn={(p.Model is null ? "ERROR" : "ok value=" + (p.Model.Header.FrameTime?.Value.ToString() ?? "-") + " acc=" + p.Model.Header.FrameTime?.Accepted)}   toml++={tomlpp}");
        }
        foreach (var (v, tomlpp) in new (string, string)[]
        {
            ("\"\\uD800\"", "ERROR: unicode surrogates explicitly prohibited"),
            ("\"\\e\"", "ERROR: unknown escape sequence (TOML 1.0)"),
            ("\"\\x41\"", "ERROR: unknown escape sequence (TOML 1.0)"),
            ("\"a\u0001b\"", "ERROR: raw control character in a string"),
            ("\"a\u007Fb\"", "ERROR: raw DEL in a string"),
            ("'''a''''", "?"),
        })
        {
            var p = AtxParser.Parse($"[[frame]]\nfile = {v}\n");
            Console.WriteLine($"  file = {v,-20} tomlyn={(p.Model is null ? "ERROR" : "ok value=" + Program.Esc(p.Model.Frames[0].File?.Value ?? ""))}   toml++={tomlpp}");
        }
        {
            var p = AtxParser.Parse("[[frame]]\nfile = \"a.tga\"\n# comment with a raw \u0001 control char\n");
            Console.WriteLine($"  raw control char in a comment: tomlyn={(p.Model is null ? "ERROR" : "ok")}   toml++=ERROR (control chars other than tab are prohibited in comments)");
        }

        Section("CR-only line endings");
        string cr = "[[frame]]\rfile = \"a.tga\"\r";
        var pcr = AtxParser.Parse(cr);
        Console.WriteLine($"  Tomlyn accepts bare CR: {pcr.Model is not null}; LineEndings.Detect = {LineEndings.Detect(cr)}");
        if (pcr.Model is not null)
        {
            var ecr = new AtxEditor(cr, pcr);
            Console.WriteLine($"  editor EOL = {Program.Esc(ecr.LineEnding)}");
            Console.WriteLine($"  InsertFrames -> {Program.Esc(ecr.InsertFrames(1, [new NewFrame("b.tga")]).Apply(cr))}");
        }

        Section("NaturalStringComparer is not a total order");
        var cmp = NaturalStringComparer.Instance;
        foreach (var (a, b, c) in new[] { ("Z", "٠", "29"), ("Z", "１", "29"), ("_", "٠", "19") })
        {
            Console.WriteLine($"  a={Program.Esc(a)} b={Program.Esc(b)} c={Program.Esc(c)}: "
                + $"a?b={cmp.Compare(a, b)} b?c={cmp.Compare(b, c)} a?c={cmp.Compare(a, c)}");
        }
        int threw = 0, trials = 0;
        string? firstMessage = null;
        for (int trial = 0; trial < 20000; trial++)
        {
            var list = new List<string>();
            var rr = new Random(trial);
            int cnt = rr.Next(8, 200);
            for (int i = 0; i < cnt; i++)
            {
                list.Add(rr.Next(4) switch
                {
                    0 => "Z" + rr.Next(50),
                    1 => "１" + rr.Next(10),
                    2 => "٠" + rr.Next(10),
                    _ => rr.Next(100).ToString(),
                });
            }
            trials++;
            try { list.Sort(cmp); }
            catch (InvalidOperationException ex) { threw++; firstMessage ??= ex.Message; }
        }
        Console.WriteLine($"  List.Sort(NaturalStringComparer) threw on {threw}/{trials} random lists: {firstMessage}");

        Section("BulkTiming OffsetMs int overflow");
        {
            var model = new AtxModel
            {
                Header = new AtxHeader { IsPresent = true },
                Frames = [new AtxFrame { Index = 0, FrameTime = new Located<long>(5000, true, default, default, default, TomlValueKind.Integer, "5000") }],
            };
            var plan = Cairn.Atx.Editing.BulkTiming.Plan(model,
                new BulkTimingRequest { Operation = BulkTimingOperation.OffsetMs, Offset = int.MaxValue });
            Console.WriteLine($"  5000 ms + int.MaxValue -> {plan[0].AfterMs} ms (exact = {(long)5000 + int.MaxValue})");
            var plan2 = Cairn.Atx.Editing.BulkTiming.Plan(model,
                new BulkTimingRequest { Operation = BulkTimingOperation.ScalePercent, Percent = 1e12 });
            Console.WriteLine($"  5000 ms x 1e12% -> {plan2[0].AfterMs} ms");
        }

        Section("CycleDurationMs overflow");
        {
            var frames = Enumerable.Range(0, 2000).Select(i => new AtxFrame
            {
                Index = i,
                FrameTime = new Located<long>(int.MaxValue, true, default, default, default, TomlValueKind.Integer, "x"),
            }).ToList();
            var m = new AtxModel
            {
                Header = new AtxHeader
                {
                    IsPresent = true,
                    AnimationMode = new Located<long>(2, true, default, default, default, TomlValueKind.Integer, "2"),
                },
                Frames = frames,
            };
            Console.WriteLine($"  2000 x int.MaxValue ms, Loop -> CycleDurationMs = {AtxPlayback.CycleDurationMs(m)}");
        }

        Section("FrameClipboard with CR line endings");
        {
            string toml = Cairn.Atx.Editing.FrameClipboard.ToToml(
                [new NewFrame("a.tga", 30)], LineEndingKind.Cr);
            Console.WriteLine($"  ToToml(..., Cr) = {Program.Esc(toml)}");
            Console.WriteLine($"  Parse(that).Count = {Cairn.Atx.Editing.FrameClipboard.Parse(toml).Count}");
        }

        Section("Diagnostic text carries raw user bytes");
        {
            var p15 = AtxParser.Parse("[[frame]]" + Nl + "file = " + Chr(34) + "sub/bad" + Bs + "nname" + Bs + "u0007.tga" + Chr(34) + Nl);
foreach (var dd in AtxLinter.Analyze(p15))
                Console.WriteLine($"  {dd.Code}: {Program.Esc(dd.Message)}");
        }

        Section("Generate range guards");
        var th = new Thread(() =>
        {
            var g = FrameSequence.Generate("a", int.MaxValue - 2, int.MaxValue, 0, ".tga");
            Console.WriteLine($"  Generate(MaxValue-2..MaxValue) returned {g.Count}");
        }, 1 << 20);
        th.IsBackground = true; th.Start();
        Console.WriteLine(th.Join(TimeSpan.FromSeconds(5)) ? "  (returned)" : "  *** HUNG: Generate(int.MaxValue) never returns ***");

        Section("CycleDurationMs overflow");
        Console.WriteLine("  see fuzz output");

        Section("Empty-document EOL");
        var ee = AtxEditor.Create("");
        Console.WriteLine("  SetHeaderValue on empty doc -> " + Program.Esc(ee.SetHeaderValue("material", AtxValue.String("metal")).Apply("")));
    }

    private static readonly string Nl = ((char)10).ToString();
    private static readonly string Bs = ((char)92).ToString();
    private static string Chr(int c) => ((char)c).ToString();

    private static void Section(string s) { Console.WriteLine(); Console.WriteLine("== " + s); }

    private static void Dump(string text)
    {
        var p = AtxParser.Parse(text);
        Console.WriteLine($"  {Program.Esc(text)}");
        if (p.Model is null) { Console.WriteLine("    (invalid)"); return; }
        foreach (var f in p.Model.Frames)
        {
            if (f.File is null) continue;
            Console.WriteLine($"    file ValueSpan {f.File.ValueSpan} -> {Program.Esc(f.File.ValueSpan.GetText(text))} (value {Program.Esc(f.File.Value ?? "")})");
        }
        if (p.Model.Header.FrameTime is { } ft)
            Console.WriteLine($"    frame_time ValueSpan {ft.ValueSpan} -> {Program.Esc(ft.ValueSpan.GetText(text))}");
    }

    private static void DumpBlocks(string text)
    {
        var p = AtxParser.Parse(text);
        Console.WriteLine("  " + Program.Esc(text));
        foreach (var b in p.SyntaxMap.Blocks)
            Console.WriteLine($"    {b.Kind}[{b.FrameIndex}] span {b.Span} = {Program.Esc(b.Span.GetText(text))}");
    }

    private static void Show(string text, Func<AtxEditor, TextEditBatch> op)
    {
        var e = AtxEditor.Create(text);
        var b = op(e);
        string after = b.Apply(text);
        var p = AtxParser.Parse(after);
        Console.WriteLine($"  in : {Program.Esc(text)}");
        Console.WriteLine($"  out: {Program.Esc(after)}   valid={p.Model is not null}");
        if (p.Model is null) Console.WriteLine("       " + string.Join(" | ", p.Diagnostics.Select(x => x.Message)));
    }
}
