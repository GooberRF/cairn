using Cairn.Atx.Editing;
using Cairn.Atx.Parsing;
using Cairn.Atx.Text;
using Cairn.Workspace;

namespace Cairn.Atx.Tests;

public class WorkspaceTests
{
    // ── Settings ─────────────────────────────────────────────────────────────

    [Fact]
    public void AtxSettingsRoundTripThroughTheSharedValues()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        var settings = new AppSettings { Theme = AppTheme.Dark };
        var atx = new AtxSettings(settings)
        {
            NewFileTemplate = NewFileTemplateKind.Commented,
            PreviewBackground = PreviewBackground.Custom,
            PreviewCustomColor = "#123456",
            LastImportFolder = @"C:\imports",
        };
        Assert.Contains(AtxSettings.PreviewBackgroundKey, settings.Values.Keys);
        Assert.All(settings.Values.Keys, key => Assert.StartsWith("atx.", key));

        Assert.True(SettingsStore.Save(settings, path));
        var loaded = new AtxSettings(SettingsStore.Load(path));

        Assert.Equal(AppTheme.Dark, loaded.Settings.Theme);
        Assert.Equal(NewFileTemplateKind.Commented, loaded.NewFileTemplate);
        Assert.Equal(PreviewBackground.Custom, loaded.PreviewBackground);
        Assert.Equal("#123456", loaded.PreviewCustomColor);
        Assert.Equal(@"C:\imports", loaded.LastImportFolder);
        Assert.Equal(atx.PreviewCustomColor, loaded.PreviewCustomColor);
    }

    [Fact]
    public void AtxSettingsFallBackToDefaultsForMissingOrBadValues()
    {
        var settings = new AppSettings();
        var atx = new AtxSettings(settings);
        Assert.Equal(NewFileTemplateKind.Minimal, atx.NewFileTemplate);
        Assert.Equal(PreviewBackground.Checkerboard, atx.PreviewBackground);
        Assert.Equal(AtxSettings.DefaultPreviewCustomColor, atx.PreviewCustomColor);
        Assert.Null(atx.LastImportFolder);

        settings.Set(AtxSettings.PreviewBackgroundKey, "Plaid");
        settings.Set(AtxSettings.NewFileTemplateKey, 42);
        Assert.Equal(PreviewBackground.Checkerboard, atx.PreviewBackground);
        Assert.Equal(NewFileTemplateKind.Minimal, atx.NewFileTemplate);

        atx.LastImportFolder = "";
        Assert.DoesNotContain(AtxSettings.LastImportFolderKey, settings.Values.Keys);
    }
    [Fact]
    public void SavingWritesUtf8WithoutABom()
    {
        using var temp = new TempFolder();
        string path = temp.File("out.atx");
        AtxTextFiles.WriteAllText(path, "# ─ non-ascii ─\r\n");
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("# ─ non-ascii ─\r\n", AtxTextFiles.Read(path).Text);
    }

    [Fact]
    public void ReadingStripsABomAndRemembersItWasThere()
    {
        using var temp = new TempFolder();
        string path = temp.File("bom.atx");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("[header]\n")]);
        var file = AtxTextFiles.Read(path);
        Assert.True(file.HadByteOrderMark);
        Assert.Equal("[header]\n", file.Text);
        Assert.Equal(LineEndingKind.Lf, file.LineEnding);

        // Saving it back drops the BOM, as the design requires.
        AtxTextFiles.SaveDocument(path, file.Text, file.LineEnding);
        Assert.False(AtxTextFiles.Read(path).HadByteOrderMark);
        Assert.Equal(9, new FileInfo(path).Length);
    }

    // ── Encodings ────────────────────────────────────────────────────────────

    [Fact]
    public void ReadingDetectsEveryByteOrderMark()
    {
        using var temp = new TempFolder();
        const string content = "[header]\nfile = \"café.tga\"\n";

        Check("utf8bom.atx", new System.Text.UTF8Encoding(true), AtxFileEncoding.Utf8Bom);
        Check("utf16le.atx", new System.Text.UnicodeEncoding(false, true), AtxFileEncoding.Utf16Le);
        Check("utf16be.atx", new System.Text.UnicodeEncoding(true, true), AtxFileEncoding.Utf16Be);
        Check("utf32le.atx", new System.Text.UTF32Encoding(false, true), AtxFileEncoding.Utf32Le);
        Check("utf32be.atx", new System.Text.UTF32Encoding(true, true), AtxFileEncoding.Utf32Be);

        void Check(string name, System.Text.Encoding encoding, AtxFileEncoding expected)
        {
            string path = temp.File(name);
            File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
            var file = AtxTextFiles.Read(path);
            Assert.Equal(expected, file.Encoding);
            Assert.Equal(content, file.Text);
            Assert.Equal(expected == AtxFileEncoding.Utf8Bom, file.IsUtf8);
        }
    }

    [Fact]
    public void BomlessUtf8IsReadAsUtf8()
    {
        using var temp = new TempFolder();
        const string content = "[header]\n# café ─ ok\n";
        string path = temp.Write("plain.atx", System.Text.Encoding.UTF8.GetBytes(content));
        var file = AtxTextFiles.Read(path);
        Assert.Equal(AtxFileEncoding.Utf8, file.Encoding);
        Assert.True(file.IsUtf8);
        Assert.Equal(content, file.Text);
    }

    [Fact]
    public void BytesThatAreNotValidUtf8FallBackToTheAnsiCodePage()
    {
        using var temp = new TempFolder();
        var ansi = AtxTextFiles.AnsiEncoding();
        // 0xE9 alone is "é" in Windows-1252 and an incomplete sequence in UTF-8.
        byte[] bytes = [.. System.Text.Encoding.ASCII.GetBytes("file = \"caf"), 0xE9,
            .. System.Text.Encoding.ASCII.GetBytes(".tga\"\n")];
        string path = temp.Write("ansi.atx", bytes);

        var file = AtxTextFiles.Read(path);
        Assert.Equal(AtxFileEncoding.Ansi, file.Encoding);
        Assert.False(file.IsUtf8);
        Assert.Equal(ansi.GetString(bytes), file.Text);
        // The replacement character is what the old UTF-8-always read produced, and writing it back
        // is what destroyed the file name.
        Assert.DoesNotContain('�', file.Text);
        Assert.Contains("café.tga", file.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingAlwaysWritesUtf8WhateverTheFileWas()
    {
        using var temp = new TempFolder();
        var utf16 = new System.Text.UnicodeEncoding(false, true);
        string path = temp.Write("wide.atx",
            [.. utf16.GetPreamble(), .. utf16.GetBytes("file = \"café.tga\"\n")]);

        var file = AtxTextFiles.Read(path);
        Assert.Equal(AtxFileEncoding.Utf16Le, file.Encoding);

        AtxTextFiles.SaveDocument(path, file.Text, file.LineEnding);
        var written = File.ReadAllBytes(path);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("file = \"café.tga\"\n"), written);

        var again = AtxTextFiles.Read(path);
        Assert.Equal(AtxFileEncoding.Utf8, again.Encoding);
        Assert.Equal(file.Text, again.Text);
    }

    [Fact]
    public void EncodingNamesAreWhatTheNoticeBarSays()
    {
        Assert.Equal("UTF-16", AtxTextFiles.DescribeEncoding(AtxFileEncoding.Utf16Le));
        Assert.Equal("UTF-16", AtxTextFiles.DescribeEncoding(AtxFileEncoding.Utf16Be));
        Assert.Equal("UTF-32", AtxTextFiles.DescribeEncoding(AtxFileEncoding.Utf32Be));
        string ansi = AtxTextFiles.DescribeEncoding(AtxFileEncoding.Ansi);
        Assert.StartsWith("ANSI (", ansi, StringComparison.Ordinal);
        Assert.EndsWith(")", ansi, StringComparison.Ordinal);
        // Written the way Windows writes it, not shouted: "ANSI (Windows-1252)".
        Assert.DoesNotContain("WINDOWS", ansi, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingPreservesTheFilesLineEndings()
    {
        using var temp = new TempFolder();
        string crlf = temp.File("crlf.atx");
        AtxTextFiles.WriteAllText(crlf, "[header]\r\nframe_time = 1\r\n");
        var read = AtxTextFiles.Read(crlf);
        Assert.Equal(LineEndingKind.CrLf, read.LineEnding);

        // A round trip through an editor keeps CRLF even for generated lines.
        var batch = AtxEditor.Create(read.Text).InsertFrames(0, [new NewFrame("a.tga")]);
        AtxTextFiles.SaveDocument(crlf, batch.Apply(read.Text), read.LineEnding);
        string result = AtxTextFiles.Read(crlf).Text;
        Assert.Equal("[header]\r\nframe_time = 1\r\n\r\n[[frame]]\r\nfile = \"a.tga\"\r\n", result);
        Assert.DoesNotContain('\r', result.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void SavingIsAtomicAndReplacesTheOldContent()
    {
        using var temp = new TempFolder();
        string path = temp.File("doc.atx");
        AtxTextFiles.WriteAllText(path, "first");
        AtxTextFiles.WriteAllText(path, "second");
        Assert.Equal("second", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void LineEndingDetectionPrefersTheDominantStyle()
    {
        Assert.Equal(LineEndingKind.CrLf, LineEndings.Detect(string.Empty));
        Assert.Equal(LineEndingKind.CrLf, LineEndings.Detect("no breaks at all"));
        Assert.Equal(LineEndingKind.Lf, LineEndings.Detect("a\nb\nc\r\n"));
        Assert.Equal(LineEndingKind.CrLf, LineEndings.Detect("a\r\nb\r\nc\n"));
        Assert.Equal(LineEndingKind.Cr, LineEndings.Detect("a\rb\r"));
    }

    // ── Templates and clipboard ──────────────────────────────────────────────

    [Fact]
    public void TemplatesParseCleanly()
    {
        foreach (var kind in new[] { NewFileTemplateKind.Minimal, NewFileTemplateKind.Commented })
        {
            string text = NewFileTemplates.Create(kind);
            var parse = AtxParser.Parse(text);
            Assert.NotNull(parse.Model);
            Assert.Empty(parse.Diagnostics);
            Assert.Contains("\r\n", text, StringComparison.Ordinal);
        }
        var minimal = AtxParser.Parse(NewFileTemplates.Minimal()).Model!;
        Assert.Equal(100, minimal.Header.EffectiveFrameTimeMs);
        Assert.Equal(Cairn.Atx.Schema.AtxAnimationMode.Loop, minimal.Header.EffectiveAnimationMode);
    }

    [Fact]
    public void TemplatesHonourTheRequestedLineEnding()
    {
        string lf = NewFileTemplates.Minimal(LineEndingKind.Lf);
        Assert.DoesNotContain('\r', lf);
    }

    [Fact]
    public void FramesRoundTripThroughClipboardText()
    {
        const string document = """
            [header]
            frame_time = 100

            [[frame]]
            file = "a.tga"

            [[frame]]
            file = "b.tga"
            frame_time = 250
            material = "glass"

            """;
        var model = AtxParser.Parse(document).Model!;
        string copied = FrameClipboard.ToToml(model, [0, 1]);
        var frames = FrameClipboard.Parse(copied);

        Assert.Equal(2, frames.Count);
        Assert.Equal("a.tga", frames[0].File);
        Assert.Null(frames[0].FrameTimeMs);
        Assert.Equal(250, frames[1].FrameTimeMs);
        Assert.Equal("glass", frames[1].Material);
    }

    [Fact]
    public void PastingAPlainListOfFilenamesWorks()
    {
        var frames = FrameClipboard.Parse("hazard_00.tga\r\nC:\\art\\hazard_01.tga\r\n\r\n\"hazard_02.tga\"");
        Assert.Equal(["hazard_00.tga", "hazard_01.tga", "hazard_02.tga"], frames.Select(f => f.File));
    }

    [Fact]
    public void PastingNonsenseYieldsNothing()
    {
        Assert.Empty(FrameClipboard.Parse(null));
        Assert.Empty(FrameClipboard.Parse("   "));
        Assert.Empty(FrameClipboard.Parse("just some prose without filenames"));
        Assert.False(FrameClipboard.CanParse("# only a comment"));
    }

    // ── Game directory detection ─────────────────────────────────────────────

    [Fact]
    public void AFolderWithTablesVppLooksLikeTheGame()
    {
        using var temp = new TempFolder();
        Assert.False(GameDirectoryLocator.LooksLikeGameDirectory(temp.Path));
        temp.Write("tables.vpp", "x");
        Assert.True(GameDirectoryLocator.LooksLikeGameDirectory(temp.Path));
        Assert.False(GameDirectoryLocator.LooksLikeGameDirectory(null));
        Assert.False(GameDirectoryLocator.LooksLikeGameDirectory(@"Z:\nope"));
    }

    [Fact]
    public void DetectNeverThrows()
    {
        var exception = Record.Exception(() => GameDirectoryLocator.Detect());
        Assert.Null(exception);
    }
}
