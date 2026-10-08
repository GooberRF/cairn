# Cairn

One Windows app for Red Faction modding. Cairn has eight modules, one for each kind of file: animated textures
(`.atx`), animation clips and meshes (`.rfa`, `.v3c`, read-only `.v3m`, and the exporter's and PS2 version's
`.v3d`, `.vcm`, `.rfm` and `.rfc`, read and converted), effect meshes (`.vfx`), packfiles (`.vpp`), the archives
the game loads everything from, tables (`.tbl`), the text files that define weapons, characters, items and the rest,
bitmap fonts (`.vf`), Volition bitmaps (`.vbm`) and sounds (`.wav`, `.ogg`, `.aif` and the PlayStation 2 version's
`.vse` and `.vmu`). Open any of them side by side in tabs, preview them the way the game plays them, edit them, pack
them, and catch what the game would get wrong before loading them in-game.

Cairn replaces ATX Workbench 1.1 and RFA Workbench 1.0. On first run it imports their theme, game folder, search
folders and recent files (and RFA Workbench's retarget profiles); nothing of theirs is modified.

![Cairn with an animated texture, a clip and an effect open](docs/screenshot.png)

New to the app? The [usage guide](docs/USAGE.md) walks through every module and the common jobs step by step.

## What Cairn does with each format

| Format | View | Edit | Resave | Create new | Notes |
|---|---|---|---|---|---|
| `.atx` animated texture | Yes | Yes | Yes | Yes | Needs Alpine Faction 1.4+ in game; also made from a `.vbm` |
| `.rfa` animation clip | Yes | Yes | Yes | Yes | Morph animation kept or stripped, not edited |
| `.v3c` character mesh | Yes | Yes | Yes | Yes | New from glTF import |
| `.v3m` static mesh | Yes | No | As a copy | Yes | Read-only (Save As writes a copy); new from glTF import |
| `.v3d` exporter static mesh | Yes | No | Convert to `.v3m` | No | Read-only; converted the way the game's mesh compiler did, with a report |
| `.vcm` exporter character mesh | Yes | No | Convert to `.v3c` | No | Read-only, skeleton shown; weights kept byte for byte |
| `.rfm` PS2 static mesh | Yes | No | Convert to `.v3m` | No | Read-only; converts from a same-named `.v3d` beside it when there is one; Red Faction II meshes refused |
| `.rfc` PS2 character mesh | Yes | No | Convert to `.v3c` | No | Read-only; weights are 1/16 steps; converts from a same-named `.vcm` beside it when there is one |
| `.vfx` effect | Yes | Yes | Yes | Yes | Older versions convert before editing |
| `.vpp` packfile | Yes | Yes | Yes | Yes | Undo history resets after each save |
| `.tbl` table | Yes | Yes | Yes | Yes | Unknown tables get syntax checks only |
| `.vf` font | Yes | Yes | Yes | No | No TrueType import; edit a copy of an existing font |
| `.vbm` bitmap | Yes | Yes | Yes | Yes | New from images; 1555, 4444 and 565 |
| `.peg` PS2 texture pack | Yes | No | As `.vpp` | No | Saves as a PC packfile of `.tga`; in a packfile, **Convert to .tga...** converts the textures you tick from the selected `.peg` entries; MPEG-2 backgrounds decoded (black as transparent optional) |
| `.dds` image | Yes | No | No | Yes | Made from TGA, PNG or JPG by the packfile DDS converter |
| `.tga` `.png` `.jpg` images | Yes | No | No | No | Previewed in packfiles and tables |
| `.wav` `.ogg` `.aif` sounds | Yes | No | As `.wav` | No | Played with a waveform and loops; **Convert** writes 16-bit WAV or Ogg Vorbis |
| `.vse` PS2 sound effect | Yes | No | As `.wav` | No | PS ADPCM, both header layouts; never written; **Convert** writes WAV (loop in a `smpl` chunk) or Ogg Vorbis (loop comments) |
| `.vmu` PS2 music | Yes | No | As `.wav` | No | Stereo PS ADPCM; never written; **Convert** writes WAV or Ogg Vorbis |
| `.rfl` level | Details | No | No | No | Facts and referenced files, no 3D preview |

## The suite

- **Tabs for every type.** Animated textures, clips, meshes, effects, packfiles, tables, fonts, bitmaps and sounds open side
  by side; the menus, panes and keyboard shortcuts follow the tab in front, so the same key can mean different
  things in different document types.
- **Built for Alpine Faction.** Cairn targets [Alpine Faction](https://alpinefaction.com) 1.0 to 1.5, which
  practically every player runs: checks follow how Alpine Faction loads files, nothing is flagged merely for needing
  it, and the Alpine Faction version a feature needs is shown where it is known.
- **One File menu.** **File › New** has one entry per document type; **File › Import** and **File › Export** collect
  every module's importers and exporters. The module menus **Frames**, **Clip**, **Mesh**, **Effect**,
  **Packfile**, **Table**, **Font**, **Bitmap** and **Sound** appear with the matching document.
- **Shared previews.** The packfile module's previews (images, sounds, fonts, meshes, clips, effects, animated
  textures) also show the files a table names, and files are found the same way everywhere.
- **One settings dialog** with a shared game folder and search folders, then a page per module.
- **File associations per type.** The **File associations** settings page has a check box for each extension Cairn
  opens, `.vpp` included, and shows which program opens it now. Per Windows user, no administrator rights; where
  Windows protects a default you chose yourself, Cairn opens Windows' own chooser instead of overriding it.
- Light and dark themes, crash recovery, reopen closed tab, and a reload prompt when an open file changes on disk.

## Animated textures (ATX)

`.atx` is the declarative animated texture format of the [Alpine Faction](https://alpinefaction.com) patch.

- **Visual and text editing, always in sync.** Every change made in a panel is written into the `.atx` text as you
  watch, and anything you type updates the panels. Comments and formatting are preserved, and undo is one history
  across both.
- **Live linting** against what the engine actually does: missing images, frames that don't match frame 0, bad
  alpha masks, unknown tokens, names too long for the engine, and more. Every problem says what is wrong and how to
  fix it; most have a one-click fix.
- **Animated preview** that follows the game's own playback rules, with the alpha mask applied, optional
  target-format simulation, 3×3 tiling for seam checks and an alpha-only view.
- **Frames list** with thumbnails, drag reordering, multi-select and copy/paste.
- **Add frames** from disk, from a numbered sequence detected from any one of its files, or straight out of the
  game's `.vpp` archives with an image preview (**Frames** menu).
- **Bulk frame timing**: set, clear, scale, offset, distribute or ramp the timing of many frames at once, with a
  before/after preview.
- **Import VBM** (**File › Import**): convert a legacy animated `.vbm` into frames plus a ready-to-use `.atx`.
- **Source editor** with syntax highlighting, completion, hover documentation and find/replace.
- Built-in ATX format reference.

## Animations and meshes (RFA, V3C, V3M)

- **Engine-exact preview.** Clips play through a line-for-line port of the game's own sampler and blender (eased
  slerp, Bezier positions, weights and ramps, morph animation), on any `.v3c` with textures, skeleton, collision
  spheres and prop points. "Play as action over state" layers a clip over a looping state exactly as the game does.
- **Animations library** (left pane) of every clip and mesh the game can see, loose or inside `.vpp` archives:
  characters grouped by skeleton family with the clips the game's tables give them, and a filterable clip list.
  Double-click previews a clip on the open mesh (or a mesh under the open clip); Ctrl+double-click opens it in a
  tab of its own.
- **Timeline (dope sheet)** with box selection, drag to move or copy, scaling about the playhead, cut, copy and
  paste between clips (matched by bone name, also mirrored left/right), and key at the playhead.
- **New animation clip** (**File › New**) starts a clip from nothing for any character: every bone holds the
  character's stand pose (or its bind pose), keyed and ready to pose with the gizmos, as a state or an action.
- **Pose editing** in the viewport: rotate and move gizmos in local, parent or model space, auto-key or a layer
  edit that offsets the whole track, two-bone IK on hands and feet, and a ghost of the saved clip to compare against.
- **Inspectors** for the clip header, the selected bones (weight, presets, offsets) and the selected keys (time,
  Euler or quaternion rotation, eases, position and control points).
- **Clip tools** (**Clip** menu), each with a live preview and one undo step: trim, shift, retime, reverse, make
  loopable, resample, reduce keys, mirror, offset bones with falloff, root motion, bone lengths, conform to another
  skeleton, weights, version 7/8 conversion, morph stripping and normalise.
- **Retarget** a clip (**Clip › Retarget…**), or a whole class's clips in a batch (**Tools › Batch Retarget…**),
  onto another skeleton: automatic bone mapping (editable), presets for seated, standing / locomotion and
  rotation-only motion that keep feet on the target's ground and hands on a two-handed weapon, a live preview
  against the source, and a report of what IK held. Batch runs write the table lines for the new clips.
- **Mesh editing** for `.v3c` (**Mesh** menu): bones (rename, reparent, reorder, bind pose), collision spheres, prop
  points, materials and LOD distances, typed in the Structure tab or dragged in the viewport. Reordering bones
  conforms the open clips that use them.
- **glTF 2.0** export of meshes with any number of clips, and import of animations (as new clips) and meshes (as new
  `.v3c` or `.v3m`), using REDUX's conventions so files move between the two tools. Morph (vertex) animation is not
  exported.
- **Exporter and PlayStation 2 meshes**: `.v3d` and `.vcm` (the 3ds Max exporter's static and character meshes) and
  the PS2 version's `.rfm` and `.rfc` open read-only in 3D, characters with their skeleton, and **Convert…** turns
  them into `.v3m` / `.v3c` the way the game's mesh compiler did, with a report of what was approximated (next to
  the source, into a folder, or into a packfile with **Convert meshes...**).
- **Live linting** against what the engine actually does: wrong bone counts, keys outside the clip, lone rotation
  keys the engine reads past, names too long for its buffers, and more, most with a one-click fix.
- **Table awareness**: which lines of `entity.tbl`, `weapons.tbl` and `pc_multi.tbl` play a clip, the clips a
  character gets, and ready-made `+State:` / `+Action:` lines to copy.
- Built-in RFA and V3C format reference.

## Effects (VFX)

- **Viewing and playback.** Effects play at the game's 15 frames per second with a time scrubber and speed control,
  as a loop, once (one-shot, meshes disappear at the end) or holding the last frame, since the game's callers use
  all three.
- **Effects library** (left pane) of every `.vfx` the game can see, with a filter, key facts and the tables that
  use each one.
- **Outliner** of meshes, particle systems, dummies, lights and spacewarps by parent, plus the materials: rename,
  reparent, duplicate, delete, reorder, isolate, show or hide in the preview.
- **Inspectors** for the whole effect, the selected object (flags, timing, facing size, material slots, pivot,
  particle emission and flags, spacewarp links), its material and the selected keys.
- **Materials and animated textures**: a texture picker with preview over the game's archives and your folders,
  additive and fullbright materials, animated `.vbm` textures, opacity / self-illumination / mix tracks.
- **Particles preview**: particle systems simulated in the viewport, repeatable while scrubbing.
- **Timeline** with one row per object and its tracks (transform keys, per-frame transforms, material opacity and
  self-illumination): select, move and delete keys, insert keys at the playhead.
- **Gizmos** to move, rotate and scale objects with auto-key, local axes and a pivot mode; per-object timing (frame
  rate, start, frame count with hold, loop or resample fill); convert between static, per-frame, keyframed and
  morph animation; scrolling UVs.
- **Vertex editing**: click or box-select vertices and move, delete or merge them on the current morph frame or on
  all frames.
- **Creation** from starter templates (additive flash, scrolling beam, ring shockwave, particle fountain), built-in
  primitives (plane, facing quad, facing rod, disc, ring, cylinder, cone, sphere, box), particle systems, dummies,
  lights and spacewarps; from V3M/V3C geometry; from objects of other effects (with their materials); and from glTF.
- **glTF exchange** with REDUX and Blender: `.gltf` + `.bin` (the form REDUX reads) or GLB. A current-format
  effect exported by Cairn imports back identical, and REDUX's files import identically.
- **Problems list** with quick fixes: version ranges the game cannot load, faces that would crash the game, index
  ranges, missing textures, timing that does not add up, duplicate names, unknown spacewarps, unused materials.
- **Every stock format version** opens, plays and re-saves unchanged; older versions convert to the current one
  (one undo step) to be edited. Effects inside `.vpp` archives open read-only.
- **Effects settings page**: auto-key, local axes, pivot mode and pane widths.
- Built-in VFX format reference.

## Packfiles (VPP)

![A packfile open in Cairn, with a character mesh previewed](docs/packfiles.png)

- **Open any packfile at once.** Cairn reads only the list of entries, so even the largest stock packfiles open
  instantly. Filter by name or wildcard (`*.tga`), tick the types to show, and sort by name, type, size, state
  or info. The Info column gives a one-line summary of each file, such as a level's name, author and save time or an
  image's size and format.
- **Packfiles browser** (left pane, the first tab of the start page): every packfile in the game folder, its
  `user_maps` folders, each mod folder and your search folders, in collapsible folder groups with a filter;
  double-click to open.
- **Previews for everything the game uses**: images (including animated `.vbm`, `.dds` mip levels and an alpha
  view), text, tables, fonts, sounds (Ogg Vorbis, ADPCM, the stock `.aif` files and the PS2 `.vse`/`.vmu` play, with
  a waveform), and read-only 3D previews of meshes, clips, effects and animated textures. Previews take textures, frames and meshes from the same
  packfile first, so a level's custom content shows as the game will see it.
- **Level details** for `.rfl`: author, save time, properties, statistics, and every texture, mesh, sound and effect
  the level references, with the ones missing from the packfile and the game data highlighted.
- **Add, replace, rename, remove and reorder**, from the menu or by dragging files and folders in, with a choice
  when names clash. Every change is undoable until you save.
- **Extract** to a folder, next to the packfile, by dragging entries out to Explorer, or with **Ctrl+C**.
- **Edit and put back.** Open an entry in its Windows program or in a Cairn tab; when you save the copy, Cairn
  offers to update the packfile.
- **Safe saving** of packfiles of any size: Cairn writes a new file beside the old one, checks it, then swaps it
  in, with progress and cancel. An unmodified packfile re-saves byte for byte.
- **Live checks** against the game's limits: name lengths, case-insensitive duplicates, entry counts, the 2 GB
  limit, the packfile's own name length, and types the game does not load.
- **PlayStation 2 packfiles and PEG texture packs.** A packfile from the PS2 version says so and names its PS2-only
  types; **Convert to .tga...** on selected `.peg` entries lists their textures to tick and turns them into `.tga`
  files the PC game loads (animations as numbered frames plus an `.atx`), as one undoable change per conversion,
  keeping the larger version when a name is already in the packfile. A `.peg` opens on its own as a packfile of its
  textures, saved as a new PC `.vpp`; the `.peg` is never written. MPEG-2 compressed backgrounds are decoded to
  24-bit `.tga` (no alpha), or 32-bit with black as transparent, as the PS2 can (a setting, and a choice per
  conversion); the main menu's 150 numbered frames also get a looping `.atx` (a setting switches decoding off).
  PS2 sounds (`.vse`, `.vmu`) show their rate, length and loop in the Info column, play in the preview, and
  **Convert sounds...** turns selected sounds into WAV or Ogg Vorbis entries as one undoable change (or writes them
  to a folder). PS2 and exporter meshes (`.rfm`, `.rfc`, `.v3d`, `.vcm`) preview in 3D, and **Convert meshes...**
  turns selected ones into `.v3m` / `.v3c` entries the same way.
- Built-in packfile format and limits reference.

## Tables (TBL)

![A table open in Cairn, with a texture it names previewed](docs/tables.png)

- **A text editor that knows every table.** The stock tables, level text and Alpine Faction's own tables are
  described field by field in the order the game reads them; any other `.tbl` opens with syntax checks.
- **Highlighting and folding**: comments, section headers, field names, strings, numbers, file names (underlined)
  and names from other tables; fold each section and each entry. An **Outline** of sections and entries in the
  left pane, with a filter and error marks.
- **Live checks** against how the game reads tables: fields out of order or misspelt, wrong value types, values out
  of range, missing required fields, unclosed strings and sections, `//` comments in LF-only files, a byte-order
  mark, files that cannot be found and names no table defines. Most problems have a one-click fix.
- **Completion and hover**: the fields valid at the caret in the game's order, accepted values, file names of the
  right kind and entry names from other tables; hover for what a field does and where a name points.
- **Reference preview**: click a file name and see the texture, mesh, clip, sound or effect on the right, found
  the way the game finds it; a name from another table shows the entry that defines it.
- **Go to definition and find usages** across every table in the game data, your search folders and open tabs.
- **Compare with stock**: added, removed and changed entries and fields against the game's own table of the same
  name, with a jump to each change.
- **Safe saving**: the file's encoding and line endings are kept, and an unchanged table re-saves byte for byte.
- In a packfile, a `.tbl` previews highlighted and opens in a table tab.
- Built-in table syntax reference.

## Fonts (VF)

- **Every glyph at a glance**: the font's characters in a zoomable grid on a dark, checkered or light backdrop,
  with their codes; arrow keys move through them. All font versions and pixel formats: 4-bit monochrome, 8-bit
  indexed with its palette, and RGBA 4444 (as in the PlayStation 2 fonts).
- **Sample text drawn the way the game draws it**: your own text or every character, with the font's own spacing
  and kerning, zoomed up to 8× with sharp pixels, plus the size the game measures for it.
- **Inspector**: the selected glyph's character, width, spacing, kerning pairs and stored fields, and the font's
  version, format, height, character range, palette and the texture the game builds for it.
- **Checks** against how the game loads and draws fonts: damaged files, glyphs out of range, kerning pairs the game
  never applies, and fonts too big for the game's font texture.
- **Editing with undo**: glyph metrics, kerning pairs, pixels (pencil and eraser), the character range, height and
  pixel format; replace a glyph with a picture file or the clipboard.
- **Image sheets**: export every glyph to a PNG grid plus a JSON file of the metrics, edit them in any image editor
  and import them back (an unchanged sheet gives back the same bytes).
- In a packfile, a `.vf` previews with sample text and opens in a font tab; an unchanged font saves back byte for
  byte.

## Volition bitmaps (VBM)

- **The animation large**, playing at the file's own frame rate, with frame steps, zoom, an alpha view and a mip
  level picker; a strip of every frame; size, pixel format (1555, 4444, 565), version, frames, length and mip
  levels beside it.
- **Editing with undo**: the frame rate, replacing a frame or adding frames from TGA, PNG, JPG, DDS, BMP or VBM
  files (converted to the bitmap's pixel format, mip levels rebuilt), duplicating, removing, reordering and
  reversing frames, changing the pixel format or the mip levels. Untouched frames keep their exact bytes, and an
  unchanged bitmap saves byte for byte.
- **A frame strip you can work in**: drag frames to reorder them, drop image files to add frames, copy and paste
  frames between bitmap tabs, paste a picture from the clipboard as a frame; the playing frame is marked apart from
  the selection.
- **Images of another size** are resized your way (nearest, bilinear or high quality; stretch, keep aspect with
  transparent padding, or crop the centre) with a preview; **Smooth** / **Pixels** preview scaling.
- **New bitmaps from images**, with the pixel format suggested from their transparency, and defaults in
  **Settings › Volition bitmaps**.
- **Export frames** as TGA or PNG, and **Convert to ATX**: frame images plus an animated texture, opened in its own
  tab.
- **Checks**: files cut short, extra bytes, impossible mip counts, more frames than the game plays, an animation
  without a frame rate.
- In a packfile, a `.vbm` previews playing and opens in a bitmap tab.

## Sounds

- **The PlayStation 2 version's sounds**: `.vse` sound effects (mono, 11,025 to 44,100 Hz, both header layouts) and
  `.vmu` music (stereo), in the console's 4-bit ADPCM, decoded exactly as its sound chip does; damaged files open
  with what can be decoded and a Problems list. They are read and converted, never written.
- **PC sounds too**: `.wav` (PCM, Microsoft and IMA ADPCM), `.ogg` and `.aif` open in the same read-only tabs.
- **A waveform per channel** with a time ruler, zoom (mouse wheel) and scrolling, the play head and the loop
  region; play, pause, stop, loop (seamless, at the sound's own loop points) and volume.
- **Details**: format, codec, sample rate (and the rate the console really plays), channels, bit depth, duration,
  loop points, size, every header field and what is special about the format.
- **Convert to WAV or Ogg Vorbis**: WAV is 16-bit PCM with the loop in a `smpl` chunk; Ogg Vorbis is made with the
  Xiph.Org reference encoder (libvorbis) at a quality from q-1 to q10 (default q5), with the loop as
  `LOOPSTART`/`LOOPLENGTH` comments. Into the packfile the sound came from as a new entry (undoable), next to it, or
  into a folder; in a packfile, **Convert sounds...** does a whole selection as one undoable change. A report lists
  what the conversion approximates. Defaults in **Settings › Sounds**.
- In a packfile, `.vse` and `.vmu` entries get an Info line (`22,050 Hz mono, 1.4 s, PS ADPCM`), play in the
  preview with a waveform and open in a sound tab.

## Known limitations

- The effect preview approximates spacewarp forces, draws streak particles as round sprites and approximates the
  game's blending; check those in the game.
- Effects are edited per object: there is no whole-effect retime. Older effect versions must be converted before
  editing.
- Morph (vertex) animation in clips can be kept or stripped but not edited, retargeted or exported to glTF.
- A packfile's undo history starts again after each save. Levels (`.rfl`) show their details but no preview; a
  few other types show only their first bytes.
- Cairn does not make fonts from TrueType, and reads back only the image sheets it wrote.
- Tables Cairn has no description of are checked for syntax only, and the language columns of level text are not
  checked value by value. Compare with stock looks only in the game folder's packfiles.
- PS2 MPEG-2 backgrounds have no alpha of their own (black as transparent is an option), and the main menu
  animation's 30 frames a second is a guess.
- Windows only.

## Requirements

Windows 10 or 11, x64. The portable build needs nothing else; the small framework-dependent build needs the .NET 9
Desktop Runtime.

## Where game files are looked for

On first run Cairn finds your Red Faction install from Alpine Faction's settings (unless it imported a game folder
from ATX Workbench or RFA Workbench). Change the game folder and add your own search folders under **Tools ›
Settings…**; every module shares them. Files are found by name, searching in order:

1. the open file's own folder;
2. the search folders listed in Settings (loose files, then any `.vpp` inside them);
3. the Red Faction install: its root `.vpp` archives, then the `user_maps` folders.

A loose table (`entity.tbl`, …) in an earlier location overrides the archived one, as in the game. Files opened from
inside a `.vpp` are never written back there: Save asks where to write a copy, and no save or export ever defaults
to the game directory.

## Building from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build Cairn.sln -c Release
dotnet test

.\publish.ps1                      # portable single-file build      -> dist\
.\publish.ps1 -FrameworkDependent  # small build, needs the .NET 9 Desktop Runtime
```

| Path | Contents |
|---|---|
| `src/` | the shared libraries (formats, settings, asset lookup, UI, viewport) and the `Cairn.Shell` application |
| `modules/Atx`, `modules/Rfa`, `modules/Vfx`, `modules/Vpp`, `modules/Tbl`, `modules/Vf`, `modules/Vbm`, `modules/Snd` | one folder per module: core library, user interface and tests |
| `tools/` | icon and sample generators |
| `native/vorbis/` | the prebuilt Ogg Vorbis encoder DLL (libvorbis 1.3.7 + libogg 1.3.5) and `build.ps1`, which rebuilds it from Xiph.Org's checksum-verified releases with Visual Studio's C++ tools (not needed for a normal build) |
| `samples/` | generated test files for each module (see [samples/README.md](samples/README.md)) |

Tests that need the game's files pass without them.

## Changelog

See [docs/CHANGELOG.md](docs/CHANGELOG.md).

## License

MIT; see [LICENSE](LICENSE). Author: Chris "Goober" Parsons.
Third-party components (AvalonEdit, Tomlyn, NVorbis, BCnEncoder.NET, libvorbis and libogg) are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
and in **Help › About Cairn**.

## Credits

- Cairn is developed by Chris "Goober" Parsons.
- Uses [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) for the ATX source and table editors,
  [Tomlyn](https://github.com/xoofx/Tomlyn) for TOML, [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET) to
  write `.dds` images, [NVorbis](https://github.com/NVorbis/NVorbis) to play Ogg
  Vorbis sounds and the Xiph.Org Foundation's [libvorbis and libogg](https://xiph.org/vorbis/) to write them
  (`native/vorbis/build.ps1` rebuilds the DLL from their official source releases).
- Thanks to [rafalh](https://github.com/rafalh/rf-reversed), [wardd64](https://github.com/wardd64/UnityFaction),
  and [natarii](https://github.com/natarii) for format research.
