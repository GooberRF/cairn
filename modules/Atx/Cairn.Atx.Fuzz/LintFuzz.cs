using System.Text;
using Cairn.Atx.Linting;
using Cairn.Atx.Parsing;
using Cairn.Atx.Text;

namespace Cairn.Atx.Fuzz;

public static class LintFuzz
{
    private static readonly string[] Fragments =
    {
        "[header]", "[[frame]]", "file = ", "\"a.tga\"", "frame_time = ", "100", "=", "\n", "\r\n", "\r",
        "#", "'", "\"", "[", "]", "{", "}", ",", ".", "\\", "\t", " ", "0x", "inf", "nan", "true",
        "1979-05-27T07:32:00Z", "\"\"\"", "'''", "header.", "frame.", "[[", "]]", "\u0000", "\uFEFF",
        "fil", "materal", "frame_tim", "animation_mode", "1e400", "9223372036854775808", "+", "-",
        "\U0001F600", "\uD800", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    };

    public static void Run(int iterations)
    {
        int ok = 0;
        for (int seed = 1; seed <= iterations; seed++)
        {
            var r = new Rng(seed * 7919);
            string text = Build(r);
            AtxParseResult parse;
            try { parse = AtxParser.Parse(text); }
            catch (Exception ex) { Program.Report("LINT-PARSE-THROW", ex.ToString(), text, "parse", seed); continue; }

            IReadOnlyList<Diagnostic> diags;
            Program.Cases++;
            try { diags = AtxLinter.Analyze(parse, new LintOptions { DocumentPath = @"C:\a\somewhat_long_texture_name_here.atx" }); }
            catch (Exception ex) { Program.Report("LINT-THROW", ex.ToString(), text, "lint", seed); continue; }
            ok++;

            foreach (var d in diags)
            {
                Program.Cases++;
                if (d.Span.Start < 0 || d.Span.Length < 0 || d.Span.End > text.Length)
                    Program.Report("LINT-SPAN", $"{d.Code} span {d.Span} outside 0..{text.Length}", text, "lint", seed);

                foreach (var fix in d.QuickFixes)
                {
                    if (fix.Kind != QuickFixKind.Edit) continue;
                    Program.Cases++;
                    TextEditBatch batch;
                    try { batch = fix.Apply(); }
                    catch (Exception ex)
                    { Program.Report("FIX-THROW:" + d.Code, $"{fix.Title}: {ex}", text, d.Code, seed); continue; }

                    string after;
                    try { after = batch.Apply(text); }
                    catch (Exception ex)
                    { Program.Report("FIX-APPLY-THROW:" + d.Code, $"{fix.Title}: {ex}", text, d.Code, seed); continue; }

                    var p2 = AtxParser.Parse(after);
                    if (parse.Model is not null && p2.Model is null)
                    {
                        Program.Report("FIX-BREAKS-TOML:" + d.Code,
                            $"quick fix '{fix.Title}' for {d.Code} turned a valid file into invalid TOML: "
                            + string.Join(" | ", p2.Diagnostics.Select(x => x.Message))
                            + "\nRESULT: " + Program.Esc(after),
                            text, d.Code, seed);
                        continue;
                    }
                    if (p2.Model is null || batch.IsEmpty) continue;

                    var after2 = AtxLinter.Analyze(p2);
                    if (after2.Any(x => x.Code == d.Code && x.Span.Start == d.Span.Start
                        && x.Message == d.Message))
                    {
                        Program.Report("FIX-INEFFECTIVE:" + d.Code,
                            $"quick fix '{fix.Title}' left {d.Code} in place\nRESULT: " + Program.Esc(after),
                            text, d.Code, seed);
                    }
                }
            }
        }
        Console.WriteLine($"[lint] {iterations} random documents linted ({ok} without throwing)");
    }

    private static string Build(Rng r)
    {
        int mode = r.Next(4);
        var sb = new StringBuilder();
        if (mode == 0)
        {
            int n = r.Next(1, 40);
            for (int i = 0; i < n; i++) sb.Append(r.Pick(Fragments));
        }
        else if (mode == 1)
        {
            int n = r.Next(0, 200);
            for (int i = 0; i < n; i++) sb.Append((char)r.Next(0, 0x2FF));
        }
        else if (mode == 2)
        {
            var d = Generator.Generate(r, false, r.Next(1 << 20));
            string t = d.Text;
            if (t.Length > 0 && r.Chance(70)) t = t[..r.Next(0, t.Length)];   // truncated
            sb.Append(t);
        }
        else
        {
            // Near-valid documents with typo'd keys and hostile values (for "did you mean").
            sb.Append("[header]\n");
            sb.Append("frame_tim = ").Append(r.Next(-5, 300)).Append('\n');
            sb.Append("fomat = \"").Append(r.Pick(Fragments).Replace("\"", "'").Replace("\n", "").Replace("\r", "")).Append("\"\n");
            sb.Append("materal = \"").Append(r.Pick(new[] { "metl", "roc", "wattr", "", "glas" })).Append("\"\n");
            sb.Append("format = \"").Append(r.Pick(new[] { "56", "888_rg", "", "8888_arg" })).Append("\"\n");
            sb.Append("material = \"").Append(r.Pick(new[] { "metl", "rok", "" })).Append("\"\n");
            int n = r.Next(0, 4);
            for (int i = 0; i < n; i++)
            {
                sb.Append("\n[[frame]]\n");
                if (r.Chance(80)) sb.Append("file = \"f").Append(i).Append(r.Pick(new[] { ".tga", ".atx", "", ".xyz", "/sub/a.tga" })).Append("\"\n");
                if (r.Chance(50)) sb.Append("fil = \"other").Append(i).Append(".tga\"\n");
                if (r.Chance(40)) sb.Append("frame_time = ").Append(r.Next(-3, 200)).Append('\n');
                if (r.Chance(30)) sb.Append("frame_tim = 5\n");
                if (r.Chance(30)) sb.Append("materal = \"metl\"\n");
                if (r.Chance(30)) sb.Append("material = \"").Append(r.Pick(new[] { "metl", "metal", "" })).Append("\"\n");
            }
        }
        return sb.ToString();
    }
}
