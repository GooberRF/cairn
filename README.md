# Cairn

One Windows app for Red Faction modding. Cairn has five modules, one for each kind of file: animated textures
(`.atx`), animation clips and skeletal meshes (`.rfa`, `.v3c`, with read-only `.v3m`), effect meshes (`.vfx`),
packfiles (`.vpp`), the archives the game loads everything from, and tables (`.tbl`), the text files that define
weapons, characters, items and the rest. Open any of them side by side in tabs, preview them the way the game plays
them, edit them, pack them, and catch what the game would get wrong before loading them in-game.

Cairn replaces ATX Workbench 1.1 and RFA Workbench 1.0. On first run it imports their theme, game folder, search
folders and recent files (and RFA Workbench's retarget profiles); nothing of theirs is modified.

![Cairn with an animated texture, a clip and an effect open](docs/screenshot.png)

New to the app? The [usage guide](docs/USAGE.md) walks through every module and the common jobs step by step.

## The suite

- **Tabs for every type.** Animated textures, clips, meshes, effects, packfiles and tables open side by side; the
  menus, panes and keyboard shortcuts follow the tab in front, so the same key can mean different things in
  different document types.
- **Built for Alpine Faction.** Cairn targets [Alpine Faction](https://alpinefaction.com) 1.0 to 1.5, which
  practically every player runs: checks follow how Alpine Faction loads files, nothing is flagged merely for needing
  it, and the Alpine Faction version a feature needs is shown where it is known.
- **One File menu.** **File › New** has one entry per document type; **File › Import** and **File › Export** collect
  every module's importers and exporters. The module menus **Frames**, **Clip**, **Mesh**, **Effect**,
  **Packfile** and **Table** appear with the matching document.
- **Shared previews.** The packfile module's previews (images, sounds, meshes, clips, effects, animated textures)
  also show the files a table names, and files are found the same way everywhere.
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
  view), text, sounds (Ogg Vorbis, ADPCM and the stock `.aif` files play, with a waveform), and read-only 3D
  previews of meshes, clips, effects and animated textures. Previews take textures, frames and meshes from the same
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

## Known limitations

- The effect preview approximates spacewarp forces, draws streak particles as round sprites and approximates the
  game's blending; check those in the game.
- Effects are edited per object: there is no whole-effect retime. Older effect versions must be converted before
  editing.
- Morph (vertex) animation in clips can be kept or stripped but not edited, retargeted or exported to glTF.
- A packfile's undo history starts again after each save. Levels (`.rfl`) show their details but no preview; fonts
  and a few other types show only their first bytes.
- Tables Cairn has no description of are checked for syntax only, and the language columns of level text are not
  checked value by value. Compare with stock looks only in the game folder's packfiles.
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
| `modules/Atx`, `modules/Rfa`, `modules/Vfx`, `modules/Vpp`, `modules/Tbl` | one folder per module: core library, user interface and tests |
| `tools/` | icon and sample generators |
| `samples/` | generated test files for each module (see [samples/README.md](samples/README.md)) |

Tests that need the game's files pass without them.

## Changelog

See [docs/CHANGELOG.md](docs/CHANGELOG.md).

## License

MIT; see [LICENSE](LICENSE). Author: Chris "Goober" Parsons.
Third-party components (AvalonEdit, Tomlyn, NVorbis) are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
and in **Help › About Cairn**.

## Credits

- Cairn is developed by Chris "Goober" Parsons.
- Uses [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) for the ATX source and table editors,
  [Tomlyn](https://github.com/xoofx/Tomlyn) for TOML and [NVorbis](https://github.com/NVorbis/NVorbis) to play Ogg
  Vorbis sounds in packfiles.
- Thanks to [rafalh](https://github.com/rafalh/rf-reversed), [wardd64](https://github.com/wardd64/UnityFaction),
  and [natarii](https://github.com/natarii) for format research.
