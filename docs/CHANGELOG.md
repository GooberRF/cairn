# Changelog

## 1.0.0

First release of Cairn. It combines ATX Workbench 1.1.0 and RFA Workbench 1.0.1 in one application and adds
modules for effect meshes (`.vfx`), packfiles (`.vpp`), tables (`.tbl`), fonts (`.vf`), Volition bitmaps
(`.vbm`) and sounds (`.wav`, `.ogg`, `.aif` and the PS2 `.vse` and `.vmu`). Cairn targets Alpine Faction 1.0 to 1.5: its checks follow Alpine Faction, and nothing is reported merely
because it needs Alpine Faction. Everything the two apps did, Cairn does; the notes below list what is new and what
works differently if you are coming from them.

**Suite**

- Added **tabs for every document type** (main window): animated textures, clips, meshes, effects, packfiles,
  tables, fonts, bitmaps and sounds open side by side, and the menus, panes and status bar follow the tab in front.
- Added **one File menu for all modules** (File): **New** has one entry per document type, **Import** and
  **Export** collect every module's importers and exporters, and **Open…** accepts every supported type.
- Changed **module menus** (menu bar): **Frames**, **Clip**, **Mesh**, **Effect**, **Packfile**, **Table**,
  **Font**, **Bitmap** and **Sound** appear only while a document of their type is in front.
- Changed **settings** (Tools › Settings…): one dialog with a **General** page (theme, game folder, search
  folders) shared by every module, then one page per module. Settings live in `%APPDATA%\Cairn`; recovery copies,
  caches and the crash log in `%LOCALAPPDATA%\Cairn`.
- Added **import of the old apps' settings** (first run): theme, game folder, search folders and recent files from
  ATX Workbench and RFA Workbench, RFA Workbench's other preferences and its retarget profiles. The old apps' files
  are read only, never changed.
