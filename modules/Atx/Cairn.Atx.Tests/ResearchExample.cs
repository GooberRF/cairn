namespace Cairn.Atx.Tests;

/// <summary>
/// A verbatim copy of research/mtl_gbrhazstripeex01.atx, the annotated example that documents the
/// format. Embedded so the parser test is self-contained; ParserTests also checks it still matches
/// the file on disk when research/ is present.
/// </summary>
internal static class ResearchExample
{
    public const string Text = """
# ─────────────────────────────────────────────────────────────────────────────
# Example ATX file: mtl_gbrhazstripeex01.atx
#
# This file is referenced anywhere the engine would normally request
# "mtl_gbrhazstripeex01" (e.g. a brush textured with mtl_gbrhazstripeex01.tga). The bm
# supercede chain finds mtl_gbrhazstripeex01.atx first and routes through it; the
# texture renders as the current ATX frame and the controller advances frames
# according to animation_mode.
#
# Handle for runtime events is "mtl_gbrhazstripeex01" (the .atx filename without
# extension or path, case-insensitive).
# ─────────────────────────────────────────────────────────────────────────────

[header]

# Default time between frames in milliseconds.
# Clamped to >= 1 at parse time. Defaults to 100ms if omitted.
# Can be overridden per-frame below (see [[frame]] entries).
# Runtime: changeable via ATX_Set_Frame_Time event.
frame_time = 80

# Whether the animation is playing when the level first loads.
# Defaults to true. Combined with animation_mode below:
#   - Static (0): initially_on is ignored — frames never advance automatically
#   - PingPong/Loop/PlayOnce: true = start playing, false = start paused
# Runtime: flippable via ATX_Play / ATX_Pause events.
initially_on = true

# How the controller advances frames:
#   0 = Static     — no auto playback; only manual frame changes (ATX_Set_Frame)
#   1 = PingPong   — forward to last frame, backward to first, repeating
#   2 = Loop       — forward; wraps to frame 0 after last, repeating
#   3 = PlayOnce   — forward; stops on the last frame and holds
# Out-of-range values fall back to Static with a warning.
# Defaults to Static (0) if omitted.
animation_mode = 2

# Target pixel format. Optional. When set, every decoded frame is converted
# into this format. Only meaningful for uncompressed RGB/RGBA inputs; DXT-
# compressed inputs cannot be transformed and will fail with an error log.
# Accepted tokens (case-insensitive):
#   "565"  or "565_rgb"   — 16-bit RGB, no alpha
#   "4444" or "4444_argb" — 16-bit RGBA, 4-bit alpha
#   "1555" or "1555_argb" — 16-bit RGBA, 1-bit alpha
#   "888"  or "888_rgb"   — 24-bit RGB, no alpha
#   "8888" or "8888_argb" — 32-bit RGBA, 8-bit alpha
# If omitted, frames are kept in their source format.
format = "8888_argb"

# Optional alpha mask. The named file (8-bit greyscale TGA/VBM) is loaded and
# composited as the alpha channel of every frame. Dimensions and mip count
# must match frame[0]. If the chosen `format` lacks alpha, it is auto-promoted
# to its alpha-bearing equivalent (565→4444, 888→8888).
# Use this when the colour frames don't carry alpha but you want a shared mask.
alpha_mask = "hazard_strip_mask.tga"

# ATX-wide material override. When set, every footstep / bullet impact /
# decal that hits a surface textured with this ATX uses this material instead
# of whatever the engine would derive from the bm name.
# Accepted tokens (case-insensitive):
#   default, rock, metal, flesh, water, lava, solid, sand, ice, glass
# A per-frame `material` (below) overrides this for that specific frame.
material = "metal"


# ─── Frame list ──────────────────────────────────────────────────────────────
# At least one [[frame]] entry is required. Frames are loaded and validated to
# all share the same width, height, format, and mip-count as frame[0]; any
# mismatch fails the parse with an error log.
#
# Nested .atx in `file` is rejected at parse time (no recursion).
# ─────────────────────────────────────────────────────────────────────────────

[[frame]]
# Required. The texture file for this frame. Supported by the supercede chain
# below the parser (TGA / VBM / DDS / PNG / JPG); the parser itself only
# checks that the name is non-empty and doesn't end in .atx.
file = "hazard_strip_00.tga"

[[frame]]
file = "hazard_strip_01.tga"

# Per-frame override of header.frame_time. Same clamp (>= 1).
# Lets you hold a specific frame longer (e.g. a "flash" frame at peak intensity).
frame_time = 250

# Per-frame material override. Lets a single frame report a different surface
# type — e.g. a normally-metal panel that shows a glass shard frame impacting
# as glass. Accepts the same tokens as the header material.
material = "glass"

[[frame]]
file = "hazard_strip_02.tga"

[[frame]]
file = "hazard_strip_03.tga"

""";
}
