# Changelog

## 1.0.0

First release of Cairn. It combines ATX Workbench 1.1.0 and RFA Workbench 1.0.1 in one application and adds
modules for effect meshes (`.vfx`), packfiles (`.vpp`) and tables (`.tbl`). Cairn targets Alpine Faction 1.0 to
1.5: its checks follow Alpine Faction, and nothing is reported merely because it needs Alpine Faction. Everything the two apps did, Cairn
does; the notes below list what is new and what works differently if you are coming from them.

**Suite**

- Added **tabs for every document type** (main window): animated textures, clips, meshes, effects, packfiles and
  tables open side by side, and the menus, panes and status bar follow the tab in front.
- Added **one File menu for all modules** (File): **New** has one entry per document type, **Import** and
  **Export** collect every module's importers and exporters, and **Open…** accepts every supported type.
- Changed **module menus** (menu bar): **Frames**, **Clip**, **Mesh**, **Effect**, **Packfile** and **Table**
  appear only while a document of their type is in front.
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

**Animated textures (ATX)**

- Changed **Frames menu** (menu bar): adding frames, sequences, locating files, reordering and **Bulk Frame
  Timing…** (**Ctrl+T**) are in the new **Frames** menu, shown with an animated texture in front.
- Changed **Import VBM** (File › Import): **Import VBM...** (**Ctrl+Shift+I**) and **Import VBM from VPP...** moved
  under File › Import.
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
  entries is read), with a file list you can filter by name or wildcard, filter by type and sort by column.
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
  entry counts, 2 GB, packfile name length, types the game does not load); errors block saving.
- Added **Packfiles settings page** (Tools › Settings…): `.bak` copies, confirm before removing, work copy folder;
  and a **Packfiles: format and limits** help topic.
- Added **table previews** (preview pane): a `.tbl` entry shows highlighted and foldable, with **Open in Cairn**.

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
