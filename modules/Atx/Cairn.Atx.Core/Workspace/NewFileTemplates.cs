using Cairn.Atx.Text;

namespace Cairn.Atx.Workspace;

/// <summary>The starting text for a new .atx document.</summary>
public static class NewFileTemplates
{
    /// <summary>Returns the chosen template using the given line endings.</summary>
    public static string Create(NewFileTemplateKind kind, LineEndingKind lineEnding = LineEndingKind.CrLf) =>
        kind == NewFileTemplateKind.Commented ? Commented(lineEnding) : Minimal(lineEnding);

    /// <summary>Settings only, no commentary.</summary>
    public static string Minimal(LineEndingKind lineEnding = LineEndingKind.CrLf)
    {
        string eol = lineEnding.ToText();
        return string.Join(eol,
        [
            "[header]",
            "frame_time = 100",
            "animation_mode = 2",
            "",
        ]);
    }

    /// <summary>The same settings with explanations, for someone writing their first ATX.</summary>
    public static string Commented(LineEndingKind lineEnding = LineEndingKind.CrLf)
    {
        string eol = lineEnding.ToText();
        return string.Join(eol,
        [
            "# An animated texture. Name this file after the texture it replaces, for example",
            "# mtl_panel01.atx to animate anything using mtl_panel01.tga.",
            "",
            "[header]",
            "",
            "# How long each frame is shown, in milliseconds. 100 ms is ten frames a second.",
            "frame_time = 100",
            "",
            "# 0 = Static (only level events change the frame)",
            "# 1 = Ping-Pong (forward, then backward, repeating)",
            "# 2 = Loop (forward, starting again at frame 0)",
            "# 3 = Play Once (forward, then hold on the last frame)",
            "animation_mode = 2",
            "",
            "# Add one [[frame]] section per image, in the order they should play.",
            "# Every frame must be the same size and format as the first one.",
            "",
            "# [[frame]]",
            "# file = \"my_texture_00.tga\"",
            "",
        ]);
    }
}
