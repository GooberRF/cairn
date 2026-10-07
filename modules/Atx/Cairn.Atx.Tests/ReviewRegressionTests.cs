using System.Globalization;
using Cairn.Atx.Editing;
using Cairn.Atx.Linting;
using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Playback;
using Cairn.Atx.Schema;
using Cairn.Atx.Sequences;
using Cairn.Atx.Text;
using Cairn.Workspace;

namespace Cairn.Atx.Tests;

/// <summary>
/// One test per finding of the property-based review, using the reviewer's own smallest
/// reproducer. The finding id is in each test name so a failure says which one came back.
/// </summary>
public class ReviewRegressionTests
{
    private static string Apply(string text, Func<AtxEditor, TextEditBatch> operation) =>
        operation(AtxEditor.Create(text)).Apply(text);

    private static AtxParseResult Valid(string text)
    {
        var parse = AtxParser.Parse(text);
        Assert.NotNull(parse.Model);
        return parse;
    }

    // ── F1: a '#' inside a multi-line string is not a comment ────────────────

    [Fact]
    public void F1_RemovingAFrameAfterAMultiLineStringThatLooksLikeAComment()
    {
        const string text =
            "[[frame]]\nfile = \"a.tga\"\nnote = \"\"\"\nx\n# looks like a comment\"\"\"\n"
            + "[[frame]]\nfile = \"b.tga\"\n";
        string result = Apply(text, e => e.RemoveFrames([1]));
        var after = AtxParser.Parse(result);
        Assert.NotNull(after.Model);
        Assert.Single(after.Model!.Frames);
        Assert.Equal("a.tga", after.Model.Frames[0].EffectiveFile);
        // The string is still whole, comment-looking line and all.
        Assert.Contains("# looks like a comment\"\"\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void F1_BlockBoundariesDoNotStartInsideAMultiLineString()
    {
        const string text =
            "[[frame]]\nfile = \"a.tga\"\nnote = \"\"\"\nx\n# looks like a comment\"\"\"\n"
            + "[[frame]]\nfile = \"b.tga\"\n";
        var map = Valid(text).SyntaxMap;
        Assert.Equal(2, map.FrameBlocks.Count);
        Assert.StartsWith("[[frame]]", map.FrameBlocks[1].Span.GetText(text), StringComparison.Ordinal);
    }

    [Fact]
    public void F1_MovingAFrameAfterAMultiLineStringKeepsTheFileValid()
    {
        const string text =
            "[[frame]]\nfile = \"a.tga\"\nnote = \"\"\"\nx\n# looks like a comment\"\"\"\n"
            + "[[frame]]\nfile = \"b.tga\"\n";
        string result = Apply(text, e => e.MoveFrames([1], 0));
        var after = AtxParser.Parse(result);
        Assert.NotNull(after.Model);
        Assert.Equal(["b.tga", "a.tga"], after.Model!.Frames.Select(f => f.EffectiveFile));
    }

    // ── F2: Normalize keeps the whole of a multi-line value ──────────────────

    [Fact]
    public void F2_NormalizeKeepsAMultiLineStringValueWhole()
    {
        const string text = "[[frame]]\nfile = \"\"\"\nab.tga\"\"\"\n";
        string result = Apply(text, e => e.Normalize());
        var after = AtxParser.Parse(result);
        Assert.NotNull(after.Model);
        Assert.Equal("ab.tga", after.Model!.Frames[0].EffectiveFile);
    }

    [Fact]
    public void F2_NormalizeIsIdempotentWithAMultiLineValue()
    {
        const string text = "[[frame]]\nfile = \"\"\"\nab.tga\"\"\"\n";
        string once = Apply(text, e => e.Normalize());
        string twice = Apply(once, e => e.Normalize());
        Assert.Equal(once, twice);
    }

    // ── F3: the ATX023 rename must not create a duplicate key ────────────────

    [Fact]
    public void F3_RenameIsNotOfferedWhenTheTargetKeyAlreadyExists()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\nfil = \"b.tga\"\n";
        var unknown = Single(AtxLinter.Analyze(Valid(text)), AtxRules.UnknownKey);
        Assert.DoesNotContain(unknown.QuickFixes, f => f.Kind == QuickFixKind.Edit);
        Assert.Contains("already written in this section", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void F3_RenameIsStillOfferedWhenTheTargetKeyIsAbsent()
    {
        const string text = "[[frame]]\nfil = \"b.tga\"\n";
        var unknown = Single(AtxLinter.Analyze(Valid(text)), AtxRules.UnknownKey);
        var fix = Assert.Single(unknown.QuickFixes, f => f.Kind == QuickFixKind.Edit);
        string after = fix.Apply().Apply(text);
        Assert.Equal("[[frame]]\nfile = \"b.tga\"\n", after);
    }

    // ── F4: Generate always terminates and is capped ─────────────────────────

    [Fact]
    public void F4_GenerateTerminatesNearIntMaxValue()
    {
        var names = FrameSequence.Generate("a", int.MaxValue - 2, int.MaxValue, 0, ".tga");
        Assert.Equal(3, names.Count);
        Assert.Equal("a" + int.MaxValue.ToString(CultureInfo.InvariantCulture) + ".tga", names[^1]);
    }

    [Fact]
    public void F4_GenerateIsCappedAndReportsTheRealCount()
    {
        var names = FrameSequence.Generate("a", 0, int.MaxValue, 0, ".tga");
        Assert.Equal(FrameSequence.MaxGenerated, names.Count);
        Assert.Equal(2147483648L, FrameSequence.CountFor(0, int.MaxValue));
    }

    [Fact]
    public void F4_GenerateCountsDownAsWell()
    {
        var names = FrameSequence.Generate("a", 3, 1, 2, ".tga");
        Assert.Equal(["a03.tga", "a02.tga", "a01.tga"], names);
    }

    // ── F5: values Tomlyn reads but the game's parser refuses ────────────────

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("0xFFFFFFFFFFFFFFFF")]
    [InlineData("0o1777777777777777777777")]
    public void F5_IntegersPastSixtyFourBitsAreAnError(string literal)
    {
        var parse = AtxParser.Parse($"[header]\nframe_time = {literal}\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.Contains(parse.Diagnostics, d =>
            d.Code == AtxRules.Syntax && d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("0x7FFFFFFFFFFFFFFF")]
    [InlineData("-9223372036854775808")]
    [InlineData("1_0_0")]
    [InlineData("0b1100100")]
    [InlineData("+100")]
    public void F5_IntegersThatFitAreNotAnError(string literal)
    {
        var parse = AtxParser.Parse($"[header]\nframe_time = {literal}\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.DoesNotContain(parse.Diagnostics, d => d.Code == AtxRules.Syntax);
    }

    [Theory]
    [InlineData("\"\\e\"")]
    [InlineData("\"\\x41\"")]
    [InlineData("\"\"\"\\e\"\"\"")]
    public void F5_EscapesTheGamesParserDoesNotKnowAreAnError(string literal)
    {
        var parse = AtxParser.Parse($"[[frame]]\nfile = {literal}\n");
        var error = Assert.Single(parse.Diagnostics, d => d.Code == AtxRules.Syntax);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("backslash code", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"a\\tb.tga\"")]
    [InlineData("\"a\\u0041b.tga\"")]
    [InlineData("\"a\\\\b.tga\"")]
    [InlineData("'a\\eb.tga'")]
    [InlineData("\"\"\"a\\\nb.tga\"\"\"")]
    public void F5_EscapesTomlPlusPlusAcceptsAreNotAnError(string literal)
    {
        var parse = AtxParser.Parse($"[[frame]]\nfile = {literal}\n");
        Assert.DoesNotContain(parse.Diagnostics, d => d.Code == AtxRules.Syntax);
    }

    // ── F6: Normalize keeps the comments around every kind of key ────────────

    [Fact]
    public void F6_NormalizeKeepsCommentsAroundAQuotedKey()
    {
        const string text = "[header]\n# note\n\"frame_time\" = 20  # trailing\n[[frame]]\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.Normalize());
        Assert.Contains("# note", result, StringComparison.Ordinal);
        Assert.Contains("# trailing", result, StringComparison.Ordinal);
        Assert.Equal(20, AtxParser.Parse(result).Model!.Header.EffectiveFrameTimeMs);
    }

    [Fact]
    public void F6_NormalizeKeepsATrailingCommentOnADeclarationLine()
    {
        const string text = "[header]  # the settings\nframe_time = 20\n\n[[frame]]  # the flash\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.Normalize());
        Assert.Contains("[header]  # the settings", result, StringComparison.Ordinal);
        Assert.Contains("[[frame]]  # the flash", result, StringComparison.Ordinal);
    }

    [Fact]
    public void F6_NormalizeKeepsACommentThatBelongsToNoKey()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n# a note under the frame\n";
        string result = Apply(text, e => e.Normalize());
        Assert.Contains("# a note under the frame", result, StringComparison.Ordinal);
    }

    [Fact]
    public void F6_NormalizeKeepsUnknownKeysAndTables()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\nmine = \"kept\"\n\n[notes]\nwho = \"me\"\n";
        string result = Apply(text, e => e.Normalize());
        Assert.Contains("mine = \"kept\"", result, StringComparison.Ordinal);
        Assert.Contains("[notes]", result, StringComparison.Ordinal);
        Assert.NotNull(AtxParser.Parse(result).Model);
    }

    // ── F7: block lookups are half-open ──────────────────────────────────────

    [Fact]
    public void F7_TheOffsetAtAFrameDeclarationBelongsToThatFrame()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n[[frame]]\nfile = \"b.tga\"\n";
        var map = Valid(text).SyntaxMap;
        for (int i = 0; i < map.FrameBlocks.Count; i++)
        {
            int start = map.FrameBlocks[i].DeclarationSpan.Start;
            Assert.Equal(i, map.FrameIndexAt(start));
            Assert.Equal(i, map.BlockAt(start)!.FrameIndex);
        }
        Assert.Equal(1, map.FrameIndexAt(text.Length));
    }

    // ── F8: natural order is a total order ───────────────────────────────────

    [Fact]
    public void F8_NaturalOrderIsTransitiveWithNonAsciiDigits()
    {
        var comparer = NaturalStringComparer.Instance;
        foreach (var (a, b, c) in new[] { ("Z", "\u0660", "29"), ("Z", "\uFF11", "29"), ("_", "\u0660", "19") })
        {
            int ab = Math.Sign(comparer.Compare(a, b));
            int bc = Math.Sign(comparer.Compare(b, c));
            int ac = Math.Sign(comparer.Compare(a, c));
            if (ab == bc && ab != 0) Assert.Equal(ab, ac);
        }
    }

    [Fact]
    public void F8_SortingAMixedListNeverThrows()
    {
        var random = new Random(12345);
        for (int trial = 0; trial < 500; trial++)
        {
            var list = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                list.Add(random.Next(4) switch
                {
                    0 => "Z" + random.Next(50),
                    1 => "\uFF11" + random.Next(10),
                    2 => "\u0660" + random.Next(10),
                    _ => random.Next(100).ToString(CultureInfo.InvariantCulture),
                });
            }
            list.Sort(NaturalStringComparer.Instance);
        }
    }

