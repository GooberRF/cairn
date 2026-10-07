using System.Diagnostics;
using System.Text;
using Cairn.Assets;
using Cairn.Tbl.Index;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;
using Xunit.Abstractions;

namespace Cairn.Tbl.Tests;

/// <summary>Guards for the defects found by the module review (one test per finding, numbered as the findings).</summary>
public sealed class ReviewTests(ITestOutputHelper o)
{
    private static TblTableSchema S(string f) => TblSchemaSet.Default.Find(f)!;
    private static long Time(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.ElapsedMilliseconds; }

    private ImmutableArray<TblDiagnostic> Lint(string text, string file, TblLintContext? ctx = null)
    {
        var doc = TblDocument.Parse(text, S(file));
        var d = TblLinter.Lint(doc, null, ctx);
        foreach (var x in d) o.WriteLine($"{x.Code} {x.Severity} [{x.Span.Start},{x.Span.Length}] {x.Message}");
        return d;
    }

    private static string Weapon(string glow) =>
        "#Primary Weapons\r\n$Name: \"w\"\r\n$V3D Filename: \"\"\r\n$Flags: ()\r\n" + glow + "#End\r\n#Secondary Weapons\r\n#End\r\n";

    // 1
    [Fact]
    public void F01_DeepNestingParsesOnASmallStack()
    {
        foreach (char open in "(<{")
        {
            Exception? ex = null;
            ImmutableArray<TblDiagnostic> d = [];
            var t = new Thread(() =>
            {
                try { d = TblLinter.Lint(TblDocument.Parse("#Ammo\r\n$Name: " + new string(open, 100_000) + "\r\n#End\r\n", S("ammo.tbl"))); }
                catch (Exception e) { ex = e; }
            }, 256 * 1024);
            long ms = Time(() => { t.Start(); t.Join(); });
            Assert.Null(ex);
            Assert.True(ms < 5000, $"{ms} ms");
            Assert.Contains(d, x => x.Severity == TblSeverity.Error);
        }
    }

    // 2
    [Fact]
    public void F02_BomWithInvalidUtf8RoundTrips()
    {
        byte[] b = [0xEF, 0xBB, 0xBF, .. "$Name: \"caf"u8, 0xE9, (byte)'"', 13, 10];
        var f = TblTextFiles.Decode(b);
        o.WriteLine(f.Encoding + " text=" + f.Text);
        Assert.Equal(b, TblTextFiles.Encode(f.Text, f.Encoding));
        // Mixed: valid UTF-8 and a lone 1252 byte after a BOM.
        byte[] m = [0xEF, 0xBB, 0xBF, .. "\"Caf"u8, 0xC3, 0xA9, 0x20, 0xE9, (byte)'"'];
        var g = TblTextFiles.Decode(m);
        Assert.Equal(m, TblTextFiles.Encode(g.Text, g.Encoding));
    }

    [Fact]
    public void F02_EveryGameTableRoundTripsByteForByte()
    {
        var samples = TestData.Stock().Concat(TestData.Game()).Select(s => (s.Origin, s.Bytes)).ToList();
        if (TestData.GameDirectory is { } root)
            samples.AddRange(Directory.EnumerateFiles(root, "*.tbl", SearchOption.AllDirectories).Select(p => (p, File.ReadAllBytes(p))));
        int n = 0;
        foreach (var (path, b) in samples)
        {
            var f = TblTextFiles.Decode(b);
            Assert.True(b.AsSpan().SequenceEqual(TblTextFiles.Encode(f.Text, f.Encoding)), path);
            n++;
        }
        o.WriteLine($"{n} tables");
    }

