using System.Globalization;
using System.Text;
using Cairn.Atx.Editing;
using Cairn.Atx.Model;
using Cairn.Atx.Schema;
using Cairn.Atx.Sequences;

namespace Cairn.Atx.Fuzz;

public static class SeqFuzz
{
    private static readonly char[] Alphabet =
        "01239_ -.aAzZ٠०é０".ToCharArray();

    public static void Run(int iterations)
    {
        // ── natural comparer: total order ────────────────────────────────────
        var cmp = NaturalStringComparer.Instance;
        for (int seed = 1; seed <= iterations; seed++)
        {
            var r = new Rng(seed * 2654435761u.GetHashCode() + seed);
            var pool = new List<string>();
            for (int i = 0; i < 6; i++) pool.Add(RandomName(r));

            foreach (string a in pool)
                foreach (string b in pool)
                {
                    Program.Cases++;
                    int ab = Sign(cmp.Compare(a, b)), ba = Sign(cmp.Compare(b, a));
                    if (ab != -ba)
                        Program.Report("CMP-ANTISYMMETRY",
                            $"Compare({Program.Esc(a)},{Program.Esc(b)})={ab} but Compare(b,a)={ba}",
                            $"a={Program.Esc(a)} b={Program.Esc(b)}", "compare", seed);
                }

            foreach (string a in pool)
                foreach (string b in pool)
                    foreach (string c in pool)
                    {
                        Program.Cases++;
                        int ab = Sign(cmp.Compare(a, b)), bc = Sign(cmp.Compare(b, c)), ac = Sign(cmp.Compare(a, c));
                        if (ab <= 0 && bc <= 0 && ac > 0)
                            Program.Report("CMP-TRANSITIVITY",
                                $"a<=b<=c but a>c: a={Program.Esc(a)} b={Program.Esc(b)} c={Program.Esc(c)} "
                                + $"(ab={ab} bc={bc} ac={ac})",
                                $"a={Program.Esc(a)} b={Program.Esc(b)} c={Program.Esc(c)}", "compare", seed);
                        if (ab == 0 && Sign(cmp.Compare(a, c)) != Sign(cmp.Compare(b, c)))
                            Program.Report("CMP-EQUIV",
                                $"a==b but they order differently against c: a={Program.Esc(a)} b={Program.Esc(b)} c={Program.Esc(c)}",
                                $"a={Program.Esc(a)} b={Program.Esc(b)} c={Program.Esc(c)}", "compare", seed);
                    }

            // List.Sort throws InvalidOperationException on an inconsistent comparer.
            var big = new List<string>();
            for (int i = 0; i < 30; i++) big.Add(RandomName(r));
            Program.Cases++;
            try { big.Sort(cmp); }
            catch (Exception ex) { Program.Report("CMP-SORT-THROW", ex.Message, string.Join("|", big.Select(Program.Esc)), "sort", seed); }

            // ── DetectPattern ────────────────────────────────────────────────
            string name = RandomName(r);
            Program.Cases++;
            try
            {
                var p = FrameSequence.DetectPattern(name);
                if (p is not null)
                {
                    string rebuilt = p.NameFor(p.Number);
                    if (!string.Equals(rebuilt, name, StringComparison.Ordinal)
                        && p.Padding == CountDigits(name))
                    {
                        // only a soft check; report only when the reconstruction differs in content
                        if (!rebuilt.Equals(name, StringComparison.Ordinal))
                            Program.Report("SEQ-ROUNDTRIP", $"DetectPattern({Program.Esc(name)}).NameFor(n) = {Program.Esc(rebuilt)}",
                                Program.Esc(name), "DetectPattern", seed);
                    }
                }
            }
            catch (Exception ex) { Program.Report("SEQ-DETECT-THROW", ex.ToString(), Program.Esc(name), "DetectPattern", seed); }
        }

        // ── Generate: memory / overflow ──────────────────────────────────────
        CheckGenerate("start>end", () => FrameSequence.Generate("a", 5, 1, 2, ".tga"));
        CheckGenerate("negative", () => FrameSequence.Generate("a", -3, 3, 2, ".tga"));
        CheckGenerate("padding 0", () => FrameSequence.Generate("a", 0, 3, 0, "tga"));
        CheckGenerate("huge padding", () => FrameSequence.Generate("a", 0, 1, 1000, ".tga"));
        CheckGenerate("step 0", () => FrameSequence.Generate("a", 0, 3, 2, ".tga", 0));
        CheckGenerate("step negative", () => FrameSequence.Generate("a", 0, 3, 2, ".tga", -2));
        CheckGenerateBounded("end = int.MaxValue", () => FrameSequence.Generate("a", int.MaxValue - 3, int.MaxValue, 2, ".tga"));
        CheckGenerateBounded("end = int.MinValue", () => FrameSequence.Generate("a", int.MinValue + 3, int.MinValue, 2, ".tga"));
        CheckGenerateBounded("wide range", () => FrameSequence.Generate("a", 0, 3_000_000, 2, ".tga"));

        // ── BulkTiming arithmetic ────────────────────────────────────────────
        for (int seed = 1; seed <= Math.Min(iterations, 20000); seed++)
        {
            var r = new Rng(seed * 40503);
            int n = r.Next(0, 12);
            var frames = new List<AtxFrame>();
            for (int i = 0; i < n; i++)
            {
                frames.Add(new AtxFrame
                {
                    Index = i,
                    FrameTime = r.Chance(50)
                        ? new Located<long>(r.Next(-5, 5000), true, default, default, default, TomlValueKind.Integer, "x")
                        : null,
                });
            }
            var model = new AtxModel { Header = new AtxHeader { IsPresent = true }, Frames = frames };
            var req = new BulkTimingRequest
            {
                Scope = (BulkTimingScope)r.Next(4),
                Operation = (BulkTimingOperation)r.Next(8),
                Value = r.Next(-5, 5000),
                Percent = r.Next(100) < 70 ? r.Next(0, 500) : (r.Chance(33) ? double.NaN : r.Chance(50) ? 1e18 : double.PositiveInfinity),
                Offset = r.Chance(80) ? r.Next(-1000, 1000) : (r.Chance(50) ? int.MaxValue : int.MinValue),
                TotalMs = r.Chance(80) ? r.Next(-10, 100000) : int.MaxValue,
                FromMs = r.Next(-5, 5000),
                ToMs = r.Next(-5, 5000),
                RangeStart = r.Next(-3, n + 3),
                RangeEnd = r.Next(-3, n + 3),
                Nth = r.Next(-3, 6),
                NthOffset = r.Next(-3, 6),
            };
            Program.Cases++;
            IReadOnlyList<BulkTimingChange> plan;
            try { plan = BulkTiming.Plan(model, req); }
            catch (Exception ex)
            { Program.Report("BULK-THROW", $"{req.Scope}/{req.Operation}: {ex}", Describe(req, n), "Plan", seed); continue; }

            foreach (var c in plan)
            {
                if (c.AfterMs < AtxSchema.MinFrameTimeMs && c.AfterIsOverride)
                    Program.Report("BULK-BELOW-MIN", $"{req.Operation} produced {c.AfterMs} ms", Describe(req, n), "Plan", seed);
                if (c.FrameIndex < 0 || c.FrameIndex >= n)
                    Program.Report("BULK-INDEX", $"index {c.FrameIndex} of {n}", Describe(req, n), "Plan", seed);
            }

            if (req.Operation == BulkTimingOperation.DistributeTotal && plan.Count > 0 && req.TotalMs >= plan.Count)
            {
                long sum = plan.Sum(c => (long)c.AfterMs);
                if (sum != req.TotalMs)
                    Program.Report("BULK-DISTRIBUTE-SUM",
                        $"DistributeTotal({req.TotalMs}) over {plan.Count} frames summed to {sum}",
                        Describe(req, n), "Plan", seed);
            }
            if (req.Operation == BulkTimingOperation.OffsetMs)
            {
                foreach (var c in plan)
                {
                    long exact = Math.Max(1L, (long)c.BeforeMs + req.Offset);
                    if (c.AfterMs != exact && exact <= int.MaxValue)
                        Program.Report("BULK-OFFSET-OVERFLOW",
                            $"Offset {req.Offset} on {c.BeforeMs} ms gave {c.AfterMs}, exact arithmetic gives {exact}",
                            Describe(req, n), "Plan", seed);
                }
            }
            if (req.Operation == BulkTimingOperation.ScalePercent && !double.IsNaN(req.Percent))
            {
                foreach (var c in plan)
                {
                    double exact = Math.Round(c.BeforeMs * req.Percent / 100.0, MidpointRounding.AwayFromZero);
                    if (exact > int.MaxValue && c.AfterMs == 1)
                        Program.Report("BULK-SCALE-OVERFLOW",
                            $"Scale {req.Percent}% of {c.BeforeMs} ms should be ~{exact} but gave {c.AfterMs}",
                            Describe(req, n), "Plan", seed);
                }
            }
        }

        // ── FrameClipboard round trip ────────────────────────────────────────
        for (int seed = 1; seed <= Math.Min(iterations, 20000); seed++)
        {
            var r = new Rng(seed * 22695477);
            int n = r.Next(0, 6);
            var frames = new List<NewFrame>();
            for (int i = 0; i < n; i++)
            {
                frames.Add(new NewFrame(
                    "c" + i + r.Pick(new[] { ".tga", "\"q.tga", "\\b.tga", "#h.tga", "é.tga", "\U0001F600.tga", "" }),
                    r.Chance(50) ? r.Next(1, 5000) : null,
                    r.Chance(40) ? r.Pick(new[] { "metal", "\"x\"", "" }) : null));
            }
            var eol = (Cairn.Atx.Text.LineEndingKind)r.Next(3);
            Program.Cases++;
            string toml;
            try { toml = FrameClipboard.ToToml(frames, eol); }
            catch (Exception ex) { Program.Report("CLIP-TOTOML-THROW", ex.ToString(), string.Join("|", frames.Select(f => f.File)), "clip", seed); continue; }
            IReadOnlyList<NewFrame> back;
            try { back = FrameClipboard.Parse(toml); }
            catch (Exception ex) { Program.Report("CLIP-PARSE-THROW", ex.ToString(), Program.Esc(toml), "clip", seed); continue; }

            var wanted = frames.Where(f => f.File.Length > 0).ToList();
            if (back.Count != wanted.Count)
            {
                Program.Report("CLIP-COUNT:" + eol,
                    $"round trip of {frames.Count} frames ({wanted.Count} with a name) returned {back.Count}\nTOML: {Program.Esc(toml)}",
                    string.Join("|", frames.Select(f => Program.Esc(f.File))), "clip", seed);
                continue;
            }
            for (int i = 0; i < back.Count; i++)
            {
                if (back[i].File != wanted[i].File
                    || back[i].FrameTimeMs != wanted[i].FrameTimeMs
                    || (Program.NonEmpty(back[i].Material) != Program.NonEmpty(wanted[i].Material)))
                {
                    Program.Report("CLIP-ROUNDTRIP:" + eol,
                        $"frame {i}: wrote ({Program.Esc(wanted[i].File)},{wanted[i].FrameTimeMs},{Program.Q(wanted[i].Material)}) "
                        + $"read back ({Program.Esc(back[i].File)},{back[i].FrameTimeMs},{Program.Q(back[i].Material)})\nTOML: {Program.Esc(toml)}",
                        Program.Esc(toml), "clip", seed);
                    break;
                }
            }
        }

        Console.WriteLine($"[sequences/bulk/clipboard] {iterations} rounds");
    }