    [Fact]
    public void F8_NaturalOrderStillSortsFrameNumbersLikeAPerson()
    {
        var names = new List<string> { "f10.tga", "f2.tga", "f1.tga" };
        names.Sort(NaturalStringComparer.Instance);
        Assert.Equal(["f1.tga", "f2.tga", "f10.tga"], names);
    }

    // ── F9: a leading byte-order mark shifts nothing ─────────────────────────

    [Fact]
    public void F9_SpansAreCorrectAfterAByteOrderMark()
    {
        const string text = "\uFEFF[[frame]]\nfile = \"a.tga\"\n";
        var parse = Valid(text);
        var file = parse.Model!.Frames[0].File!;
        Assert.Equal("\"a.tga\"", file.ValueSpan.GetText(text));
    }

    [Fact]
    public void F9_EditingAFileWithAByteOrderMarkKeepsItAtTheFront()
    {
        const string text = "\uFEFF[[frame]]\nfile = \"a.tga\"\n[[frame]]\nfile = \"b.tga\"\n";
        string result = Apply(text, e => e.MoveFrames([1], 0));
        Assert.StartsWith("\uFEFF[[frame]]", result, StringComparison.Ordinal);
        Assert.Equal(1, result.Count(c => c == '\uFEFF'));
        var after = AtxParser.Parse(result);
        Assert.NotNull(after.Model);
        Assert.Equal(["b.tga", "a.tga"], after.Model!.Frames.Select(f => f.EffectiveFile));
    }

