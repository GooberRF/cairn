using System.Diagnostics;
using System.Text;
using Cairn.Atx.Linting;
using Cairn.Atx.Parsing;
using Tomlyn.Parsing;

namespace Cairn.Atx.Fuzz;

public static class Bench
{
    public static void Run()
    {
        var sb = new StringBuilder(4 << 20);
        sb.Append("[header]\nframe_time = 80\nanimation_mode = 2\n\n");
        for (int i = 0; i < 100_000; i++) sb.Append("[[frame]]\nfile = \"seq_").Append(i).Append(".tga\"\n\n");
        string text = sb.ToString();
        Console.WriteLine($"{text.Length} chars");

        var w = Stopwatch.StartNew();
        var doc = SyntaxParser.Parse(text, "x.atx", validate: true);
        Console.WriteLine($"Tomlyn only: {w.ElapsedMilliseconds} ms (errors={doc.HasErrors})");

        w.Restart();
        var p = AtxParser.Parse(text);
        Console.WriteLine($"AtxParser.Parse: {w.ElapsedMilliseconds} ms");

        w.Restart();
        AtxLinter.Analyze(p);
        Console.WriteLine($"Lint: {w.ElapsedMilliseconds} ms");
    }
}