- Changed **file associations** (Tools › Settings… › File associations; also Tools › File Associations…): a
  settings page with an **Open with Cairn** check box for every extension Cairn opens, `.vpp` included, and the
  program that opens each one now. Per Windows user, applied on **OK**. Where you chose a default yourself in
  Windows, Cairn adds itself to **Open with** and offers **Choose default…** (Windows' own chooser) instead of
  overriding your choice. The old RFA/ATX Workbench associations can be removed from the same page.
- Changed **keyboard shortcuts** (everywhere): shortcuts are scoped to the document type in front, so the same key
  can do different things in an animated texture and in a clip. **Help › Keyboard Shortcuts** (**F1**) lists them
  by document type.
- Added **Reopen Closed Tab** (File menu, **Ctrl+Shift+T**) and crash recovery for every document type.
- Changed **single instance** (Explorer, command line): files opened while Cairn runs go to the running window,
  whatever their type.
- Added **shared previews** (packfile preview pane, table reference preview): the same image, sound, mesh, clip,
  effect and animated texture previews and file details in every module that shows a file by name, found the way the
  game finds it (a `.tga` that exists only as `.dds`, `.v3d` and `.mvf` names).
- Changed **left and right panes** (main window): the left pane belongs to the document in front (a library, the
  packfile's file types, a table's outline) and a document can add a pane on its right (a packfile's preview, a
  table's reference preview).
- Added the **Packfiles browser** (left pane; the start page's first tab, and a second tab beside a packfile): every
  `.vpp` in the game folder, its `user_maps` folders, each mod folder under `mods` and your search folders, grouped
  by folder (collapsible headings, as in the Animations library) with sizes, a filter box and a refresh button. Double-click or **Enter** opens a packfile; the context
  menu adds **Show in Explorer** and **Copy path**. The start page remembers its own left tab.
- Changed the **Recent list** (start page, File › Open Recent): a file opened from inside a packfile with **Open in
  Cairn** is listed as "packfile › file" and reopens from the packfile, instead of as a temporary work copy whose
  link broke once the copy was cleaned up. Such temporary paths left by earlier builds are no longer listed.
- Changed **Save As and export dialogs** (every module): they never start in the game directory or a folder inside
  it, not even for a file opened from there; they start in the last folder saved to outside it, or in Documents.

**Animated textures (ATX)**

- Changed **Frames menu** (menu bar): adding frames, sequences, locating files, reordering and **Bulk Frame
  Timing…** (**Ctrl+T**) are in the new **Frames** menu, shown with an animated texture in front.
- Changed **Import VBM** (File › Import): **Import VBM...** (**Ctrl+Shift+I**) and **Import VBM from VPP...** moved
  under File › Import. Opening a `.vbm` now opens it in a bitmap tab, whose **Convert to ATX…** runs the same import.
- Changed **new animated texture** (File › New › Animated texture…).
- Changed **frame clipboard, find and comment commands** (Edit menu): **Cut Frames**, **Copy Frames**, **Paste
  Frames**, **Find...**, **Find and Replace...** and **Toggle Comment** appear in the Edit menu with an animated
  texture in front.

**Animations and meshes (RFA)**

- Changed **New Clip** (File › New › Animation clip…): still **Ctrl+N** with a clip or mesh in front.
- Changed **glTF** (File › Import › Animation from glTF…, File › Import › Mesh from glTF…, File › Export › glTF…):
  the import and export commands moved into the Import and Export submenus; **Ctrl+I** and **Ctrl+E** are unchanged.
- Changed **library** (left pane): the clip and mesh library is the **Animations** tab of the left pane.
- Changed **settings** (Tools › Settings…): theme, game folder and search folders are on the shared **General**
  page; library, viewport and time display options are on the **Animations and meshes** page.
- Added **exporter and PS2 meshes** (open, packfile preview and Info column): `.v3d` and `.vcm` (the 3ds Max
  exporter's static and character meshes) and `.rfm` and `.rfc` (Red Faction's PlayStation 2 meshes) open
  read-only in 3D, characters with their skeleton, with a banner and **Convert…**. They are recognised by content,
  so an exporter mesh named `.v3m` opens as one. Red Faction II's `.rfm`/`.rfc` open with the reason in Problems.
- Added **Convert to .v3m/.v3c** (the banner, File › Export, and the packfile's **Convert meshes…** for a
  selection): next to the source, into a folder, or into the packfile as new entries in one undo step, with a report
  of what was approximated. Exporter meshes convert the way the game's mesh compiler did (the PS2 demo's `.v3d`
  files convert to the same geometry and size as the `.v3m` it compiled from them); a PS2 mesh converts from its
  same-named `.v3d`/`.vcm` when that is beside it (anywhere in its packfile). Next to the source is never the game
  directory; Save As never writes over the file the tab was read from, and is off for a file that could not be read.
- Added **V3C027** (mesh Problems): a mesh with no submeshes.
- Changed **mesh reading** (every `.v3m`/`.v3c`): collision sphere and bone sections whose size field is smaller
  than their records (written by a mod tool; the game reads the records by their layout) are read as the game reads
  them and saved back unchanged, so such mod meshes, which would not open before, now do.

**Effects (VFX)**

- Added **effect documents** (File › Open, toolbar New, Effect › New from template): view and play `.vfx` files at 15 frames per
  second as a loop, one-shot or holding the last frame, with an outliner, inspectors for the effect, objects,
  materials and keys, and a timeline.
- Added **Effects library** (left pane): every `.vfx` in the game's archives and your folders, with a filter, key
  facts and the tables that use it.
- Added **preview** (viewport): animated meshes, alpha-blended, additive and fullbright materials, animated `.vbm`
  textures, camera-facing quads and rods, particles, and overlays for dummies, lights, spacewarps and emitters.
- Added **editing** (viewport, inspectors, timeline): gizmos with auto-key, local axes and pivot mode, vertex
  editing on morph frames, key editing, per-object timing with hold, loop or resample fill, animation-type
  conversion and scrolling UVs; a texture picker with preview; an **Effects** settings page.
- Added **creation** (Effect › Add, Effect › New from template, File › Import): four starter templates, primitives, particle systems, dummies, lights, spacewarps and
  materials; geometry from `.v3m` and `.v3c`; objects from other effects; effects from glTF.
- Added **glTF exchange** (File › Export, File › Import): `.gltf` + `.bin` (read by REDUX) or GLB; a current-format
  effect exported by Cairn imports back identical, REDUX's files import identically, and plain glTF from Blender
  imports as new objects.
- Added **Problems** (bottom pane): checks for what the game cannot load or would crash on, missing textures, timing
  and naming issues, with quick fixes.
- Added **older format versions** (Effect › Convert to current format): every stock version opens and re-saves
  byte-identically, and converts to the current version in one undo step for editing.

**Packfiles (VPP)**

- Added **packfile documents** (File › Open, File › New › Packfile…): open any `.vpp` at once (only its list of
  entries is read), with a file list you can filter by name or wildcard, filter by type and sort by column. Type
  the first letters of a name to select and scroll to it. An **Autoplay sounds** toggle on the main toolbar plays a
  sound as soon as it is selected.
- Added the **Info column** (packfile file list): one short line per file, read from its header in the background,
  such as `Regicide by --ReWiReD-- (Tuesday, August 06, 2002 at 01:30:23)` for a level, `256x256, 24-bit, RLE
  compressed` for a Targa image, `512x512, DXT1, 10 mipmaps`, `22,050 Hz, 16-bit mono, 1.2 s` or
  `30 bones, 560 triangles, 3 LODs`; sortable, with the whole line in its tooltip and a dim *unreadable* for broken data.
- Added **preview** (right pane): images (animated `.vbm`, `.dds` mip levels, alpha view), text with find, sounds
  with a waveform (including Ogg Vorbis, ADPCM and the stock `.aif` files), and read-only previews of meshes, clips,
  effects and animated textures, which take their textures, frames and meshes from the same packfile first.
- Added **details** (right pane): facts for every type, and for levels (`.rfl`) the author, save time, properties,
  statistics and every referenced file, marked as in this packfile, in the game data or missing.
- Changed **level details** (right pane): no **Gravity** row, since levels never store gravity.
- Added **editing** (Packfile menu, toolbar, context menu, drag and drop): add files and folders with a choice for
  name clashes, replace, rename (**F2**), remove (**Del**) and reorder entries, each one undo step.
- Added **extraction** (Packfile › Extract selected… **Ctrl+E**, context menu): to a folder, next to the packfile,
  by dragging entries to Explorer, or with **Ctrl+C**.
- Added **work copies** (double-click, **Enter**, Open with…, Open in Cairn): open an entry in its Windows program or
  a Cairn tab, then put the saved copy back with **Update packfile**.
- Added **safe saving** (File › Save): a new file is written beside the old one, checked, then swapped in, with
  progress and cancel; the file on disk is untouched until then. Optional `.bak` copy (Settings › Packfiles).
- Added **problems** (status bar): live checks against the game's limits (names, case-insensitive duplicates,
  entry counts, 2 GB, packfile name length, types the game does not load); errors block saving. A name listed twice
  with identical contents (as in the stock `ui.vpp`) is only a note, so the stock packfile can be saved.
- Added **Packfiles settings page** (Tools › Settings…): `.bak` copies, confirm before removing, work copy folder;
  and a **Packfiles: format and limits** help topic.
- Added **table previews** (preview pane): a `.tbl` entry shows highlighted and foldable, with **Open in Cairn**.
- Added **PlayStation 2 packfiles** (bar above the list; **Convert to .tga...** in the context menu of `.peg` entries
  and the Packfile menu): a packfile from the PS2 version says so, its PS2-only types have names (**PS2 texture
  pack**, **PS2 static mesh**, **PS2 character mesh**, **PS2 sound effect**, **PS2 music**), and the selected `.peg`
  entries convert to `.tga` files the PC game loads, one or several at a time, each conversion one undoable change;
  saving then writes a PC packfile. A dialog lists the selected packs' textures with a tick each, what each becomes
  (an animation's `.atx`, the menu frames' `.atx`) and what happens to names the packfile already has, with **Treat
  black as transparent** and **Keep the .peg entries**. Textures of one name are compared, whether in packs
  converted together or already in the packfile from an earlier conversion: identical copies are converted once,
  and when they differ the largest is kept (a larger one replaces the entry, an animation with its frames) and the
  summary lists each conflict; long animation names that shorten alike get distinct frame names instead of being
  dropped. A replaced animation's old frames stay when another `.atx` still names them (frame or alpha mask, read
  with the animated texture parser), and the summary says which frames were removed and which kept.
