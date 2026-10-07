using System.Diagnostics;
using System.Text;
using Cairn.Tbl.Model;
using Cairn.Tbl.Text;
using Xunit.Abstractions;

namespace Cairn.Tbl.Tests;

public sealed class TextTests(ITestOutputHelper output)
{
    private static string Concat(string text, ImmutableArray<TblLexToken> tokens)
    {
        var sb = new StringBuilder(text.Length);
        int expected = 0;
        foreach (var t in tokens)
        {
            Assert.Equal(expected, t.Start);
            Assert.True(t.Length > 0);
            sb.Append(text, t.Start, t.Length);
            expected = t.End;
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData("")]
    [InlineData("$Name: \"a\"\r\n")]
    [InlineData("#Primary Weapons // c\r\n$Flags: (\"a\" \"b\")\n+State: \"idle\" \"x.mvf\"\r$V: <1, 2,3>")]
    [InlineData("\"unterminated\r\n/* open")]
    [InlineData("$Display Name: XSTR(296, \"Remote\")\t\r\n#End  ")]
    [InlineData("$ + : :: \"\" () <> {} , // /* */ # #End $x")]
    public void LexerIsLossless(string text) => Assert.Equal(text, Concat(text, TblLexer.Lex(text)));

    [Fact]
    public void LexerClassifiesTheEngineSyntax()
    {
        const string text = "$Body Temperature(F):  -98.6 +State: \"a\" En: yes 0x1F 1e3 1.0f $NoColon";
        var tokens = TblLexer.Lex(text).Where(t => !t.IsTrivia).Select(t => (t.Kind, t.GetText(text))).ToList();
        Assert.Equal((TblLexKind.FieldName, "$Body Temperature(F):"), tokens[0]);
        Assert.Equal((TblLexKind.Number, "-98.6"), tokens[1]);
        Assert.Equal((TblLexKind.FieldName, "+State:"), tokens[2]);
        Assert.Equal((TblLexKind.String, "\"a\""), tokens[3]);
        Assert.Equal((TblLexKind.BareKey, "En:"), tokens[4]);
        Assert.Equal((TblLexKind.Boolean, "yes"), tokens[5]);
        Assert.Equal((TblLexKind.Number, "0x1F"), tokens[6]);
        Assert.Equal((TblLexKind.Number, "1e3"), tokens[7]);
        Assert.Equal(TblLexKind.Word, tokens[8].Item1);
        var last = TblLexer.Lex(text).Last();
        Assert.True(last.Flags.HasFlag(TblLexFlags.MarkerWithoutColon));
    }

    [Fact]
    public void EncodingRoundTrips()
    {
        byte[] ansi = [(byte)'G', (byte)'r', (byte)':', 0xF6, 0xE9, 0x80, (byte)'\r', (byte)'\n'];
        var file = TblTextFiles.Decode(ansi);
        Assert.Equal(TblFileEncoding.Ansi, file.Encoding);
        Assert.Equal("Gr:öé€\r\n", file.Text);
        Assert.Equal(ansi, TblTextFiles.Encode(file.Text, file.Encoding));

        byte[] utf8 = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a ö\n")];
        var u = TblTextFiles.Decode(utf8);
        Assert.Equal(TblFileEncoding.Utf8Bom, u.Encoding);
        Assert.Equal(LineEndingKind.Lf, u.LineEnding);
        Assert.Equal(utf8, TblTextFiles.Encode(u.Text, u.Encoding));

        byte[] plain = Encoding.UTF8.GetBytes("ö");
        Assert.Equal(TblFileEncoding.Utf8, TblTextFiles.Decode(plain).Encoding);
        Assert.Equal(['Ω'], TblTextFiles.Unencodable("aΩö", TblFileEncoding.Ansi));
    }

    [Fact]
    public void EveryTableRoundTripsThroughDecodeLexAndEncode()
    {
        var samples = TestData.Stock().Concat(TestData.Game()).ToList();
        if (samples.Count == 0) return;
        var sw = Stopwatch.StartNew();
        long chars = 0;
        foreach (var s in samples)
        {
            var file = TblTextFiles.Decode(s.Bytes);
            Assert.True(file.Text == Concat(file.Text, TblLexer.Lex(file.Text)), s.Origin);
            Assert.True(s.Bytes.AsSpan().SequenceEqual(TblTextFiles.Encode(file.Text, file.Encoding)), "bytes: " + s.Origin);
            chars += file.Text.Length;
        }
        output.WriteLine($"{samples.Count} tables ({TestData.Stock().Count} research stock, {TestData.Game().Count(g => g.IsStock)} game stock), {chars:N0} characters, {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void LineMapMapsBothWays()
    {
        var map = new LineMap("a\r\nbc\nd\re");
        Assert.Equal(4, map.LineCount);
        Assert.Equal((2, 2), map.PositionOf(4));
        Assert.Equal(6, map.OffsetOf(3, 1));
        Assert.Equal(4, map.LineOf(100));
    }

    [Fact]
    public void ApplyAllEditsFromTheEnd()
    {
        string r = TextEdit.ApplyAll("abcdef", [TextEdit.Insert(0, "X"), TextEdit.Replace(new TextSpan(2, 2), "YY"), TextEdit.Delete(new TextSpan(5, 1))]);
        Assert.Equal("XabYYe", r);
    }

    [Fact]
    public void ClassifierCoversStringsAndNames()
    {
        const string text = "$Name: \"x\"\r\n$V3D Filename: \"a.v3d\"\r\n";
        var doc = TblDocument.Parse(text);
        var spans = TblClassifier.Classify(doc);
        Assert.Contains(spans, s => s.Class == TblTextClass.FileName && s.Span.GetText(text) == "a.v3d");
        Assert.Contains(spans, s => s.Class == TblTextClass.FieldName && s.Span.GetText(text) == "$Name:");
        for (int i = 1; i < spans.Length; i++) Assert.True(spans[i].Span.Start >= spans[i - 1].Span.End);
    }
}
