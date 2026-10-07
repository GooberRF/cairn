using Cairn.Atx.Linting;
using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Atx.Text;

namespace Cairn.Atx.Tests;

public class ParserTests
{
    private static AtxModel Model(string text)
    {
        var parse = AtxParser.Parse(text);
        Assert.NotNull(parse.Model);
        return parse.Model!;
    }

    // ── parse.h parity: [header] ─────────────────────────────────────────────

    [Fact]
    public void MissingHeaderUsesDefaults()
    {
        var model = Model("[[frame]]\nfile = \"a.tga\"\n");
        Assert.False(model.Header.IsPresent);
        Assert.Equal(AtxSchema.DefaultFrameTimeMs, model.Header.EffectiveFrameTimeMs);
        Assert.True(model.Header.EffectiveInitiallyOn);
        Assert.Equal(AtxAnimationMode.Static, model.Header.EffectiveAnimationMode);
        Assert.Null(model.Header.EffectiveFormat);
        Assert.Null(model.Header.EffectiveAlphaMask);
        Assert.Null(model.Header.EffectiveMaterial);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("1", 1)]
    [InlineData("80", 80)]
    public void FrameTimeIsClampedToOne(string written, int expected)
    {
        var model = Model($"[header]\nframe_time = {written}\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.Equal(expected, model.Header.EffectiveFrameTimeMs);
    }

    [Theory]
    [InlineData("0", AtxAnimationMode.Static)]
    [InlineData("1", AtxAnimationMode.PingPong)]
    [InlineData("2", AtxAnimationMode.Loop)]
    [InlineData("3", AtxAnimationMode.PlayOnce)]
    [InlineData("4", AtxAnimationMode.Static)]
    [InlineData("-1", AtxAnimationMode.Static)]
    [InlineData("99999", AtxAnimationMode.Static)]
    public void AnimationModeFallsBackToStaticOutOfRange(string written, AtxAnimationMode expected)
    {
        var model = Model($"[header]\nanimation_mode = {written}\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.Equal(expected, model.Header.EffectiveAnimationMode);
    }

    [Fact]
    public void EmptyStringsCountAsUnset()
    {
        var model = Model("""
            [header]
            format = ""
            alpha_mask = ""
            material = ""
            [[frame]]
            file = "a.tga"
            material = ""
            """);
        Assert.Null(model.Header.EffectiveFormat);
        Assert.Null(model.Header.EffectiveAlphaMask);
        Assert.Null(model.Header.EffectiveMaterial);
        Assert.Null(model.Frames[0].MaterialOverride);
        // The keys are still present in the document, which is what ATX037 reports.
        Assert.NotNull(model.Header.Format);
        Assert.NotNull(model.Frames[0].Material);
    }

    [Fact]
    public void WrongTypesAreIgnoredLikeTomlPlusPlus()
    {
        var model = Model("""
            [header]
            frame_time = "80"
            initially_on = 1.0
            animation_mode = "2"
            format = 565
            [[frame]]
            file = "a.tga"
            """);
        // node::value<T>() only converts between the numeric kinds; a string is never a number
        // and a float is never a bool, so all four lines are dropped and the defaults apply.
        Assert.Equal(AtxSchema.DefaultFrameTimeMs, model.Header.EffectiveFrameTimeMs);
        Assert.True(model.Header.EffectiveInitiallyOn);
        Assert.Equal(AtxAnimationMode.Static, model.Header.EffectiveAnimationMode);
        Assert.Null(model.Header.EffectiveFormat);
        Assert.False(model.Header.FrameTime!.Accepted);
        Assert.False(model.Header.InitiallyOn!.Accepted);
        Assert.False(model.Header.AnimationMode!.Accepted);
        Assert.Equal(TomlValueKind.String, model.Header.FrameTime.ActualKind);
        Assert.Equal(TomlValueKind.Float, model.Header.InitiallyOn.ActualKind);
    }

    [Fact]
    public void IntegersAndBooleansConvertLikeTomlPlusPlus()
    {
        // toml++ node::value<bool>() accepts an integer node with C truthiness, and
        // node::value<int64_t>() accepts a boolean node as 1 or 0. The game therefore does read
        // these lines, which is the opposite of what "wrong type, ignored" would imply.
        var off = Model("""
            [header]
            initially_on = 0
            animation_mode = 2
            [[frame]]
            file = "a.tga"
            """);
        Assert.True(off.Header.InitiallyOn!.Accepted);
        Assert.False(off.Header.EffectiveInitiallyOn);

        var on = Model("[header]\ninitially_on = 5\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.True(on.Header.InitiallyOn!.Accepted);
        Assert.True(on.Header.EffectiveInitiallyOn);

        var flags = Model("""
            [header]
            frame_time = true
            animation_mode = true
            [[frame]]
            file = "a.tga"
            frame_time = false
            """);
        Assert.True(flags.Header.FrameTime!.Accepted);
        Assert.Equal(1, flags.Header.EffectiveFrameTimeMs);
        Assert.Equal(AtxAnimationMode.PingPong, flags.Header.EffectiveAnimationMode);
        // false reads as 0, which the >= 1 clamp then raises.
        Assert.Equal(1, flags.Frames[0].FrameTimeOverrideMs);
    }

    [Fact]
    public void OversizedIntegersNarrowBeforeTheGameClampsThem()
    {
        // parse.h does std::max<int>(1, static_cast<int>(*v)) — the narrowing happens first, so a
        // value that does not fit in an int wraps rather than saturating.
        var wrapped = Model("""
            [header]
            frame_time = 4294967296
            animation_mode = 4294967298
            [[frame]]
            file = "a.tga"
            """);
        Assert.Equal(1, wrapped.Header.EffectiveFrameTimeMs);
        Assert.Equal(AtxAnimationMode.Loop, wrapped.Header.EffectiveAnimationMode);

        var big = Model("[header]\nframe_time = 2147483648\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.Equal(1, big.Header.EffectiveFrameTimeMs);
    }

    [Fact]
    public void WholeNumberFloatIsAcceptedButFlaggedAsFloat()
    {
        var model = Model("[header]\nframe_time = 80.0\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.True(model.Header.FrameTime!.Accepted);
        Assert.Equal(TomlValueKind.Float, model.Header.FrameTime.ActualKind);
        Assert.Equal(80, model.Header.EffectiveFrameTimeMs);
    }

    [Fact]
    public void FractionalFloatIsNotLosslessSoItIsIgnored()
    {
        var model = Model("[header]\nframe_time = 80.5\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.False(model.Header.FrameTime!.Accepted);
        Assert.Equal(AtxSchema.DefaultFrameTimeMs, model.Header.EffectiveFrameTimeMs);
    }

    [Fact]
    public void FormatAndMaterialTokensAreCaseInsensitive()
    {
        Assert.NotNull(AtxSchema.ParseFormatToken("8888_ARGB"));
        Assert.NotNull(AtxSchema.ParseFormatToken("565"));
        Assert.NotNull(AtxSchema.ParseFormatToken("1555_Argb"));
        Assert.Null(AtxSchema.ParseFormatToken("8888_rgba"));
        Assert.Equal(2, AtxSchema.ParseMaterial("METAL")!.Index);
        Assert.Null(AtxSchema.ParseMaterial("metl"));
    }

    // ── parse.h parity: frames ───────────────────────────────────────────────

    [Fact]
    public void FramesAreReadInDocumentOrder()
    {
        var model = Model("""
            [[frame]]
            file = "a.tga"
            [[frame]]
            file = "b.tga"
            [header]
            frame_time = 40
            [[frame]]
            file = "c.tga"
            """);
        Assert.Equal(["a.tga", "b.tga", "c.tga"], model.Frames.Select(f => f.EffectiveFile));
        Assert.Equal(40, model.Header.EffectiveFrameTimeMs);
    }

    [Fact]
    public void PerFrameOverridesClampAndInherit()
    {
        var model = Model("""
            [header]
            frame_time = 80
            material = "metal"
            [[frame]]
            file = "a.tga"
            [[frame]]
            file = "b.tga"
            frame_time = 0
            material = "glass"
            """);
        Assert.Null(model.Frames[0].FrameTimeOverrideMs);
        Assert.Equal(80, model.FrameTimeMs(0));
        Assert.Equal("metal", model.MaterialFor(0));
        Assert.Equal(1, model.Frames[1].FrameTimeOverrideMs);
        Assert.Equal(1, model.FrameTimeMs(1));
        Assert.Equal("glass", model.MaterialFor(1));
        Assert.Equal(1, model.OverrideCount);
    }

    [Fact]
    public void DuplicateKeysAreATomlError()
    {
        var parse = AtxParser.Parse("[header]\nframe_time = 1\nframe_time = 2\n");
        Assert.Null(parse.Model);
        Assert.Contains(parse.Diagnostics, d => d.Code == AtxRules.Syntax);
    }

    [Fact]
    public void DuplicateTablesAreATomlError()
    {
        var parse = AtxParser.Parse("[header]\nframe_time = 1\n[header]\nframe_time = 2\n");
        Assert.Null(parse.Model);
        Assert.Contains(parse.Diagnostics, d => d.Code == AtxRules.Syntax);
    }

    [Fact]
    public void SyntaxErrorReportsLineAndColumn()
    {
        var parse = AtxParser.Parse("[header]\nframe_time = \n");
        Assert.Null(parse.Model);
        var diagnostic = Assert.Single(parse.Diagnostics);
        Assert.Equal(AtxRules.Syntax, diagnostic.Code);
        Assert.StartsWith("Line 2", diagnostic.Message);
    }

    [Fact]
    public void SyntaxErrorsAreWordedForDesignersNotForTheParser()
    {
        // Tomlyn reports "Unexpected token found `]` (token: `closebracket`) while expecting
        // `]]` (token: `closebracketdouble`)". The token names are parser internals and mean
        // nothing to a level designer, so they must not reach the banner or the problems panel.
        var parse = AtxParser.Parse("[header]\n\n[[frame]\nfile = \"a.tga\"\n");
        var diagnostic = Assert.Single(parse.Diagnostics, d => d.Code == AtxRules.Syntax);
        Assert.DoesNotContain("token:", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("`", diagnostic.Message, StringComparison.Ordinal);
        Assert.StartsWith("Line 3, column ", diagnostic.Message, StringComparison.Ordinal);
        Assert.EndsWith(".", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyDocumentParsesWithNoFrames()
    {
        var parse = AtxParser.Parse(string.Empty);
        Assert.NotNull(parse.Model);
        Assert.Empty(parse.Model!.Frames);
        Assert.False(parse.Model.Header.IsPresent);
        Assert.True(parse.IsCanonical);
    }

    // ── Layout detection ─────────────────────────────────────────────────────

    [Fact]
    public void StandardLayoutIsCanonical()
    {
        var parse = AtxParser.Parse("[header]\nframe_time = 1\n\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.True(parse.IsCanonical);
        Assert.Empty(parse.NonCanonicalReasons);
    }

    [Fact]
    public void HeaderAfterFramesIsStillCanonical()
    {
        var parse = AtxParser.Parse("[[frame]]\nfile = \"a.tga\"\n\n[header]\nframe_time = 1\n");
        Assert.True(parse.IsCanonical);
    }

    [Fact]
    public void InlineTableArrayIsNonCanonicalButStillReadable()
    {
        var parse = AtxParser.Parse("frame = [{file=\"a.tga\"},{file=\"b.tga\", frame_time=50}]\n");
        Assert.False(parse.IsCanonical);
        Assert.Contains(NonCanonicalReason.FramesAsInlineTableArray, parse.NonCanonicalReasons);
        Assert.Equal(["a.tga", "b.tga"], parse.Model!.Frames.Select(f => f.EffectiveFile));
        Assert.Equal(50, parse.Model.Frames[1].FrameTimeOverrideMs);
    }

    [Fact]
    public void DottedHeaderKeysAreNonCanonicalButStillReadable()
    {
        var parse = AtxParser.Parse("header.frame_time = 80\nheader.material = \"ice\"\n[[frame]]\nfile=\"a.tga\"\n");
        Assert.False(parse.IsCanonical);
        Assert.Contains(NonCanonicalReason.HeaderAsDottedKeys, parse.NonCanonicalReasons);
        Assert.Equal(80, parse.Model!.Header.EffectiveFrameTimeMs);
        Assert.Equal("ice", parse.Model.Header.EffectiveMaterial);
    }

    [Fact]
    public void InlineHeaderTableIsNonCanonicalButStillReadable()
    {
        var parse = AtxParser.Parse("header = { frame_time = 30 }\n[[frame]]\nfile=\"a.tga\"\n");
        Assert.False(parse.IsCanonical);
        Assert.Contains(NonCanonicalReason.HeaderAsInlineTable, parse.NonCanonicalReasons);
        Assert.Equal(30, parse.Model!.Header.EffectiveFrameTimeMs);
    }

    [Fact]
    public void DottedKeyInsideTableIsNonCanonical()
    {
        var parse = AtxParser.Parse("[header]\nextra.thing = 1\n[[frame]]\nfile=\"a.tga\"\n");
        Assert.False(parse.IsCanonical);
        Assert.Contains(NonCanonicalReason.DottedKeyInTable, parse.NonCanonicalReasons);
    }

    [Fact]
    public void FrameSetToAScalarIsAStructureError()
    {
        var parse = AtxParser.Parse("frame = 5\n");
        Assert.Contains(parse.Diagnostics, d => d.Code == AtxRules.BadStructure
            && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void HeaderSetToAScalarWarnsAndDefaultsApply()
    {
        var parse = AtxParser.Parse("header = 5\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.Contains(parse.Diagnostics, d => d.Code == AtxRules.BadStructure
            && d.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(AtxSchema.DefaultFrameTimeMs, parse.Model!.Header.EffectiveFrameTimeMs);
    }

    [Fact]
    public void UnknownTopLevelKeysAreRecorded()
    {
        var model = Model("title = \"hello\"\n[[frame]]\nfile=\"a.tga\"\n\n[extra]\nx = 1\n");
        Assert.Equal(["extra", "title"], model.UnknownTopLevel.Select(u => u.Name).Order());
    }

    // ── Spans ────────────────────────────────────────────────────────────────

    [Fact]
    public void SpansAreCharacterOffsetsWithCrlfAndNonAscii()
    {
        const string text = "# ─── banner ───\r\n[header]\r\nframe_time = 80 # per frame\r\n";
        var parse = AtxParser.Parse(text);
        var frameTime = parse.Model!.Header.FrameTime!;

        Assert.Equal("80", frameTime.ValueSpan.GetText(text));
        Assert.Equal("frame_time", frameTime.KeySpan.GetText(text));
        Assert.Equal("frame_time = 80 # per frame\r\n", frameTime.LineSpan.GetText(text));
        Assert.Equal("[header]", parse.Model.Header.TableHeaderSpan!.Value.GetText(text));
        Assert.Equal(LineEndingKind.CrLf, parse.SyntaxMap.LineEnding);
    }

    [Fact]
    public void FrameBlockSpansIncludeAttachedComments()
    {
        const string text = """
            [[frame]]
            file = "a.tga"

            # this comment belongs to the second frame
            [[frame]]
            file = "b.tga"

            """;
        var parse = AtxParser.Parse(text);
        var blocks = parse.SyntaxMap.FrameBlocks;
        Assert.Equal(2, blocks.Count);
        Assert.StartsWith("# this comment", blocks[1].Span.GetText(text));
        Assert.EndsWith("\n", blocks[0].Span.GetText(text));
        // The blank line between them belongs to the block above only up to the attached comment.
        Assert.DoesNotContain("# this comment", blocks[0].Span.GetText(text));
    }

    [Fact]
    public void PreambleIsNotPartOfAnyBlock()
    {
        const string text = "# banner\n# more banner\n\n[header]\nframe_time = 1\n";
        var parse = AtxParser.Parse(text);
        Assert.Equal("# banner\n# more banner\n\n", parse.SyntaxMap.Preamble.GetText(text));
    }

    [Fact]
    public void FrameIndexAtOffsetFindsTheBlock()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n";
        var parse = AtxParser.Parse(text);
        Assert.Equal(0, parse.SyntaxMap.FrameIndexAt(text.IndexOf("a.tga", StringComparison.Ordinal)));
        Assert.Equal(1, parse.SyntaxMap.FrameIndexAt(text.IndexOf("b.tga", StringComparison.Ordinal)));
    }

    // ── The annotated research example ───────────────────────────────────────

    [Fact]
    public void ResearchExampleParsesCleanly()
    {
        string text = ResearchExample.Text;
        var parse = AtxParser.Parse(text, "mtl_gbrhazstripeex01.atx");

        Assert.Empty(parse.Diagnostics);
        Assert.True(parse.IsCanonical);
        var model = parse.Model!;

        Assert.Equal(80, model.Header.EffectiveFrameTimeMs);
        Assert.True(model.Header.EffectiveInitiallyOn);
        Assert.Equal(AtxAnimationMode.Loop, model.Header.EffectiveAnimationMode);
        Assert.Equal("8888_argb", model.Header.EffectiveFormat);
        Assert.Equal("hazard_strip_mask.tga", model.Header.EffectiveAlphaMask);
        Assert.Equal("metal", model.Header.EffectiveMaterial);

        Assert.Equal(4, model.Frames.Count);
        Assert.Equal(
            ["hazard_strip_00.tga", "hazard_strip_01.tga", "hazard_strip_02.tga", "hazard_strip_03.tga"],
            model.Frames.Select(f => f.EffectiveFile));
        Assert.Equal(250, model.Frames[1].FrameTimeOverrideMs);
        Assert.Equal("glass", model.Frames[1].MaterialOverride);
        Assert.Equal(80, model.FrameTimeMs(0));
        Assert.Equal(250, model.FrameTimeMs(1));

        // Spans still line up in a file full of box-drawing characters.
        Assert.Equal("80", model.Header.FrameTime!.ValueSpan.GetText(text));
        Assert.Equal("\"glass\"", model.Frames[1].Material!.ValueSpan.GetText(text));
    }

    [Fact]
    public void ResearchExampleOnDiskMatchesTheEmbeddedCopy()
    {
        string? path = TestPaths.Research("mtl_gbrhazstripeex01.atx");
        if (path is null || !File.Exists(path)) return; // research/ is not shipped with the repo
        string onDisk = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(ResearchExample.Text.Replace("\r\n", "\n"), onDisk);
    }
}
