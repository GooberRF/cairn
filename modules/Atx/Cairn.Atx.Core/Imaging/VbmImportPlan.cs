using System.Globalization;
using Cairn.Atx.Editing;
using Cairn.Atx.Schema;
using Cairn.Atx.Text;

namespace Cairn.Atx.Imaging;

/// <summary>How much a planning message matters.</summary>
public enum VbmImportSeverity
{
    /// <summary>The import cannot run until it is fixed.</summary>
    Error,

    /// <summary>The import will run; the result may not be what was meant.</summary>
    Warning,

    /// <summary>Worth saying, nothing to fix.</summary>
    Note,
}

/// <summary>One thing the plan has to say about itself.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Text">The sentence shown to the user.</param>
public sealed record VbmImportMessage(VbmImportSeverity Severity, string Text);

/// <summary>What the user chose in the import dialog.</summary>
public sealed record VbmImportOptions
{
    /// <summary>
    /// Write <c>format = "1555" | "4444" | "565"</c> into the header so the game stores the frames
    /// in the same 16-bit format the VBM used. On by default: it makes the result identical to the
    /// original both to look at and in video memory.
    /// </summary>
    public bool SetFormatToMatchSource { get; init; } = true;

    /// <summary>
    /// The animation mode to write, or null to let the plan choose: Loop for an animation, Static
    /// for a single frame (where anything else would only earn an ATX031 note).
    /// </summary>
    public AtxAnimationMode? AnimationMode { get; init; }

    /// <summary>First source frame to export, counting from 0.</summary>
    public int FirstFrame { get; init; }

    /// <summary>Last source frame to export, or null for the last one in the file.</summary>
    public int? LastFrame { get; init; }

    /// <summary>Put a short explanatory comment block at the top of the generated .atx.</summary>
    public bool IncludeComments { get; init; } = true;
}

/// <summary>
/// Everything an import will do, worked out before anything is written: which frames become which
/// files, what the .atx will be called, what it will say, and what is wrong with any of that.
///
/// <para>
/// It is a pure value built from a header and some names, so the dialog can rebuild it on every
/// keystroke to drive a live file list and inline validation, and the tests can assert on the exact
/// text that would reach the disk without going near one.
/// </para>
/// </summary>
public sealed class VbmImportPlan
{
    /// <summary>The extension every exported frame gets.</summary>
    public const string FrameExtension = ".tga";

    /// <summary>The extension of the generated file.</summary>
    public const string AtxExtension = ".atx";

    /// <summary>Narrowest zero-padding an exported frame number gets, so `_00` rather than `_0`.</summary>
    public const int MinimumPadWidth = 2;

    private VbmImportPlan(
        VbmInfo source, string sourceName, string outputFolder, string atxFileName,
        string frameBaseName, IReadOnlyList<int> sourceFrames, IReadOnlyList<string> frameFileNames,
        VbmImportOptions options, int frameTimeMs, AtxAnimationMode animationMode)
    {
        Source = source;
        SourceName = sourceName;
        OutputFolder = outputFolder;
        AtxFileName = atxFileName;
        FrameBaseName = frameBaseName;
        SourceFrames = sourceFrames;
        FrameFileNames = frameFileNames;
        Options = options;
        FrameTimeMs = frameTimeMs;
        AnimationMode = animationMode;
    }

    private IReadOnlyList<VbmImportMessage> _messages = [];

    /// <summary>The header the plan was built from.</summary>
    public VbmInfo Source { get; }

    /// <summary>The .vbm's own file name, e.g. <c>titan_flame.vbm</c>.</summary>
    public string SourceName { get; }

    /// <summary>Where the files are written.</summary>
    public string OutputFolder { get; }

    /// <summary>The generated file's name, e.g. <c>titan_flame.atx</c>.</summary>
    public string AtxFileName { get; }

    /// <summary>The generated file's full path.</summary>
    public string AtxPath => Combine(OutputFolder, AtxFileName);

    /// <summary>The stem every frame name is built from.</summary>
    public string FrameBaseName { get; }

    /// <summary>Which source frame each exported file comes from, in output order.</summary>
    public IReadOnlyList<int> SourceFrames { get; }

    /// <summary>The frame files, in order, as bare names.</summary>
    public IReadOnlyList<string> FrameFileNames { get; }

    /// <summary>The options this plan was built with.</summary>
    public VbmImportOptions Options { get; }

