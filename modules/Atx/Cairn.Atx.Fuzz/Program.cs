using System.Globalization;
using System.Text;
using Cairn.Atx.Editing;
using Cairn.Atx.Linting;
using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Playback;
using Cairn.Atx.Schema;
using Cairn.Atx.Sequences;
using Cairn.Atx.Text;

namespace Cairn.Atx.Fuzz;

public static class Program
{
    public static readonly Dictionary<string, Failure> Failures = new();
    public static long Cases;

    public sealed record Failure(string Id, string Detail, string Text, string Op, int Seed)
    {
        public int Count;
    }

    /// <summary>Prefix added to every failure id, so BOM-only failures land in their own bucket.</summary>
    public static string Tag = "";

    /// <summary>
    /// Differences that are known and deliberate rather than defects — the documented trailing
    /// line-break drift of move-there-and-back, for instance. They are printed, but they do not
    /// fail the run.
    /// </summary>
    public static readonly Dictionary<string, Failure> Notes = new();

    public static void Report(string rawId, string detail, string text, string op, int seed) =>
        Record(Failures, rawId, detail, text, op, seed);

    /// <summary>Records something worth knowing that is not a failure.</summary>
    public static void ReportInfo(string rawId, string detail, string text, string op, int seed) =>
        Record(Notes, rawId, detail, text, op, seed);

    /// <summary>
    /// Clears everything recorded so far, so a caller that is not this program's own Main — the
    /// regression test that runs a short fixed-seed slice — starts from a known state.
    /// </summary>
    public static void Reset()
    {
        Failures.Clear();
        Notes.Clear();
        Cases = 0;
        Tag = string.Empty;
        Generator.NoMultiline = false;
    }

    /// <summary>Every failure, formatted the way the console report formats them.</summary>
    public static string FailureReport() => string.Join(
        Environment.NewLine,
        Failures.Values.Select(f =>
            $"{f.Id} (x{f.Count}) seed={f.Seed} op={f.Op}{Environment.NewLine}  {f.Detail}"
            + $"{Environment.NewLine}  INPUT: {Esc(f.Text)}"));

    private static void Record(
        Dictionary<string, Failure> into, string rawId, string detail, string text, string op, int seed)
    {
        string id = Tag + rawId;
        if (!into.TryGetValue(id, out var f))
        {
            f = new Failure(id, detail, text, op, seed);
            into[id] = f;
        }
        else if (text.Length < f.Text.Length)
        {
            var nf = new Failure(id, detail, text, op, seed) { Count = f.Count };
            into[id] = nf;
            f = nf;
        }
        f.Count++;
    }

    public static int Main(string[] args)
    {
        int iterations = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 50_000;
        string only = args.Length > 1 ? args[1] : "all";

        if (only == "repro") { Repro.Run(); return 0; }
        if (only == "bench") { Bench.Run(); return 0; }
        if (only == "editor2") { Generator.NoMultiline = true; only = "editor"; }
        if (only is "all" or "editor") EditorFuzz.Run(iterations);
        if (only is "all" or "lint") LintFuzz.Run(Math.Max(2000, iterations / 5));
        if (only is "all" or "play") PlaybackFuzz.Run(Math.Max(2000, iterations / 5));
        if (only is "all" or "seq") SeqFuzz.Run(Math.Max(2000, iterations / 2));

        Console.WriteLine();
        Console.WriteLine($"=== {Cases} property checks, {Failures.Count} distinct failures, "
            + $"{Notes.Count} informational ===");
        foreach (var f in Failures.Values.OrderByDescending(x => x.Count))
        {
            Console.WriteLine();
            Console.WriteLine($"### {f.Id}  (x{f.Count})  seed={f.Seed}  op={f.Op}");
            Console.WriteLine($"    {f.Detail}");
            Console.WriteLine("    INPUT: " + Esc(f.Text));
        }
        foreach (var f in Notes.Values.OrderByDescending(x => x.Count))
        {
            Console.WriteLine();
            Console.WriteLine($"--- (informational) {f.Id}  (x{f.Count})  seed={f.Seed}  op={f.Op}");
            Console.WriteLine($"    {f.Detail}");
            Console.WriteLine("    INPUT: " + Esc(f.Text));
        }
        return Failures.Count == 0 ? 0 : 1;
    }

    public static string Esc(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    // ── Game-visible spec, the thing the model must get right ────────────────

    public static string SpecOf(AtxModel m)
    {
        var sb = new StringBuilder();
        var h = m.Header;
        sb.Append("H{ft=").Append(h.EffectiveFrameTimeMs)
          .Append(",io=").Append(h.EffectiveInitiallyOn)
          .Append(",am=").Append((int)h.EffectiveAnimationMode)
          .Append(",fmt=").Append(Q(h.EffectiveFormat))
          .Append(",mask=").Append(Q(h.EffectiveAlphaMask))
          .Append(",mat=").Append(Q(h.EffectiveMaterial))
          .Append("}\n");
        foreach (var f in m.Frames)
        {
            sb.Append("F{file=").Append(Q(f.EffectiveFile))
              .Append(",ft=").Append(f.FrameTimeOverrideMs?.ToString(CultureInfo.InvariantCulture) ?? "-")
              .Append(",mat=").Append(Q(f.MaterialOverride))
              .Append("}\n");
        }
        return sb.ToString();
    }

    public static string SpecOf(GDoc d, List<GFrame> frames)
    {
        var sb = new StringBuilder();
        bool present = d.HeaderPresent;
        sb.Append("H{ft=").Append(present && d.FrameTime is { } ft ? Clamp(ft) : 100)
          .Append(",io=").Append(present && d.InitiallyOn is { } io ? io : true)
          .Append(",am=").Append(present && d.AnimationMode is { } am ? Mode(am) : 0)
          .Append(",fmt=").Append(Q(present ? NonEmpty(d.Format) : null))
          .Append(",mask=").Append(Q(present ? NonEmpty(d.AlphaMask) : null))
          .Append(",mat=").Append(Q(present ? NonEmpty(d.Material) : null))
          .Append("}\n");
        foreach (var f in frames)
        {
            sb.Append("F{file=").Append(Q(NonEmpty(f.File)))
              .Append(",ft=").Append(f.FrameTime is { } v ? Clamp(v).ToString(CultureInfo.InvariantCulture) : "-")
              .Append(",mat=").Append(Q(NonEmpty(f.Material)))
              .Append("}\n");
        }
        return sb.ToString();
    }

    public static int Clamp(long v) => Math.Max(1, unchecked((int)v));
    public static int Mode(long v) { int n = unchecked((int)v); return n is >= 0 and <= 3 ? n : 0; }
    public static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    public static string Q(string? s) => s is null ? "-" : Esc(s);
}
