using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Atx.Text;

namespace Cairn.Atx.Editing;

/// <summary>
/// Converts frames to and from plain text, so Ctrl+C/Ctrl+V works between tabs and into any text
/// editor. Copied frames are written as ordinary <c>[[frame]]</c> blocks; pasting accepts those,
/// a bare list of filenames, or anything in between.
/// </summary>
public static class FrameClipboard
{
    /// <summary>The most clipboard text <see cref="Parse"/> will look at, in characters.</summary>
    public const int MaxParseLength = 1 << 20;

    /// <summary>The most frames <see cref="Parse"/> will return from one paste.</summary>
    public const int MaxParseFrames = 10_000;

    /// <summary>Renders frames as <c>[[frame]]</c> blocks separated by blank lines.</summary>
    public static string ToToml(
        IEnumerable<NewFrame> frames, LineEndingKind lineEnding = LineEndingKind.CrLf)
    {
        ArgumentNullException.ThrowIfNull(frames);
        // A bare CR is not a line break in TOML, so a document that uses them cannot have text
        // generated for it that way — the game's parser would read the whole thing as one line.
        string eol = (lineEnding == LineEndingKind.Cr ? LineEndingKind.Lf : lineEnding).ToText();
        var blocks = new List<string>();
        foreach (var frame in frames)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[[").Append(AtxSchema.FrameArray).Append("]]").Append(eol);
            sb.Append(AtxSchema.KeyFile).Append(" = ")
              .Append(TomlText.QuoteBasicString(frame.File)).Append(eol);
            if (frame.FrameTimeMs is { } ms)
                sb.Append(AtxSchema.KeyFrameTime).Append(" = ").Append(ms).Append(eol);
            if (!string.IsNullOrEmpty(frame.Material))
                sb.Append(AtxSchema.KeyMaterial).Append(" = ")
                  .Append(TomlText.QuoteBasicString(frame.Material)).Append(eol);
            blocks.Add(sb.ToString());
        }
        return string.Join(eol, blocks);
    }

    /// <summary>Renders the chosen frames of a document.</summary>
    public static string ToToml(
        AtxModel model, IReadOnlyList<int> indices, LineEndingKind lineEnding = LineEndingKind.CrLf)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(indices);
        var frames = indices
            .Where(i => i >= 0 && i < model.Frames.Count)
            .Distinct()
            .OrderBy(i => i)
            .Select(i => new NewFrame(
                model.Frames[i].EffectiveFile ?? string.Empty,
                model.Frames[i].FrameTimeOverrideMs,
                model.Frames[i].MaterialOverride));
        return ToToml(frames, lineEnding);
    }

    /// <summary>
    /// Reads frames out of clipboard text. Returns an empty list when nothing usable is found.
    /// </summary>
    public static IReadOnlyList<NewFrame> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        // The clipboard can hold anything at all — a whole novel pasted by accident. Read only as
        // much as could plausibly be frames, so a stray Ctrl+V never freezes the window.
        if (text.Length > MaxParseLength) text = text[..MaxParseLength];
        // Text copied out of another program often carries a byte-order mark. It is not part of
        // the document, and leaving it in front of the first [[frame]] only confuses the parser.
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (text.Length == 0) return [];

        var parse = AtxParser.Parse(text);
        if (parse.Model is { Frames.Count: > 0 } model)
        {
            var frames = model.Frames
                .Where(f => f.EffectiveFile is { Length: > 0 })
                .Take(MaxParseFrames)
                .Select(f => new NewFrame(f.EffectiveFile!, f.FrameTimeOverrideMs, f.MaterialOverride))
                .ToList();
            if (frames.Count > 0) return frames;
        }

        // Fall back to one filename per line, which is what dragging from Explorer or typing gives.
        var loose = new List<NewFrame>();
        foreach (string raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (loose.Count >= MaxParseFrames) break;
            string line = raw.Trim().Trim('"');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('[')) continue;
            if (line.Contains('=')) continue;
            string name = Path.GetFileName(line);
            if (name.Length == 0 || Path.GetExtension(name).Length == 0) continue;
            loose.Add(new NewFrame(name));
        }
        return loose;
    }

    /// <summary>True when the text looks like something <see cref="Parse"/> can use.</summary>
    public static bool CanParse(string? text) => Parse(text).Count > 0;
}