    [Fact]
    public void F9_ClipboardTextDropsALeadingByteOrderMark()
    {
        var frames = FrameClipboard.Parse("\uFEFF[[frame]]\r\nfile = \"a.tga\"\r\n");
        Assert.Equal("a.tga", Assert.Single(frames).File);
    }

    // ── F10 / F11: timing arithmetic does not overflow ───────────────────────

    [Fact]
    public void F10_CycleDurationDoesNotOverflow()
    {
        var model = new AtxModel
        {
            Header = new AtxHeader
            {
                IsPresent = true,
                AnimationMode = Number(2),
            },
            Frames = [.. Enumerable.Range(0, 2000).Select(i => new AtxFrame
            {
                Index = i,
                FrameTime = Number(int.MaxValue),
            })],
        };
        Assert.Equal(2000L * int.MaxValue, AtxPlayback.CycleDurationMs(model));
    }

    [Fact]
    public void F11_BulkOffsetSaturatesInsteadOfWrapping()
    {
        var model = new AtxModel
        {
            Header = new AtxHeader { IsPresent = true },
            Frames = [new AtxFrame { Index = 0, FrameTime = Number(5000) }],
        };
        var plan = BulkTiming.Plan(model, new BulkTimingRequest
        {
            Operation = BulkTimingOperation.OffsetMs,
            Offset = int.MaxValue,
        });
        Assert.Equal(int.MaxValue, plan[0].AfterMs);

        var scaled = BulkTiming.Plan(model, new BulkTimingRequest
        {
            Operation = BulkTimingOperation.ScalePercent,
            Percent = 1e12,
        });
        Assert.Equal(int.MaxValue, scaled[0].AfterMs);

        var nan = BulkTiming.Plan(model, new BulkTimingRequest
        {
            Operation = BulkTimingOperation.ScalePercent,
            Percent = double.NaN,
        });
        Assert.Equal(AtxSchema.MinFrameTimeMs, nan[0].AfterMs);
    }