    // 3 (core part; the stale-model check is the self-test tbl.editor quick fixes)
    [Fact]
    public void F03_LineEndingFixInsertsOnlyLineBreaks()
    {
        string text = "#Ammo\n// x\n$Name: \"a\"\r$HUD Icon Filename: \"x.tga\"\r\n#End\n";
        var d = Lint(text, "ammo.tbl").First(x => x.Code == "TBL009");
        Assert.All(d.QuickFixes[0].Edits, e => Assert.True(e.Span.Length == 0 && e.NewText is "\r" or "\n"));
        string fixedText = TextEdit.ApplyAll(text, d.QuickFixes[0].Edits);
        Assert.Equal(LineEndings.Normalize(text, LineEndingKind.CrLf), fixedText);
        Assert.DoesNotContain(Lint(fixedText, "ammo.tbl"), x => x.Severity == TblSeverity.Error);
    }

    // 4
    [Fact]
    public void F04_LfCommentBlockLintsInLinearTime()
    {
        string text = "#Ammo\n" + string.Concat(Enumerable.Repeat("// commented out line\n", 40_000));
        long t = Time(() => TblLinter.Lint(TblDocument.Parse(text, S("ammo.tbl"))));
        Assert.True(t < 2000, $"took {t} ms");
    }

    // 5
    [Fact]
    public void F05_LexerIsLinearOnMarkerRunsWithoutColon()
    {
        string big = string.Concat(Enumerable.Repeat("$ ", 80_000)) + string.Concat(Enumerable.Repeat("+", 80_000));
        long t = Time(() => TblLexer.Lex(big));
        Assert.True(t < 1000, $"took {t} ms");
    }

    // 6
    [Fact]
    public void F06_WordGluedToBooleanIsAnError()
    {
        var text = Weapon("$Glow: None\r\n");
        var d = Lint(text, "weapons.tbl");
        int at = text.IndexOf("None", StringComparison.Ordinal);
        Assert.Contains(d, x => x.Span.Start <= at && at < x.Span.End && x.Severity == TblSeverity.Error);
    }

    // 7
    [Fact]
    public void F07_SectionNameEndIsNotEnd()
    {
        var d = Lint("#Ammo\r\n$Name: \"a\"\r\n$HUD Icon Filename: \"x.tga\"\r\n#Ammo End\r\n", "ammo.tbl");
        Assert.Contains(d, x => x.Severity == TblSeverity.Error);
        var ok = Lint("#Ammo\r\n$Name: \"a\"\r\n$HUD Icon Filename: \"x.tga\"\r\n#end\r\n", "ammo.tbl");
        Assert.DoesNotContain(ok, x => x.Severity == TblSeverity.Error);
    }

    // 8
    [Fact]
    public void F08_NonBreakingSpaceIsNotWhite()
    {
        string text = "#Ammo\r\n $Name: \"a\"\r\n$HUD Icon Filename: \"x.tga\"\r\n#End\r\n";
        var d = Lint(text, "ammo.tbl");
        var nbsp = Assert.Single(d, x => x.Severity == TblSeverity.Error && x.Span.Start == text.IndexOf(' '));
        // The fix replaces just that character and the table is clean again.
        Assert.DoesNotContain(Lint(TextEdit.ApplyAll(text, nbsp.QuickFixes[0].Edits), "ammo.tbl"), x => x.Severity == TblSeverity.Error);
    }

    // 9
    [Fact]
    public void F09_StringLimitIs254BytesEverywhere()
    {
        string longName = new string('a', 300) + ".tga";
        Assert.Contains(Lint($"#Ammo\r\n$Name: \"a\"\r\n$HUD Icon Filename: \"{longName}\"\r\n#End\r\n", "ammo.tbl"),
            x => x.Severity == TblSeverity.Error);
        string name = new string('é', 200);
        Assert.Contains(Lint($"#Ammo\r\n$Name: \"{name}\"\r\n$HUD Icon Filename: \"x.tga\"\r\n#End\r\n", "ammo.tbl",
            new TblLintContext { Encoding = TblFileEncoding.Utf8 }), x => x.Code == "TBL115");
        Assert.DoesNotContain(Lint($"#Ammo\r\n$Name: \"{name}\"\r\n$HUD Icon Filename: \"x.tga\"\r\n#End\r\n", "ammo.tbl",
            new TblLintContext { Encoding = TblFileEncoding.Ansi }), x => x.Code == "TBL115");
    }

