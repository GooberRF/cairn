using Cairn.Atx.Linting;
using Cairn.Atx.Parsing;

namespace Cairn.Atx.Tests;

public class LinterTests
{
    private static IReadOnlyList<Diagnostic> Lint(string text, LintOptions? options = null) =>
        AtxLinter.Analyze(AtxParser.Parse(text), options);

    private static string[] Codes(string text, LintOptions? options = null) =>
        [.. Lint(text, options).Select(d => d.Code).Distinct()];

    private const string Clean = """
        [header]
        frame_time = 80
        animation_mode = 2
        initially_on = true
        format = "8888_argb"
        material = "metal"

        [[frame]]
        file = "a.tga"

        [[frame]]
        file = "b.tga"
        frame_time = 250
        material = "glass"

        """;

    [Fact]
    public void ACleanDocumentHasNoStructuralProblems()
    {
        Assert.Empty(Lint(Clean));
    }

    [Fact]
    public void TheResearchExampleHasNoStructuralProblems()
    {
        Assert.Empty(AtxLinter.Analyze(AtxParser.Parse(ResearchExample.Text)));
    }

    // ── One test per rule: triggered, then not triggered ─────────────────────

    [Fact]
    public void Atx001SyntaxError()
    {
        Assert.Contains(AtxRules.Syntax, Codes("[header]\nframe_time = \n"));
        Assert.DoesNotContain(AtxRules.Syntax, Codes(Clean));
    }

    [Fact]
    public void Atx002NoFrames()
    {
        var diagnostic = Assert.Single(Lint("[header]\nframe_time = 80\n"), d => d.Code == AtxRules.NoFrames);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(diagnostic.QuickFixes, f => f.Kind == QuickFixKind.AddFrames);
        Assert.DoesNotContain(AtxRules.NoFrames, Codes(Clean));
    }

    [Fact]
    public void Atx003FrameFileMissingEmptyOrWrongType()
    {
        Assert.Contains(AtxRules.FrameFileMissing, Codes("[[frame]]\nframe_time = 10\n"));
        Assert.Contains(AtxRules.FrameFileMissing, Codes("[[frame]]\nfile = \"\"\n"));
        Assert.Contains(AtxRules.FrameFileMissing, Codes("[[frame]]\nfile = 42\n"));
        Assert.DoesNotContain(AtxRules.FrameFileMissing, Codes(Clean));
    }

    [Fact]
    public void Atx004NestedAtx()
    {
        var diagnostic = Assert.Single(Lint("[[frame]]\nfile = \"other.atx\"\n"), d => d.Code == AtxRules.NestedAtx);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(0, diagnostic.FrameIndex);
        Assert.DoesNotContain(AtxRules.NestedAtx, Codes(Clean));
    }

    [Fact]
    public void Atx005UnknownFormat()
    {
        var diagnostic = Assert.Single(
            Lint("[header]\nformat = \"888_rgba\"\n[[frame]]\nfile = \"a.tga\"\n"),
            d => d.Code == AtxRules.UnknownFormat);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Did you mean \"888_rgb\"", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("565", diagnostic.Help, StringComparison.Ordinal);
        Assert.DoesNotContain(AtxRules.UnknownFormat, Codes(Clean));
    }

    [Fact]
    public void Atx006UnknownMaterialInHeaderAndFrame()
    {
        var header = Assert.Single(
            Lint("[header]\nmaterial = \"metl\"\n[[frame]]\nfile = \"a.tga\"\n"),
            d => d.Code == AtxRules.UnknownMaterial);
        Assert.Contains("Did you mean \"metal\"", header.Message, StringComparison.Ordinal);

        var frame = Assert.Single(
            Lint("[[frame]]\nfile = \"a.tga\"\nmaterial = \"glas\"\n"),
            d => d.Code == AtxRules.UnknownMaterial);
        Assert.Equal(0, frame.FrameIndex);
        Assert.DoesNotContain(AtxRules.UnknownMaterial, Codes(Clean));
    }

    [Fact]
    public void Atx007BadStructure()
    {
        var frames = Assert.Single(Lint("frame = 5\n"), d => d.Code == AtxRules.BadStructure);
        Assert.Equal(DiagnosticSeverity.Error, frames.Severity);
        Assert.Contains(frames.QuickFixes, f => f.Kind == QuickFixKind.ConvertToStandardLayout);

        var header = Assert.Single(
            Lint("header = 5\n[[frame]]\nfile = \"a.tga\"\n"), d => d.Code == AtxRules.BadStructure);
        Assert.Equal(DiagnosticSeverity.Warning, header.Severity);
        Assert.DoesNotContain(AtxRules.BadStructure, Codes(Clean));
    }