- Added **PEG texture packs** (File › Open, Recent, file associations, Open in Cairn): a `.peg` opens in a packfile
  tab of its textures converted to 32-bit `.tga` (animations as numbered frames plus an `.atx`), with a bar saying
  that saving writes a PC packfile (`.vpp`); the `.peg` is never written. `.peg` entries preview their textures
  (animations play) and show their contents in the Info column and the details.
- Added **PS2 MPEG-2 backgrounds**: the full-screen pictures PEG texture packs store as MPEG-2 (menu backgrounds,
  legal screen, Extras pages, multiplayer previews, HUD portraits) are decoded by Cairn's own MPEG-2 intra-picture
  decoder and convert to 24-bit `.tga` (no alpha) like the other textures, in previews, conversions and PNG
  extracts. The main menu's 150 numbered frames (`plan-0001` to `plan-0150`) also get `interface-bg-mm.atx`, looping
  them at 30 frames a second (the PS2 rate is not known). **Settings › Packfiles › Decode PS2 MPEG-2 backgrounds**
  (on by default) switches decoding off; a background that cannot be decoded is listed with the reason. **Treat
  black as transparent** (off by default; threshold 0 to 64, 25 as the PS2 game uses; optional soft edge) keys out
  black as the PS2 can and makes the backgrounds 32-bit `.tga` with alpha, in previews, conversions and PNG extracts.