    // ── F12: float to integer conversion matches toml++ ──────────────────────

    [Theory]
    [InlineData("9223372036854775808.0")]
    [InlineData("1e19")]
    [InlineData("inf")]
    [InlineData("nan")]
    public void F12_FloatsThatDoNotFitAreNotRead(string literal)
    {
        var parse = Valid($"[header]\nframe_time = {literal}\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.False(parse.Model!.Header.FrameTime!.Accepted);
        Assert.Equal(AtxSchema.DefaultFrameTimeMs, parse.Model.Header.EffectiveFrameTimeMs);
    }

    [Theory]
    [InlineData("1e2", 100)]
    [InlineData("80.0", 80)]
    public void F12_WholeNumberedFloatsStillRead(string literal, int expected)
    {
        var parse = Valid($"[header]\nframe_time = {literal}\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.True(parse.Model!.Header.FrameTime!.Accepted);
        Assert.Equal(expected, parse.Model.Header.EffectiveFrameTimeMs);
    }

    // ── F13: generated text never uses bare carriage returns ─────────────────

    [Fact]
    public void F13_ClipboardTextIsValidTomlForACarriageReturnDocument()
    {
        string toml = FrameClipboard.ToToml([new NewFrame("a.tga", 30)], LineEndingKind.Cr);
        Assert.DoesNotContain('\r', toml);
        Assert.Equal("a.tga", Assert.Single(FrameClipboard.Parse(toml)).File);
    }

    // ── F14: adding and removing a frame leaves the file as it was ───────────

    [Fact]
    public void F14_AddingThenRemovingTheLastFrameRestoresTheFile()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n";
        string added = Apply(text, e => e.InsertFrames(2, [new NewFrame("c.tga")]));
        string removed = Apply(added, e => e.RemoveFrames([2]));
        Assert.Equal(text, removed);
    }

    [Fact]
    public void F14_AddingThenRemovingDoesNotAccumulateBlankLines()
    {
        string text = "[[frame]]\nfile = \"a.tga\"\n";
        for (int i = 0; i < 5; i++)
        {
            string added = Apply(text, e => e.InsertFrames(1, [new NewFrame("c.tga")]));
            text = Apply(added, e => e.RemoveFrames([1]));
        }
        Assert.Equal("[[frame]]\nfile = \"a.tga\"\n", text);
    }

    [Fact]
    public void F14_AddingThenRemovingRestoresAFileWithAHeaderAtTheBottom()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n\n[header]\nframe_time = 80\n";
        string added = Apply(text, e => e.InsertFrames(1, [new NewFrame("c.tga")]));
        Assert.NotNull(AtxParser.Parse(added).Model);
        string removed = Apply(added, e => e.RemoveFrames([1]));
        Assert.Equal(text, removed);
    }

    // ── F15: user text never breaks a one-line problem row ───────────────────

    [Fact]
    public void F15_DiagnosticsDoNotCarryRawNewlinesOrControlCharacters()
    {
        const string text = "[[frame]]\nfile = \"sub/bad\\nname\\u0007.tga\"\n";
        foreach (var diagnostic in AtxLinter.Analyze(Valid(text)))
        {
            AssertPrintable(diagnostic.Message);
            AssertPrintable(diagnostic.Help);
            foreach (var fix in diagnostic.QuickFixes) AssertPrintable(fix.Title);
        }

        static void AssertPrintable(string value)
        {
            Assert.DoesNotContain(value, c => c is '\n' or '\r' or '\t' || c < 0x20 || c == 0x7F);
        }
    }