    private static void CheckGenerate(string what, Func<IReadOnlyList<string>> f)
    {
        Program.Cases++;
        try { var list = f(); if (list.Count > 5_000_000) Program.Report("SEQ-HUGE", $"{what} produced {list.Count} names", what, "Generate", 0); }
        catch (Exception ex) { Program.Report("SEQ-GENERATE-THROW", $"{what}: {ex.GetType().Name} {ex.Message}", what, "Generate", 0); }
    }

    private static void CheckGenerateBounded(string what, Func<IReadOnlyList<string>> f)
    {
        Program.Cases++;
        var t = new Thread(() =>
        {
            try { var list = f(); if (list.Count > 5_000_000) Program.Report("SEQ-HUGE", $"{what} produced {list.Count} names", what, "Generate", 0); }
            catch (Exception ex) { Program.Report("SEQ-GENERATE-THROW", $"{what}: {ex.GetType().Name} {ex.Message}", what, "Generate", 0); }
        }, 1 << 20);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(6)))
            Program.Report("SEQ-GENERATE-HANG", $"{what}: FrameSequence.Generate did not return within 6 s", what, "Generate", 0);
    }

    private static int Sign(int v) => v < 0 ? -1 : v > 0 ? 1 : 0;

    private static int CountDigits(string s) { int c = 0; foreach (char ch in s) if (char.IsDigit(ch)) c++; return c; }

    private static string RandomName(Rng r)
    {
        int len = r.Next(0, 9);
        var sb = new StringBuilder();
        for (int i = 0; i < len; i++) sb.Append(r.Pick(Alphabet));
        if (r.Chance(40)) sb.Append(".tga");
        return sb.ToString();
    }

    private static string Describe(BulkTimingRequest q, int n) =>
        $"n={n} scope={q.Scope} op={q.Operation} value={q.Value} pct={q.Percent} off={q.Offset} total={q.TotalMs} "
        + $"from={q.FromMs} to={q.ToMs} range={q.RangeStart}..{q.RangeEnd} nth={q.Nth}+{q.NthOffset}";
}