    // 10
    [Fact]
    public void F10_BareLfInsideStringIsOneString()
    {
        var d = Lint("#Ammo\r\n$Name: \"a\nb\"\r\n$HUD Icon Filename: \"x.tga\"\r\n#End\r\n", "ammo.tbl");
        Assert.DoesNotContain(d, x => x.Severity == TblSeverity.Error);
    }

    // 11
    [Fact]
    public void F11_ColorNeedsBracesAndCommas()
    {
        foreach (var bad in new[] { "255 255 255", "{255 255 255}", "(255, 255, 255)" })
        {
            var text = Weapon("$Glow: true\r\n+Inner Radius: 1.0\r\n+Outer Radius: 2.0\r\n+Color: " + bad + "\r\n");
            int at = text.IndexOf(bad, StringComparison.Ordinal);
            Assert.Contains(Lint(text, "weapons.tbl"), x => x.Span.Start <= at && at <= x.Span.End && x.Severity == TblSeverity.Error);
        }
        var good = Weapon("$Glow: true\r\n+Inner Radius: 1.0\r\n+Outer Radius: 2.0\r\n+Color: {255, 255, 255}\r\n");
        int g = good.IndexOf("+Color", StringComparison.Ordinal);
        Assert.DoesNotContain(Lint(good, "weapons.tbl"), x => x.Span.Start >= g && x.Span.Start < g + 30);
        // The quick fix inserts text the engine accepts: the diagnostic goes and nothing new appears.
        var missing = Weapon("$Glow: true\r\n+Inner Radius: 1.0\r\n+Outer Radius: 2.0\r\n");
        var before = Lint(missing, "weapons.tbl");
        var miss = before.First(x => x.Code == "TBL108" && x.Message.Contains("+Color"));
        var after = Lint(TextEdit.ApplyAll(missing, miss.QuickFixes[0].Edits), "weapons.tbl");
        Assert.Equal(before.Length - 1, after.Length);
        Assert.DoesNotContain(after, x => x.Message.Contains("+Color"));
    }

    // 12
    [Fact]
    public void F12_AlpineStartIsCaseSensitive()
    {
        var d = Lint("#start\r\n$Sniper Rifle Scope Color: FF0000\r\n#End\r\n", "af_client.tbl");
        Assert.Contains(d, x => x.Severity is TblSeverity.Error or TblSeverity.Warning);
    }

    // 13
    [Fact]
    public void F13_ChildCompletionKeepsTheReadOrder()
    {
        string head = "#Primary Weapons\r\n$Name: \"w\"\r\n$V3D Filename: \"\"\r\n$Flags: ()\r\n$Glow: true\r\n+";
        string text = head + "\r\n+Outer Radius: 2.0\r\n#End\r\n";
        var c = Cairn.Tbl.Assist.TblAssist.Complete(TblDocument.Parse(text, S("weapons.tbl")), head.Length);
        o.WriteLine(string.Join(" | ", c.Items.Select(i => i.Label)));
        Assert.DoesNotContain(c.Items, i => i.Label is "+Color:" or "+Outer Radius:" || i.Label.StartsWith('$'));
        Assert.Contains(c.Items, i => i.Label == "+Inner Radius:");
    }

    // 21
    [Fact]
    public void F21_LinesBlockIsLinear()
    {
        string text = "$Name: \"x\"\r\nEn:\r\n" + string.Concat(Enumerable.Repeat("Word: more text\r\n", 40_000)) + "#End\r\n";
        long t = Time(() => TblDocument.Parse(text, S("endgame.tbl")));
        Assert.True(t < 1000, $"took {t} ms");
        // The block still holds every line.
        var block = TblDocument.Parse(text, S("endgame.tbl")).AllFields.First(f => f.Marker == "En:");
        Assert.Equal(3 * 40_000, block.Values.Length);
    }
}
