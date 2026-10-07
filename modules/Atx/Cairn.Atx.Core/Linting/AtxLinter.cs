using Cairn.Atx.Editing;
using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Atx.Text;

namespace Cairn.Atx.Linting;

/// <summary>Extra context the linter needs that the document text does not carry.</summary>
public sealed class LintOptions
{
    /// <summary>Full path of the .atx file, when it has been saved. Used by the name-length rule.</summary>
    public string? DocumentPath { get; init; }
}

/// <summary>
/// The structural half of the linter: every rule that can be decided from the text alone. Rules
/// that need image files live in <see cref="AtxAssetLinter"/> and run asynchronously.
/// </summary>
public static class AtxLinter
{
    /// <summary>
    /// Runs every structural rule, including the TOML errors the parser already found.
    /// Results are ordered by position in the file.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Analyze(AtxParseResult parse, LintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(parse);
        options ??= new LintOptions();
        var results = new List<Diagnostic>(parse.Diagnostics);

        var model = parse.Model;
        if (model is null) return Ordered(results);

        var editor = new AtxEditor(parse.Text, parse);
        CheckDocument(results, parse, model, editor, options);
        CheckHeader(results, model, editor, parse.SyntaxMap.HeaderKeys);
        CheckFrames(results, model, editor, parse.SyntaxMap.FrameKeys);
        return Ordered(results);
    }

    private static IReadOnlyList<Diagnostic> Ordered(List<Diagnostic> results) =>
        [.. results.OrderBy(d => d.Span.Start).ThenBy(d => d.Code, StringComparer.Ordinal)];

    // ── Document-level ────────────────────────────────────────────────────────

    private static void CheckDocument(
        List<Diagnostic> results, AtxParseResult parse, AtxModel model, AtxEditor editor, LintOptions options)
    {
        if (model.Frames.Count == 0)
        {
            results.Add(new Diagnostic(
                AtxRules.NoFrames, DiagnosticSeverity.Error,
                "This texture has no frames, so the game will not load it.",
                "Add at least one [[frame]] section with a file = \"name.tga\" line. Use "
                + "Frames > Add Frames… to pick images.",
                parse.SyntaxMap.Blocks.Count > 0
                    ? parse.SyntaxMap.Blocks[^1].DeclarationSpan
                    : new TextSpan(0, Math.Min(1, parse.Text.Length)),
                null, null,
                [new QuickFix("Add frames…", QuickFixKind.AddFrames)]));
        }
        else if (model.Frames.Count == 1 && model.Header.EffectiveAnimationMode != AtxAnimationMode.Static)
        {
            var mode = model.Header.EffectiveAnimationMode;
            results.Add(new Diagnostic(
                AtxRules.SingleFrameAnimated, DiagnosticSeverity.Info,
                $"The animation mode is {AtxSchema.AnimationModeLabel(mode)} but there is only one frame, "
                + "so nothing will appear to move.",
                "Add more frames, or set the animation mode to Static if this texture is meant to "
                + "change only through level events.",
                model.Header.AnimationMode?.ValueSpan ?? model.Frames[0].HeaderSpan,
                null, AtxSchema.KeyAnimationMode));
        }

        foreach (var unknown in model.UnknownTopLevel)
        {
            results.Add(new Diagnostic(
                AtxRules.UnknownTopLevel, DiagnosticSeverity.Warning,
                $"'{UserText.Printable(unknown.Name)}' is not part of the ATX format, so the game ignores it.",
                "An .atx file has one [header] section and one [[frame]] section per frame. "
                + "Remove this line, or move its setting into [header].",
                unknown.KeySpan, null, unknown.Name));
        }

        if (options.DocumentPath is { Length: > 0 } path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > AtxSchema.MaxBitmapNameLength)
            {
                results.Add(new Diagnostic(
                    AtxRules.NameTooLong, DiagnosticSeverity.Warning,
                    $"The file name '{Path.GetFileName(path)}' is too long: the texture name "
                    + $"'{name}' is {name.Length} characters and the engine only keeps "
                    + $"{AtxSchema.MaxBitmapNameLength}.",
                    $"Rename the file so the part before '.atx' is at most "
                    + $"{AtxSchema.MaxBitmapNameLength} characters. The trimmed name is also what "
                    + "level events use, so a long name breaks ATX_Play and friends.",
                    new TextSpan(0, 0)));
            }
        }
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private static void CheckHeader(
        List<Diagnostic> results, AtxModel model, AtxEditor editor,
        IReadOnlyDictionary<string, AtxKeyEntry> present)
    {
        var header = model.Header;

        CheckType(results, header.FrameTime, AtxSchema.KeyFrameTime, AtxValueKind.Integer, null, editor, null);
        CheckType(results, header.InitiallyOn, AtxSchema.KeyInitiallyOn, AtxValueKind.Boolean, null, editor, null);
        CheckType(results, header.AnimationMode, AtxSchema.KeyAnimationMode, AtxValueKind.Integer, null, editor, null);
        CheckType(results, header.Format, AtxSchema.KeyFormat, AtxValueKind.String, null, editor, null);
        CheckType(results, header.AlphaMask, AtxSchema.KeyAlphaMask, AtxValueKind.String, null, editor, null);
        CheckType(results, header.Material, AtxSchema.KeyMaterial, AtxValueKind.String, null, editor, null);

        // Compare the number the game compares: it narrows to int before clamping, so a 64-bit
        // value that does not fit can land below 1 even though the digits in the file look large.
        if (header.FrameTime is { Accepted: true } ft
            && AtxHeader.NarrowToInt(ft.Value) < AtxSchema.MinFrameTimeMs)
        {
            results.Add(new Diagnostic(
                AtxRules.FrameTimeTooSmall, DiagnosticSeverity.Warning,
                $"A frame time of {AtxHeader.NarrowToInt(ft.Value)} ms is not possible; "
                + "the game will use 1 ms instead.",
                "Set frame_time to 1 or more. 1 ms is roughly 1000 frames a second, which is far "
                + "faster than the game draws — 50 to 150 ms suits most animated textures.",
                ft.ValueSpan, null, AtxSchema.KeyFrameTime,
                [EditFix("Set to 1", () => editor.SetHeaderValue(AtxSchema.KeyFrameTime, AtxValue.Integer(1)))]));
        }

        if (header.AnimationMode is { Accepted: true } mode
            && AtxSchema.ParseAnimationMode(AtxHeader.NarrowToInt(mode.Value)) is null)
        {
            results.Add(new Diagnostic(
                AtxRules.AnimationModeOutOfRange, DiagnosticSeverity.Warning,
                $"animation_mode {AtxHeader.NarrowToInt(mode.Value)} is not one of the four modes, "
                + "so the game falls back to Static.",
                "Use 0 for Static, 1 for Ping-Pong, 2 for Loop, or 3 for Play Once.",
                mode.ValueSpan, null, AtxSchema.KeyAnimationMode));
        }

        if (header.InitiallyOn is { Accepted: true } on
            && header.EffectiveAnimationMode == AtxAnimationMode.Static)
        {
            results.Add(new Diagnostic(
                AtxRules.InitiallyOnWithStatic, DiagnosticSeverity.Info,
                $"initially_on = {(on.Value ? "true" : "false")} has no effect while the animation mode is Static.",
                "Static textures never advance on their own, so there is nothing to start or stop. "
                + "Either remove this line or choose Loop, Ping-Pong or Play Once.",
                on.KeySpan, null, AtxSchema.KeyInitiallyOn,
                [EditFix("Remove key", () => editor.RemoveHeaderKey(AtxSchema.KeyInitiallyOn))]));
        }

        CheckEmptyString(results, header.Format, AtxSchema.KeyFormat, editor, null);
        CheckEmptyString(results, header.AlphaMask, AtxSchema.KeyAlphaMask, editor, null);
        CheckEmptyString(results, header.Material, AtxSchema.KeyMaterial, editor, null);

        if (header.EffectiveFormat is { } formatToken && AtxSchema.ParseFormatToken(formatToken) is null)
        {
            string? suggestion = EditDistance.Closest(formatToken, AtxSchema.AllFormatSpellings);
            var fixes = new List<QuickFix>();
            if (suggestion is not null)
            {
                fixes.Add(EditFix($"Replace with \"{suggestion}\"",
                    () => editor.SetHeaderValue(AtxSchema.KeyFormat, AtxValue.String(suggestion))));
            }
            results.Add(new Diagnostic(
                AtxRules.UnknownFormat, DiagnosticSeverity.Error,
                $"\"{UserText.Printable(formatToken)}\" is not a pixel format the game knows, so it will refuse to load this texture."
                + (suggestion is null ? string.Empty : $" Did you mean \"{suggestion}\"?"),
                "Valid formats are: " + string.Join(", ", AtxSchema.AllFormatSpellings) + ". "
                + "Remove the line entirely to keep each frame in the format it was saved in.",
                header.Format!.ValueSpan, null, AtxSchema.KeyFormat, fixes));
        }

        if (header.EffectiveMaterial is { } material && AtxSchema.ParseMaterial(material) is null)
        {
            results.Add(MaterialDiagnostic(material, header.Material!.ValueSpan, null,
                token => editor.SetHeaderValue(AtxSchema.KeyMaterial, AtxValue.String(token))));
        }

        if (header.EffectiveAlphaMask is { } mask)
        {
            CheckFileName(results, mask, header.AlphaMask!.ValueSpan, null, AtxSchema.KeyAlphaMask,
                stripped => editor.SetHeaderValue(AtxSchema.KeyAlphaMask, AtxValue.String(stripped)));

            var format = AtxSchema.ParseFormatToken(header.EffectiveFormat);
            if (format is not null && !Cairn.Formats.Imaging.EngineFormats.HasAlpha(format.Format))
            {
                var promoted = Cairn.Formats.Imaging.EngineFormats.PromoteToAlpha(format.Format);
                results.Add(new Diagnostic(
                    AtxRules.MaskPromotesFormat, DiagnosticSeverity.Info,
                    $"format \"{UserText.Printable(header.EffectiveFormat)}\" has no transparency, so the alpha mask makes "
                    + $"the game switch to {Cairn.Formats.Imaging.EngineFormats.DisplayName(promoted)} automatically.",
                    "This is fine — it just uses more memory than the format you asked for. Set the "
                    + "format to the alpha-capable one yourself if you want it stated explicitly.",
                    header.Format!.ValueSpan, null, AtxSchema.KeyFormat));
            }
            else if (format is { Format: Cairn.Formats.Imaging.EngineFormat.Argb1555 })
            {
                results.Add(new Diagnostic(
                    AtxRules.MaskWith1555, DiagnosticSeverity.Info,
                    "format \"1555\" only stores transparency as fully on or fully off, so the alpha "
                    + "mask will be reduced to hard edges.",
                    "Anything 50% grey or lighter in the mask becomes opaque, the rest becomes "
                    + "invisible. Use \"4444\" or \"8888\" if you want soft edges.",
                    header.Format!.ValueSpan, null, AtxSchema.KeyFormat));
            }
        }

        foreach (var unknown in header.UnknownKeys)
        {
            results.Add(UnknownKeyDiagnostic(unknown, AtxKeyScope.Header, null, present));
        }
    }

    // ── Frames ────────────────────────────────────────────────────────────────

    private static void CheckFrames(
        List<Diagnostic> results, AtxModel model, AtxEditor editor,
        IReadOnlyList<IReadOnlyDictionary<string, AtxKeyEntry>> frameKeys)
    {
        for (int i = 0; i < model.Frames.Count; i++)
        {
            var frame = model.Frames[i];
            int index = i;

            CheckType(results, frame.FrameTime, AtxSchema.KeyFrameTime, AtxValueKind.Integer, index, editor, index);
            CheckType(results, frame.Material, AtxSchema.KeyMaterial, AtxValueKind.String, index, editor, index);

            // file: the game treats a missing, empty or wrong-typed value as the same failure.
            if (frame.File is null || !frame.File.Accepted || frame.File.Value!.Length == 0)
            {
                string what = frame.File is null ? "has no file line"
                    : !frame.File.Accepted ? "has a file value that is not text in quotes"
                    : "has an empty file name";
                results.Add(new Diagnostic(
                    AtxRules.FrameFileMissing, DiagnosticSeverity.Error,
                    $"Frame {index} {what}, so the game will refuse to load this texture.",
                    "Every [[frame]] needs a line like file = \"hazard_strip_00.tga\" naming the "
                    + "image for that frame.",
                    frame.File?.ValueSpan ?? frame.HeaderSpan, index, AtxSchema.KeyFile));
            }
            else
            {
                string file = frame.File.Value;
                if (file.EndsWith(".atx", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new Diagnostic(
                        AtxRules.NestedAtx, DiagnosticSeverity.Error,
                        $"Frame {index} points at another .atx file ('{UserText.Printable(file)}'), which is not allowed.",
                        "An animated texture cannot contain another animated texture. Point the frame "
                        + "at an image file instead (.tga, .dds, .png, .jpg or .vbm).",
                        frame.File.ValueSpan, index, AtxSchema.KeyFile));
                }
                else
                {
                    CheckFileName(results, file, frame.File.ValueSpan, index, AtxSchema.KeyFile,
                        stripped => editor.SetFrameValue([index], AtxSchema.KeyFile, AtxValue.String(stripped)));
                }
            }

            if (frame.FrameTime is { Accepted: true } ft)
            {
                if (AtxHeader.NarrowToInt(ft.Value) < AtxSchema.MinFrameTimeMs)
                {
                    results.Add(new Diagnostic(
                        AtxRules.FrameTimeTooSmall, DiagnosticSeverity.Warning,
                        $"Frame {index} asks for {AtxHeader.NarrowToInt(ft.Value)} ms, which is not "
                        + "possible; the game will use 1 ms.",
                        "Set this frame's frame_time to 1 or more, or remove the line so the frame "
                        + "uses the texture's own frame time.",
                        ft.ValueSpan, index, AtxSchema.KeyFrameTime,
                        [EditFix("Set to 1",
                            () => editor.SetFrameValue([index], AtxSchema.KeyFrameTime, AtxValue.Integer(1)))]));
                }
                else if (frame.FrameTimeOverrideMs == model.Header.EffectiveFrameTimeMs)
                {
                    results.Add(new Diagnostic(
                        AtxRules.RedundantFrameTime, DiagnosticSeverity.Info,
                        $"Frame {index} sets frame_time to {frame.FrameTimeOverrideMs} ms, which is "
                        + "already the texture's frame time.",
                        "Remove the line to keep the file tidy; the frame will still be shown for "
                        + $"{model.Header.EffectiveFrameTimeMs} ms, and it will follow the texture if "
                        + "you change that later.",
                        ft.KeySpan, index, AtxSchema.KeyFrameTime,
                        [EditFix("Remove override",
                            () => editor.RemoveFrameKey([index], AtxSchema.KeyFrameTime))]));
                }
            }

            CheckEmptyString(results, frame.Material, AtxSchema.KeyMaterial, editor, index);

            if (frame.MaterialOverride is { } material)
            {
                if (AtxSchema.ParseMaterial(material) is null)
                {
                    results.Add(MaterialDiagnostic(material, frame.Material!.ValueSpan, index,
                        token => editor.SetFrameValue([index], AtxSchema.KeyMaterial, AtxValue.String(token))));
                }
                else if (string.Equals(material, model.Header.EffectiveMaterial, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new Diagnostic(
                        AtxRules.RedundantMaterial, DiagnosticSeverity.Info,
                        $"Frame {index} sets material to \"{UserText.Printable(material)}\", which is already the texture's material.",
                        "Remove the line to keep the file tidy; the frame will still report "
                        + $"\"{UserText.Printable(material)}\" for footsteps and impacts.",
                        frame.Material!.KeySpan, index, AtxSchema.KeyMaterial,
                        [EditFix("Remove override",
                            () => editor.RemoveFrameKey([index], AtxSchema.KeyMaterial))]));
                }
            }

            foreach (var unknown in frame.UnknownKeys)
            {
                results.Add(UnknownKeyDiagnostic(unknown, AtxKeyScope.Frame, index,
                    index < frameKeys.Count ? frameKeys[index] : EmptyKeys));
            }
        }
    }

    // ── Shared rule bodies ────────────────────────────────────────────────────

    private static void CheckType<T>(
        List<Diagnostic> results, Located<T>? located, string key, AtxValueKind expected,
        int? frameIndex, AtxEditor editor, int? frameForFix)
    {
        if (located is null) return;
        var wanted = expected switch
        {
            AtxValueKind.Integer => TomlValueKind.Integer,
            AtxValueKind.Boolean => TomlValueKind.Boolean,
            _ => TomlValueKind.String,
        };
        if (located.ActualKind == wanted) return;

        string where = frameIndex is { } f ? $"Frame {f}: " : string.Empty;
        string expectedText = expected switch
        {
            AtxValueKind.Integer => "a whole number, written without quotes (for example 80)",
            AtxValueKind.Boolean => "true or false, written without quotes",
            _ => "text in double quotes (for example \"metal\")",
        };

        var fixes = new List<QuickFix>();
        if (ConvertedValue(located, expected) is { } converted)
        {
            fixes.Add(EditFix($"Change to {UserText.Printable(converted.ToToml())}", () => frameForFix is { } fi
                ? editor.SetFrameValue([fi], key, converted)
                : editor.SetHeaderValue(key, converted)));
        }

        // The game does not reject every wrong-typed value: toml++ converts a whole-numbered float
        // and a boolean to an integer, and an integer to a boolean. Saying "ignored" for those
        // would tell the designer something untrue, so the two cases are worded separately.
        string effect = located.Accepted
            ? $"The game still reads it, as {ReadsAs(located, key)}, but writing it this way is "
              + "easy to get wrong."
            : "The game ignores the line completely and uses the default instead.";

        results.Add(new Diagnostic(
            AtxRules.WrongType,
            DiagnosticSeverity.Warning,
            $"{where}{key} should be {expectedText}, but it is written as "
            + $"{Describe(located.ActualKind)}. {effect}",
            $"Rewrite the value as {expectedText}.",
            located.ValueSpan, frameIndex, key, fixes));
    }

    /// <summary>
    /// What the game ends up using for a wrong-typed value it nevertheless accepts, phrased for the
    /// diagnostic message. Frame times are shown after the >= 1 clamp and animation modes are named,
    /// because the raw number on its own would still leave the designer guessing.
    /// </summary>
    private static string ReadsAs<T>(Located<T> located, string key) => located switch
    {
        Located<bool> b => b.Value ? "true" : "false",
        Located<long> n when key == AtxSchema.KeyFrameTime =>
            $"{AtxHeader.ClampFrameTime(n.Value)} ms",
        Located<long> n when key == AtxSchema.KeyAnimationMode =>
            AtxSchema.ParseAnimationMode(AtxHeader.NarrowToInt(n.Value)) is { } m
                ? $"{AtxHeader.NarrowToInt(n.Value)} ({AtxSchema.AnimationModeLabel(m)})"
                : $"{AtxHeader.NarrowToInt(n.Value)}, which is out of range",
        Located<long> n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "that value",
    };

    private static AtxValue? ConvertedValue<T>(Located<T> located, AtxValueKind expected)
    {
        string raw = located.RawText.Trim();
        switch (expected)
        {
            case AtxValueKind.Integer:
                if (located is Located<long> { Accepted: true } ok) return AtxValue.Integer(ok.Value);
                if (raw.Length > 1 && raw[0] == '"' && raw[^1] == '"'
                    && long.TryParse(raw[1..^1], out long parsed)) return AtxValue.Integer(parsed);
                return null;
            case AtxValueKind.Boolean:
                // An integer the game already reads as a boolean converts to exactly that boolean,
                // so the quick fix never changes what the file means.
                if (located is Located<bool> { Accepted: true } flag) return AtxValue.Boolean(flag.Value);
                if (raw is "\"true\"") return AtxValue.Boolean(true);
                if (raw is "\"false\"") return AtxValue.Boolean(false);
                return null;
            default:
                if (located.ActualKind is TomlValueKind.Integer or TomlValueKind.Float or TomlValueKind.Boolean)
                    return AtxValue.String(raw);
                return null;
        }
    }

    private static string Describe(TomlValueKind kind) => kind switch
    {
        TomlValueKind.String => "text in quotes",
        TomlValueKind.Integer => "a whole number",
        TomlValueKind.Float => "a number with a decimal point",
        TomlValueKind.Boolean => "true or false",
        TomlValueKind.Array => "a list",
        TomlValueKind.InlineTable => "a group in braces",
        TomlValueKind.DateTime => "a date or time",
        _ => "something else",
    };

    private static void CheckEmptyString(
        List<Diagnostic> results, Located<string>? located, string key, AtxEditor editor, int? frameIndex)
    {
        if (located is not { Accepted: true, Value.Length: 0 }) return;
        string where = frameIndex is { } f ? $"Frame {f}: " : string.Empty;
        results.Add(new Diagnostic(
            AtxRules.EmptyString, DiagnosticSeverity.Info,
            $"{where}{key} is set to an empty value, which counts as not being set at all.",
            "Remove the line, or fill in a value.",
            located.ValueSpan, frameIndex, key,
            [EditFix("Remove key", () => frameIndex is { } fi
                ? editor.RemoveFrameKey([fi], key)
                : editor.RemoveHeaderKey(key))]));
    }

    private static void CheckFileName(
        List<Diagnostic> results, string fileName, TextSpan span, int? frameIndex, string key,
        Func<string, TextEditBatch> stripPath)
    {
        string where = frameIndex is { } f ? $"Frame {f}: " : string.Empty;

        if (fileName.Contains('/') || fileName.Contains('\\'))
        {
            string bare = Path.GetFileName(fileName.Replace('\\', '/'));
            results.Add(new Diagnostic(
                AtxRules.PathSeparator, DiagnosticSeverity.Warning,
                $"{where}'{UserText.Printable(fileName)}' includes a folder path, and Red Faction's "
                + "file system has no folders.",
                $"Use just the file name, '{UserText.Printable(bare)}', and make sure the file sits "
                + "next to the .atx or inside one of your search folders.",
                span, frameIndex, key,
                [EditFix($"Use '{UserText.Printable(bare)}'", () => stripPath(bare))]));
            fileName = bare;
        }

        if (fileName.Length > AtxSchema.MaxBitmapNameLength)
        {
            results.Add(new Diagnostic(
                AtxRules.NameTooLong, DiagnosticSeverity.Warning,
                $"{where}'{UserText.Printable(fileName)}' is {fileName.Length} characters long and "
                + $"the engine only keeps {AtxSchema.MaxBitmapNameLength}.",
                $"Rename the image to {AtxSchema.MaxBitmapNameLength} characters or fewer, including "
                + "its extension, and update the frame to match.",
                span, frameIndex, key));
        }

        string extension = Path.GetExtension(fileName);
        bool known = AtxSchema.TextureExtensions.Any(e =>
            e != ".atx" && string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
        if (!known)
        {
            results.Add(new Diagnostic(
                AtxRules.UnsupportedExtension, DiagnosticSeverity.Warning,
                extension.Length == 0
                    ? $"{where}'{UserText.Printable(fileName)}' has no file extension, so the game may not find an image for it."
                    : $"{where}'{UserText.Printable(extension)}' is not a texture type the game loads.",
                "Use one of: " + string.Join(", ",
                    AtxSchema.TextureExtensions.Where(e => e != ".atx")) + ".",
                span, frameIndex, key));
        }
    }

    private static Diagnostic MaterialDiagnostic(
        string token, TextSpan span, int? frameIndex, Func<string, TextEditBatch> replace)
    {
        string? suggestion = EditDistance.Closest(token, AtxSchema.AllMaterialTokens);
        var fixes = new List<QuickFix>();
        if (suggestion is not null)
        {
            fixes.Add(EditFix($"Replace with \"{suggestion}\"", () => replace(suggestion)));
        }
        string where = frameIndex is { } f ? $"Frame {f}: " : string.Empty;
        return new Diagnostic(
            AtxRules.UnknownMaterial, DiagnosticSeverity.Error,
            $"{where}\"{UserText.Printable(token)}\" is not a surface material the game knows, so it "
            + "will refuse to load this texture."
            + (suggestion is null ? string.Empty : $" Did you mean \"{suggestion}\"?"),
            "Valid materials are: " + string.Join(", ", AtxSchema.AllMaterialTokens) + ".",
            span, frameIndex, AtxSchema.KeyMaterial, fixes);
    }

    private static readonly Dictionary<string, AtxKeyEntry> EmptyKeys = new(StringComparer.Ordinal);

    private static Diagnostic UnknownKeyDiagnostic(
        UnknownKey unknown, AtxKeyScope scope, int? frameIndex,
        IReadOnlyDictionary<string, AtxKeyEntry> present)
    {
        var valid = AtxSchema.Keys.Where(k => k.Scope == scope).Select(k => k.Name).ToList();
        string? suggestion = EditDistance.Closest(unknown.Name, valid);
        string section = scope == AtxKeyScope.Header ? "[header]" : "a [[frame]] entry";
        string where = frameIndex is { } f ? $"Frame {f}: " : string.Empty;
        string name = UserText.Printable(unknown.Name);

        var fixes = new List<QuickFix>();
        // Offer the rename only when the name it would produce is not already in this section.
        // Renaming 'fil' to 'file' in a frame that already has a file line would write the key
        // twice, which is a TOML error — the game would then refuse a file it was loading before.
        if (suggestion is not null && !present.ContainsKey(suggestion))
        {
            // Renaming means replacing the key token itself, which is a plain text edit.
            fixes.Add(new QuickFix($"Rename to '{suggestion}'", QuickFixKind.Edit,
                () => new TextEditBatch([TextEdit.Replace(unknown.KeySpan, suggestion)], "Rename key")));
        }

        return new Diagnostic(
            AtxRules.UnknownKey, DiagnosticSeverity.Warning,
            $"{where}'{name}' is not a setting {section} understands, so the game ignores it."
            + (suggestion is null ? string.Empty
                : present.ContainsKey(suggestion)
                    ? $" Did you mean '{suggestion}'? That setting is already written in this "
                      + "section, so this line is probably a leftover."
                    : $" Did you mean '{suggestion}'?"),
            $"Settings allowed here: {string.Join(", ", valid)}.",
            unknown.KeySpan, frameIndex, unknown.Name, fixes);
    }

    private static QuickFix EditFix(string title, Func<TextEditBatch> create) =>
        new(title, QuickFixKind.Edit, create);
}