- Added **Extract as PNG...** (Packfile menu): the selected images, and every texture of selected `.peg` entries,
  as PNG files.

**Tables (TBL)**

- Added **table documents** (File › Open, File › New › Table): a text editor for `.tbl` files that knows the stock
  tables, level text and Alpine Faction's own tables field by field; any other table opens with syntax checks.
- Added **highlighting and folding** (editor): comments, section headers, field names, strings, numbers, file names
  and names from other tables in their own styles; every section, entry and block comment folds.
- Added **Outline** (left pane): sections with entry counts and entries with error marks, a filter, and a jump on
  click; **Go to Entry…** (Table menu, **Ctrl+Shift+O**).
- Added **problems** (bottom pane, status bar): live checks against how the game reads tables (field order,
  unknown fields, value types and ranges, required fields, conditional fields, unclosed strings and sections, `//`
  comments in LF-only files, a byte-order mark, missing files, undefined names, Alpine option values Alpine cannot read), with quick
  fixes (**Ctrl+.**) and **F8** / **Shift+F8** to step through them. A missing file or name that the game's own copy
  of the table also lacks is shown as information. Clicking the error or warning count opens the Problems tab.
- Added **completion and hover** (editor, **Ctrl+Space**): fields valid at the caret in the game's order, accepted
  values, file names of the right kind, entry names from other tables; descriptions, types and ranges on hover.
- Added **reference preview** (right pane): click a file name, or move the caret onto it, to preview the file with
  its details; a name from another table shows its defining entry. A table opened from a packfile finds its files
  in that packfile first.
- Added **Go to Definition** (Table menu, **F12**, **Ctrl+click**) and **Find Usages** (Table menu, **Shift+F12**,
  Usages tab) across every table in the game data, the search folders and open tabs.
- Added **Compare with Stock** (Table menu, Compare tab): added, removed and changed entries and fields against the
  game's own table of the same name, following your edits, with a jump to each change.
- Added **safe saving** (File › Save): encoding (ANSI, Latin-1, UTF-8, with or without a byte-order mark) and line
  endings kept, an unchanged table re-saves byte for byte, a prompt before saving errors or characters the encoding
  cannot hold.