    [Fact]
    public void F15_AVeryLongNameIsCutShortInTheMessage()
    {
        string name = new('z', 500);
        var text = $"[[frame]]\nfile = \"{name}.tga\"\n";
        var diagnostic = Single(AtxLinter.Analyze(Valid(text)), AtxRules.NameTooLong);
        Assert.True(diagnostic.Message.Length < 300, diagnostic.Message);
        Assert.Contains('…', diagnostic.Message);
    }

    // ── S3: a pathological dotted key parses quickly ─────────────────────────

    [Fact]
    public void S3_AnAbsurdlyDeepDottedKeyParsesQuickly()
    {
        string key = string.Join('.', Enumerable.Repeat("b", 100_000));
        string text = $"a.{key} = 1\n[[frame]]\nfile = \"a.tga\"\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var parse = AtxParser.Parse(text);
        watch.Stop();
        Assert.NotNull(parse.Model);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void S3_NoSpellingSuggestionIsMadeForAnAbsurdlyLongKey()
    {
        string name = new('q', 5000);
        string text = $"[[frame]]\nfile = \"a.tga\"\n{name} = 1\n";
        var unknown = Single(AtxLinter.Analyze(Valid(text)), AtxRules.UnknownKey);
        Assert.DoesNotContain("Did you mean", unknown.Message, StringComparison.Ordinal);
    }

    // ── S4: a big but legal .atx opens without a freeze ──────────────────────

    [Fact]
    public void S4_AVeryLargeDocumentParsesAndLintsQuickly()
    {
        static string Document(int frames)
        {
            var sb = new System.Text.StringBuilder(frames * 40);
            sb.Append("[header]\nframe_time = 80\nanimation_mode = 2\n\n");
            for (int i = 0; i < frames; i++)
            {
                sb.Append("[[frame]]\nfile = \"seq_").Append(i).Append(".tga\"\n\n");
            }
            return sb.ToString();
        }

        static double ParseAndLintMs(string text, out AtxParseResult parse)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            parse = AtxParser.Parse(text);
            _ = AtxLinter.Analyze(parse);
            return watch.Elapsed.TotalMilliseconds;
        }

        // What this guards is the growth rate, not the speed of the machine: a wall-clock budget
        // fails whenever the suite shares the CPU with a build. Four times the frames should cost
        // about four times as long; a quadratic slip would cost sixteen.
        ParseAndLintMs(Document(2_000), out _); // warm the JIT so it is not billed to the small run

        string small = Document(25_000);
        string large = Document(100_000);
        Assert.True(large.Length > 2_000_000, $"only {large.Length} characters");

        double smallMs = Math.Min(ParseAndLintMs(small, out _), ParseAndLintMs(small, out _));
        double largeMs = ParseAndLintMs(large, out var parse);

        Assert.NotNull(parse.Model);
        Assert.Equal(100_000, parse.Model!.Frames.Count);
        Assert.True(largeMs < Math.Max(smallMs, 1) * 10,
            $"25,000 frames took {smallMs:F0} ms, 100,000 took {largeMs:F0} ms");
    }

    [Fact]
    public void S4_AnOversizedFileIsRefusedBeforeItIsRead()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cairn-atx-too-big-{Guid.NewGuid():N}.atx");
        try
        {
            using (var file = File.Create(path)) file.SetLength(AtxTextFiles.MaxFileBytes + 1);
            var error = Assert.Throws<FileTooLargeException>(() => AtxTextFiles.Read(path));
            Assert.Equal(AtxTextFiles.MaxFileBytes + 1, error.Length);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // ── S17: a huge paste is capped rather than chewed over ──────────────────

    [Fact]
    public void S17_AnEnormousPasteIsCappedInsteadOfFreezing()
    {
        string huge = string.Concat(Enumerable.Repeat("[[frame]]\nfile = \"a.tga\"\n\n", 200_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var frames = FrameClipboard.Parse(huge);
        watch.Stop();
        Assert.True(frames.Count <= FrameClipboard.MaxParseFrames);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Located<long> Number(long value) =>
        new(value, true, default, default, default, TomlValueKind.Integer,
            value.ToString(CultureInfo.InvariantCulture));

    private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics, string code) =>
        Assert.Single(diagnostics, d => d.Code == code);
}