    [Fact]
    public void Atx020WrongType()
    {
        var diagnostic = Assert.Single(
            Lint("[header]\nframe_time = \"80\"\n[[frame]]\nfile = \"a.tga\"\n"),
            d => d.Code == AtxRules.WrongType);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("ignores the line completely", diagnostic.Message, StringComparison.Ordinal);

        // A whole-number float is accepted by the game but still worth flagging — and the message
        // must not claim it is ignored, because it is not.
        var lossless = Assert.Single(
            Lint("[header]\nframe_time = 80.0\n[[frame]]\nfile = \"a.tga\"\n"),
            d => d.Code == AtxRules.WrongType);
        Assert.Contains("still reads it, as 80 ms", lossless.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ignores", lossless.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(AtxRules.WrongType, Codes(Clean));
    }

    [Fact]
    public void Atx020TellsTheTruthAboutIntegerAndBooleanConversions()
    {
        // toml++ reads an integer as a bool and a bool as an integer, so saying "ignored" here
        // would tell the designer the opposite of what the game does.
        var flag = Assert.Single(
            Lint("[header]\ninitially_on = 0\nanimation_mode = 2\n[[frame]]\nfile = \"a.tga\"\n"),
            d => d.Code == AtxRules.WrongType);
        Assert.Contains("still reads it, as false", flag.Message, StringComparison.Ordinal);
        Assert.Contains(flag.QuickFixes, f => f.Title.Contains("false", StringComparison.Ordinal));

        var mode = Assert.Single(
            Lint("[header]\nanimation_mode = true\n[[frame]]\nfile = \"a.tga\"\n"),
            d => d.Code == AtxRules.WrongType);
        Assert.Contains("still reads it, as 1 (Ping-Pong)", mode.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Atx021FrameTimeBelowOne()
    {
        Assert.Contains(AtxRules.FrameTimeTooSmall,
            Codes("[header]\nframe_time = 0\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.Contains(AtxRules.FrameTimeTooSmall,
            Codes("[[frame]]\nfile = \"a.tga\"\nframe_time = -2\n"));
        Assert.DoesNotContain(AtxRules.FrameTimeTooSmall, Codes(Clean));
    }

    [Fact]
    public void Atx022AnimationModeOutOfRange()
    {
        Assert.Contains(AtxRules.AnimationModeOutOfRange,
            Codes("[header]\nanimation_mode = 7\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.DoesNotContain(AtxRules.AnimationModeOutOfRange, Codes(Clean));
    }

    [Fact]
    public void Atx023UnknownKey()
    {
        var header = Assert.Single(
            Lint("[header]\nframe_rate = 12\n[[frame]]\nfile = \"a.tga\"\n"),
            d => d.Code == AtxRules.UnknownKey);
        Assert.Contains("Did you mean 'frame_time'", header.Message, StringComparison.Ordinal);

        var frame = Assert.Single(
            Lint("[[frame]]\nfile = \"a.tga\"\nfilename = \"b.tga\"\n"),
            d => d.Code == AtxRules.UnknownKey);
        Assert.Equal(0, frame.FrameIndex);
        Assert.DoesNotContain(AtxRules.UnknownKey, Codes(Clean));
    }

    [Fact]
    public void Atx024UnknownTopLevel()
    {
        Assert.Contains(AtxRules.UnknownTopLevel, Codes("title = \"x\"\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.Contains(AtxRules.UnknownTopLevel, Codes("[[frame]]\nfile = \"a.tga\"\n\n[extra]\nx = 1\n"));
        Assert.DoesNotContain(AtxRules.UnknownTopLevel, Codes(Clean));
    }

    [Fact]
    public void Atx026PathSeparator()
    {
        var diagnostic = Assert.Single(
            Lint("[[frame]]\nfile = \"textures/a.tga\"\n"), d => d.Code == AtxRules.PathSeparator);
        Assert.Contains("a.tga", diagnostic.Help, StringComparison.Ordinal);
        Assert.Contains(AtxRules.PathSeparator, Codes("[[frame]]\nfile = \"sub\\\\a.tga\"\n"));
        Assert.DoesNotContain(AtxRules.PathSeparator, Codes(Clean));
    }

    [Fact]
    public void Atx027NameTooLong()
    {
        Assert.Contains(AtxRules.NameTooLong,
            Codes("[[frame]]\nfile = \"a_very_long_frame_file_name_here.tga\"\n"));
        Assert.Contains(AtxRules.NameTooLong,
            Codes("[header]\nalpha_mask = \"a_very_long_mask_file_name_here.tga\"\n[[frame]]\nfile = \"a.tga\"\n"));

        var options = new LintOptions { DocumentPath = @"C:\work\an_extremely_long_atx_document_name.atx" };
        Assert.Contains(AtxRules.NameTooLong, Codes(Clean, options));
        Assert.DoesNotContain(AtxRules.NameTooLong,
            Codes(Clean, new LintOptions { DocumentPath = @"C:\work\short.atx" }));
        Assert.DoesNotContain(AtxRules.NameTooLong, Codes(Clean));
    }

    [Fact]
    public void Atx028UnsupportedExtension()
    {
        Assert.Contains(AtxRules.UnsupportedExtension, Codes("[[frame]]\nfile = \"a.bmp\"\n"));
        Assert.Contains(AtxRules.UnsupportedExtension, Codes("[[frame]]\nfile = \"a\"\n"));
        Assert.DoesNotContain(AtxRules.UnsupportedExtension, Codes(Clean));
    }

    [Fact]
    public void Atx030InitiallyOnWithStatic()
    {
        var diagnostic = Assert.Single(
            Lint("[header]\nanimation_mode = 0\ninitially_on = false\n[[frame]]\nfile = \"a.tga\"\n"),
            d => d.Code == AtxRules.InitiallyOnWithStatic);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.DoesNotContain(AtxRules.InitiallyOnWithStatic, Codes(Clean));
        // Not reported when the key is simply absent.
        Assert.DoesNotContain(AtxRules.InitiallyOnWithStatic,
            Codes("[header]\nanimation_mode = 0\n[[frame]]\nfile = \"a.tga\"\n"));
    }

    [Fact]
    public void Atx031SingleFrameAnimated()
    {
        Assert.Contains(AtxRules.SingleFrameAnimated,
            Codes("[header]\nanimation_mode = 2\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.DoesNotContain(AtxRules.SingleFrameAnimated,
            Codes("[header]\nanimation_mode = 0\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.DoesNotContain(AtxRules.SingleFrameAnimated, Codes(Clean));
    }

    [Fact]
    public void Atx033RedundantFrameTime()
    {
        Assert.Contains(AtxRules.RedundantFrameTime,
            Codes("[header]\nframe_time = 80\n[[frame]]\nfile = \"a.tga\"\nframe_time = 80\n"));
        Assert.DoesNotContain(AtxRules.RedundantFrameTime, Codes(Clean));
    }

    [Fact]
    public void Atx034RedundantMaterial()
    {
        Assert.Contains(AtxRules.RedundantMaterial,
            Codes("[header]\nmaterial = \"metal\"\n[[frame]]\nfile = \"a.tga\"\nmaterial = \"METAL\"\n"));
        Assert.DoesNotContain(AtxRules.RedundantMaterial, Codes(Clean));
    }

    [Fact]
    public void Atx035MaskPromotesFormat()
    {
        Assert.Contains(AtxRules.MaskPromotesFormat,
            Codes("[header]\nformat = \"565\"\nalpha_mask = \"m.tga\"\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.Contains(AtxRules.MaskPromotesFormat,
            Codes("[header]\nformat = \"888\"\nalpha_mask = \"m.tga\"\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.DoesNotContain(AtxRules.MaskPromotesFormat,
            Codes("[header]\nformat = \"8888\"\nalpha_mask = \"m.tga\"\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.DoesNotContain(AtxRules.MaskPromotesFormat, Codes(Clean));
    }

    [Fact]
    public void Atx036MaskWith1555()
    {
        Assert.Contains(AtxRules.MaskWith1555,
            Codes("[header]\nformat = \"1555\"\nalpha_mask = \"m.tga\"\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.DoesNotContain(AtxRules.MaskWith1555,
            Codes("[header]\nformat = \"1555\"\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.DoesNotContain(AtxRules.MaskWith1555, Codes(Clean));
    }

    [Fact]
    public void Atx037EmptyString()
    {
        Assert.Contains(AtxRules.EmptyString,
            Codes("[header]\nmaterial = \"\"\n[[frame]]\nfile = \"a.tga\"\n"));
        Assert.Contains(AtxRules.EmptyString,
            Codes("[[frame]]\nfile = \"a.tga\"\nmaterial = \"\"\n"));
        Assert.DoesNotContain(AtxRules.EmptyString, Codes(Clean));
    }

    // ── Quick fixes ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AtxRules.FrameTimeTooSmall, "[header]\nframe_time = 0\n[[frame]]\nfile = \"a.tga\"\n")]
    [InlineData(AtxRules.FrameTimeTooSmall, "[[frame]]\nfile = \"a.tga\"\nframe_time = 0\n")]
    [InlineData(AtxRules.UnknownFormat, "[header]\nformat = \"888_rgba\"\n[[frame]]\nfile = \"a.tga\"\n")]
    [InlineData(AtxRules.UnknownMaterial, "[header]\nmaterial = \"metl\"\n[[frame]]\nfile = \"a.tga\"\n")]
    [InlineData(AtxRules.UnknownMaterial, "[[frame]]\nfile = \"a.tga\"\nmaterial = \"glas\"\n")]
    [InlineData(AtxRules.UnknownKey, "[header]\nframe_rate = 12\n[[frame]]\nfile = \"a.tga\"\n")]
    [InlineData(AtxRules.PathSeparator, "[[frame]]\nfile = \"textures/a.tga\"\n")]
    [InlineData(AtxRules.InitiallyOnWithStatic, "[header]\ninitially_on = false\n[[frame]]\nfile = \"a.tga\"\n")]
    [InlineData(AtxRules.RedundantFrameTime,
        "[header]\nframe_time = 80\n[[frame]]\nfile = \"a.tga\"\nframe_time = 80\n[[frame]]\nfile = \"b.tga\"\n")]
    [InlineData(AtxRules.RedundantMaterial,
        "[header]\nmaterial = \"metal\"\n[[frame]]\nfile = \"a.tga\"\nmaterial = \"metal\"\n")]
    [InlineData(AtxRules.EmptyString, "[header]\nmaterial = \"\"\n[[frame]]\nfile = \"a.tga\"\n")]
    [InlineData(AtxRules.EmptyString, "[[frame]]\nfile = \"a.tga\"\nmaterial = \"\"\n")]
    [InlineData(AtxRules.WrongType, "[header]\nframe_time = \"80\"\n[[frame]]\nfile = \"a.tga\"\n")]
    public void EveryQuickFixResolvesItsDiagnostic(string code, string text)
    {
        var diagnostic = Assert.Single(Lint(text), d => d.Code == code);
        var fix = Assert.Single(diagnostic.QuickFixes, f => f.Kind == QuickFixKind.Edit);

        string result = fix.Apply().Apply(text);
        Assert.NotEqual(text, result);
        Assert.DoesNotContain(code, Codes(result));
        Assert.DoesNotContain(AtxRules.Syntax, Codes(result));
    }

    [Fact]
    public void DiagnosticsAreOrderedByPosition()
    {
        const string text = """
            [header]
            frame_time = 0
            material = "metl"

            [[frame]]
            file = "a.atx"
            """;
        var diagnostics = Lint(text);
        Assert.True(diagnostics.Count >= 3);
        for (int i = 1; i < diagnostics.Count; i++)
        {
            Assert.True(diagnostics[i - 1].Span.Start <= diagnostics[i].Span.Start);
        }
    }

    [Fact]
    public void DiagnosticsCarryTheFrameAndKeyTheyConcern()
    {
        const string text = "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\nmaterial = \"nope\"\n";
        var diagnostic = Assert.Single(Lint(text), d => d.Code == AtxRules.UnknownMaterial);
        Assert.Equal(1, diagnostic.FrameIndex);
        Assert.Equal("material", diagnostic.Key);
        Assert.Contains("Frame 1", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASyntaxErrorSuppressesEverythingElse()
    {
        var diagnostics = Lint("[header\nframe_time = 80\n");
        Assert.All(diagnostics, d => Assert.Equal(AtxRules.Syntax, d.Code));
    }
}