    /// <summary>The header <c>frame_time</c>, in milliseconds.</summary>
    public int FrameTimeMs { get; }

    /// <summary>The header <c>animation_mode</c>.</summary>
    public AtxAnimationMode AnimationMode { get; }

    /// <summary>Everything wrong or worth knowing about the plan.</summary>
    public IReadOnlyList<VbmImportMessage> Messages => _messages;

    /// <summary>True when nothing blocks the import.</summary>
    public bool CanRun => FrameFileNames.Count > 0
        && !Messages.Any(m => m.Severity == VbmImportSeverity.Error);

    /// <summary>The full path of each frame file, in order.</summary>
    public IReadOnlyList<string> FramePaths =>
        [.. FrameFileNames.Select(n => Combine(OutputFolder, n))];

    /// <summary>Every file the import writes: the frames, then the .atx.</summary>
    public IReadOnlyList<string> AllPaths => [.. FramePaths, AtxPath];

    /// <summary>True when the exported frames carry an alpha channel (1555 and 4444 do; 565 does not).</summary>
    public bool FramesHaveAlpha => TgaWriter.NeedsAlpha(Source.Format);

    /// <summary>What one exported frame will weigh, in bytes.</summary>
    public long FrameSizeBytes =>
        TgaWriter.SizeOf(Source.Width, Source.Height, FramesHaveAlpha);

    /// <summary>What the whole set of frames will weigh, in bytes.</summary>
    public long TotalFrameBytes => FrameSizeBytes * FrameFileNames.Count;

    /// <summary>The .atx stem, i.e. the texture name the game will know this by.</summary>
    public string AtxStem => StripExtension(AtxFileName, AtxExtension);

