using Cairn.Atx.Editing;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Tests;

public class EditorTests
{
    private static string Apply(string text, Func<AtxEditor, Cairn.Atx.Text.TextEditBatch> operation) =>
        operation(AtxEditor.Create(text)).Apply(text);

    // ── Header keys ──────────────────────────────────────────────────────────

    [Fact]
    public void SettingAnExistingHeaderValueKeepsTheTrailingComment()
    {
        const string text = "[header]\nframe_time = 80 # twelve and a half fps\n\n[[frame]]\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.SetHeaderValue(AtxSchema.KeyFrameTime, AtxValue.Integer(120)));
        Assert.Equal("[header]\nframe_time = 120 # twelve and a half fps\n\n[[frame]]\nfile = \"a.tga\"\n", result);
    }

    [Fact]
    public void NewHeaderKeysGoInSchemaOrder()
    {
        const string text = "[header]\nframe_time = 80\nmaterial = \"metal\"\n\n[[frame]]\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.SetHeaderValue(AtxSchema.KeyAnimationMode, AtxValue.Integer(2)));
        Assert.Equal(
            "[header]\nframe_time = 80\nanimation_mode = 2\nmaterial = \"metal\"\n\n[[frame]]\nfile = \"a.tga\"\n",
            result);
    }

    [Fact]
    public void NewHeaderKeyGoesAboveTheCommentsOfTheKeyItPrecedes()
    {
        const string text =
            "[header]\n"
            + "frame_time = 80\n"
            + "\n"
            + "# which surface this sounds like\n"
            + "material = \"metal\"\n";
        string result = Apply(text, e => e.SetHeaderValue(AtxSchema.KeyFormat, AtxValue.String("8888")));
        Assert.Equal(
            "[header]\n"
            + "frame_time = 80\n"
            + "\n"
            + "format = \"8888\"\n"
            + "# which surface this sounds like\n"
            + "material = \"metal\"\n",
            result);
    }

    [Fact]
    public void HeaderIsCreatedAfterTheFileBanner()
    {
        const string text = "# my animated texture\n\n[[frame]]\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.SetHeaderValue(AtxSchema.KeyFrameTime, AtxValue.Integer(80)));
        Assert.Equal("# my animated texture\n\n[header]\nframe_time = 80\n\n[[frame]]\nfile = \"a.tga\"\n", result);
    }

