# Cairn usage guide

Cairn is a Windows editor for Red Faction modders. It opens the game's animated textures, animations, meshes,
effects, packfiles, tables, fonts, bitmaps and sounds in tabs of one window, and checks every file against what the
game will do with it. Cairn targets [Alpine Faction](https://alpinefaction.com), the patch practically every player
runs, and shows the Alpine Faction version a feature needs where it matters (for example "Alpine Faction 1.1+").

![Cairn with several documents open](screenshot.png)

## Start here

1. **Run Cairn.** It needs Windows 10 or 11 (64-bit). The portable build is a single program with nothing to
   install; the smaller build needs the .NET 9 Desktop Runtime.
2. **First start.** If you used ATX Workbench or RFA Workbench, Cairn takes over their theme, game folder, search
   folders, recent files and retarget profiles. The old apps keep working.
3. **Check the game folder.** Cairn finds Red Faction through Alpine Faction's settings. If it did not, set it under
   **Tools › Settings… › General** (see [Game folder and search folders](#game-folder-and-search-folders)). Without
   it the libraries stay empty and stock files cannot be found.
4. **Try a sample** from the `samples` folder, and [associate file types](#file-associations) if you want
   double-clicking in Explorer to open Cairn.

## I want to…

| I want to… | Go to |
|---|---|
| Understand how the game finds my files and how to ship a mod | [How the game finds your files](#how-the-game-finds-your-files), [Release a mod in a packfile](#release-a-mod-in-a-packfile) |
| Add a custom texture to a level | [Add a custom texture to a level](#add-a-custom-texture-to-a-level) |
| Make an animated texture | [Make a new animated texture](#make-a-new-animated-texture), [Make an animated texture for a level or mesh](#make-an-animated-texture-for-a-level-or-mesh) |
| Turn an old animated `.vbm` into an `.atx` | [Convert an old animated VBM](#convert-an-old-animated-vbm) |
| Make or edit a `.vbm` | [Make a new bitmap from images](#make-a-new-bitmap-from-images) |
| Convert textures to DDS | [Convert images to DDS](#convert-images-to-dds) |
| Look inside a packfile, or change a file in it | [Open a packfile and find files in it](#open-a-packfile-and-find-files-in-it), [Edit a file and put it back](#edit-a-file-and-put-it-back) |
| Check a level for missing files | [Read a level's details](#read-a-levels-details) |
| Find which tables or levels use a file | [Find what uses a file](#find-what-uses-a-file) |
| Edit `weapons.tbl` or `entity.tbl` without breaking the game | [Edit the game's tables safely](#edit-the-games-tables-safely), [Compare a modded table with the stock one](#compare-a-modded-table-with-the-stock-one) |
| Watch an animation on a character | [Preview a clip on a character](#preview-a-clip-on-a-character) |
| Make a new animation | [Make a new animation](#make-a-new-animation) |
| Move animations to another character | [Retarget animations to another character](#retarget-animations-to-another-character) |
| Edit a character mesh | [Edit a character mesh](#edit-a-character-mesh) |
| Work in Blender | [Import and export glTF](#import-and-export-gltf), [Build an effect from a glTF file](#build-an-effect-from-a-gltf-file) |
| Edit an effect | [Open and inspect a stock effect](#open-and-inspect-a-stock-effect) |
| Change a font's characters | [Replace a glyph with a picture](#replace-a-glyph-with-a-picture), [Edit the whole font in an image editor](#edit-the-whole-font-in-an-image-editor) |
| Get a sound out as WAV or Ogg | [Convert a sound to WAV or Ogg Vorbis](#convert-a-sound-to-wav-or-ogg-vorbis), [Convert many sounds in a packfile](#convert-many-sounds-in-a-packfile) |
| Use the PlayStation 2 version's content | [Convert the PlayStation 2 version](#convert-the-playstation-2-version), [Convert the PS2 version's textures for the PC game](#convert-the-ps2-versions-textures-for-the-pc-game), [Convert exporter and PS2 meshes](#convert-exporter-and-ps2-meshes) |
| Fix something that does not work | [Troubleshooting and FAQ](#troubleshooting-and-faq) |

## Contents

- [The Cairn window](#the-cairn-window)
  - [Tabs, panes and menus](#tabs-panes-and-menus) ·
    [The start page and the Packfiles browser](#the-start-page-and-the-packfiles-browser) ·
    [Open, save and close](#open-save-and-close) · [Game folder and search folders](#game-folder-and-search-folders) ·
    [File associations](#file-associations) · [Themes](#themes) ·
    [Recovery and files changed outside Cairn](#recovery-and-files-changed-outside-cairn) ·
    [Help and keyboard shortcuts](#help-and-keyboard-shortcuts) ·
    [Coming from ATX Workbench or RFA Workbench](#coming-from-atx-workbench-or-rfa-workbench)
- [Making a mod](#making-a-mod)
  - [How the game finds your files](#how-the-game-finds-your-files) ·
    [Release a mod in a packfile](#release-a-mod-in-a-packfile) ·
    [Add a custom texture to a level](#add-a-custom-texture-to-a-level) ·
    [Make an animated texture for a level or mesh](#make-an-animated-texture-for-a-level-or-mesh) ·
    [Find what uses a file](#find-what-uses-a-file) ·
    [Convert the PlayStation 2 version](#convert-the-playstation-2-version) ·
    [Edit the game's tables safely](#edit-the-games-tables-safely)
- [Animated textures (ATX)](#animated-textures-atx)
  - [Try the ATX samples](#try-the-atx-samples) · [Make a new animated texture](#make-a-new-animated-texture) ·
    [Change the timing of many frames](#change-the-timing-of-many-frames) ·
    [Convert an old animated VBM](#convert-an-old-animated-vbm) ·
    [Fix a frame whose image cannot be found](#fix-a-frame-whose-image-cannot-be-found) ·
    [Check a texture for seams and alpha](#check-a-texture-for-seams-and-alpha) ·
    [Good to know (animated textures)](#good-to-know-animated-textures) ·
    [Limitations (animated textures)](#limitations-animated-textures)
- [Animations and meshes (RFA, V3C, V3M)](#animations-and-meshes-rfa-v3c-v3m)
  - [What you can do](#what-you-can-do) · [Try it with the samples](#try-it-with-the-samples) ·
    [Browse the Animations library](#browse-the-animations-library) ·
    [Preview a clip on a character](#preview-a-clip-on-a-character) · [Edit poses and keys](#edit-poses-and-keys) ·
    [Use the clip tools](#use-the-clip-tools) ·
    [Check how an action blends over a state](#check-how-an-action-blends-over-a-state) ·
    [Make a new animation](#make-a-new-animation) ·
    [Retarget animations to another character](#retarget-animations-to-another-character) ·
    [Edit a character mesh](#edit-a-character-mesh) · [Import and export glTF](#import-and-export-gltf) ·
    [Convert exporter and PS2 meshes](#convert-exporter-and-ps2-meshes) ·
    [See which table lines play a clip](#see-which-table-lines-play-a-clip) ·
    [Fix the problems Cairn reports](#fix-the-problems-cairn-reports) ·
    [Get your work into the game](#get-your-work-into-the-game) · [Good to know](#good-to-know) ·
    [Limitations](#limitations)
- [Effects (VFX)](#effects-vfx)
  - [Open and inspect a stock effect](#open-and-inspect-a-stock-effect) ·
    [Change a material's opacity and texture](#change-a-materials-opacity-and-texture) ·
    [Retime an object](#retime-an-object) · [Add a facing quad that glows](#add-a-facing-quad-that-glows) ·
    [Start from a template](#start-from-a-template) ·
    [Build an effect from a glTF file](#build-an-effect-from-a-gltf-file) ·
    [Reuse geometry from a mesh or another effect](#reuse-geometry-from-a-mesh-or-another-effect) ·
    [Edit the vertices of a morph frame](#edit-the-vertices-of-a-morph-frame) ·
    [Rotate or scale around a different point](#rotate-or-scale-around-a-different-point) ·
    [Fix effect problems](#fix-effect-problems) ·
    [Round-trip an effect through Blender or REDUX](#round-trip-an-effect-through-blender-or-redux) ·
    [Edit an older effect](#edit-an-older-effect) · [Good to know (effects)](#good-to-know-effects) ·
    [Limitations (effects)](#limitations-effects)
- [Packfiles (VPP)](#packfiles-vpp)
  - [Open a packfile and find files in it](#open-a-packfile-and-find-files-in-it) ·
    [Preview what is inside](#preview-what-is-inside) · [Read a level's details](#read-a-levels-details) ·
    [Extract files](#extract-files) · [Edit a file and put it back](#edit-a-file-and-put-it-back) ·
    [Add files and folders](#add-files-and-folders) ·
    [Rename, replace and remove entries](#rename-replace-and-remove-entries) ·
    [Convert images to DDS](#convert-images-to-dds) · [Create a new packfile](#create-a-new-packfile) ·
    [Save a packfile safely](#save-a-packfile-safely) ·
    [Convert the PS2 version's textures for the PC game](#convert-the-ps2-versions-textures-for-the-pc-game) ·
    [Open a PEG texture pack on its own](#open-a-peg-texture-pack-on-its-own) ·
    [Good to know (packfiles)](#good-to-know-packfiles) · [Limitations (packfiles)](#limitations-packfiles)
- [Tables (TBL)](#tables-tbl)
  - [Open a table](#open-a-table) · [Find your way around a table](#find-your-way-around-a-table) ·
    [Fix problems with quick fixes](#fix-problems-with-quick-fixes) ·
    [Use completion and hover](#use-completion-and-hover) ·
    [Preview a file the table names](#preview-a-file-the-table-names) ·
    [Go to definition and find usages](#go-to-definition-and-find-usages) ·
    [Compare a modded table with the stock one](#compare-a-modded-table-with-the-stock-one) ·
    [Save a table safely](#save-a-table-safely) · [Good to know (tables)](#good-to-know-tables) ·
    [Limitations (tables)](#limitations-tables)
- [Fonts (VF)](#fonts-vf)
  - [Open a font](#open-a-font) · [See how text will look in the game](#see-how-text-will-look-in-the-game) ·
    [Inspect a glyph](#inspect-a-glyph) · [Edit a font and save it](#edit-a-font-and-save-it) ·
    [Change a glyph's width and spacing](#change-a-glyphs-width-and-spacing) ·
    [Paint a glyph's pixels](#paint-a-glyphs-pixels) ·
    [Replace a glyph with a picture](#replace-a-glyph-with-a-picture) · [Edit kerning](#edit-kerning) ·
    [Add or remove characters, change the height or the format](#add-or-remove-characters-change-the-height-or-the-format) ·
    [Edit the whole font in an image editor](#edit-the-whole-font-in-an-image-editor) ·
    [Good to know (fonts)](#good-to-know-fonts) · [Limitations (fonts)](#limitations-fonts)
- [Volition bitmaps (VBM)](#volition-bitmaps-vbm)
  - [Open a bitmap](#open-a-bitmap) · [Look at the frames](#look-at-the-frames) ·
    [Change the frame rate](#change-the-frame-rate) · [Replace a frame with an image](#replace-a-frame-with-an-image) ·
    [Fit images of another size](#fit-images-of-another-size) ·
    [Add, remove and reorder frames](#add-remove-and-reorder-frames) ·
    [Make a new bitmap from images](#make-a-new-bitmap-from-images) ·
    [Export frames as images](#export-frames-as-images) ·
    [Convert a bitmap to an animated texture](#convert-a-bitmap-to-an-animated-texture) ·
    [Good to know (bitmaps)](#good-to-know-bitmaps) · [Limitations (bitmaps)](#limitations-bitmaps)
- [Sounds (VSE, VMU, WAV, OGG)](#sounds-vse-vmu-wav-ogg)
  - [Open a sound](#open-a-sound) · [Play a sound and its loop](#play-a-sound-and-its-loop) ·
    [Zoom into the waveform](#zoom-into-the-waveform) ·
    [Convert a sound to WAV or Ogg Vorbis](#convert-a-sound-to-wav-or-ogg-vorbis) ·
    [Convert many sounds in a packfile](#convert-many-sounds-in-a-packfile) ·
    [Good to know (sounds)](#good-to-know-sounds) · [Limitations (sounds)](#limitations-sounds)
- [Troubleshooting and FAQ](#troubleshooting-and-faq)
  - [Game folder and libraries](#game-folder-and-libraries) ·
    [Missing files and textures](#missing-files-and-textures) · [Opening and saving](#opening-and-saving) ·
    [Sounds and previews](#sounds-and-previews) · [Animations and meshes](#animations-and-meshes) ·
    [Cairn itself](#cairn-itself) · [Where Cairn keeps its files](#where-cairn-keeps-its-files)
- [Glossary](#glossary)
- [Keyboard shortcuts](#keyboard-shortcuts)

Each chapter lists what you can do, walks through the common tasks, and ends with what is good to know and what
Cairn does not do. Full tables of menus, columns, problem codes and format limits are in the
[reference](REFERENCE.md).

## The Cairn window

Cairn is one window with a module per kind of file. Every file you open becomes a tab, and tabs of any type sit side
by side; the menus and panes change to suit the tab in front.

### Tabs, panes and menus

- **Tabs.** One per open file. An asterisk marks unsaved changes; the tooltip shows the full path, or the packfile a
  file came from. Opening a file that is already open switches to its tab. **Ctrl+Tab** and **Ctrl+Shift+Tab** move
  between tabs.
- **Left pane.** Belongs to the tab in front: the **Animations** library for clips and meshes, the **Effects**
  library for effects, the **File types** panel and **Packfiles** browser for a packfile, the **Outline** for a
  table. Other types have no left pane. **Ctrl+Shift+L** shows or hides it.
- **Bottom pane.** Panels for the tab in front, such as a timeline or the **Problems** list. **Ctrl+Shift+M** shows
  or hides it. Pane sizes and visibility are remembered.
- **Status bar.** Facts about the tab in front, such as its frame count or problem count. Click a problem count to
  open the problems.
- **Menus.** **File**, **Edit**, then the menu of the tab in front (**Frames**, **Clip**, **Mesh**, **Effect**,
  **Packfile**, **Table**, **Font**, **Bitmap** or **Sound**), **View**, **Tools** and **Help**. **File › New** has an
  entry per kind of file Cairn creates; importers and exporters are under **File › Import** and **File › Export**.

### The start page and the Packfiles browser

With no tab open, Cairn shows the start page: recent files and buttons to create or open a file. A file you opened
from inside a packfile is listed as "packfile › file"; clicking it opens the packfile and that file again. The left
pane then shows every library for browsing, with the **Packfiles** browser first.

The **Packfiles** browser lists every `.vpp` Cairn can see, grouped by folder: the game folder, its `user_maps`
folders, each folder of the game's `mods` folder, and your search folders. Hover a heading or a file for its full
path.

- Type in the filter box to find a packfile or folder by name.
- Click a heading's arrow (or select it and press **Left** or **Right**) to collapse or expand it.
- Double-click a packfile, or press **Enter**, to open it. Right-click for **Open**, **Show in Explorer** and
  **Copy path**.
- The refresh button lists the folders again; changing the game folder or search folders does the same.

### Open, save and close

- **Open.** **File › Open…** (**Ctrl+O**) takes any type Cairn knows, several at once. You can also drop files on the
  window or double-click them in Explorer once types are associated. A file opened from Explorer while Cairn runs
  opens as a new tab in the running window.
- **Files from packfiles.** A file opened from inside a `.vpp` (from a library, for example) is read-only in place:
  **Save** asks where to write a copy. To change the files inside a packfile, open the packfile itself and use
  **Open in Cairn** on the entry; see [Edit a file and put it back](#edit-a-file-and-put-it-back).
- **Save** (**Ctrl+S**), **Save As…** (**Ctrl+Shift+S**) and **Save All** write through a temporary file, so a failed
  save never leaves a half-written file. Cairn never changes what you did not edit: open a file and save it unchanged
  and you get the same bytes back.
- **Save As never starts in the game folder.** That folder holds the game's own files (and the game does not load
  loose files anyway; assets go in packfiles), so when the file's own folder (or an export's last folder) is the
  game folder or inside it, the dialog starts in the last folder you saved to elsewhere, or in Documents. You can
  still choose the game folder yourself.
- **Files with errors.** When the Problems list has errors, Cairn asks before saving; a packfile with errors is not
  saved until they are fixed.
- **Close.** **Close Tab** (**Ctrl+W** or **Ctrl+F4**) asks before discarding changes (**Save**, **Don't Save**,
  **Cancel**); closing the window asks once for every unsaved file. **Reopen Closed Tab** (**Ctrl+Shift+T**) brings back the last tab you closed, unsaved changes
  included.
- **Undo and redo** (**Ctrl+Z**, **Ctrl+Y** or **Ctrl+Shift+Z**) belong to each tab; the **Edit** menu names the step
  they will undo or redo.

### Game folder and search folders

**Tools › Settings… › General** holds the settings every module shares:

- **Red Faction folder**: the game install. **Auto-detect** finds it from Alpine Faction's settings; **Browse…**
  picks it yourself. The page says whether the folder looks like an install.
- **Search folders**: extra folders where Cairn looks for the textures, meshes, clips and tables your files name,
  such as your mod's work folder. **Add…**, **Remove**, **Move up** and **Move down** set them and their order.

For previews and checks, Cairn finds files by name, as the game does. It looks in the open file's own folder (or
packfile) first, then in your search folders (loose files before any `.vpp` in the same folder), then in the game: its packfiles and
its `user_maps` folders. Which copy the *game* uses is a different question; see
[How the game finds your files](#how-the-game-finds-your-files).

### File associations

**Tools › File Associations…** (also a page of **Tools › Settings…**) has a row per type Cairn opens. Tick **Open with
Cairn** to have double-clicking that type in Explorer open Cairn; untick it to give the type back to the program
Cairn replaced. **Opens now with** shows the current program. Nothing changes until you press **OK**, and changes
apply to your Windows account only.

If you chose a default for a type yourself in Windows, Windows protects it: Cairn adds itself to the type's **Open
with** list, and **Choose default…** opens Windows' chooser. More detail is in the
[reference](REFERENCE.md#file-associations-in-detail).

### Themes

**View › Theme** offers **System** (follow Windows), **Light** and **Dark**; the same choice is under **Tools ›
Settings… › General › Appearance** as **Follow Windows**, **Light** and **Dark**. The theme changes as you pick it.

### Recovery and files changed outside Cairn

- **Recovery.** Every 30 seconds Cairn keeps a copy of each file with unsaved changes. If Cairn closes with unsaved
  work, the next start offers **Recover unsaved work**: **Restore selected** opens the ticked files as unsaved tabs
  and deletes the rest, **Not now** keeps every copy for next time, **Discard all** deletes them.
- **Crashes.** If something goes wrong, Cairn shows the details and writes them to a crash log (see
  [Where Cairn keeps its files](#where-cairn-keeps-its-files)).
- **Changed on disk.** When an open file changes on disk, its tab says so. Choose **Reload** to take the file on
  disk, or **Keep mine**. If the file is deleted or moved, the tab keeps its contents and says so; **Save** writes it
  back.

### Help and keyboard shortcuts

**F1** opens the help topic for the tab in front, or the keyboard shortcut list when no tab is open. The **Help** menu
has each module's topics (format summaries and limits) and **Keyboard Shortcuts**, which lists every shortcut grouped
by document type.

Shortcuts belong to a document type: a key such as **Space** or **Ctrl+T** can mean one thing in an animated texture
and another in a clip, and the tab in front decides. Single keys without **Ctrl**, **Alt** or **Shift** are ignored
while you type in a text box. See [Keyboard shortcuts](#keyboard-shortcuts).

### Coming from ATX Workbench or RFA Workbench

Cairn replaces both apps. Their menus (**Frames**, **Clip**, **Mesh**) appear when a file of their type is in front,
and all settings are in **Tools › Settings…**. See [what moved](REFERENCE.md#coming-from-the-workbenches).

## Making a mod

This chapter covers the jobs that use several modules: getting files into the game, adding textures to a level,
finding what uses a file, converting the PlayStation 2 version and editing tables.

### How the game finds your files

- **By name only.** The game asks for a file by its bare name (`lava.tga`, `ult2_walk.rfa`), ignoring upper and lower
  case. Folders do not matter, and a packfile has no folders inside it. Two different files with the same name
  cannot both be used.
- **Spellings in tables and levels.** Tables name clips `.mvf` and the game loads the `.rfa`; a `.v3d` or `.vcm` name
  loads the `.v3m` or `.v3c`.
- **Texture names find other formats.** When a level or mesh asks for `lava.tga`, the game takes `lava.atx`,
  `lava.dds`, `lava.png`, `lava.jpg` or `lava.vbm` first if one exists (in that order; PNG and JPG need Alpine
  Faction 1.4.0+). So you can replace a texture with an animated texture or a DDS without touching the level or
  mesh, as long as the replacement is allowed to replace files (below).
- **Where the game loads packfiles from**, in this order: the stock packfiles in the game folder; then the packfiles
  directly inside `user_maps\projects` (Alpine Faction 1.1.0+), `user_maps\single` and `user_maps\multi`; then
  `client_mods`; then `mods\<name>` when the game is started with `-mod <name>` (the launcher's mod choice does
  this). Sub-folders are not searched. When two packfiles hold the same name, the one loaded later wins if it may
  replace files.
- **Who may replace a stock file.**
  - A mod in `mods\<name>` may replace anything, tables included. This is how total conversions work.
  - `client_mods` packfiles may replace stock files, but of the tables only `strings.tbl`, `hud.tbl`,
    `hud_personas.tbl`, `personas.tbl`, `credits.tbl`, `endgame.tbl`, `ponr.tbl` and HUD message files
    (`*_text.tbl`).
  - Level packfiles in `user_maps` may replace each other's files, but not the game's, unless the player turns on
    **Allow clientside mods in legacy directories** in the Alpine Faction launcher's options; even then, only the
    tables `client_mods` may replace. Give a level's own textures, sounds and meshes **new names**.
  - Packfiles added after the game has started (such as levels downloaded while playing) cannot replace anything
    already loaded.
- **Only packfiles count.** The game loads textures, meshes, clips, sounds and other assets from packfiles, never
  from loose files, so put everything you make into a `.vpp` in one of the folders above. (Cairn also never offers
  the game folder as a save location, so you don't overwrite the game's own files by accident.)

The game's own limits on names and sizes are in the [reference](REFERENCE.md#packfile-limits).

### Release a mod in a packfile

1. Choose **File › New › Packfile…** and drag your files onto it (see
   [Create a new packfile](#create-a-new-packfile)). A level packfile holds the `.rfl` and every custom file it
   uses; a mod packfile holds the files it replaces or adds.
2. Check the problems (click the count in the status bar). Cairn warns about types the game does not load (`.psd`,
   PlayStation 2 files), texture, sound and font names longer than 31 characters (the game cuts them off), and
   names that clash.
3. Keep the packfile's own file name to 31 characters including `.vpp`, or the game does not load it.
4. Save it outside the game folder, then copy it where it belongs:
   - a single-player level: `user_maps\single`; a multiplayer level: `user_maps\multi`;
   - a mod: `mods\<ModName>\`, started with `-mod <ModName>`;
   - a client-side replacement (textures, sounds, HUD text): `client_mods`.
5. For a level, select the `.rfl` in the packfile and read its references (see
   [Read a level's details](#read-a-levels-details)): nothing should be **missing**.

### Add a custom texture to a level

1. **Make the image.** Any of `.tga`, `.dds`, `.png` or `.jpg`. Textures on level geometry should have sides that are
   powers of two (64, 128, 256…). Use a 32-bit `.tga` (or a `.dds` with alpha) when it needs transparency.
2. **Name it.** At most 31 characters including the extension, and not the name of a stock texture.
3. **Use it in the level editor.** Put it in the game's `user_maps\textures` folder while you build the level;
   Cairn looks there too, so the level's previews and checks find it.
4. **Ship it.** Add the image to the level's packfile next to the `.rfl`. Select the `.rfl` and check its
   **References** group: each texture should say **in this packfile** or **in the game data**. Anything **missing**
   is listed first.
5. **Optional: convert to DDS.** Select the images in the packfile and choose **Packfile › Convert images to DDS…**
   (**Ctrl+Shift+D**). DDS files are smaller and carry mipmaps, and the game loads `name.dds` in place of a requested
   `name.tga`, so the level needs no change. See [Convert images to DDS](#convert-images-to-dds).

A packfile's previews take textures from the same packfile first, so a mesh or effect in your level packfile shows
its custom textures before the packfile is anywhere near the game.

### Make an animated texture for a level or mesh

Animated textures (`.atx`, Alpine Faction 1.4.0+) list frame images and their timing.

1. Make the frames as images, numbered (`lava_00.tga`, `lava_01.tga`…).
2. Choose **File › New › Animated texture…**, add the frames and set the timing (see
   [Make a new animated texture](#make-a-new-animated-texture)). Save the `.atx` next to the frames.
3. **Name the `.atx` after the texture the faces use.** If the level applies `lava.tga`, save `lava.atx`: the game
   takes the `.atx` first and plays it. Nothing in the level or mesh needs to change.
4. Add the `.atx` and every frame (and its alpha mask, if any) to the packfile. The packfile's preview plays the
   animation using frames from that packfile, so a missing frame shows before you ship.

An old animated `.vbm` converts in one step; see [Convert an old animated VBM](#convert-an-old-animated-vbm).

### Find what uses a file

- **Tables.** In a table tab, put the caret on a file name or an entry name and press **Shift+F12** (**Table › Find
  Usages**): the **Usages** panel lists every use in every table Cairn knows. Clicking a file name in a table also
  shows, below its preview, which tables use it. See
  [Go to definition and find usages](#go-to-definition-and-find-usages).
- **Levels.** Select a `.rfl` in a packfile: the **References** group lists every texture, mesh, clip, effect, sound
  and music file it names, and where each was found.
- **Animations.** The **Animations** library and a clip's **Table usage** panel show which characters and table
  entries play each clip (see [See which table lines play a clip](#see-which-table-lines-play-a-clip)).
- **Effects.** The **Effects** library lists the tables that use each effect; type a table's name in its filter.

### Convert the PlayStation 2 version

The PS2 version's packfiles open in Cairn like any other, but most of what they hold is in PS2 formats the PC game
does not load. Open the PS2 `.vpp`; a bar above the list says what can be converted. Work through it in this order:

1. **Textures.** Select the `.peg` texture packs and choose **Convert to .tga…**. See
   [Convert the PS2 version's textures for the PC game](#convert-the-ps2-versions-textures-for-the-pc-game).
2. **Meshes.** Select the `.rfm` and `.rfc` meshes and choose **Convert meshes…**; they become `.v3m` and `.v3c`,
   using the `.tga` names from step 1. See [Convert exporter and PS2 meshes](#convert-exporter-and-ps2-meshes).
3. **Sounds.** Select the `.vse` and `.vmu` sounds and choose **Convert sounds…**; they become `.wav` or `.ogg`. See
   [Convert many sounds in a packfile](#convert-many-sounds-in-a-packfile).
4. **Save As** a new packfile, so the original stays as it was.

PS2 levels cannot be converted: the PC game cannot use them.

### Edit the game's tables safely

Tables (`weapons.tbl`, `entity.tbl` and the rest) decide what weapons, characters and items are. The game reads them
strictly: a misplaced field stops it with an error box.

1. **Start from a copy.** Open the stock table from its packfile (`tables.vpp`) with **Open in Cairn**, or with **Go
   to Definition** from another table, then **File › Save As…** into your mod's work folder. Never change the stock
   packfiles.
2. **Edit with the checks on.** Cairn checks every line as you type, with quick fixes for most problems (see
   [Fix problems with quick fixes](#fix-problems-with-quick-fixes)). Clear every error before you test.
3. **See what you changed.** **Table › Compare with Stock** lists every entry and field you added, removed or
   changed (see [Compare a modded table with the stock one](#compare-a-modded-table-with-the-stock-one)).
4. **Ship it where the game reads it.** A table replaces the stock one completely; there is no merging.
   `weapons.tbl`, `entity.tbl` and most others work only from a mod (`mods\<name>`). `strings.tbl`, `hud.tbl` and
   the other tables listed under [How the game finds your files](#how-the-game-finds-your-files) also work from
   `client_mods`. A table with a new name, such as a level's own `<level>_info.tbl`, needs no permission: put it in
   the level's packfile.

The animation module writes ready-made table lines for new clips; paste them into your copy of `entity.tbl` in a
table tab (see [Get your work into the game](#get-your-work-into-the-game)). The full rules the game follows are in the
[reference](REFERENCE.md#how-the-game-reads-tables).

## Animated textures (ATX)

An `.atx` file is Alpine Faction's animated texture (1.4.0+): a short text file that lists frame images, their timing
and options such as an alpha mask.

An animated texture tab has three parts that always agree: the **frames list** with thumbnails, the **preview**, and
the **source editor** with the `.atx` text. A change in any of them shows in the others at once, your comments and
formatting are kept, and one undo history covers all three. The **Problems** panel sits inside the tab.

**What you can do**

- Build an animated texture from images on disk or in the game's packfiles.
- Reorder frames and change the timing of one frame or many at once.
- Turn an old animated `.vbm` into an `.atx` with its frames.
- Preview it the way the game plays it, tiled to check seams, or as alpha only.
- Edit the text directly, with completion, hover help and quick fixes.
- Find and fix missing frames and other problems before the game does.

### Try the ATX samples

1. Open `samples/atx/hazard_strip.atx` (**File › Open…**). The preview plays the strip; the frames list shows its
   eight frames.
2. Open `samples/atx/broken_example.atx`. The Problems panel lists what is wrong with it and how to fix each one.

### Make a new animated texture

1. Choose **File › New › Animated texture…**.
2. Add frames:
   - **Frames › Add Frames › Browse…** (**Insert**) picks images on disk;
   - **Frames › Add Frames › From VPP…** (**Ctrl+Shift+V**) picks images from the game's packfiles, with a preview;
   - **Frames › Add Sequence…** finds a numbered run of images from any one of them (**From files**), or makes the
     names from a pattern (**From pattern**).
3. Put the frames in order: drag them, or use **Move Up** / **Move Down** (**Alt+Up** / **Alt+Down**), **Reverse
   Selection** and **Sort Selection by Name**.
4. Set the timing (next section) and watch the preview.
5. Clear the Problems panel and save (**Ctrl+S**). Save the `.atx` next to its images, or make sure the images are in
   a search folder.

To use it in a level or on a mesh, see
[Make an animated texture for a level or mesh](#make-an-animated-texture-for-a-level-or-mesh).

### Change the timing of many frames

1. Select the frames (**Ctrl+A** selects all).
2. Choose **Frames › Bulk Frame Timing…** (**Ctrl+T**).
3. Set, clear, scale, offset, distribute or ramp the timing. The dialog shows the timing before and after; **OK**
   applies it as one undo step.

For one frame, right-click it › **Edit frame time…**.

### Convert an old animated VBM

1. Choose **File › Import › Import VBM…** (**Ctrl+Shift+I**) for a `.vbm` on disk, or **File › Import › Import VBM
   from VPP…** for one in the game's packfiles.
2. Pick the folder and names. Cairn writes the frames as images and opens a ready-to-use `.atx` that lists them.
   Check the timing and save it.

A `.vbm` open in its own tab has **Convert to ATX…**, which does the same for the bitmap as it is in the tab (see
[Convert a bitmap to an animated texture](#convert-a-bitmap-to-an-animated-texture)).

### Fix a frame whose image cannot be found

The frames list marks the frame and the Problems panel names the missing file. Put the image in the `.atx` file's
folder or a search folder, or select the frame and choose **Frames › Locate File…** to pick it.

### Check a texture for seams and alpha

The preview's options show the texture tiled 3×3 to reveal seams, the alpha channel alone to check the mask, and a
simulation of the game's texture format to show roughly what it does to the colours. **Space** plays and pauses.

### Good to know (animated textures)

- Frames are stored as bare file names, never paths. Cairn finds them in the `.atx` file's folder, your search
  folders, the game's packfiles and its `user_maps` folders (see
  [Game folder and search folders](#game-folder-and-search-folders)).
- Every frame should match the first frame's size; the Problems panel says when one does not.
- Most problems have a one-click fix: right-click the underlined text, or use the button in the Problems panel.
- **Tools › Settings… › Animated textures** chooses whether new files start minimal or with an explanatory comment
  above each setting, and what the preview shows behind a frame (checkerboard, black, white or a colour of yours).
- Hover a keyword in the source editor for what it does. **Ctrl+E** moves the focus to the source editor.
- To ship the `.atx`, put it and all its images in a packfile (see
  [Release a mod in a packfile](#release-a-mod-in-a-packfile)). An `.atx` inside a packfile previews with frames from
  the same packfile, so you can check the set is complete.
- **Help › Animated textures › ATX format reference** (or **F1**) has the full `.atx` format. The frames list and menu commands are listed in
  the [reference](REFERENCE.md#atx-frames-list-and-menus).

### Limitations (animated textures)

- The format simulation approximates the game's texture conversion; the game is the final check.
- Cairn does not paint or edit frame images; edit them in an image editor.

## Animations and meshes (RFA, V3C, V3M)

Watch, fix, make and retarget character animations (`.rfa`), edit character meshes (`.v3c`), look at static meshes
(`.v3m`), and swap both with Blender through glTF.

### What you can do

- [Preview any clip on any character](#preview-a-clip-on-a-character) as the game plays it, and see which clips the
  game's tables give each character.
- [Pose bones and edit keys](#edit-poses-and-keys), and [trim, retime, loop, mirror or tidy a clip](#use-the-clip-tools).
- [Check how an action blends over a state](#check-how-an-action-blends-over-a-state), such as a reload over a walk.
- [Make a new animation](#make-a-new-animation), in Cairn or in Blender.
- [Retarget animations to another character](#retarget-animations-to-another-character), one clip or a whole set.
- [Edit a character mesh](#edit-a-character-mesh): collision spheres, prop points, textures, levels of detail and
  bones.
- [Import and export glTF](#import-and-export-gltf) to animate or model in Blender, including
  [rigging a new mesh to a stock skeleton](#rig-a-new-mesh-to-a-stock-skeleton).
- [Convert exporter and PS2 meshes](#convert-exporter-and-ps2-meshes) (`.v3d`, `.vcm`, `.rfm`, `.rfc`) to
  `.v3m`/`.v3c`.
- [See which table lines play a clip](#see-which-table-lines-play-a-clip) and copy ready-made `+State:` and
  `+Action:` lines.
- [Fix the problems Cairn reports](#fix-the-problems-cairn-reports) before you start the game.

Static meshes (`.v3m`) open read-only. This module doesn't change tables: it writes lines for you to paste, and the
[Tables](#tables-tbl) module edits them. Packing files into a `.vpp` is done in [Packfiles](#packfiles-vpp).

**How clips and meshes fit together.**

- A **clip** (`.rfa`) is one animation: a walk, a reload, a death, a seated driver. The tables spell clips `.mvf`.
- A **character mesh** (`.v3c`, spelt `.vcm` in the tables) has a skeleton of up to 50 bones, each with a number.
  A clip stores one track per bone *number*, not per name, so it only plays correctly on meshes with the same bones
  in the same order. Meshes that share a skeleton form a **skeleton family**; any clip made for one plays on all of
  them. Cairn shows bone names by borrowing them from the mesh you preview a clip on.
- Clips store **keys**: a **rotation key** turns a bone relative to its parent, a **position key** places it (and so
  sets its length). A bone with no position keys collapses onto its parent's joint.
- Time is shown in **frames**, 30 per second; stock clips start at frame 1. Click the time readout under the
  viewport to switch to seconds or ticks (4800 per second).
- The tables give each character **states** and **actions**. A state is a base animation that loops (stand, walk,
  crouch, seated in a jeep). An action plays once on top of it (fire, reload, flinch, death). Each bone of an action
  has a **weight** from 0 to 10: 10 replaces the state on that bone, 5 shares it, 0 leaves it to the state. **Ramp
  in** and **ramp out** fade the action in and out. States ignore ramps.

More terms (ease, control points, bind pose, LOD, prop point, collision sphere, morph data, IK) are in the
[concepts list](REFERENCE.md#clip-and-mesh-concepts).

### Try it with the samples

The `samples\rfa` folder that comes with Cairn holds a small 15-bone figure (`sample_figure.v3c`) with its texture,
three clean clips (`sample_figure_idle.rfa`, `sample_figure_walk.rfa`, `sample_figure_wave.rfa`) and a deliberately
broken one (`sample_figure_broken.rfa`). You can follow most of this chapter with them, without the game.

1. Choose **Tools › Settings…**, and on the **General** page under **SEARCH FOLDERS** press **Add…** and pick the
   `samples\rfa` folder. Press **OK**. The library now lists the figure and its clips.
2. Open `sample_figure_walk.rfa` (double-click it in the library's **Clips** tab, or **File › Open…**).
3. In the viewport header, check that **Preview mesh** shows `sample_figure.v3c`. If another 15-bone mesh was
   picked, choose `sample_figure.v3c` from the box; Cairn remembers your pick for that clip.
4. Click the viewport and press **Space** to play.

Retargeting needs two different skeletons, so its walk-through uses stock characters.

### Browse the Animations library

The **Animations** pane on the left lists every clip and mesh Cairn can find in the open file's folder, your search
folders and the game (see [The Cairn window](#the-cairn-window) for the game folder and search order). If it is
empty, set the game folder or add a search folder there.

- The **Meshes** tab groups characters by skeleton family ("miner family (9)", with the bone count). Expand a
  character to see the clips the tables give it, grouped by class and table, each with its slot ("state stand",
  "action fire (12mm)"). A clip a table names but no folder has is shown in grey italics. Static and unreadable
  meshes are under **Static meshes**.
- The **Clips** tab lists every clip with its length, bone count and a **morph** badge for clips with vertex
  animation. **Compatible with the active mesh** shows only clips that fit the tab in front.
- The filter box takes part of a name or a wildcard such as `ult2_*.rfa`.
- **Double-click** (or **Enter**) previews on the tab in front where it can: a clip plays on the mesh tab in front,
  a mesh becomes the preview mesh of the clip tab in front. Otherwise it opens a new tab. The line under the
  library says what double-click will do right now.
- **Ctrl+double-click**, **Ctrl+Enter** or a **middle-click** always opens a new tab. To make double-click always
  open a tab, choose **Always open in a new tab** under **Tools › Settings… › Animations and meshes › LIBRARY**.
- Drag an entry onto the viewport to preview it there.
- Right-click an entry for **Open in new tab**, **Preview on this mesh** / **Preview this clip**, **Open containing
  folder**, **Extract to…**, **Copy name**, **Retarget…** (clips) and **New Clip for This Mesh…** (characters).
- After copying new files in, press the library's ⟳ button or choose **Tools › Refresh Library**.

### Preview a clip on a character

*You want to see how a stock clip moves, or which clips a character has.*

1. In the library's **Meshes** tab, double-click a character, for example `ult2_guard.v3c`. It opens posed in a
   suitable clip (one the tables give it, or a stand clip); the **Preview clip** box in the viewport header shows
   which.
2. Click the viewport and press **Space** to play. Step with **Left**/**Right**, or drag along the time bar.
3. Expand the character in the library: under it are the clips the tables give it. Double-click one to play it on
   the mesh tab in front.
4. To edit a clip, open it in its own tab: **Ctrl+double-click** it, middle-click it, or right-click › **Open in
   new tab**.

A clip tab plays on a **preview mesh** you pick in the viewport header (or with **Clip › Choose Preview Mesh…**).
The list offers the meshes the tables play the clip on first, then meshes with the same bone count, then the rest
marked "does not fit". Cairn remembers your pick for each clip. If the bone counts differ, the viewport says so: the
game would play the clip just as wrongly on that mesh. A mesh tab's **Preview clip** box works the same way and has
a **Bind pose** entry for no clip. Neither choice is saved into the file.

Useful while looking:

- **Camera**: left-drag orbits, middle-drag (or **Shift**+left-drag) pans, the wheel zooms. **F** frames the
  selected bones or everything; **1**, **3** and **7** give front, side and top views; **5** switches perspective.
- The viewport toolbar shows or hides the skeleton, bone names (**Aa**), the grid, collision spheres, prop points
  and the root's path, and switches between the clip and the bind pose. The eye button picks textured, flat or
  hidden mesh, full bright and the background.
- The speed box under the viewport plays from 0.1× to 4×.
- **Clip › Compare With…** (**Ctrl+Alt+G**) plays a second clip as a ghost skeleton in step with this one, for
  example a stock original beside your edit. The chip in the viewport header hides it or stops comparing.
- The **Table usage** tab in the bottom panel lists every table line that plays the clip; see
  [See which table lines play a clip](#see-which-table-lines-play-a-clip).

Every control is described in the reference: [viewport](REFERENCE.md#animation-viewport),
[transport](REFERENCE.md#transport-and-layered-preview) and [library](REFERENCE.md#animations-library).

### Edit poses and keys

Every edit is one step in the tab's undo history (**Ctrl+Z**, **Ctrl+Y**), and the **Edit** menu names the step it
will undo. Nothing is written until you save. A clip opened from inside a `.vpp` asks where to save a copy.

Pose editing needs a preview mesh that fits the clip, because it needs the bone names and hierarchy.

#### Pose bones in the viewport

*An elbow clips through the body, the head looks the wrong way, a hand misses the weapon.*

1. Click the joint of the bone to change (**Ctrl+click** adds more bones). Its row comes into view in the timeline.
2. Press **E** for the rotate tool, or **W** for the move tool (only the root and bones whose position is animated
   can move). Choose the axes in the space box next to the tools: **Local**, **Parent** or **Model**.
3. Decide how the change applies with the **Key** toggle in the viewport toolbar:
   - **Key on** (auto-key, the default): the change is written as a key at the playhead, for the selected bones
     only. The rest of the motion stays as it was. Use it to fix one moment.
   - **Key off** (layer edit): the same change is added to *every* key of the bone, so the whole animation shifts.
     Use it for something wrong throughout, like a head tilted the whole time. The small arrow next to **Key**
     limits the change to a time range that fades in and out.
4. Drag a ring (rotate) or an arrow (move). Hold **Ctrl** to snap to 5° or 1 cm; **Esc** cancels the drag. Each
   drag is one undo step.
5. For hands and feet, turn on **IK**, choose the move tool and drag the hand or foot itself: the arm or leg bends
   to follow while the shoulder or hip stays put. IK writes keys, so it needs **Key** on.

The badge at the bottom left of the viewport always says what a drag will do ("Auto-key — keys hand-l at the
playhead (12 f)") or why there is no gizmo: no fitting preview mesh, the bind pose showing, a clip tool open, no
bone selected, or a bone that cannot move. IK works on the stock humanoid rigs and on any skeleton whose bone names
say upper arm, forearm, hand, thigh, shin and foot on each side.

For an exact offset typed as numbers, use **Clip › Offset Bone…** (**Ctrl+Alt+F**): it does the same as a layer
edit. Tool details are in [pose editing tools](REFERENCE.md#pose-editing-tools).

#### Work with keys in the timeline

The **Timeline** tab of the bottom panel shows every key of every bone: rotation keys as diamonds, position keys as
squares, the bone's weight at the right of its row and a coloured dot on bones with a problem.

- **Select** keys by clicking (**Ctrl** toggles, **Shift** adds) or by dragging a box on empty space. Click a mark
  in the **All keys** row to select every key at that time; double-click a row name for all keys of a bone.
- **Move** selected keys by dragging (they snap to frames; hold **Alt** for free). **Ctrl+drag** copies them.
  Drag a grip on the ruler to scale the selection in time about the playhead.
- **Delete** with **Del**. **Copy**, **cut** and **paste at the playhead** with **Ctrl+C**, **Ctrl+X**, **Ctrl+V**;
  keys paste into any clip in any tab, matched by bone name. **Ctrl+Shift+V** pastes mirrored onto the left/right
  partner bones.
- **K** keys the selected bones where they are at the playhead, to hold a pose or give you keys to edit.
- Drag the start and end triangles on the ruler to change where the clip starts and ends.
- Double-click a key to open it in the **Key** inspector on the right.

Deleting every position key of a bone makes it collapse onto its parent; the status bar and Problems tab warn you.
All mouse actions and keys are listed under [timeline](REFERENCE.md#animation-timeline).

#### Fine-tune a key

The **Key** inspector edits the keys selected in the timeline:

- **ROTATION**: angles in degrees (or the quaternion), relative to the parent bone.
- **EASES**: **Ease in** and **Ease out** make the bone arrive or leave gently, from 0 % (even speed) to 100 %. A
  small curve shows the effect.
- **POSITION** and **CONTROL POINTS** for position keys: the control points shape the path between keys.
  **Auto (linear)** gives straight segments at even speed; **Auto (smooth)** passes smoothly through each key.

The **Bone** inspector sets the selected bones' weight (**0**, **5**, **10**, or to the **Upper body**, **Lower
body** or **All bones**) and shows whether the clip's bone length differs from the mesh. The **Clip** inspector
holds the start, end, ramps and version. Every field has a tooltip saying what the game does with it; all fields
are listed under [inspectors](REFERENCE.md#clip-bone-and-key-inspectors).

#### Clean up a pop

*A bone jumps at one moment, often at the end of a clip or where two halves were joined.*

1. Play slowly (set the speed to 0.25×, or step with **Left**/**Right**) and click the joint that jumps.
2. Look at the **Problems** tab first: a pop is often a reported problem with a one-click fix (a lone rotation key,
   a segment the game snaps instead of turning, control points left at zero, a key outside the clip).
3. Otherwise pick the fix that suits the cause:
   - A stray key: select it in the timeline and press **Del**, or drag it to a better time.
   - A key with the wrong pose: double-click it and correct it in the **Key** inspector, or pose the bone with
     **Key** on.
   - A sudden jump at a key: in the **Key** inspector's **EASES**, a negative ease is shown in orange; set it to 0 %.
   - The end does not match the start of a looping clip: [make it loop](#make-a-clip-loop).
   - Many messy keys: **Clip › Reduce Keys…** or **Clip › Resample (Bake)…**.
4. Turn on **Ghost of the saved clip** (viewport eye button) to compare your edit with the version on disk.

### Use the clip tools

The **Clip** menu's tools each open a dialog that previews the result live in the main viewport (a **Preview**
badge shows in its header) and lists **WHAT WILL CHANGE**. Nothing changes until you press **OK**, which applies it
as one undo step.

#### Trim a clip

1. Optionally select keys in the timeline that span the part to keep.
2. Choose **Clip › Trim / Crop to Range…** (**Ctrl+Alt+T**).
3. Set **From** and **To**. **From playhead** and **To playhead** copy the playhead's time; **Whole clip** and
   **Selected keys** reset them.
4. Press **OK**. What stays plays exactly as before, and ramps that no longer fit are shortened.

#### Make a clip faster or slower

1. Choose **Clip › Retime…** (**Ctrl+Alt+R**).
2. **Scale by** a percentage (200 % is twice as long, half the speed) or **Set length to** an exact duration.
3. Under **SCALE ABOUT**, choose what stays put: **Start**, **Playhead** or **End**. Press **OK**.

To move a clip in time without changing its speed, use **Clip › Shift in Time…** (**Ctrl+Alt+H**); **Start at
1 f** lines it up with frame 1, as stock clips are.

#### Make a clip loop

*A walk or idle hitches when it starts again.*

1. Choose **Clip › Make Loopable…**.
2. Under **BLEND**, choose **The end blends into the first pose** (the usual choice) or **The start blends out of
   the last pose**, and set the **Window**: how long the cross-fade lasts.
3. For a walk or run that travels, keep the root's travel on the axis it moves along (**Keep Z (forward)** is
   ticked for you when the root travels forward), so the character does not slide back.
4. The summary shows the seam (the largest jump from the last pose to the first) before and after. Press **OK**.

The root travel options need a fitting preview mesh; the blend works without one.

#### Other clip tools

| Tool | Use it to |
|---|---|
| **Reverse…** (**Ctrl+Alt+V**) | Play the clip backwards. |
| **Recompute Start/End…** | Set start and end to the first and last key after edits. |
| **Resample (Bake)…** (**Ctrl+Alt+B**) | Replace messy keys with evenly spaced ones, for example after an import. |
| **Reduce Keys…** (**Ctrl+Alt+D**) | Drop keys the motion does not need, within a tolerance. Makes baked clips easier to edit. |
| **Mirror Left/Right…** | Make a left-handed version: every bone takes its partner's motion, reflected. |
| **Offset Bone…** (**Ctrl+Alt+F**) | Add a typed rotation or move to every key of the selected bones, over the clip or a range. |
| **Remove / Scale Root Motion…** | Make the clip play in place, or change how far it travels. |
| **Set Bone Lengths…** | Stop a clip stretching the character, using the stand clip's bone lengths (problem RFA023). |
| **Conform to Skeleton…** | Re-lay a clip out for a mesh with the same bones in a different order, or a few extra or missing bones. For a really different rig, retarget. |
| **Set Weights…** | Set how strongly an action drives each bone (all, upper body, lower body, selected). |
| **Normalise…** | Repair structural problems in clips from other tools, clearing several Problems at once. |
| **Convert to Version 7 / 8**, **Strip Morph Data** | Change the format version, or remove vertex animation that only fits one mesh. |

Every tool's options are in [clip tools](REFERENCE.md#clip-tools) and
[versions and morph data](REFERENCE.md#versions-and-morph-data).

### Check how an action blends over a state

*An upper-body action looks right on its own but wrong in game over a walk, or a new action snaps in.*

The game plays an action on top of the current state using the action's bone weights and ramps. The layered
preview plays that same blend.

1. Open the action clip. With the samples, open `sample_figure_wave.rfa` (its arm bones have weight 10, the rest 0).
2. Under the viewport, press **Play as action over state** and pick the state in the box beside it. The preview
   mesh's own table states come first, then every clip with the same bone count (with the samples, pick
   `sample_figure_idle.rfa`).
3. Play. A badge over the viewport says the layered preview is on; the ramps are the shaded ends of the time bar.
4. Adjust and watch:
   - Bones the action should own need weight 10; bones that should keep the state need 0. Change weights in the
     **Bone** inspector or with **Clip › Set Weights…**. The timeline shows each bone's weight.
   - If the action snaps in or out, lengthen **Ramp in** or **Ramp out** in the **Clip** inspector.

Only the preview changes. Clip tool previews also play layered while this is on. If nothing seems to change, the
weights may all be 0, or the ramps longer than the clip (problem RFA022).

### Make a new animation

*Your character needs a motion no stock clip has: a gesture, a taunt, a different idle.*

Cairn suits short and derivative work: a variant of a stock motion, a pose fix, a short new gesture. It is also
where every clip gets its game-specific finishing: weights and ramps, the layered preview, looping, key reduction,
the Problems list, a unique name and a table line. A substantial new motion is easier to animate in Blender, since
Cairn has no curve editor, no onion skinning, no constraints, and IK only on arms and legs. You can mix the two:
block a motion out in Blender and finish it here, or start here and move to Blender when it outgrows the tools.

**In Cairn**

1. Choose **File › New › Animation clip…** (**Ctrl+N**), or right-click the character in the library › **New Clip
   for This Mesh…**.
2. Under **CHARACTER MESH**, check the mesh (the tab in front is picked for you; **Browse…** takes a `.v3c`).
3. Under **KIND AND LENGTH**, choose **State (loops)** or **Action (plays once)** and set the **Length**.
4. Under **STARTING POSE**, keep **A reference clip's pose**: the character's stand clip is picked for you, so the
   new clip has the same bone lengths as the character's other clips. Use **The mesh's bind pose** only when the
   character has no stand clip.
5. Enter a new **NAME** (at most 59 characters) and press **Create**. The clip opens in a new, unsaved tab with
   every bone holding the starting pose.
6. Pose it: move the playhead, click a joint, press **E** and drag with **Key** on; each drag writes a key. Use
   **W** with **IK** on to place a hand or foot. **K** in the timeline holds the selected bones' pose. See
   [Edit poses and keys](#edit-poses-and-keys).
7. Shape the timing by dragging keys in the timeline and setting eases in the **Key** inspector.
8. For a state, use **Clip › Make Loopable…**. For an action, set the bone weights and ramps and check it with
   [Play as action over state](#check-how-an-action-blends-over-a-state).
9. Clear the Problems tab, then **File › Save** (**Ctrl+S**); it asks where to write the new file.
10. In the **Table usage** tab, press **Copy table line** and add it to your class (see
    [Get your work into the game](#get-your-work-into-the-game)).

**Through Blender**

1. Open the character (or a related clip playing on it) and choose **File › Export › glTF…** (**Ctrl+E**). Under
   **ANIMATIONS**, tick a stock clip close to what you want, at least its stand clip.
2. In Blender, animate that armature. Pose bones only; never move bones in Edit Mode, and keep the bone names.
   Every clip of a character must carry the same bone lengths, and the clip takes them from the animation.
3. Export glTF 2.0 with the armature and its animation, then in Cairn choose **File › Import › Animation from
   glTF…** (**Ctrl+I**) with the character as the **TARGET MESH**. For an action, set **Ramp in** and **Ramp out**.
4. Finish it as in steps 6 to 10 above: weights and ramps, looping, **Clip › Reduce Keys…** for a densely baked
   motion, the Problems list, a new name and the table line.

Every New Clip option is in the [New Clip dialog](REFERENCE.md#new-clip-dialog) reference.

### Retarget animations to another character

Clips only fit the skeleton they were made for. **Retarget** moves a clip onto another skeleton bone by bone,
matched by name, giving it the target's proportions while keeping what matters in place: hands on the wheel, feet
on the floor.

#### Retarget one clip

*Your female miner needs the jeep-driving pose only the male rig has, or your custom character has to sit in the
jeep.*

1. Open the clip (for example `park_jeep_driver.rfa`), playing on the mesh it was made for, and choose **Clip ›
   Retarget…** (**Ctrl+R**). You can also right-click a clip in the library › **Retarget…**.
2. On the **Source & target** tab, check **KIND OF CLIP (PRESET)**. Cairn suggests one and says why:

   | Preset | Use it for | What it keeps in place |
   |---|---|---|
   | **Seated / fixed controls** | Drivers, gunners, turret operators. | Hips on the seat; hands and feet where the source's were. |
   | **Standing / locomotion** | Standing, walking, running, crouching, jumping, dying; almost everything else. | The target's hips at its own height, feet on its own ground; a two-handed weapon stays gripped. |
   | **Rotation only** | Swimming and anything with no contact with the ground or a control. | Nothing: every joint turns as the source's. |

3. Under **TARGET RIG**, choose the **Target mesh**: the character that should get the clip. The rest of the rig
   settings fill in by themselves for the four stock humanoid rigs.
4. Under **OUTPUT**, check the **File name** (such as `af_female_jeep_driver.rfa`) and the **Save folder**.
5. Watch the preview: the target mesh plays the result, with the source as a thin coloured skeleton in step.
   **Side by side** shows the source mesh beside it instead.
6. If the target is not a stock rig, check the **Bone map** tab: each target bone lists the source bone that drives
   it. Rows with errors stop the retarget; **Auto-map** pairs everything again.
7. Read the **Report** tab. The **OUTPUT CHECKLIST** should be all ticked. **PINNED CONTACTS** should show about
   0 cm for each held hand or foot; more means the limb was "fully stretched" and could not reach. Differences in
   **HANDS, FEET AND HEAD** are proportions, not errors (a shorter character's head sits lower).
8. Press **Retarget** to open the result in a new, unsaved tab where you can fine-tune it, or **Save as…** to write
   it now.

If the character floats or sinks, use **Standing / locomotion** rather than Seated for anything that stands, and
check both rigs have a stand clip as **Reference clip**. If a limb is fully stretched (often deaths lying flat),
try another preset or fix that moment afterwards with IK. Every option is described under
[Retarget dialog](REFERENCE.md#retarget-dialog).

#### Retarget a whole character's set

*You built a new character type and want every clip the miner has.*

1. Choose **Tools › Batch Retarget…**.
2. On the **Rigs** tab, set the source and target meshes.
3. On the **Clips & output** tab, leave the preset on **Automatic (per clip)**: each clip gets the preset the single
   dialog would suggest.
4. Fill the queue: pick a class under **Every clip the tables give** (for example `miner1`) and press **Add class**
   for every state, action and weapon-specific clip it has; or tick clips under **ADD CLIPS FROM THE LIBRARY** and
   press **Add checked**; or **Add files…**.
5. Check **QUEUE AND OUTPUT NAMES**: a warning marks a name that is too long, taken by another clip or used twice.
6. Set the output **Folder** and **Name pattern**, then press **Run**. If any output exists, Cairn asks once whether
   to replace. **Stop** stops after the clip in progress; what is done stays written.
7. The **RESULTS** list shows each clip's status, preset and pinned contacts. Double-click one to open it on the
   target mesh.
8. Press **Copy table lines** for `entity.tbl` lines that give the new clips the same states and actions as their
   sources, ready for your new class. **Save report…** keeps a record.

Name pattern tokens and the other options are under [Batch retarget](REFERENCE.md#batch-retarget).

### Edit a character mesh

Open a `.v3c` and use the inspector's **Structure** tab. Select a node in the tree (or click a joint, collision
sphere or prop point in the viewport) and edit it in the pane below the tree, or drag it in the viewport with the
move (**W**) and rotate (**E**) tools. Turn on **Spheres** and **Prop points** in the viewport toolbar to see and
click them; if a joint wins the click, click the same spot again.

- **Collision sphere too small or in the wrong place**: select the sphere, then with **W** drag the arrows to place
  it and drag its outline (or the round grip) to change the radius. Or type **CENTRE** and **Radius**, and change
  **Bone**. Spheres have no rotation.
- **New hit sphere**: select a bone and choose **Mesh › Add Collision Sphere**. Name it as the tables expect
  (`head`, `torso`…).
- **Weapon held in the wrong spot**: select the prop point and drag it with **W** and **E**, or type its
  **POSITION** and **ORIENTATION**. Pick a preview clip that holds the weapon so you place it in the pose that
  matters; it follows its bone in every other pose. Edits apply to every level of detail.
- **A joint in the wrong place in the rest pose**: select the joint and drag it. While you do, the viewport shows the
  bind pose and the skin stays still (the preview clip comes back when you press **Q** or select something else).
  The **Children** toggle decides whether the bones below come along.
- **Wrong texture**: select the material under **Materials** and type a **Texture** name, or press **Browse…** to
  pick from every texture the game and your folders provide.
- **Level of detail switches too early**: select the LOD and change its **Distance**.
- **Bone named wrong or on the wrong parent**: select it and change **Name** or **Parent**. Renaming changes no
  clip, since clips use bone numbers.
- **Bones in the wrong order**: **Mesh › Reorder Bones…**. Every clip made for the mesh then needs conforming; the
  dialog offers to conform the open clip tabs that preview this mesh in the same step. Clips that are not open:
  open each and use **Clip › Conform to Skeleton…** with the saved mesh.

**F2** renames the selected node, **Del** removes the selected sphere or prop point. Each change is one undo step.
Save the mesh under a new name and point your class's `$V3D Filename:` at it (spelt `.vcm`). Every editor and the
**Mesh** menu are described under [mesh editing](REFERENCE.md#mesh-editing).

### Import and export glTF

Cairn reads and writes glTF 2.0 with the same conventions as REDUX, so files move between Cairn, REDUX, Blender and
other glTF tools. A clip exported and imported back unchanged comes back exactly; if another tool re-saved it, the
motion comes back very closely but timing, ramps and weights take the import's defaults. Morph (vertex) animation is
never exported.

#### Send a clip or mesh to Blender

1. Open the mesh, or a clip playing on the mesh you want.
2. Choose **File › Export › glTF…** (**Ctrl+E**).
3. Under **INCLUDE**, pick the levels of detail, collision spheres, prop points and **Textures (as PNG)**, or
   **Skeleton only (no geometry)**.
4. Under **ANIMATIONS**, tick other clips of the same skeleton to put in the same file.
5. Under **FORMAT**, choose **.gltf + .bin + PNG textures** (the only one REDUX reads) or **Single .glb** (one
   file, fine for Blender).
6. Leave **Write RF key extras (rf_keys)** on so a clip comes back exactly if you import it unchanged.
7. Choose the output path and press **Export**. The dialog lists what was written and any warnings.

#### Bring an animation in from Blender

1. In Blender, export glTF 2.0 (`.gltf` or `.glb`) with the armature and its animations. Name the bones like the
   target character's; Cairn matches exact names first, then the stock rigs' naming, then body part and side.
2. In Cairn choose **File › Import › Animation from glTF…** (**Ctrl+I**), or drop the file on the window (a file
   with both animations and meshes asks which to import).
3. Tick the **ANIMATIONS** to import and choose the **TARGET MESH**.
4. On the **Bone map** tab, fix any bone paired with the wrong node. Bones with no node hold the reference clip's
   pose and do not move.
5. Check **OPTIONS** if the file did not come from Cairn or REDUX: **Start at** (stock clips start at frame 1),
   **Bone weight**, **Ramp in**/**Ramp out** for actions, **Reduce keys**.
6. Press **Import**. Each animation opens as a new, unsaved clip tab on the target mesh. Fix anything in the
   Problems tab, then save.

#### Build or re-skin a character mesh

*You modelled a new character, or a new outfit for an existing skeleton.*

1. To keep an existing character's skeleton, collision spheres and prop points (so all its clips keep working),
   open that character first.
2. Choose **File › Import › Mesh from glTF…**.
3. Under **BUILD**, choose **Character (.v3c)** or **Static mesh (.v3m)**. To re-skin, tick **Replace the geometry
   but keep the skeleton, spheres and prop points of** and pick the open character.
4. Check **OPTIONS**: **Scale** (0.01 for a file in centimetres), **Texture names** (**Name them .tga** is
   recommended) and the LOD distances.
5. Read the **PRE-FLIGHT** list: everything that breaks a limit, or was converted or assumed, with a fix hint.
   Errors disable **Import**.
6. Press **Import**. The mesh opens in a new, unsaved tab; save it.

Name objects `_LOD0`, `_LOD1`, `_LOD2` in Blender to set the levels of detail. Collision spheres and prop points
come from nodes named `rf_csphere::name` and `rf_prop::name` under their bones.

#### Rig a new mesh to a stock skeleton

*You want your new model to play every clip a stock character has.*

Rigging (giving every vertex its bone weights) happens in Blender; Cairn has no modelling or weight painting. Cairn
gives Blender the skeleton and takes the result back onto the very same skeleton, so every clip for it plays on
your mesh.

1. **Export the skeleton.** Open a character with that skeleton and export glTF (**Ctrl+E**), with a stand clip
   and a walk if you want to test in Blender. Prefer a character whose bind pose is a T-pose: `merc_grunt.v3c`
   rather than `merc_com.v3c`, `ult2_guard.v3c` rather than `riot_guard.v3c`.
2. **Fit and weight in Blender.** Move, rotate and scale your mesh over the armature, apply the mesh's transforms,
   parent it to the armature (for example **With Automatic Weights**) and paint weights until the joints bend
   well. Leave the stock mesh out of the export.
3. **Export glTF 2.0** with your mesh and the armature.
4. **Import onto the skeleton.** With the same character open, choose **File › Import › Mesh from glTF…**,
   **Character (.v3c)**, tick **Replace the geometry but keep the skeleton, spheres and prop points of** and pick
   it. Read the pre-flight list and press **Import**.
5. **Test with stock clips.** With the new tab in front, double-click the character's clips in the library. Check
   elbows, shoulders, knees and neck in a walk, a run and a crouch; fix the weights in Blender and import again.
6. **Finish** in the **Structure** tab (fit the collision spheres, place prop points, check textures and LOD
   distances) and save under a new name.

Rules on the Blender side:

- **Don't change the armature's rest pose**, its scale or its bones. Move the mesh to fit the skeleton, never the
  bones in Edit Mode; Cairn keeps the stock bind pose, so a moved bone would make the skin distort in every clip.
  Use **Scale** 0.01 in the import dialog rather than scaling objects.
- **Don't add, rename or delete bones.** Joints are matched by name; a joint that matches no bone (MI011) hands its
  vertices to its parent. A new bone means a new skeleton, and no stock clip fits a new skeleton.
- **Weight every vertex** (an unweighted one won't bend, MI006), **at most 4 bones per vertex** (Blender:
  **Weights › Limit Total**, 4; more are cut to the strongest 4, MI007).
- **At most 3 levels of detail, 7 textures each.** Object names hold 23 characters and texture names 31, in plain
  Latin letters.
- **Textures are names, not files.** The mesh stores only each texture's file name; ship the textures with your mod.
  The game also finds a `.png` or `.dds` with the same name as the `.tga`.

All dialog options and pre-flight codes are under
[glTF options for clips and meshes](REFERENCE.md#gltf-options-for-clips-and-meshes).

### Convert exporter and PS2 meshes

Cairn reads four mesh formats the PC game does not load, shows them in 3D and converts them to `.v3m`/`.v3c`. It
never saves them in their own format.

| Format | What it is | Converts to |
|---|---|---|
| `.v3d` | The 3ds Max exporter's static mesh (the source a `.v3m` was made from) | `.v3m` |
| `.vcm` | The exporter's character mesh, with skeleton, collision spheres, prop points and weights | `.v3c` |
| `.rfm` | Red Faction's PlayStation 2 static mesh | `.v3m` |
| `.rfc` | Red Faction's PlayStation 2 character mesh | `.v3c` |

(The tables also use `.vcm` and `.v3d` as spellings of `.v3c` and `.v3m`; those are the PC meshes, not these source
files.)

1. **Open one** like any file, or select it in a packfile and use **Open in Cairn**. The tab shows the converted
   mesh, read-only, with a banner and a **Convert…** button. A character plays preview clips like a `.v3c`.
   **Problems** lists, as information, what converting approximates.
2. **Convert** with **Convert…** on the banner or **File › Export › Convert to .v3m/.v3c…**. Choose where the file
   goes: next to the source, into a folder, or into the packfile it came from as a new entry (save the packfile to
   keep it). A taken name gets a free one (`box (2).v3m`) unless you tick **Replace**. The window lists what the
   conversion changed.
3. **Many at once**: in a packfile, select the meshes and choose **Convert meshes…** (right-click, or the Packfile
   menu). You get one report of what could not be converted and what was approximated.

PS2 meshes lost some detail in the PS2 build. When the exporter mesh with the same name is beside a `.rfm`/`.rfc`
(same packfile or folder), Cairn converts that instead and says so. Texture names are kept; they name `.tga` files,
which the packfile's **Convert to .tga…** makes from the PS2 texture packs (see [Packfiles](#packfiles-vpp)). Red
Faction II meshes and damaged files open with the reason in Problems and cannot be converted. The whole PS2
conversion route is in [Making a mod](#making-a-mod); conversion details are under
[exporter and PS2 mesh conversion](REFERENCE.md#exporter-and-ps2-mesh-conversion).

### See which table lines play a clip

The **Table usage** tab of the bottom panel reads the game's `entity.tbl`, `weapons.tbl`, `pc_multi.tbl` and
`fpgun.tbl` (your mod's copy wins if it comes first in the search order).

- **For a clip** it lists every table line naming it (table, class, state or action, weapon block, line number) and
  the meshes that play it, with a warning when a mesh's bone count differs. Double-click a mesh to preview the clip
  on it.
- **For a mesh** it lists each class that uses it, with its clips. Double-click a clip to open it, or right-click ›
  **Preview on this mesh**.

**Copy table line** (or **Ctrl+C** on a row) copies the line, a whole list for a group row, or a new `+State:`
line for a clip no table uses. Choose **Layout** **entity.tbl** or **weapons.tbl** to match the table you paste
into. Clips are written as `.mvf`:

```
	+State:                 "stand"                 "af_female_stand.mvf"
```

To add the line, open your mod's copy of `entity.tbl` (or `weapons.tbl`) in the [Tables](#tables-tbl) module and
paste it into the class block. There, **Go to Definition** finds the class and **Compare with Stock** shows what you
changed. A clip no table names is never played: "as it stands this clip is never used". The panel is described
in full under [table usage panel](REFERENCE.md#table-usage-panel).

### Fix the problems Cairn reports

Cairn checks the open clip or mesh after every change against what the game does. Open `sample_figure_broken.rfa`
from the samples to see it.

1. Click the error or warning count in the status bar, or the **Problems** tab.
2. Each row says what is wrong, why it matters in game, and how to fix it. Click the grey location link to select
   that bone, jump to that time or focus that field.
3. Many rows have one-click fixes, such as **Hold the rotation with a key at the start and the end** or **Set the
   ramps to 2400 in, 2400 out**; each is one undo step. Some open a dialog: **Choose another preview mesh…**,
   **Conform to skeleton…**, **Locate the file…** for a missing texture, **Save as…** for a name problem.
4. The filter buttons show or hide errors, warnings and suggestions.

Errors mean the game will misbehave; warnings mean the file loads but probably not as intended; suggestions are
harmless tidy-ups. You can save a file with errors, but Cairn asks first. Every code is listed under
[problem codes](REFERENCE.md#clip-and-mesh-problem-codes).

### Get your work into the game

Cairn writes `.rfa`, `.v3c` and `.v3m` files and gives you table lines. Before you package them:

1. **Name each clip uniquely.** The game knows a clip by its file name alone, across the whole game. A clip that
   shares a stock clip's name either replaces it everywhere or is never loaded (RFA024). Give new clips new names
   unless replacing a stock clip is the point.
2. **Keep names to 59 characters**, including `.rfa`; longer names can crash the game (RFA014).
3. **Match the bone count.** A clip must have exactly the bones of every mesh it plays on (RFA002, RFA013).
4. **Add table lines.** The game only plays clips a table names. Copy them from
   [Table usage](#see-which-table-lines-play-a-clip) or after a batch retarget, and paste them in the
   [Tables](#tables-tbl) module. Remember the spellings: `.mvf` is `.rfa`, `.vcm` is `.v3c`, `.v3d` is `.v3m`.
5. **Clear the errors** in each file's Problems tab.
6. **Package the files** with your mod or level, for example in a `.vpp`; see [Making a mod](#making-a-mod). The game
   only loads clips and meshes from packfiles, not from loose files.

### Good to know

- **A clip fits one skeleton.** Bone count and order must match the mesh. To move a clip to another character,
  retarget it; for the same bones in another order, conform it.
- **Clip names are global** and at most 59 characters. Two clips with the same name cannot both be loaded.
- **Bone lengths come from the clip.** Every clip of a character should carry the same lengths, or the character
  stretches (RFA023). Starting new clips from the stand clip's pose avoids this.
- **Morph (vertex) animation** fits only the one mesh it was made for and plays on the most detailed LOD. Retarget,
  glTF export and conforming to another mesh drop it; strip it when a clip plays on several meshes.
- **Files from inside a `.vpp`** open as copies: Save asks where to write. To change a file inside a packfile, open
  the packfile and use **Open in Cairn** on the entry.
- **Read-only `.v3m`**: Save is greyed out; use **Save As…** for a copy. The other saving rules are under
  [saving clips and meshes](REFERENCE.md#saving-clips-and-meshes).
- **Settings** for this module are under **Tools › Settings… › Animations and meshes**: what library double-click
  does, the viewport display and the time unit ([settings](REFERENCE.md#animations-and-meshes-settings)).
- **Help**: **F1** in a clip or mesh tab opens the **RFA & V3C format reference** (every stored field and what the
  game does with it). **Help › Exporter and PS2 meshes** covers the converted formats, and **Help › Keyboard
  Shortcuts** lists every key ([full table](REFERENCE.md#animations-and-meshes-keyboard-shortcuts)).
- Keys such as **Space**, **E**, **W**, **K** and the arrows work in the area that has focus: click the viewport or
  timeline first. **Ctrl+Shift+P** plays or pauses from anywhere.

### Limitations

- Cairn is not a full animation suite: no curve editor (eases are per-key sliders), no onion skinning (one ghost
  clip at a time), no constraints, and IK only on two-bone arms and legs.
- No modelling or weight painting: meshes are built in another tool and imported through glTF.
- Morph (vertex) animation cannot be edited, retargeted or exported; it can only be kept or stripped. Converting
  version 8 to 7 can move a talking mouth by a few millimetres, and reversing a version 7 clip with morph data makes
  the morph run one step early.
- Reordering a mesh's bones and then undoing in the mesh tab (or undoing the conform in a clip tab) leaves the
  conformed clip and its preview out of step; the two tabs' histories are not linked.
- The mesh gizmos move one collision sphere or prop point at a time.
- **Shift+Left**/**Shift+Right** step ten frames only on the time bar.
- The Help, Reorder Bones, Texture Browser and clip tool windows do not remember their size.
- A glTF import started while another dialog is open is not queued.

## Effects (VFX)

A `.vfx` file is one of the game's effects: an explosion, muzzle flash, glow, spark or debris, made of animated
meshes, particle systems, dummies, lights, spacewarps and their materials.

An effect tab shows the **outliner** (the object list) on the left, the **viewport** with its play controls in the
middle, and the **Effect**, **Object** and **Material** inspector tabs on the right (a **Keys** tab appears when keys
are selected, a **Vertices** tab in vertex mode). The **Timeline** and **Problems** are in the bottom pane, and the
**Effects** library is in the left pane.

**What you can do**

- Browse every stock effect, see which tables use it, and play it as a loop, once, or holding the last frame.
- Change materials, textures, opacity and glow over time.
- Move, rotate and scale objects and key them over time; retime objects.
- Add primitives (planes, facing quads, rings, spheres…), particle systems, lights and more, or start from a template.
- Edit the vertices of morph meshes frame by frame.
- Build effects from glTF files made in Blender or REDUX, and export them back.
- Find what the game would get wrong, with quick fixes.

### Open and inspect a stock effect

1. In the **Effects** library, type part of a name, a source packfile or a table into the filter. Each effect shows
   where it comes from, the tables that use it, its frame count, object count and format version. The library is
   empty until the game folder is set.
2. Double-click an effect to open it. An effect from a packfile opens read-only; **Save As** writes a copy.
3. Press play. Choose **Effect › Loop**, **Effect › One-shot** or **Effect › Hold last frame** to see how it plays for
   each kind of caller; the file itself does not say which the game uses. Drag the time bar to step through frames.
4. Click an object in the outliner or the viewport; the **Object** tab shows its values. The **Effect** tab shows the
   format version, end frame, length and what the effect contains.

You can also open the samples in `samples/vfx`: the templates and one effect per primitive.

### Change a material's opacity and texture

1. Select the material under **Materials** in the outliner (or a mesh that uses it) and open the **Material** tab.
2. To change the texture, type a name in **Texture 1** or press **…** beside it: the picker searches the game's
   packfiles and your folders, with a preview. Animated `.vbm` textures play in it.
3. The **Opacity** and **Self-illumination** tracks (and **Mix** for a two-texture material) are curves over time;
   type the value at the playhead in **At playhead**. Values run from 0 to 1.
4. Turn on **Additive** for glows and flashes that should brighten what is behind them.

### Retime an object

Timing is set per object, on the **Object** tab under **Timing**: **Frame rate**, **Start time** and **Frame count**.
**Lengthen by** chooses how new frames are filled: hold the last frame, loop from the start, or stretch the animation.
Move keys in the **Timeline**, or type exact times on the **Keys** tab. Then check **End frame** on the **Effect**
tab; the Problems tab warns when it no longer matches the longest object.

### Add a facing quad that glows

1. Choose **Effect › Add › Material**; on the **Material** tab pick its texture and turn on **Additive**.
2. Choose **Effect › Add › Primitive…** (also on the toolbar and the outliner's right-click menu) and pick the facing
   quad, choosing the new material in the dialog. A facing quad always turns to the camera, like a sprite.
3. Its size is **Width** and **Height** under **Facing (at the playhead)** on the **Object** tab, set per frame. Move
   the playhead and type a value at each frame you want to change, or drag the scale gizmo (**R**).
   **Apply size to all frames** copies the current size to every frame.
4. Play the effect. To change the material later, use **Material slots** on the **Object** tab.

### Start from a template

**Effect › New from template** offers **Additive flash**, **Scrolling beam**, **Ring shockwave** and **Particle
fountain**: small animated effects to build on. An empty effect comes from **File › New › Effect**.

### Build an effect from a glTF file

1. Model and animate in Blender (or another tool) and export glTF 2.0.
2. Choose **File › Import › Effect › glTF as effect…**, or open or drop the `.gltf` or `.glb` on the window. A file
   exported as an effect by Cairn or REDUX comes back exactly; any other glTF becomes new objects, with node animation
   sampled at 15 frames a second and morph targets as morph frames.
3. Check the Problems tab, then **Save As** a `.vfx`.

### Reuse geometry from a mesh or another effect

- **File › Import › Effect › Geometry from mesh (V3M/V3C)…** turns a static mesh's parts into effect meshes.
- **File › Import › Effect › Objects from another effect…** asks for a `.vfx`, then the objects to copy. Their
  materials come along.

### Edit the vertices of a morph frame

1. Select a mesh and press **Tab** (or **Effect › Vertex mode › Vertex mode**). The **Vertices** tab appears. A
   keyframed or static mesh becomes a morph mesh with **Make morph mesh** there.
2. Click vertices or drag a box around them. Move them with the gizmo or type positions on the **Vertices** tab.
3. **Shift+F** switches between editing the current frame and all frames. **Esc** leaves vertex mode.

Facing quads and rods cannot be edited this way: the game builds their corners from their size.

### Rotate or scale around a different point

Turn on **Pivot mode** (on the viewport bar or in the **Effect** menu) to move, rotate or scale the selected mesh's
pivot with the gizmo instead of the mesh. The pivot's values are also on the **Object** tab under **Pivot**.

### Fix effect problems

The **Problems** tab lists what the game would get wrong: a format version it cannot load, faces that would crash it,
missing textures, timing that does not add up, duplicate or empty names, unused materials and more. Double-click an
entry (or press **Enter**) to select what it is about. Where there is a quick fix, press **Fix** or pick it from the
entry's right-click menu; each fix is one undo step.

### Round-trip an effect through Blender or REDUX

1. Choose **File › Export › Effect as glTF…**. Save as `.gltf` (with a `.bin` beside it, the form REDUX reads) or as
   a single `.glb`.
2. Edit it in Blender, or keep working in REDUX.
3. Import the result as in [Build an effect from a glTF file](#build-an-effect-from-a-gltf-file).

### Edit an older effect

An effect in an older format version opens with a bar saying it can be viewed and saved unchanged. Press **Convert**
on the bar, or choose **Effect › Convert to current format**, to edit it. The conversion is one undo step.

### Good to know (effects)

- Effects run at 15 frames a second. Each mesh appears at its start frame and disappears after its end frame.
- The parent of an object is for organising only: the game does not combine parent and child movement.
- The effect does not decide whether it loops: the game code that spawns it does. Explosions and impacts play once;
  glows and attached effects usually loop.
- **W**, **E** and **R** move, rotate and scale; **Q** hides the gizmo; **K** inserts a key. With **Auto-key** on, a
  gizmo drag on a keyframed mesh adds a key at the current frame.
- An effect only appears in the game if a table names it: open that table and add the line (see
  [Edit the game's tables safely](#edit-the-games-tables-safely)), then ship both in a packfile.
- **Help › Effects** has the format reference and what the preview shows exactly or approximately. Menus, inspectors
  and the format primer are in the [reference](REFERENCE.md#effect-format-primer).

### Limitations (effects)

- Editing needs the current format version; older files must be converted first, and are then saved in it.
- There is no whole-effect retime or frame-rate change: timing is set per object.
- Particles under a spacewarp, streak particles and blending are approximations; check them in the game (see
  [Effect preview accuracy](REFERENCE.md#effect-preview-accuracy)).
- Skins, cameras and PBR material values in a glTF file are not carried over.

## Packfiles (VPP)

A packfile (`.vpp`) is the archive Red Faction loads its levels, textures, sounds, meshes, animations, effects and
tables from: a flat list of files, its *entries*, with no folders inside.

A packfile tab shows the **file list** with a toolbar on the left, and on the right the **preview** of the selected
entry above its **details**. The left pane has the **File types** panel and the **Packfiles** browser. Even the
largest packfile opens at once. Every change (add, replace, rename, remove, reorder) waits as a pending change you can
undo until you save.

![A packfile open in Cairn, with a character mesh previewed](packfiles.png)

**What you can do**

- Browse every packfile of the game, its `user_maps` and mods, and find files in them by name or type.
- Preview images, sounds, meshes, clips, effects, animated textures, fonts, tables and text without extracting.
- Read a level's details, including every file it needs and whether it can be found.
- Extract files, or edit them in another program or in Cairn and put them back.
- Add, rename, replace and remove entries, and build new packfiles for your mods.
- Convert images to DDS.
- Convert the PlayStation 2 version's textures, meshes and sounds for the PC game.
- Save safely, even over the packfile you are editing.

### Open a packfile and find files in it

1. Open a `.vpp` from the **Packfiles** browser in the left pane (double-click it; see
   [The start page and the Packfiles browser](#the-start-page-and-the-packfiles-browser)), with **File › Open…**
   (**Ctrl+O**), by dropping it on the window, or by double-clicking it in Explorer once `.vpp` is associated.
2. Type part of a name in the filter box above the list. Wildcards work: `*.tga` shows every Targa image,
   `lev??.rfl` matches `lev01.rfl`. **Esc** clears the box.
3. To jump instead of filter, click in the list and type the first letters of a name: the first entry that starts
   with them is selected. Pause briefly to start a new search.
4. To see only some kinds of file, tick them in the **File types** panel (or **All types** next to the filter). Types
   are grouped (**Images**, **Sounds**, **Meshes**, **Animations**, **Effects**, **Levels**, **Tables**…), each with
   its count; **Show all types** clears the choice.
5. Click a column header (**Name**, **Type**, **Size**, **State**, **Info**) to sort by it; click again to reverse.
   This only sorts the view, not the packfile.

The **Info** column gives one line about each file, read from its contents: an image's size and format, a sound's
rate and length, a mesh's triangle count, a level's name, author and save date. It fills in a moment after the
packfile opens. The status bar shows the entry count, the size when saved, pending changes and problems.

### Preview what is inside

Click an entry: the preview shows it and the details below describe it. Select several to see their count, total
size and a bar per type.

- **Images** (`.tga`, `.dds`, `.png`, `.jpg`, `.vbm`) on a checkerboard. **Fit**, **100%** and the mouse wheel zoom;
  **Alpha** shows the alpha channel alone. A `.dds` with mipmaps has a mip level box; an animated `.vbm` plays.
- **Sounds** (`.wav`, `.ogg`, `.aif`, `.mp3`, and the PS2 `.vse` and `.vmu`): **Play**, **Stop**, a position slider
  and a waveform you can click. Turn on **Autoplay sounds** (the speaker button on the main toolbar) to play each
  sound as soon as you select it; it stays on until you turn it off.
- **Meshes, clips, effects and animated textures** play in a 3D or animated preview. A clip plays on a character mesh
  with the same number of bones.
- **Tables** show highlighted and foldable, **fonts** show their characters, **text** shows numbered lines with
  **Find** (**Ctrl+F**) and **Copy**.
- **Other types** show their first bytes in hex.

**Open in Cairn** above the preview opens the entry in a tab of its own, for every type Cairn edits.

Previews look for the files they need **inside the same packfile first**: a mesh takes its textures from the
packfile's images, an animated texture its frames, a clip its character. Only what the packfile lacks is looked up in
your search folders and the game. So a level's custom textures show at once, pending changes included.

Entries larger than 48 MB wait for **Preview anyway**.

### Read a level's details

Levels (`.rfl`) have no picture; select one and read the details pane. It is grouped:

- **Level**: name, author, when it was last saved, which games load it, single or multiplayer.
- **Properties**: ambient light, fog, the geomod texture and hardness.
- **Statistics**: rooms, faces, lights, respawn points, and which entity, item, clutter and event classes the level
  uses, and how often.
- **Alpine Faction**: the level's Alpine settings, when it has them.
- **References**: every texture, mesh, clip, effect, sound and music file the level names, and where Cairn found it:
  **in this packfile**, **in the game data**, or **missing**. Missing files are listed first and highlighted; the
  game would not find them either unless another packfile brings them.
- **Preloads**, **Notes** and **Sections**: the editor's preload lists, anything unusual, the file's sections.

Long groups start collapsed; click a heading to open it. **Copy all** copies every row as text.

### Extract files

- **To a folder**: select entries and choose **Packfile › Extract selected…** (**Ctrl+E**), or right-click ›
  **Extract to…**. **Packfile › Extract all…** writes every entry.
- **Next to the packfile**: right-click › **Extract here**.
- **By dragging** the selection into Explorer, or **Ctrl+C** and then paste in Explorer.
- **As PNG**: **Packfile › Extract as PNG…** writes the selected images (and every texture of selected `.peg` packs)
  as PNG files.

When files of the same name exist, Cairn asks once: **Overwrite**, **Skip existing** or **Cancel**. Progress shows in
the status bar, where you can cancel.

### Edit a file and put it back

1. Double-click an entry (or press **Enter**) to open it in the program Windows uses for its type, or right-click ›
   **Open with…** to choose. Cairn gives the program a *work copy*; the packfile is not touched.
2. Edit and save in that program.
3. A bar appears above the list: "A work copy was changed outside the packfile". Press **Update packfile** to put the
   changes in as one undo step, or **Ignore**.
4. Save the packfile.

For types Cairn edits itself (animated textures, clips, meshes, effects, tables, fonts, bitmaps), right-click ›
**Open in Cairn** (or **Open in Cairn** above the preview) opens the work copy in a tab. Save the tab (**Ctrl+S**) and
the same bar offers **Update packfile**. Work copies are deleted when the packfile closes.

### Add files and folders

- **Packfile › Add files…**, the toolbar's **Add files…**, or the **+** on the main toolbar.
- **Packfile › Add folder…**: every file in a folder and its sub-folders (only the names are kept).
- **Drag and drop** files or folders from Explorer onto the tab.

Added entries show **added** in the **State** column and are read from your files when you save. When an added
file's name is already in the packfile (ignoring case, as the game does), Cairn asks: **Replace**, **Keep both**
(under a free name such as `name (2).tga`) or **Skip**, with answers for all clashes at once. A renamed copy is a
different file to the game: nothing uses `name (2).tga` unless you point something at it.

### Rename, replace and remove entries

- **Rename**: select an entry and press **F2**, type the name, press **Enter**. Names the game cannot use are refused,
  with the reason in the status bar.
- **Replace**: right-click › **Replace…** and pick a file; its data goes in under the entry's name.
- **Remove**: select entries and press **Del**.

Every change is an undo step and shows in the **State** column (**added**, **replaced**, **renamed**) until you save.
**Packfile › Sort by** reorders the entries inside the packfile, if you like them tidy; the game does not care.

### Convert images to DDS

Alpine Faction loads `name.dds` wherever `name.tga` is asked for, so you can turn a packfile's textures into DDS
files (smaller, with mipmaps) without changing any level, mesh or table.

1. Select the `.tga`, `.png` or `.jpg` entries (or none, to convert every such image) and choose **Packfile › Convert
   images to DDS…** (**Ctrl+Shift+D**, or right-click › **Convert to DDS…**).
2. Pick the **Format**: **Auto** chooses DXT1 for opaque images, DXT1 with 1-bit alpha or DXT5 from each image's
   transparency. Set **Mipmaps**, the filter, the quality and whether sides are resized to a power of two.
3. Choose the output: **Replace the originals in the packfile**, **Keep the originals too**, or **Write to a folder**.
   The before-and-after preview and the size summary show what you get.
4. **Convert**. The change is one undo step; save the packfile to keep it.

The options are listed in the [reference](REFERENCE.md#dds-conversion-options).

### Create a new packfile

1. Choose **File › New › Packfile…**. An empty packfile opens.
2. Add files and folders.
3. Save (**Ctrl+S**) and pick a name and folder. Keep the file name to 31 characters including `.vpp`, or the game
   does not load it.

Where to put it is in [Release a mod in a packfile](#release-a-mod-in-a-packfile).

### Save a packfile safely

**Save** and **Save As…** first check the packfile: errors (a name the game cannot use, two entries whose names differ
only in case, an added file that changed on disk) stop the save with a list of what to fix; warnings do not. Cairn
then writes the new packfile to a temporary file, checks it, and only then swaps it in. Until then the original is
untouched, which is why you can save over the packfile you are editing. A cancelled or failed save leaves the file as
it was.

The status bar shows the progress; click it to cancel. To keep the previous version as `name.vpp.bak`, tick **Keep a
.bak copy when saving over a packfile** in **Tools › Settings… › Packfiles**. After a save, the undo history starts
again.

### Convert the PS2 version's textures for the PC game

The PlayStation 2 version's packfiles keep their textures in PEG texture packs (`.peg`), which the PC game cannot
load. Cairn turns them into `.tga` files (and `.atx` animated textures) that it can.

1. Open the PS2 `.vpp`. A bar above the list says it is from the PS2 version and what can be converted.
2. Select a `.peg` entry. The preview lists its textures (size, format, frames) and shows the selected one; the
   **Info** column says what each pack holds. Select several to convert them together (right-click › **Select all of
   this type** selects them all).
3. Right-click and choose **Convert to .tga…** (or **Packfile › Convert to .tga…**). The dialog lists every texture
   with a tick, its kind, what it **Becomes** and a **Note** when the packfile already has that name. **Tick all**,
   **Tick none** and **Only MPEG-2 backgrounds** change the ticks. Below the list:
   - **Treat black as transparent (MPEG-2 backgrounds)**, with **Threshold** and **Soft edge**: gives the full-screen
     backgrounds transparency the way the PS2 could;
   - **Keep the .peg entries in the packfile**: off replaces each `.peg` with its files; on adds them after it.
4. **Convert**. The textures go where the `.peg` was, as one undo step. A notice says how many were converted and what
   was left out.
5. **Save As** a new packfile; the PC game can now use its textures.

What the textures become:

- A single texture becomes `name.tga`, 32-bit with alpha. The game takes a `.tga` where a `.vbm` was asked for, so
  nothing needs renaming.
- An animated texture becomes its frames (`name_00.tga`…) plus `name.atx` listing them. The game takes the `.atx`
  first, so levels and meshes need no change. The frame rate comes from the PC game's own `.vbm` of that name, or
  15 frames a second; change `frame_time` in the `.atx` if needed.
- **MPEG-2 backgrounds** (menu pictures, Extras pages, multiplayer previews, HUD portraits) are decoded into 24-bit
  `.tga` files, or 32-bit with **Treat black as transparent**. The main menu's spinning planet becomes 150 frames and an
  `.atx` that plays them.
- When two packs (or the packfile itself) hold different textures of one name, Cairn keeps the larger one, and the
  summary says which it kept.

The exact rules are in the [reference](REFERENCE.md#peg-conversion-rules). PS2 meshes and sounds convert with
**Convert meshes…** and **Convert sounds…** (see [Convert the PlayStation 2 version](#convert-the-playstation-2-version)).

### Open a PEG texture pack on its own

Open a `.peg` with **File › Open…**, or with **Open in Cairn** on a `.peg` entry. It opens as a packfile tab listing
the `.tga` files its textures become, under a bar that says what is not converted. The `.peg` is only read: **Save**
writes a new PC `.vpp`. Before saving you can preview, extract (the `.tga` files), **Extract as PNG…**, rename or
remove entries as in any packfile.

### Good to know (packfiles)

- The game finds files by name, ignoring case; when two loaded packfiles hold the same name, which one wins depends
  on where they are (see [How the game finds your files](#how-the-game-finds-your-files)).
- Entry names can be up to 59 characters, but textures, sounds and fonts are cut off by the game after 31. The
  **File types** panel shows a **Names longer than 31 characters** filter while any entry has one.
- Click the problem count in the status bar for the problems; double-click one to select its entry. Each says what is
  wrong and what to do. The codes are in the [reference](REFERENCE.md#packfile-problem-codes).
- **Tools › Settings… › Packfiles**: a `.bak` copy when saving over a packfile, asking before removing entries, the
  folder for work copies, and the PS2 background settings (decoding MPEG-2 backgrounds, black as transparent).
- When the packfile changes on disk, a bar offers **Reload** or **Keep mine**; saving waits until you reload.
- **Help › Packfiles** (**F1**) summarises the format and the game's limits; the
  [reference](REFERENCE.md#packfile-limits) has them in full, with every column, Info line and menu.

### Limitations (packfiles)

- The undo history starts again after each save.
- Levels have no visual preview, only their details; legacy motion files (`.mvf`), editor groups (`.rfg`) and
  Photoshop files show as hex.
- A packfile inside a packfile cannot be opened directly: extract it first.
- PS2 levels cannot be converted. MPEG-2 backgrounds have no transparency of their own, and the main menu
  animation's speed is a guess. A `.peg` opened on its own converts every texture with the current settings.
- Crash recovery keeps your list of changes and the paths of added files, not their data.

## Tables (TBL)

Tables (`.tbl`) are the text files that tell Red Faction what its weapons, characters, items, sounds, effects, HUD
and more are. A table tab is a text editor that knows how the game reads each one.

Around a table tab: the **Outline** of its sections and entries on the left, the editor in the middle, the
**Reference preview** of the file or name at the caret on the right, and the **Problems**, **Usages** and **Compare**
panels in the bottom pane. The **Table** menu appears with a table in front. What you see in the editor is exactly
what is saved.

![A table open in Cairn, with a texture it names previewed on the right](tables.png)

**What you can do**

- Edit any of the game's tables, Alpine Faction's tables and a level's text, with live checks as you type.
- Fix most problems with one click.
- Complete field names, values, file names and names from other tables.
- Preview the textures, meshes, sounds and effects a table names, and see where each was found.
- Jump to where a name is defined, and find every place a name or file is used.
- Compare your modded table with the stock one.
- Save without changing the encoding or line endings the game needs.

### Open a table

- **From disk**: **File › Open…** (**Ctrl+O**), drop it on the window, or double-click it once `.tbl` is associated.
  `samples/tbl` has a small `weapons.tbl` to try.
- **From a packfile**: open the `.vpp` (the stock tables are in `tables.vpp`) and click the table: the preview shows
  it read-only. **Open in Cairn** opens it in a table tab (see
  [Edit a file and put it back](#edit-a-file-and-put-it-back)).
- **From another table**: **Go to Definition** opens the game's own table at the right entry. A table from inside a
  packfile is read-only in place: **Save** asks where to write a copy.
- **A new table**: **File › New › Table**.

Cairn knows the stock tables, a level's `<level>_text.tbl` and Alpine Faction's own tables (see the
[list](REFERENCE.md#tables-cairn-knows)). A table with any other name opens too; the status bar then says **Unknown
table** and only its syntax is checked.

### Find your way around a table

- **Highlighting.** Comments are italic, section headers (`#Primary Weapons`) bold; field names, strings, numbers and
  keywords each have a colour. **File names** are underlined and **names from other tables** have a dotted
  underline: click either to preview it. **Problems** have a wavy underline; point at one to read it.
- **Folding.** Every section, entry and `/* */` comment has a fold box in the margin.
- **Outline.** The left pane lists sections (with their entry counts) and entries, marks entries with errors or
  warnings, and highlights the one at the caret. Click an entry to jump to it, or type in **Filter entries**.
- **Go to an entry.** **Table › Go to Entry…** (**Ctrl+Shift+O**): type part of a name and press **Enter**. **Ctrl+G**
  goes to a line.
- **Find and replace.** **Ctrl+F** and **Ctrl+H**; **Replace all** is one undo step.

### Fix problems with quick fixes

Cairn checks the table each time you pause typing. The **Problems** panel lists every problem with its line and
message; the status bar counts errors and warnings. Click a problem to go to it, or press **F8** / **Shift+F8** for the
next or previous one.

Many problems have a quick fix: a button on the problem's row, the light bulb in the editor, or **Edit › Quick
Fixes…** (**Ctrl+.**). Each fix is one undo step. For example:

- a misspelt field: **Change to $Fire Wait:**;
- a required field missing: **Insert $Damage Type:**;
- a section without its end: **Add #End**; a string without its closing quote: **Add the closing quote**;
- a field the game does not read in this place: **Remove $Weapon Icon:**;
- `//` comments in a file with Unix line endings: **Convert line endings to CRLF**;
- a UTF-8 byte-order mark: **Save without byte-order mark**.

**Errors** are what stops the game while it loads the table; **warnings** are likely mistakes it puts up with (a value
out of range, a file or name it cannot find); **information** is worth knowing. When the game's own copy of the table
has the same missing file or name, it is only information. **Tools › Settings… › Tables** chooses which kinds are
reported.

### Use completion and hover

- **Completion** (**Ctrl+Space**) lists what may be written at the caret: the fields the game reads at this point, in
  its order; a field's accepted values; file names of the right kind from the game and your folders; and names from
  other tables. It also opens by itself after `$` or `+` at the start of a line and after a field's colon. **Enter**
  or **Tab** inserts, **Esc** closes.
- **Hover** a field name to read what it does, its type and range, and for a field Alpine Faction added, the first
  version that reads it ("Alpine Faction 1.1+"). Hover a file name to see which file it finds, and a name from
  another table to see where it is defined.

### Preview a file the table names

Click a file name in the table, or move the caret onto it: the **Reference preview** on the right shows it (images,
sounds, meshes, clips, effects, animated textures) and, below, where Cairn found it and which tables use it. Files
are found the way the game finds them, so a `.tga` that exists only as `.dds` and a clip written `.mvf` are found, and
the details say so. A name that cannot be found says where Cairn looked.

A name from another table (an ammo type, a sound, a vclip) shows the entry that defines it, with its table and line.
**Go to definition** and **Open in Cairn** at the top of the pane open it.

### Go to definition and find usages

Cairn indexes every table in the game, your search folders and your open tabs, and keeps the index up to date as you
type.

- **Go to definition**: put the caret on a name from another table and press **F12** (**Table › Go to Definition**),
  or **Ctrl+click** it. The defining table opens at the entry (a game table opens read-only). On a file name, it opens
  the file when Cairn edits that type.
- **Find usages**: put the caret on an entry's name, a name from another table or a file name and press **Shift+F12**
  (**Table › Find Usages**). The **Usages** panel lists every use: table, entry, line, field and text. Click a row to
  go there.

### Compare a modded table with the stock one

With your table in front, choose **Table › Compare with Stock**. Cairn finds the game's own table of that name and
lists, in the **Compare** panel, every entry that was **added**, **removed** or **changed**, with each changed field
and its **Stock** and **This table** values. Click a row to go to it; a changed field also shows the stock lines.
Untick **Only changes** to list unchanged entries too. The comparison follows your edits; **Compare again** looks for
the stock table afresh, for example after you change the game folder.

### Save a table safely

**Save** (**Ctrl+S**) writes the text exactly as it is, in the file's own encoding and line endings; the status bar
shows both (for example **ANSI (Windows-1252)** and **CRLF**).

- If you type characters the encoding cannot hold, Save offers **Save as UTF-8**; such characters may look wrong in
  the game.
- If the table has errors, Save asks first: the game would stop on them.
- A table from a packfile or the game data is never written back there: Save asks where to write a copy.

For which tables the game accepts from a level, a client mod or a full mod, see
[Edit the game's tables safely](#edit-the-games-tables-safely).

### Good to know (tables)

The game reads tables strictly. The rules that catch people most:

- **Order matters.** Fields are read in a fixed order, each only in its place. A misplaced, misspelt or unknown field
  stops the game with "Expected … but found …". Every space in a field name counts.
- **Keep Windows line endings (CRLF) and no byte-order mark.** With Unix line endings a `//` comment swallows the rest
  of the file; a byte-order mark stops the game at the first line.
- **Strings** are in double quotes, on one line. **Numbers** are plain decimals (`-1`, `0.5`), no `1e3` or `+1`.
  Booleans are `true` or `false`.
- **Lists and flags** go in brackets, with quoted items separated by spaces: `("alt_fire" "underwater")`.
- **One file wins whole.** A modded `weapons.tbl` replaces the stock one entirely; there is no merging.

The complete rules, including Alpine Faction's line-based tables, are in the
[reference](REFERENCE.md#how-the-game-reads-tables). **Help › Tables › Table syntax** (**F1**) has a summary.

### Limitations (tables)

- The language columns of `<level>_text.tbl` are not checked value by value.
- Tables Cairn has no description of (an unknown name, Dash Faction's `dashoptions.tbl`) get syntax checks only.
- Compare with Stock looks only in the game folder's packfiles, and compares entries and values, not comments or
  formatting.
- The index of tables is built after Cairn has read the game data; until then, go to definition and find usages may
  miss tables (the status bar says so).

## Fonts (VF)

Fonts (`.vf`) are Red Faction's bitmap fonts: one small picture (a *glyph*) per character, all of the same height.

The game's fonts are in `ui.vpp`: `bigfont.vf` and `smallfont.vf` (large HUD numbers and older text) and
`rfpc-large.vf`, `rfpc-medium.vf` and `rfpc-small.vf` (menus, HUD and scoreboard). A font tab shows every glyph in a
grid, a line of sample text drawn the way the game draws it, and the selected glyph's details in the inspector on the
right. The **Problems** panel is in the bottom pane; the **Font** menu appears with a font in front.

**What you can do**

- See exactly how any text will look and measure in the game.
- Change a glyph's width and spacing, and the font's height and default spacing.
- Paint a glyph's pixels, or replace a glyph with a picture from a file or the clipboard.
- Add, change and remove kerning pairs.
- Add or remove characters, and change the pixel format.
- Edit the whole font in any image editor through an image sheet.
- Catch what would make the game refuse the font ("Font too big!") before you ship it.

### Open a font

- **From disk**: **File › Open…** (**Ctrl+O**), drop it on the window, or double-click it once `.vf` is associated.
- **From a packfile**: open `ui.vpp` (or a mod's packfile) and click a `.vf` entry: the preview shows the sample text
  and every character. **Open in Cairn** opens it in a font tab.

### See how text will look in the game

1. Type in the **Sample** box under the glyph grid. The text is drawn with the font's own spacing and kerning, at the
   **Zoom** you pick (1× to 8×, pixels stay sharp).
2. The line under the sample gives the size the game measures for the text, and lists the characters the font does
   not have: the game leaves a gap for each.
3. **All characters** shows every character of the font instead of the sample.

**Backdrop** draws the glyphs on dark (the stock fonts are white), a checkerboard or light.

### Inspect a glyph

Click a glyph in the grid, or move with the arrow keys (**Home** and **End** jump to the first and last). The
inspector shows it enlarged with its **Character**, **Code**, **Width** (its pixels), **Spacing** (how far the pen
moves after it), its kerning pairs, and any problems that concern it. Hover a glyph in the grid for its width and
spacing.

### Edit a font and save it

1. Open the font. To change a stock font, open it from `ui.vpp`, then **File › Save As…** a copy for your mod; never
   change the game's own `ui.vpp`. A font opened from a mod's packfile with **Open in Cairn** is a work copy: saving
   the tab offers **Update packfile** (see [Edit a file and put it back](#edit-a-file-and-put-it-back)).
2. Edit as in the sections below. Every edit is one undo step (**Ctrl+Z**, **Ctrl+Y**), and the checks run again.
3. Save (**Ctrl+S**). When the font has errors, Cairn asks once first.
4. Ship the font in your mod's packfile under the stock name it replaces (see
   [Release a mod in a packfile](#release-a-mod-in-a-packfile)).

### Change a glyph's width and spacing

Select the glyph. In the inspector, **Width**, **Spacing** and **User data** are number boxes: type a value and press
**Enter**, or use the arrows or the mouse wheel. A narrower width cuts columns off the right of the glyph; a wider one
adds clear columns. Under **Font**, **Height** (rows added or cut at the bottom) and **Default spacing** (the gap for
missing characters) work the same way.

### Paint a glyph's pixels

The enlarged glyph at the top of the inspector is a small pixel editor. The left button paints the value in the box
next to **Pencil** and **Eraser**: coverage 0 to 14 for a monochrome font, a palette colour for an indexed one (click
the palette to pick), or four hex digits (`FFFF`) for an RGBA 4444 font. The right button, or the left with **Eraser**,
clears. One stroke is one undo step.

### Replace a glyph with a picture

1. Select the glyph and choose **Font › Replace Glyph from Image…** (**Ctrl+R**, or **Replace Glyph…** on the
   toolbar), or copy a picture in another program and choose **Font › Paste Glyph Image** (**Ctrl+Shift+V**).
2. The window shows the picture and the glyph it becomes:
   - **Height**: scale the picture to the font height, or keep its size (top- or bottom-aligned).
   - **Width**: the picture's, or the glyph's present width.
   - **Coverage from**: the picture's transparency, its brightness (light on dark) or its darkness (dark on light);
     **Automatic** picks transparency when the picture has any.
   - **Threshold**: 0 keeps soft edges; higher values make each pixel solid or clear.
   - **Move the spacing with the width** keeps the gap after the glyph.
3. **Replace**. A colour font takes the nearest colours it can hold.

### Edit kerning

A kerning pair moves one character closer to (or further from) a particular character before it. The inspector's
**Kerning** list shows every pair the selected character takes part in, with its offset (type or spin it; 0 removes
it) and **Remove**. To add one, choose **followed by** (this character first) or **after** (the other one first), type
the other character (or `#` and its code, `#65`) and an offset, and press **Add pair**. Cairn keeps the pairs in the
order the game needs.

The game never applies a pair that involves glyph number 128 or later (character 160 and above in a font that starts
at space); the list and the Problems panel say so.

### Add or remove characters, change the height or the format

- **Font › Add or Remove Characters…**: the first and last character, and the width of new blank glyphs. Removed
  characters lose their kerning pairs.
- **Font › Change Height…**: the new height, and whether rows are added or cut at the bottom or the top.
- **Font › Pixel Format**: 4-bit monochrome, 8-bit indexed or RGBA 4444. Monochrome to a colour format looks the same
  in the game; a colour font to monochrome keeps only the transparency.

After each, the inspector's **Texture** row and the Problems panel show whether the font still fits the game's font
texture.

### Edit the whole font in an image editor

1. **Font › Export Image Sheet…** (**Ctrl+Shift+E**, also under **File › Export**). Choose **Extra cell width** (room
   to widen glyphs), whether a **Guide colour** marks the space around the glyphs, and for monochrome fonts grey on
   black or white on transparent. Save the `.png`; a `.json` file of the same name is written beside it with each
   glyph's metrics and the kerning.
2. Edit the PNG in any image editor, keeping its size. Paint inside each glyph's cell; to widen a glyph, paint into
   the guide colour to its right and raise its `width` in the `.json`.
3. **Font › Import Image Sheet…** (**Ctrl+Shift+I**, also under **File › Import**) and pick the `.png` or `.json`. The
   import is one undo step; a sheet you did not change changes nothing.

### Good to know (fonts)

- **"Font too big"**: the game builds a texture of at most 256 × 256 pixels from the glyphs and refuses a font whose
  glyphs do not fit. The inspector's **Texture** row shows how full it is.
- The game spaces text by each glyph's **spacing**, not its width, plus any kerning pair. A character the font lacks
  moves the pen by the default spacing and draws nothing.
- The game shows fonts with 4 bits per colour channel; Cairn shows the colours as the game will.
- Characters are named by the Windows-1252 code page, as the game reads text.
- Double-click a problem (or press **Enter**) to select its character. The codes, pixel formats and the image sheet
  layout are in the [reference](REFERENCE.md#font-problem-codes).

### Limitations (fonts)

- The pixel editor has a pencil and an eraser only; use an image sheet for bigger changes.
- Image sheets need the `.json` Cairn wrote; sheets from other font tools are not read.
- A font made for another code page (a Russian translation, for example) shows its glyphs under Western character
  names.
- Cairn does not make fonts from TrueType fonts. Alpine Faction can use a TrueType font directly where a font is asked
  for by name (`name.ttf:size`); the stock interface still asks for `.vf` files.

## Volition bitmaps (VBM)

A Volition bitmap (`.vbm`) is Red Faction's own image format: one or more frames of 16-bit pixels with optional mip
levels, and a frame rate when it is animated. Interface panels, animated level textures and many effect textures are
VBMs.

A bitmap tab shows the animation large in the middle, the bitmap's facts and actions on the right, and a strip of
every frame along the bottom. The **Problems** panel is in the bottom pane; the **Bitmap** menu and the **Convert to
ATX…** and **Export Frames…** buttons appear with a bitmap in front.

**What you can do**

- Play an animated bitmap, step through its frames, zoom, and check its alpha and mip levels.
- Change the frame rate.
- Replace frames with images, add, remove, duplicate and reorder frames, also by drag and drop or copy and paste.
- Change the pixel format and the number of mip levels.
- Make a new bitmap from a set of images.
- Export frames as TGA or PNG images.
- Convert a bitmap to an animated texture (`.atx`).

### Open a bitmap

- **From disk**: **File › Open…** (**Ctrl+O**), drop it on the window, or double-click it once `.vbm` is associated.
- **From a packfile**: open the packfile (for example `maps2.vpp` or `ui.vpp`) and click a `.vbm`: the preview plays
  it. **Open in Cairn** opens it in a bitmap tab; saving the tab offers to update the packfile (see
  [Edit a file and put it back](#edit-a-file-and-put-it-back)).

An animated bitmap starts playing, and pauses while another tab is in front.

### Look at the frames

1. **Play** / **Pause** (**Space**) plays at the bitmap's frame rate. **|<**, **<**, **>** and **>|** (or **comma**
   and **period**) step through the frames.
2. Click a frame in the strip to show it; **Ctrl**+click and **Shift**+click select several. The frame on show has an
   accent outline, so you can see where the animation is while other frames stay selected.
3. **Fit** and **100%** set the zoom; the mouse wheel and **+** / **−** zoom in and out. **Smooth** blends a zoomed
   bitmap, **Pixels** shows each pixel as a sharp square. Until you pick one, Cairn uses pixels above 100% and smooth
   at 100% and below.
4. **Alpha** shows the transparency as grey (white is opaque); **Checkerboard** turns the pattern behind clear pixels
   on or off.
5. For a bitmap with mipmaps, the mip box shows a smaller level at full size, so you can see how much detail a
   distant surface keeps.

### Change the frame rate

Type the new rate in **Frame rate** on the right and press **Enter** (or use its arrows or the mouse wheel). Playback
follows at once.

### Replace a frame with an image

1. Select the frame and choose **Bitmap › Replace Frame…** (**Ctrl+R**), or **Replace frame…** on the right.
2. Pick a `.tga`, `.png`, `.jpg`, `.dds`, `.bmp` or another `.vbm` (its first frame).
3. If the image is another size, the resize window opens (next section). The image is converted to the bitmap's pixel
   format, and the frame's mip levels are made again from it.

### Fit images of another size

Whenever an image going into a bitmap (replace, add, drop or paste) is not the frame size, **Resize to the frame
size** shows the image beside the frame it will become:

- **Filter**: **Nearest (sharp pixels)** for pixel art and small icons, **Bilinear (smooth)**, or **High quality**
  (the sharpest smooth result).
- **Fit**: **Stretch to the frame** fills it and may change the shape; **Keep aspect (transparent padding)** fits the
  whole image inside, with clear borders; **Crop the centre** fills the frame and cuts off what sticks out.

**Resize** uses your choice for every image of that drop or paste and remembers it. Tick **Use this every time without
asking** to skip the window; **Tools › Settings… › Volition bitmaps** turns the question back on.

### Add, remove and reorder frames

- **Bitmap › Add Frames…** (**Insert**) adds images after the selected frames, in the order you pick them (a `.vbm`
  adds all its frames).
- **Drop image files on the frame strip** to add them where you drop; an accent line shows the place.
- **Drag frames along the strip** to reorder them. Frames dragged from another bitmap's strip are copied in.
- **Ctrl+C** and **Ctrl+V** copy and paste frames, within a bitmap or between bitmap tabs. **Ctrl+V** also pastes a
  picture copied in another program, or image files copied in Explorer, as new frames. Copying also puts the first
  frame on the Windows clipboard as a picture.
- **Duplicate Frames** (**Ctrl+D**), **Remove Frames** (**Delete**), **Move Earlier** / **Move Later** (**Alt+Left** /
  **Alt+Right**) and **Reverse Frame Order** act on the selection. The last frame cannot be removed.
- **Bitmap › Pixel Format** converts every frame to 1555, 4444 or 565; **Bitmap › Mip Levels** sets how many mip
  levels each frame has.

### Make a new bitmap from images

1. Choose **File › New › Volition bitmap** and pick the images in the order they should play (a numbered set such as
   `flame_00.tga`… sorts that way by name).
2. In **New VBM** check the **Size** (the first image's; the others are resized to it), the **Pixel format** (Cairn
   suggests 565 for opaque images, 1555 for on/off transparency and 4444 for soft transparency), the **Frame rate**
   and the **Mip levels** (interface images need none; textures on level geometry usually want a few).
3. **Create** opens the bitmap in a new tab; save it with **Ctrl+S**.

### Export frames as images

1. Choose **Bitmap › Export Frames…** (**Ctrl+Shift+E**, or **File › Export › VBM Frames as Images…**).
2. Pick the **Folder**, the **File name** stem, **TGA** or **PNG**, and **All frames** or **Selected frames**. The
   window lists the names it will write: `stem_00.tga`, `stem_01.tga`…
3. **Export**. Existing images are replaced only after you confirm.

### Convert a bitmap to an animated texture

1. Choose **Convert to ATX…** on the toolbar, on the right or in the **Bitmap** menu (also **File › Export › VBM as
   ATX…**).
2. The **Import VBM** window opens for the bitmap as it is in the tab, unsaved edits included: pick the folder, the
   `.atx` name and the frame names, as in [Convert an old animated VBM](#convert-an-old-animated-vbm).
3. The new `.atx` opens in its own tab.

### Good to know (bitmaps)

- Every edit is one undo step, and frames you do not touch are kept exactly.
- Fewer bits per channel lose colour or transparency: 565 has no transparency, 1555 makes each pixel opaque or clear,
  4444 has 16 levels.
- Textures on level geometry and meshes need sides that are powers of two; interface images do not.
- The game keeps the frame count in one byte, so a bitmap can have at most 255 frames.
- **Tools › Settings… › Volition bitmaps** sets what **New VBM** starts with (frame rate, pixel format, mipmaps) and how
  images of another size are resized. Facts, pixel formats and problem codes are in the
  [reference](REFERENCE.md#bitmap-settings).

### Limitations (bitmaps)

- There is no painting; edit a frame in an image editor and use **Replace Frame**.
- **New VBM** sizes are limited to 4096 pixels a side.
- Frames cannot be dragged out of Cairn as files; use **Export Frames**.
- A picture pasted from another program has whatever transparency that program put on the clipboard; many put none,
  and such a picture is pasted fully opaque.

## Sounds (VSE, VMU, WAV, OGG)

The Sounds module plays sound files and converts them to WAV or Ogg Vorbis: the PC game's `.wav`, `.ogg` and `.aif`
files, and the PlayStation 2 version's sound effects (`.vse`) and music (`.vmu`).

A sound tab shows the play and zoom toolbar at the top, a waveform of each channel in the middle and the details on
the right. The **Problems** panel is in the bottom pane; the **Sound** menu appears with a sound in front. Sound tabs
are read-only: Cairn plays and converts sounds but does not edit them.

**What you can do**

- Play a sound and its loop, and see where the loop is.
- Zoom into the waveform.
- Read everything the file says about itself: format, rate, channels, length, loop.
- Convert a sound to a 16-bit WAV or an Ogg Vorbis file, keeping its loop points.
- Convert many sounds in a packfile at once, for example the whole PS2 version.

### Open a sound

- **From disk**: **File › Open…** (**Ctrl+O**), drop it on the window, or double-click it once its type is
  associated (see [File associations](#file-associations); `.wav` and `.ogg` are left unticked by **Select all** there,
  since other programs use them too).
- **From a packfile**: click a sound entry; the preview plays it with a waveform (see
  [Preview what is inside](#preview-what-is-inside)). **Open in Cairn** on the preview opens it in a sound tab.

### Play a sound and its loop

1. **Play** / **Pause** (**Space**), **Stop** (back to the start), **Home** (go to the start).
2. **Loop** (**L**) repeats the sound's loop without a gap: the loop points the file carries, or the whole sound when
   it has none. A looping PS2 sound starts with **Loop** on; the loop is shaded in the waveform.
3. Click the waveform to move the play position. The toolbar shows the position and the length.
4. **Volume** sets the volume of every sound tab, and is remembered.

The sound pauses when another tab comes to the front.

### Zoom into the waveform

The mouse wheel zooms around the pointer and **Shift**+wheel scrolls; **+**, **−** and **Fit** on the toolbar zoom
around the middle or show the whole sound. Zoomed in, the view follows the play position.

### Convert a sound to WAV or Ogg Vorbis

1. Choose **Sound › Convert…** (**Ctrl+Shift+E**, or **Convert…** on the toolbar).
2. Pick the **Format**:
   - **WAV (16-bit PCM)**: the sound exactly as decoded.
   - **Ogg Vorbis (smaller, lossy)**: set the **Quality** from q-1 (smallest) to q10 (best). The default q5 is about
     160 kbit/s for stereo music and far less for a mono effect; the text beside the slider gives the rate. The stock
     game loads `.wav`; Alpine Faction also loads `.ogg`.
3. Tick **Keep loop points** to write the loop into the file, where many tools and players read it. (The game loops a
   sound because a table or level asks it to, not because of the file.)
4. Choose **Where**: into the packfile the sound came from (as a new entry; the packfile changes when you save it),
   next to the source, or a folder. **Next to the source** is off when that is the game folder.
5. The new file is named after the sound (`alarm_03.vse` gives `alarm_03.wav`). If that name is taken, Cairn uses a
   free one such as `alarm_03 (2).wav`, unless you tick **Replace**; then it asks before replacing.
6. **What the conversion changes** lists what is approximated, such as re-encoding a lossy sound or a PS2 sound's
   exact playing rate. Nothing is refused for them. Press **Convert**.

A format the sound is already in is not offered: encoding an Ogg again would only lose quality. **File › Save As** on
a sound tab also writes a WAV.

### Convert many sounds in a packfile

1. In the packfile, select the sounds (tick the **Sounds** types in the **File types** panel to list only them, then
   **Ctrl+A**).
2. Choose **Packfile › Convert sounds…** (also on the right-click menu). Entries that are not sounds are left out.
3. Choose the format, quality, loop points, where the files go and whether to replace taken names, as for one sound.
4. **Convert**. Progress shows in the packfile's status bar, where **Cancel** stops with nothing changed. New entries
   are added as one undo step.
5. A report lists sounds that could not be converted, sounds left out (already in that format, or their new name is
   taken) and what was approximated.

This is the quickest way to bring the PS2 version's sounds to the PC game; see
[Convert the PlayStation 2 version](#convert-the-playstation-2-version).

### Good to know (sounds)

- The game treats a sound as Ogg Vorbis only when its name ends in lower-case `.ogg`; Cairn always names it that way.
- A PS2 sound plays at a rate a little off the standard one (a 22,050 Hz sound plays at 22,043 Hz). Cairn plays and
  converts at the standard rate and says so.
- **Tools › Settings… › Sounds** sets what **Convert** offers first: the format, the Ogg quality, where the files go
  and whether to keep loop points. Replacing is never a setting; you tick it each time.
- The **Info** column of a packfile shows each sound's rate, channels, length and whether it loops.
- The details, PS2 formats and problem codes are in the [reference](REFERENCE.md#sound-details-pane).

### Limitations (sounds)

- Sounds are not edited: no trimming, gain or resampling.
- **File › Save As** writes WAV only; use **Convert…** for Ogg Vorbis.
- MP3 files play in packfile previews but do not open in sound tabs.
- AIFF loop markers are not read.
- Cairn never writes `.vse` or `.vmu` files.

## Troubleshooting and FAQ

### Game folder and libraries

**Cairn did not find my game, or found the wrong copy.** Open **Tools › Settings… › General**, press **Auto-detect**
or **Browse…** and pick the folder that holds `RF.exe`. The page says whether it looks like an install. See
[Game folder and search folders](#game-folder-and-search-folders).

**The Animations, Effects or Packfiles library is empty.** No game folder or search folder is set, or it is the wrong
one. Set it as above. After copying new files in, press the library's refresh button. The first library build after
setting the game folder reads every packfile and can take a while; later starts are quick.

### Missing files and textures

**A texture is grey or listed as missing.** Cairn finds files by name only, like the game. Check the spelling, then
put the file next to the file that uses it or in a search folder. For a mesh, **Locate the file…** on the problem
lets you pick it. For a level, see [Add a custom texture to a level](#add-a-custom-texture-to-a-level).

**A name longer than 31 characters.** The game cuts texture, sound and font names off after 31 characters (including
the extension), so the file is never found. Rename it and whatever uses it. In a packfile, the **File types** panel's
**Names longer than 31 characters** filter lists them.

**My change does not show in the game.** Check that the file's name matches exactly what the level, mesh or table
asks for; that your packfile is in a folder the game loads (sub-folders are not searched); and that a file of the same
name loaded later does not win. By default a level packfile cannot replace the game's own files, and only a mod can replace
`weapons.tbl` and similar tables. See [How the game finds your files](#how-the-game-finds-your-files).

**Can Cairn write DDS files?** Yes: in a packfile, **Packfile › Convert images to DDS…** (**Ctrl+Shift+D**). See
[Convert images to DDS](#convert-images-to-dds).

### Opening and saving

**A file opened read-only, or Save asks where to save.** One of these:

- It came from inside a packfile (from a library, for example). Cairn never writes back into a packfile that way. To
  change the file inside the packfile, open the packfile and use **Open in Cairn** on the entry (see
  [Edit a file and put it back](#edit-a-file-and-put-it-back)).
- It is a static mesh (`.v3m`) or an exporter or PS2 mesh: these open read-only. Use **Save As…** or **Convert…**.
- It is a sound: sound tabs only play and convert.
- It is an effect in an older format version: press **Convert** on its bar first (see
  [Edit an older effect](#edit-an-older-effect)).
- It could not be read: the reason is in **Problems**.

**Save As does not offer the game folder.** On purpose: that folder holds the game's own files, and the game does
not load loose files from it anyway. You can still browse to it yourself, but to use your work in the game, put it in
a packfile in the right folder (see
[Release a mod in a packfile](#release-a-mod-in-a-packfile)).

**A packfile will not save.** Its problems include an error (a name the game cannot use, two entries whose names
differ only in case, an added file that changed or vanished, or the packfile changed on disk). The list shows what
to fix; the [reference](REFERENCE.md#packfile-problem-codes) explains each code. If the packfile changed on disk,
press **Reload** on its bar first.

**"The system clipboard is busy" or the clipboard is unavailable.** Another program held the Windows clipboard.
What you copied still pastes inside Cairn; try again for other programs.

### Sounds and previews

**A sound does not play.** Check **Volume** on a sound tab and Windows' own volume. MP3 files play only in packfile
previews. A damaged file says why in **Problems**; the other sounds still play.

**The preview differs from the game.** Previews follow the game's rules, but some things are approximations: texture
format simulation, effect particles under spacewarps and blending. The game is the final check.

### Animations and meshes

**"No preview mesh" or "No mesh in the library has N bones".** A clip needs a character with exactly its number of
bones. Set the game folder or add a folder holding the mesh, or pick any mesh in the **Preview mesh** box to see it
anyway (it will play wrongly).

**The timeline shows "Bone 0", "Bone 1"…** The preview mesh does not fit the clip, so there are no bone names, and
mirroring, conforming and pose editing are unavailable. Pick a mesh with the clip's bone count.

**There is no gizmo when I press W or E.** The badge at the bottom left says why: pose editing needs a fitting preview
mesh, the bind pose off and a bone selected. In a mesh tab, select a joint, sphere or prop point first.

**My edit changed the whole animation, not just this moment.** **Key** (auto-key) was off, so the drag edited every
frame. Undo, turn **Key** on and drag again. IK drags need **Key** on too.

**Pasted keys "found no bone here and were skipped".** Keys paste by bone name; the other clip is for another rig.
Use retarget instead (see [Retarget animations to another character](#retarget-animations-to-another-character)).

**Retarget: the character floats or sinks, or a limb is "fully stretched".** Use **Standing / locomotion** for anything
that stands and give both rigs a stand clip as **Reference clip**. A stretched limb means the target's limb is too
short to reach; try another preset, or fix the moment afterwards with IK.

**A talking face stops moving after retargeting or export.** Morph (vertex) animation only fits the mesh it was made
for; retargeting, glTF export and conforming drop it.

**glTF import: the animation is offset in time, or some bones do not move.** Change **Start at** in the import
options; map the missing bones in the **Bone map** tab, or rename them in Blender to match. REDUX reads only
`.gltf` with its `.bin`, not `.glb`. See [Import and export glTF](#import-and-export-gltf).

### Cairn itself

**Cairn offered to recover files at start.** It closed with unsaved work last time. Restore what you want; **Not
now** keeps the copies for next time.

**A pane is gone.** **Ctrl+Shift+L** and **Ctrl+Shift+M** (or the **View** menu) show the left and bottom panes. Drag
the splitters to resize; sizes are remembered.

### Where Cairn keeps its files

| What | Where |
|---|---|
| Settings | `%APPDATA%\Cairn\settings.json` |
| Retarget profiles | `%APPDATA%\Cairn\profiles` |
| Recovery copies | `%LOCALAPPDATA%\Cairn\recovery` |
| Crash log | `%LOCALAPPDATA%\Cairn\crash.log` |
| Caches | `%LOCALAPPDATA%\Cairn` |
| Packfile work copies | `%LOCALAPPDATA%\Cairn\work` (or the folder set in **Settings › Packfiles**), deleted when the packfile closes |

To start over with default settings, close Cairn and delete `settings.json`; the next start sets everything up
again.

## Glossary

**Alpine Faction.** The community patch for Red Faction that practically every player runs. Cairn checks files
against what Alpine Faction does, and shows the first Alpine Faction version a feature needs where it matters.

**Alpha, alpha mask.** The transparency of an image's pixels. An animated texture can take its alpha from a separate
mask image.

**Animated texture (`.atx`).** Alpine Faction's text file that lists frame images and their timing (1.4.0+).

**Clip (`.rfa`).** One character animation: a walk, a reload, a death. Tables spell clips `.mvf`.

**Character mesh (`.v3c`), static mesh (`.v3m`).** A model with a skeleton (characters), or without one (items,
props, level objects). Tables spell them `.vcm` and `.v3d`. The 3ds Max exporter's own source files also use
`.vcm` and `.v3d`, and the PS2 version has `.rfc` and `.rfm`; Cairn converts all four.

**Entry.** One file inside a packfile.

**Frame time, frame rate.** How long each frame of an animated texture shows (`frame_time`, in the `.atx`), or how many
frames a bitmap or effect plays per second.

**Game folder.** The Red Faction install, the folder that holds `RF.exe`. Set under **Tools › Settings… › General**.

**Glyph.** The picture of one character in a font.

**Kerning pair.** A spacing correction applied when one particular character follows another.

**Mip levels (mipmaps).** Smaller copies of a texture, each half the size of the one before, used for distant
surfaces.

**Mod (`mods` folder).** A set of packfiles in `mods\<name>`, loaded when the game starts with `-mod <name>`. A mod
may replace any stock file, tables included.

**Packfile (`.vpp`).** The game's archive: a flat list of files with no folders. See
[How the game finds your files](#how-the-game-finds-your-files).

**PEG (`.peg`).** The PlayStation 2 version's texture pack. Cairn converts its textures to `.tga` and `.atx`.

**Problems.** Cairn's live checks of a file against what the game will do with it: **errors** the game cannot cope
with, **warnings** that probably do not do what you want, and **information**.

**Search folders.** Extra folders where Cairn looks for the files your files name, after the file's own folder and
before the game. See [Game folder and search folders](#game-folder-and-search-folders).

**Search order.** The order Cairn looks for a file by name: the open file's own folder (or packfile), the search
folders, then the game.

**Stock.** The files that come with the game, as opposed to *modded* ones.

**Table (`.tbl`).** A text file that defines weapons, characters, items, sounds and more.

**`user_maps`.** The game folder's folder for levels: `user_maps\single` and `user_maps\multi` hold level packfiles,
and `user_maps\textures` holds the level editor's custom textures.

**Volition bitmap (`.vbm`).** Red Faction's own 16-bit image format, often animated.

**Work copy.** The temporary file Cairn extracts when you open a packfile entry in another program or in a Cairn tab;
saving it offers to update the packfile.

Terms for clips and meshes (bones, keys, ticks, states and actions, weights, retargeting) are explained in
[Animations and meshes](#animations-and-meshes-rfa-v3c-v3m).

## Keyboard shortcuts

These work in every tab. Each module adds its own; **Help › Keyboard Shortcuts** lists every one, grouped by
document type, and each chapter names the shortcuts of its tasks. The tab in front decides what a key does: a
module's shortcut (such as **Ctrl+Shift+M** for an animated texture's Problems panel) takes the place of a global one
while its tab is in front. Single keys without **Ctrl**, **Alt** or **Shift** are ignored while you type in a text
box. See [Help and keyboard shortcuts](#help-and-keyboard-shortcuts).

| Keys | Action |
|---|---|
| **Ctrl+O** | Open a file |
| **Ctrl+S** | Save |
| **Ctrl+Shift+S** | Save as |
| **Ctrl+W**, **Ctrl+F4** | Close the tab |
| **Ctrl+Shift+T** | Reopen the last closed tab |
| **Ctrl+Tab**, **Ctrl+Shift+Tab** | Next or previous tab |
| **Ctrl+Z** | Undo |
| **Ctrl+Y**, **Ctrl+Shift+Z** | Redo |
| **Ctrl+Shift+L** | Show or hide the left pane |
| **Ctrl+Shift+M** | Show or hide the bottom pane |
| **F1** | Help for the tab in front (keyboard shortcuts when no tab is open) |
| **Alt+F4** | Exit |

Shortcuts you will meet in several modules:

| Keys | Usually |
|---|---|
| **Space** | Play or pause |
| **F2** | Rename |
| **Del** | Remove or delete |
| **Ctrl+D** | Duplicate |
| **Ctrl+E** | Extract (packfiles), focus the source editor (animated textures) |
| **Ctrl+Shift+E** | Export or convert (sounds, bitmaps, font image sheets) |
| **Ctrl+R** | Replace with an image (bitmap frames, font glyphs) |
| **Q**, **W**, **E**, **R** | Hide the gizmo, move, rotate, scale (animations, meshes, effects) |
| **Ctrl+F**, **Ctrl+H** | Find, find and replace |