    /// <summary>
    /// True when the generated .atx is named after the .vbm, and therefore takes its place
    /// everywhere the game uses that texture.
    /// </summary>
    public bool ReplacesSource => string.Equals(
        AtxStem, StripExtension(SourceName, ".vbm"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The sentence the dialog shows about superseding: the good news when the names match, the
    /// warning when they do not. The rule is <c>bm_read_header</c>'s extension list — .atx is
    /// probed before .vbm, so same-named .atx wins with no level edits at all.
    /// </summary>
    public string SupersedeNote => ReplacesSource
        ? $"{AtxFileName} replaces {SourceName} everywhere the game uses that texture — "
          + "the game looks for an .atx before a .vbm, so no level needs editing."
        : $"{AtxFileName} is a new texture called {AtxStem}. It will not replace "
          + $"{SourceName}; for that, name it {StripExtension(SourceName, ".vbm")}{AtxExtension}.";

    /// <summary>How the timing came out, e.g. "15 fps → 67 ms per frame (14.9 fps).".</summary>
    public string TimingSummary
    {
        get
        {
            if (Source.FrameCount == 1)
            {
                return $"A single frame never advances, so frame_time is the format's own default "
                    + $"of {AtxSchema.DefaultFrameTimeMs} ms.";
            }
            if (Source.Fps <= 0)
            {
                return $"The VBM states no usable frame rate, so each frame gets "
                    + $"{AtxSchema.DefaultFrameTimeMs} ms ({Format(1000.0 / AtxSchema.DefaultFrameTimeMs)} fps).";
            }
            double effective = 1000.0 / FrameTimeMs;
            string tail = Math.Abs(effective - Source.Fps) < 0.05
                ? "."
                : $" ({Format(effective)} fps).";
            return $"{Source.Fps} fps → {FrameTimeMs} ms per frame{tail}";
        }
    }

    /// <summary>The text of the generated .atx, with CRLF line endings.</summary>
    public string AtxText => BuildText(LineEndingKind.CrLf);

    /// <summary>
    /// Builds a plan. Nothing here touches the disk, so a dialog can call it on every keystroke.
    /// </summary>
    /// <param name="source">The VBM header, from <see cref="VbmCodec.ReadInfo"/>.</param>
    /// <param name="sourceName">The .vbm's own file name, used for the defaults and the comment.</param>
    /// <param name="outputFolder">Where the files go.</param>
    /// <param name="atxFileName">The .atx name, or null for the default.</param>
    /// <param name="frameBaseName">The frame stem, or null for the default.</param>
    /// <param name="options">What the user chose, or null for the defaults.</param>
    public static VbmImportPlan Create(
        VbmInfo source, string sourceName, string outputFolder,
        string? atxFileName = null, string? frameBaseName = null, VbmImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        sourceName = string.IsNullOrWhiteSpace(sourceName) ? "texture.vbm" : sourceName.Trim();
        options ??= new VbmImportOptions();
        outputFolder = outputFolder?.Trim() ?? string.Empty;

        var frames = FrameRange(source.FrameCount, options);
        int pad = PadWidth(frames.Count);
        string atx = (atxFileName ?? DefaultAtxName(sourceName)).Trim();
        if (atx.Length > 0 && !atx.EndsWith(AtxExtension, StringComparison.OrdinalIgnoreCase))
            atx += AtxExtension;
        string baseName = (frameBaseName ?? DefaultFrameBaseName(sourceName, frames.Count)).Trim();

        var names = new List<string>(frames.Count);
        for (int i = 0; i < frames.Count; i++) names.Add(FrameFileName(baseName, i, pad));

        // The format says the fps field means nothing when there is one frame, and stock files
        // prove it: single-frame VBMs carry 1, 2, 15 and everything between. Taking one of those
        // literally would write frame_time = 1000 into a texture that never advances.
        int frameTime = FrameTimeFor(source.FrameCount > 1 ? source.Fps : 0);
        var mode = options.AnimationMode
            ?? (frames.Count > 1 ? AtxAnimationMode.Loop : AtxAnimationMode.Static);

        var plan = new VbmImportPlan(
            source, sourceName, outputFolder, atx, baseName, frames, names, options, frameTime, mode);
        plan._messages = Validate(plan);
        return plan;
    }

    // ── Naming ────────────────────────────────────────────────────────────────

    /// <summary>
    /// How many digits an exported frame number gets: enough for the highest one, never fewer than
    /// two. Ten frames run 00–09 and a thousand run 000–999, which is what makes them sort the way
    /// they play in every file listing there is.
    /// </summary>
    /// <param name="frameCount">How many frames are exported.</param>
    public static int PadWidth(int frameCount)
    {
        int digits = Math.Max(1, frameCount - 1).ToString(CultureInfo.InvariantCulture).Length;
        return Math.Max(MinimumPadWidth, digits);
    }

    /// <summary>One frame's file name.</summary>
    /// <param name="baseName">The stem.</param>
    /// <param name="ordinal">Its position in the export, counting from 0.</param>
    /// <param name="pad">Digits to pad the number to.</param>
    public static string FrameFileName(string baseName, int ordinal, int pad) =>
        baseName + "_" + ordinal.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0')
        + FrameExtension;

    /// <summary>The default .atx name: the .vbm's own name with the extension swapped.</summary>
    /// <param name="sourceName">The .vbm's file name.</param>
    public static string DefaultAtxName(string sourceName) =>
        Sanitise(StripExtension(BareName(sourceName), ".vbm")) + AtxExtension;

    /// <summary>
    /// The default frame stem: the .vbm's name without its extension, cut down until every name it
    /// produces fits the engine's bitmap-name buffer. A texture called
    /// <c>mtl_reactor_coolant_pipe.vbm</c> with a hundred frames has no room for its own name plus
    /// <c>_00.tga</c>, and a name the engine cannot hold is a frame the game cannot find.
    /// </summary>
    /// <param name="sourceName">The .vbm's file name.</param>
    /// <param name="frameCount">How many frames are exported.</param>
    public static string DefaultFrameBaseName(string sourceName, int frameCount)
    {
        string stem = Sanitise(StripExtension(BareName(sourceName), ".vbm"));
        int room = MaxBaseLength(PadWidth(frameCount));
        if (stem.Length > room) stem = stem[..Math.Max(1, room)];
        // Trimming can leave a trailing separator, which reads as a typo in every file listing.
        stem = stem.TrimEnd('_', '-', ' ', '.');
        return stem.Length == 0 ? "frame" : stem;
    }

    /// <summary>
    /// The longest stem whose generated names still fit <see cref="AtxSchema.MaxBitmapNameLength"/>,
    /// given the padding in use.
    /// </summary>
    /// <param name="pad">Digits each frame number is padded to.</param>
    public static int MaxBaseLength(int pad) =>
        AtxSchema.MaxBitmapNameLength - 1 - pad - FrameExtension.Length;

    /// <summary>
    /// A name safe to build a file name out of: anything Windows refuses becomes an underscore.
    /// Only used for the <i>defaults</i>, which may be derived from a VPP entry name; whatever the
    /// user then types is validated and reported, never quietly rewritten under them.
    /// </summary>
    /// <param name="name">The raw name.</param>
    public static string Sanitise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var text = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            text.Append(Array.IndexOf(invalid, c) >= 0 || c is '"' or '\\' ? '_' : c);
        }
        return text.ToString().Trim();
    }