    [Fact]
    public void HeaderIsCreatedWhenThereIsNoneAtAll()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.SetHeaderValue(AtxSchema.KeyAnimationMode, AtxValue.Integer(2)));
        Assert.Equal("[header]\nanimation_mode = 2\n\n[[frame]]\nfile = \"a.tga\"\n", result);
    }

    [Fact]
    public void HeaderIsCreatedInAnEmptyDocumentUsingCrlf()
    {
        string result = Apply(string.Empty, e => e.SetHeaderValue(AtxSchema.KeyFrameTime, AtxValue.Integer(80)));
        Assert.Equal("[header]\r\nframe_time = 80\r\n", result);
    }

    [Fact]
    public void SettingAnAbsentHeaderKeyToItsDefaultDoesNothing()
    {
        const string text = "[header]\nmaterial = \"metal\"\n\n[[frame]]\nfile = \"a.tga\"\n";
        var editor = AtxEditor.Create(text);
        Assert.True(editor.SetHeaderValue(AtxSchema.KeyFrameTime, AtxValue.Integer(100)).IsEmpty);
        Assert.True(editor.SetHeaderValue(AtxSchema.KeyInitiallyOn, AtxValue.Boolean(true)).IsEmpty);
        Assert.True(editor.SetHeaderValue(AtxSchema.KeyAnimationMode, AtxValue.Integer(0)).IsEmpty);
        Assert.False(editor.SetHeaderValue(AtxSchema.KeyAnimationMode, AtxValue.Integer(2)).IsEmpty);
    }

    [Fact]
    public void RemovingAHeaderKeyRemovesOnlyItsLine()
    {
        const string text = "[header]\nframe_time = 80\ninitially_on = false\n\n[[frame]]\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.RemoveHeaderKey(AtxSchema.KeyInitiallyOn));
        Assert.Equal("[header]\nframe_time = 80\n\n[[frame]]\nfile = \"a.tga\"\n", result);
    }

    [Fact]
    public void HeaderAfterFramesIsStillEditable()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n\n[header]\nframe_time = 80\n";
        string result = Apply(text, e => e.SetHeaderValue(AtxSchema.KeyFrameTime, AtxValue.Integer(40)));
        Assert.Equal("[[frame]]\nfile = \"a.tga\"\n\n[header]\nframe_time = 40\n", result);
    }

    // ── Frame keys ───────────────────────────────────────────────────────────

    [Fact]
    public void NewFrameKeysGoAfterFileInSchemaOrder()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\nmaterial = \"glass\"\n";
        string result = Apply(text, e => e.SetFrameValue([0], AtxSchema.KeyFrameTime, AtxValue.Integer(50)));
        Assert.Equal("[[frame]]\nfile = \"a.tga\"\nframe_time = 50\nmaterial = \"glass\"\n", result);
    }

    [Fact]
    public void AKeyCanBeAddedToAFileWithNoTrailingNewline()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"";
        string result = Apply(text, e => e.SetFrameValue([0], AtxSchema.KeyFrameTime, AtxValue.Integer(50)));
        Assert.Equal("[[frame]]\nfile = \"a.tga\"\nframe_time = 50\n", result);
    }

    [Fact]
    public void SettingSeveralFramesAtOnceIsOneBatch()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\nframe_time = 10\n";
        var editor = AtxEditor.Create(text);
        var batch = editor.SetFrameValue([0, 1], AtxSchema.KeyFrameTime, AtxValue.Integer(50));
        Assert.Equal(2, batch.Count);
        Assert.Equal(
            "[[frame]]\nfile = \"a.tga\"\nframe_time = 50\n\n[[frame]]\nfile = \"b.tga\"\nframe_time = 50\n",
            batch.Apply(text));
    }

    [Fact]
    public void RemovingAFrameKeyLeavesTheRestOfTheBlock()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\nframe_time = 50 # hold\nmaterial = \"glass\"\n";
        string result = Apply(text, e => e.RemoveFrameKey([0], AtxSchema.KeyFrameTime));
        Assert.Equal("[[frame]]\nfile = \"a.tga\"\nmaterial = \"glass\"\n", result);
    }

    // ── Frame blocks ─────────────────────────────────────────────────────────

    [Fact]
    public void InsertingTheFirstFrameIntoAHeaderOnlyFile()
    {
        const string text = "[header]\nframe_time = 80\n";
        string result = Apply(text, e => e.InsertFrames(0, [new NewFrame("a.tga")]));
        Assert.Equal("[header]\nframe_time = 80\n\n[[frame]]\nfile = \"a.tga\"\n", result);
    }

    [Fact]
    public void InsertingTheFirstFrameIntoAnEmptyFile()
    {
        string result = Apply(string.Empty, e => e.InsertFrames(0, [new NewFrame("a.tga")]));
        Assert.Equal("[[frame]]\r\nfile = \"a.tga\"\r\n", result);
    }

    [Fact]
    public void InsertingBeforeAnExistingFrame()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.InsertFrames(0, [new NewFrame("b.tga", 40, "glass")]));
        Assert.Equal(
            "[[frame]]\nfile = \"b.tga\"\nframe_time = 40\nmaterial = \"glass\"\n\n[[frame]]\nfile = \"a.tga\"\n",
            result);
    }

    [Fact]
    public void AppendingSeveralFramesSeparatesThemWithBlankLines()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.InsertFrames(1, [new NewFrame("b.tga"), new NewFrame("c.tga")]));
        Assert.Equal(
            "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n\n[[frame]]\nfile = \"c.tga\"\n",
            result);
    }

    [Fact]
    public void AppendingToAFileWithNoTrailingNewline()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"";
        string result = Apply(text, e => e.InsertFrames(1, [new NewFrame("b.tga")]));
        Assert.Equal("[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n", result);
    }

    [Fact]
    public void RemovingAFrameTakesItsAttachedComments()
    {
        const string text =
            "[[frame]]\nfile = \"a.tga\"\n\n# the flash frame\n[[frame]]\nfile = \"b.tga\"\n\n[[frame]]\nfile = \"c.tga\"\n";
        string result = Apply(text, e => e.RemoveFrames([1]));
        Assert.Equal("[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"c.tga\"\n", result);
    }

    [Fact]
    public void RemovingEveryFrameLeavesTheHeader()
    {
        const string text = "[header]\nframe_time = 80\n\n[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n";
        string result = Apply(text, e => e.RemoveFrames([0, 1]));
        Assert.Equal("[header]\nframe_time = 80\n\n", result);
        Assert.Empty(AtxParser.Parse(result).Model!.Frames);
    }

    [Fact]
    public void MovingAFrameCarriesItsComments()
    {
        const string text =
            "[[frame]]\nfile = \"a.tga\"\n\n# keep me with b\n[[frame]]\nfile = \"b.tga\"\n\n[[frame]]\nfile = \"c.tga\"\n";
        string result = Apply(text, e => e.MoveFrames([1], 0));
        Assert.Equal(
            "# keep me with b\n[[frame]]\nfile = \"b.tga\"\n\n[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"c.tga\"\n",
            result);
    }

    [Fact]
    public void MovingAFrameToTheEnd()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n";
        string result = Apply(text, e => e.MoveFrames([0], 2));
        Assert.Equal(["b.tga", "a.tga"],
            AtxParser.Parse(result).Model!.Frames.Select(f => f.EffectiveFile));
    }

    [Fact]
    public void MovingAFrameWhereItAlreadyIsDoesNothing()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n";
        Assert.True(AtxEditor.Create(text).MoveFrames([0], 0).IsEmpty);
        Assert.True(AtxEditor.Create(text).MoveFrames([1], 2).IsEmpty);
    }

    [Fact]
    public void DuplicatingAFrameCopiesItsWholeBlock()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\nframe_time = 40\n\n[[frame]]\nfile = \"b.tga\"\n";
        string result = Apply(text, e => e.DuplicateFrames([0]));
        Assert.Equal(
            "[[frame]]\nfile = \"a.tga\"\nframe_time = 40\n\n[[frame]]\nfile = \"a.tga\"\nframe_time = 40\n\n"
            + "[[frame]]\nfile = \"b.tga\"\n",
            result);
    }

    [Fact]
    public void ReversingFramesSwapsWholeBlocks()
    {
        const string text =
            "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n\n[[frame]]\nfile = \"c.tga\"\n";
        string result = Apply(text, e => e.ReverseFrames([0, 1, 2]));
        Assert.Equal(["c.tga", "b.tga", "a.tga"],
            AtxParser.Parse(result).Model!.Frames.Select(f => f.EffectiveFile));
    }

    [Fact]
    public void SortingFramesUsesNaturalOrder()
    {
        const string text =
            "[[frame]]\nfile = \"f_10.tga\"\n\n[[frame]]\nfile = \"f_2.tga\"\n\n[[frame]]\nfile = \"f_1.tga\"\n";
        string result = Apply(text, e => e.SortFrames([0, 1, 2]));
        Assert.Equal(["f_1.tga", "f_2.tga", "f_10.tga"],
            AtxParser.Parse(result).Model!.Frames.Select(f => f.EffectiveFile));
    }

    [Fact]
    public void SortingOnlyTheSelectedFramesLeavesTheOthersInPlace()
    {
        const string text =
            "[[frame]]\nfile = \"z.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n\n[[frame]]\nfile = \"a.tga\"\n";
        string result = Apply(text, e => e.SortFrames([1, 2]));
        Assert.Equal(["z.tga", "a.tga", "b.tga"],
            AtxParser.Parse(result).Model!.Frames.Select(f => f.EffectiveFile));
    }

    // ── Line endings ─────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedTextMatchesTheFilesLineEnding()
    {
        const string text = "[header]\r\nframe_time = 80\r\n";
        string result = Apply(text, e => e.InsertFrames(0, [new NewFrame("a.tga")]));
        Assert.Equal("[header]\r\nframe_time = 80\r\n\r\n[[frame]]\r\nfile = \"a.tga\"\r\n", result);
        Assert.DoesNotContain("\n\n", result.Replace("\r\n", ""), StringComparison.Ordinal);
    }

    // ── Normalize ────────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeRewritesAnInlineTableArrayAsBlocks()
    {
        const string text = "# a hand-written file\n\nheader.frame_time = 40\nframe = [{file=\"a.tga\"},{file=\"b.tga\",frame_time=90}]\n";
        string result = Apply(text, e => e.Normalize());
        var parse = AtxParser.Parse(result);
        Assert.True(parse.IsCanonical);
        Assert.Equal(40, parse.Model!.Header.EffectiveFrameTimeMs);
        Assert.Equal(["a.tga", "b.tga"], parse.Model.Frames.Select(f => f.EffectiveFile));
        Assert.Equal(90, parse.Model.Frames[1].FrameTimeOverrideMs);
        Assert.StartsWith("# a hand-written file", result);
    }

    [Fact]
    public void NormalizeKeepsCommentsAttachedToKeysAndFrames()
    {
        const string text =
            "[header]\n"
            + "# how fast\n"
            + "frame_time = 80\n"
            + "\n"
            + "# the flash\n"
            + "[[frame]]\n"
            + "file = \"a.tga\"\n";
        string result = Apply(text, e => e.Normalize());
        Assert.Contains("# how fast\nframe_time = 80", result, StringComparison.Ordinal);
        Assert.Contains("# the flash\n[[frame]]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeKeepsCommentsAttachedToTheHeaderBlock()
    {
        // No blank line, so the comment belongs to the header block rather than the preamble.
        const string text =
            "# what this texture is for\n"
            + "header.frame_time = 90\n"
            + "frame = [{file=\"a.tga\"}]\n";
        string result = Apply(text, e => e.Normalize());
        Assert.Contains("# what this texture is for\n[header]", result, StringComparison.Ordinal);
        Assert.True(AtxParser.Parse(result).IsCanonical);
    }

    [Fact]
    public void NormalizeOnAnAlreadyStandardFileChangesNothing()
    {
        const string text = "[header]\nframe_time = 80\n\n[[frame]]\nfile = \"a.tga\"\n";
        Assert.True(AtxEditor.Create(text).Normalize().IsEmpty);
    }

    // ── Property-style round trip ────────────────────────────────────────────

    public static TheoryData<string> Documents() =>
    [
        "[header]\nframe_time = 80\n\n[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n",
        "[[frame]]\nfile = \"a.tga\"",
        "# banner\n\n[[frame]]\nfile = \"a.tga\"\n\n# second\n[[frame]]\nfile = \"b.tga\"\n",
        "[[frame]]\r\nfile = \"a.tga\"\r\n\r\n[[frame]]\r\nfile = \"b.tga\"\r\n\r\n[header]\r\nframe_time = 20\r\n",
        ResearchExample.Text,
    ];

    [Theory]
    [MemberData(nameof(Documents))]
    public void EveryOperationSurvivesAReparse(string text)
    {
        var before = AtxParser.Parse(text).Model!;
        int count = before.Frames.Count;
        var all = Enumerable.Range(0, count).ToList();

        Check(e => e.SetHeaderValue(AtxSchema.KeyFrameTime, AtxValue.Integer(33)),
            m => Assert.Equal(33, m.Header.EffectiveFrameTimeMs));
        Check(e => e.SetHeaderValue(AtxSchema.KeyMaterial, AtxValue.String("ice")),
            m => Assert.Equal("ice", m.Header.EffectiveMaterial));
        Check(e => e.SetHeaderValue(AtxSchema.KeyAnimationMode, AtxValue.Integer(1)),
            m => Assert.Equal(AtxAnimationMode.PingPong, m.Header.EffectiveAnimationMode));
        Check(e => e.RemoveHeaderKey(AtxSchema.KeyFrameTime),
            m => Assert.Null(m.Header.FrameTime));
        Check(e => e.SetFrameValue(all, AtxSchema.KeyFrameTime, AtxValue.Integer(17)),
            m => Assert.All(m.Frames, f => Assert.Equal(17, f.FrameTimeOverrideMs)));
        Check(e => e.RemoveFrameKey(all, AtxSchema.KeyFrameTime),
            m => Assert.All(m.Frames, f => Assert.Null(f.FrameTimeOverrideMs)));
        Check(e => e.InsertFrames(count, [new NewFrame("added.tga")]),
            m =>
            {
                Assert.Equal(count + 1, m.Frames.Count);
                Assert.Equal("added.tga", m.Frames[^1].EffectiveFile);
            });
        Check(e => e.InsertFrames(0, [new NewFrame("first.tga")]),
            m => Assert.Equal("first.tga", m.Frames[0].EffectiveFile));
        Check(e => e.DuplicateFrames([0]), m => Assert.Equal(count + 1, m.Frames.Count));
        Check(e => e.RemoveFrames([0]), m => Assert.Equal(count - 1, m.Frames.Count));
        Check(e => e.ReverseFrames(all),
            m => Assert.Equal(
                before.Frames.Select(f => f.EffectiveFile).Reverse(),
                m.Frames.Select(f => f.EffectiveFile)));
        Check(e => e.SortFrames(all), m => Assert.Equal(count, m.Frames.Count));
        Check(e => e.MoveFrames([0], count), m => Assert.Equal(count, m.Frames.Count));
        Check(e => e.Normalize(), m =>
        {
            Assert.Equal(before.Frames.Select(f => f.EffectiveFile), m.Frames.Select(f => f.EffectiveFile));
            Assert.Equal(before.Header.EffectiveFrameTimeMs, m.Header.EffectiveFrameTimeMs);
            Assert.Equal(before.Header.EffectiveMaterial, m.Header.EffectiveMaterial);
        });

        void Check(Func<AtxEditor, Cairn.Atx.Text.TextEditBatch> operation, Action<Cairn.Atx.Model.AtxModel> verify)
        {
            string result = operation(AtxEditor.Create(text)).Apply(text);
            var parse = AtxParser.Parse(result);
            Assert.Null(parse.Diagnostics.FirstOrDefault(d => d.Code == Cairn.Atx.Linting.AtxRules.Syntax));
            Assert.NotNull(parse.Model);
            verify(parse.Model!);
        }
    }

    [Fact]
    public void MovingToTheEndOfAFileWithNoTrailingNewlineStaysValid()
    {
        // Alt+Down on the second-to-last frame appends at text.Length. Without a leading break the
        // moved [[frame]] runs onto the previous line and the whole document stops parsing.
        const string text = "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"";
        string moved = AtxEditor.Create(text).MoveFrames([0], 2).Apply(text);

        var parse = AtxParser.Parse(moved);
        Assert.NotNull(parse.Model);
        Assert.Equal(["b.tga", "a.tga"], parse.Model!.Frames.Select(f => f.EffectiveFile));
    }

    [Fact]
    public void RemovingAKeyNeverDeletesALineItShares()
    {
        // The key's "line" in a header written as an inline table is the whole header, so deleting
        // it would silently take every other setting with it. Refuse instead — the file has a
        // "Convert to standard layout" offer for exactly this.
        const string inline = "header = { frame_time = 250, material = \"metal\" }\n\n"
            + "[[frame]]\nfile = \"a.tga\"\n";
        Assert.True(AtxEditor.Create(inline).RemoveHeaderKey("material").IsEmpty);

        // Setting an existing key is still fine: only the value token is replaced.
        string set = AtxEditor.Create(inline)
            .SetHeaderValue("material", AtxValue.String("glass")).Apply(inline);
        var parse = AtxParser.Parse(set);
        Assert.Equal("glass", parse.Model!.Header.EffectiveMaterial);
        Assert.Equal(250, parse.Model.Header.EffectiveFrameTimeMs);

        // A dotted-key header does give each key its own line, so removal works there.
        const string dotted = "header.frame_time = 250\nheader.material = \"metal\"\n\n"
            + "[[frame]]\nfile = \"a.tga\"\n";
        string removed = AtxEditor.Create(dotted).RemoveHeaderKey("material").Apply(dotted);
        var after = AtxParser.Parse(removed);
        Assert.Null(after.Model!.Header.EffectiveMaterial);
        Assert.Equal(250, after.Model.Header.EffectiveFrameTimeMs);

        // And a plain [header] is untouched by the new rule.
        const string plain = "[header]\nframe_time = 250\nmaterial = \"metal\"\n\n"
            + "[[frame]]\nfile = \"a.tga\"\n";
        string plainRemoved = AtxEditor.Create(plain).RemoveHeaderKey("material").Apply(plain);
        Assert.Equal("[header]\nframe_time = 250\n\n[[frame]]\nfile = \"a.tga\"\n", plainRemoved);
    }
}