- Added **find and replace, go to line and toggle comment** (Edit menu, **Ctrl+F**, **Ctrl+H**, **Ctrl+G**,
  **Ctrl+/**).
- Added **Tables settings page** (Tools › Settings…): which problems to report, completion while typing, word wrap;
  and a **Table syntax** help topic.

**Fonts (VF)**

- Added **font tabs** (File › Open…, or Open in Cairn on a `.vf` in a packfile): every glyph in a zoomable grid with
  its character, on a dark, checkered or light backdrop; the arrow keys move the selection. Reads all font
  versions and pixel formats (4-bit monochrome, 8-bit indexed with its palette, RGBA 4444).
- Added **sample text drawn as the game draws it** (font tab): your own text or every character, with the font's
  spacing and kerning, zoomed 1× to 8× with sharp pixels, and the size the game measures for it.
- Added **glyph and font inspector** (font tab, right): character, code, width, spacing, pixel offset, kerning
  pairs, user data; version, format, height, character range, kerning pair count, the texture the game builds and
  the palette of indexed fonts.
- Added **font checks** (Problems panel): damaged or unsupported files, glyphs out of range, kerning pairs the game
  never applies, fonts too big for the game's font texture ("Font too big!"), and more.
- Added **font previews in packfiles** (preview pane): a summary, the sample text and every character; the details
  pane lists the character range, default spacing, widest glyph and texture size.
- **Save As** writes a font back byte for byte; a **Fonts (.vf)** help topic.
- Added **font editing with undo** (font tab, Font menu): width, spacing, user data, height and default spacing in the
  inspector; a pencil and eraser on the enlarged glyph; kerning pairs added, changed and removed in the inspector
  (kept sorted as the game needs, with a warning for pairs the game never applies); add or remove characters;
  convert the pixel format. Checks re-run after every edit, and saving a font with errors asks first.
- Added **Replace Glyph from Image** and **Paste Glyph Image** (Font menu, Ctrl+R, Ctrl+Shift+V): a picture file or
  the clipboard becomes the selected glyph, scaled or kept at its size, with coverage from transparency or
  brightness and an optional threshold, previewed before it is applied.
- Added **image sheets** (Font menu, File › Export and Import, Ctrl+Shift+E, Ctrl+Shift+I): a PNG of every glyph in a
  grid plus a JSON file of the metrics; edit either and import it back. An unchanged sheet gives back the same font
  bytes.
- Fixed the font tab's zoom boxes cutting "×" in half.
- Added a check for **kerning the game applies without a pair** (VF046, also in the glyph's kerning list): the game's
  lookup can run into the next character's pairs, so adding a pair may move other text.
- Fixed damaged fonts: sizes no font can have (a huge height, glyph tables claiming far more pixels than the file
  holds) are reported instead of using gigabytes of memory; a negative glyph width is reported and read as 0; text too
  wide to draw shows a note instead of an empty tab; a single row of glyphs taller than the texture is reported as
  overrunning it (VF061), not as "Font too big!"; an edit that fails on a damaged font is reported, never a crash.
- Fixed image sheets: exporting asks before replacing an existing `.json` (or `.png`); a hand-edited sidecar with
  empty entries or impossible layout numbers is reported instead of crashing the import.
- Fixed the **Problems** panel of fonts, bitmaps and effects showing each problem as one line of raw text instead of
  columns, and its "No problems found" text overlapping the column headings (an empty list now shows only the text).

**Volition bitmaps (VBM)**

- Added **bitmap tabs** (File › Open…, or Open in Cairn on a `.vbm` in a packfile): the animation large, playing at
  the file's frame rate, with play and pause (**Space**), frame steps (**,** and **.**), zoom, an alpha view, a
  checkerboard toggle and a mip level picker; a strip of every frame with its number; size, pixel format, version,
  frames, length, mip levels and file size beside it.
- Added **bitmap editing** (Bitmap menu, frame strip): frame rate, **Replace Frame…** (**Ctrl+R**) and **Add
  Frames…** (**Insert**) from TGA, PNG, JPG, DDS or VBM files, resized after a prompt and converted to the bitmap's
  pixel format with its mip levels rebuilt; duplicate, remove, move and reverse frames; change the pixel format or
  the number of mip levels. Each one undo step; Save and Save As as for every document. An unchanged bitmap saves
  byte for byte (checked against every stock `.vbm`).
- Added **New VBM from images** (File › New › Volition bitmap, start page): pick images in playing order, then
  the size, pixel format (suggested from their transparency), frame rate and mip levels.
- Added **Export Frames** (Bitmap menu, File › Export, toolbar, **Ctrl+Shift+E**): all or the selected frames as
  TGA or PNG, named `name_00`, `name_01`… as Import VBM names them.
- Added **Convert to ATX…** (Bitmap menu, toolbar, File › Export): the Import VBM window for the bitmap as it is in
  the tab; the new `.atx` opens. A bitmap from a packfile offers your last import folder, not a temporary one.
- Added **bitmap checks** (Problems panel): files cut short (the complete frames are kept), extra bytes, impossible
  mip counts, more than the 255 frames the game plays, an animation without a frame rate, sizes that are not powers
  of two, and more; a **Volition bitmaps** help topic and a `.vbm` file association.
- Added **drag and drop in the frame strip**: drag the selected frames to reorder them (one undo step per drop; from
  another bitmap's strip they are copied in), and drop TGA, PNG, JPG, DDS, BMP or VBM files to add them as frames
  where they land.
- Added **copy and paste of frames** (**Ctrl+C** / **Ctrl+V**, Bitmap and Edit menus, the strip's right-click menu):
  within a bitmap or between bitmap tabs (exact bytes when the layouts match), and **paste a picture** from the
  clipboard or copied image files as new frames. Pasting still works from Cairn's own copy when the Windows clipboard
  is busy.
- Added a **play mark** in the frame strip: the frame on show has an accent outline and a play mark, apart from the
  selection.
- Added **Resize to the frame size** for images of another size: nearest, bilinear or high-quality filter; stretch,
  keep aspect with transparent padding, or crop the centre; with a preview of the frame in the bitmap's pixel format.
  The choice is remembered.
- Added **Smooth** / **Pixels** to the bitmap view bar (until chosen: pixels above 100% zoom, smooth otherwise, as in
  the animated textures preview).
- Added **Settings › Volition bitmaps**: the frame rate, pixel format and mipmaps New VBM starts with, and the resize
  filter and fit (and whether to ask). BMP files can now be used for frames.
- Fixed an unreadable bitmap staying read-only after the file was fixed and reloaded; a frame rate spin no longer
  rebuilds the frame strip and re-checks the bitmap at every step.

**Sounds**

- Added **sound tabs** (File › Open…, or Open in Cairn on a sound in a packfile) for the PlayStation 2 version's
  `.vse` sound effects and `.vmu` music and for `.wav`, `.ogg` and `.aif` files: read-only, with a waveform per
  channel (time ruler, zoom with the mouse wheel, scrolling, play head, loop region), play and pause (**Space**),
  stop, seamless loop (**L**) at the sound's own loop points, and volume.
- Added **PS2 sound decoding**: PS ADPCM with all five filters, loop flags and the console's handling of
  out-of-range frames; both `.vse` header layouts (the older one's silent padding dropped); `.vmu` stereo in 16 KB
  blocks. Every one of about 3,300 PS2 sounds decodes. Damaged files open with what can be decoded and a
  **Problems** list (SND codes). Ogg Vorbis files decode to exactly the length their last page records.
- Added **sound details**: format, codec, sample rate (with the rate the console really plays), channels, bit depth,
  duration, loop points and their source, size, every PS2 header field and what is special about the format.
- Added **Convert…** (Sound menu, **Ctrl+Shift+E**) and **Convert sounds…** (Packfile menu and the list's right-click
  menu, for a selection): 16-bit WAV with the loop in a `smpl` chunk, or Ogg Vorbis made with the Xiph.Org reference
  encoder (libvorbis 1.3.7) at a quality from q-1 to q10 (default q5) with the loop as `LOOPSTART`/`LOOPLENGTH`
  comments; into the packfile as new entries (one undo step), next to the source (never the game directory) or into a
  folder; a report of what the conversion approximates. A conversion never takes its source's place: a format the
  sound is in already is not offered, an output is never named like its source (`master (converted).wav`), a taken
  name gets a free name (one sound) or is left out (a batch), and Replace, never remembered, asks first. A batch lists
  sounds that would give one name (`x.wav` + `x.aif`) and damaged files, and goes on. Save As on a sound tab writes
  a WAV. PS2 sounds are never written.
- Added **PS2 sounds in packfiles**: an Info line such as `22,050 Hz mono, 1.4 s, PS ADPCM`, details (codec, pitch,
  envelope, flags), and playback with a waveform in the preview. Every sound preview now has **Open in Cairn**.
- Added **Settings › Sounds** (format, Ogg quality, where converted files go, loop points), a **Sounds** help topic,
  and file associations for `.vse`, `.vmu`, `.wav`, `.ogg` and `.aif` (Select all leaves the general `.wav`,
  `.ogg` and `.aif` unticked).