    // ── Timing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The header <c>frame_time</c> for a VBM running at <paramref name="fps"/>: the nearest whole
    /// millisecond, never below the engine's own minimum of 1. A file that states no usable rate —
    /// zero, negative, or a single frame where the field means nothing — gets the format's default.
    /// </summary>
    /// <param name="fps">The VBM header's fps field.</param>
    public static int FrameTimeFor(int fps)
    {
        if (fps <= 0) return AtxSchema.DefaultFrameTimeMs;
        double ms = Math.Round(1000.0 / fps, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(ms, AtxSchema.MinFrameTimeMs, int.MaxValue);
    }

    // ── Generated text ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the .atx text. Laid out exactly as <c>NewFileTemplates</c> and <c>AtxEditor</c> write
    /// one — <c>[header]</c> first, one blank line between blocks, values through
    /// <see cref="AtxValue"/> so strings are escaped the way the rest of the app escapes them — so a
    /// generated file is indistinguishable from one the editor produced.
    /// </summary>
    /// <param name="lineEnding">Line ending to use; new files are CRLF.</param>
    public string BuildText(LineEndingKind lineEnding)
    {
        string eol = lineEnding.ToText();
        var lines = new List<string>();

        if (Options.IncludeComments)
        {
            lines.Add("# " + ImportComment());
            lines.Add("# " + SupersedeComment());
            lines.Add(string.Empty);
        }

        lines.Add("[" + AtxSchema.HeaderTable + "]");
        lines.Add($"{AtxSchema.KeyFrameTime} = {AtxValue.Integer(FrameTimeMs).ToToml()}");
        lines.Add($"{AtxSchema.KeyAnimationMode} = {AtxValue.Integer((int)AnimationMode).ToToml()}");
        if (Options.SetFormatToMatchSource && FormatToken() is { } token)
            lines.Add($"{AtxSchema.KeyFormat} = {AtxValue.String(token).ToToml()}");

        foreach (string name in FrameFileNames)
        {
            lines.Add(string.Empty);
            lines.Add("[[" + AtxSchema.FrameArray + "]]");
            lines.Add($"{AtxSchema.KeyFile} = {AtxValue.String(name).ToToml()}");
        }
        lines.Add(string.Empty);
        return string.Join(eol, lines);
    }

    /// <summary>The first comment line: where this file came from.</summary>
    public string ImportComment()
    {
        string frames = Source.FrameCount == 1 ? "1 frame" : $"{Source.FrameCount} frames";
        // The fps field is only meaningful for an animation, so it is only quoted for one.
        string rate = Source.Fps > 0 && Source.FrameCount > 1 ? $" at {Source.Fps} fps" : string.Empty;
        return $"Imported from {SourceName} (VBM v{Source.Version}, {Source.Width} x {Source.Height}, "
            + $"{FormatToken() ?? "unknown"}, {frames}{rate}) by ATX Workbench.";
    }

    /// <summary>The second comment line: what having this file next to the .vbm does.</summary>
    public string SupersedeComment() => ReplacesSource
        ? $"The game looks for an .atx before a .vbm, so this file takes the place of "
          + $"{SourceName} wherever that texture is used."
        : $"The game looks for an .atx before a .vbm. This one is called {AtxStem}, so it does not "
          + $"take the place of {SourceName}.";

    /// <summary>The <c>format</c> token matching the source, or null for a format with no token.</summary>
    public string? FormatToken() =>
        AtxSchema.FormatTokens.FirstOrDefault(f => f.Format == Source.Format)?.Token;

    // ── Validation ────────────────────────────────────────────────────────────

    private static IReadOnlyList<VbmImportMessage> Validate(VbmImportPlan plan)
    {
        var messages = new List<VbmImportMessage>();
        void Error(string text) => messages.Add(new VbmImportMessage(VbmImportSeverity.Error, text));
        void Warn(string text) => messages.Add(new VbmImportMessage(VbmImportSeverity.Warning, text));
        void Note(string text) => messages.Add(new VbmImportMessage(VbmImportSeverity.Note, text));

        if (plan.OutputFolder.Length == 0)
        {
            Error("Choose a folder to write the frames into.");
        }
        else if (!IsUsableFolder(plan.OutputFolder))
        {
            Error($"'{plan.OutputFolder}' is not a folder name this computer can use.");
        }

        if (plan.FrameFileNames.Count == 0)
            Error("That frame range is empty — pick at least one frame.");

        CheckFileName(plan.AtxFileName, "The .atx name", Error, Warn);
        if (plan.FrameFileNames.Count > 0)
        {
            if (plan.FrameBaseName.Length == 0) Error("The frame name cannot be empty.");
            else if (HasBadCharacters(plan.FrameBaseName))
            {
                Error("The frame name contains characters a file name cannot hold. RF's file "
                    + "system is flat, so it has to be a plain name with no folders in it.");
            }
            else
            {
                // Every generated name shares that stem and differs only in its number, so the last
                // one — the longest — answers for all of them.
                CheckFileName(plan.FrameFileNames[^1], "The frame file name", Error, Warn);
            }
        }

        string superseded = plan.AtxStem + FrameExtension;
        if (plan.FrameFileNames.Any(n => string.Equals(n, superseded, StringComparison.OrdinalIgnoreCase)))
        {
            Warn($"One of the frames is called {superseded}, the same stem as {plan.AtxFileName}. "
                + "That works, but the .atx and one of its own frames then share a texture name, "
                + "which is confusing to read later.");
        }

        if (plan.Source.Fps <= 0 && plan.Source.FrameCount > 1)
        {
            Note($"The VBM states no usable frame rate, so each frame gets "
                + $"{AtxSchema.DefaultFrameTimeMs} ms. Change frame_time afterwards if it plays wrong.");
        }
        if (plan.Source.FrameCount == 1)
        {
            Note("This VBM holds a single frame, so the .atx will have one frame and nothing to "
                + "animate. It is still a valid texture.");
        }
        if (!plan.Source.LengthMatchesHeader)
        {
            Warn("The file is not the size its header implies, so the frames may not all be there. "
                + "Anything missing is reported rather than written.");
        }
        return messages;
    }

    private static bool HasBadCharacters(string name) =>
        name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

    private static void CheckFileName(
        string name, string label, Action<string> error, Action<string> warn)
    {
        if (name.Length == 0)
        {
            error($"{label} cannot be empty.");
            return;
        }
        if (HasBadCharacters(name))
        {
            error($"{label} contains characters a file name cannot hold. RF's file system is flat, "
                + "so it has to be a plain name with no folders in it.");
            return;
        }
        // The same check a .vpp entry has to pass before anything is written under its name:
        // reserved device names, trailing dots and spaces, anything that is a path rather than a
        // name. An import writes files, so it earns the same scrutiny.
        if (!Assets.VppExtraction.IsSafeEntryName(name))
        {
            error($"{label} is not a name Windows will let a file have.");
            return;
        }
        if (name.Length > AtxSchema.MaxBitmapNameLength)
        {
            warn($"{label} is {name.Length} characters. The engine's bitmap name holds "
                + $"{AtxSchema.MaxBitmapNameLength}, so the game will not find this one.");
        }
    }

    private static bool IsUsableFolder(string folder)
    {
        try
        {
            return Path.GetFullPath(folder).Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<int> FrameRange(int frameCount, VbmImportOptions options)
    {
        int first = Math.Clamp(options.FirstFrame, 0, Math.Max(0, frameCount - 1));
        int last = Math.Clamp(options.LastFrame ?? frameCount - 1, 0, Math.Max(0, frameCount - 1));
        if (last < first) return [];
        var frames = new List<int>(last - first + 1);
        for (int i = first; i <= last; i++) frames.Add(i);
        return frames;
    }

    private static string Combine(string folder, string name)
    {
        if (folder.Length == 0) return name;
        try { return Path.Combine(folder, name); }
        catch (ArgumentException) { return name; }
    }

    private static string BareName(string name)
    {
        try { return Path.GetFileName(name); }
        catch (ArgumentException) { return name; }
    }

    private static string StripExtension(string name, string extension) =>
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? name[..^extension.Length]
            : name;

    private static string Format(double value) =>
        value.ToString("0.#", CultureInfo.CurrentCulture);
}
