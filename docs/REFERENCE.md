# Cairn reference

The details behind the [usage guide](USAGE.md): formats, limits, problem codes, menus, settings and field tables.
Read the guide for how to do things; come here when you need the exact meaning of a column, an option or a code.

## Contents

- [Window and settings](#window-and-settings)
  - [Main menus](#main-menus) · [Settings pages](#settings-pages) · [File search order](#file-search-order) ·
    [File associations in detail](#file-associations-in-detail) · [Cairn's own files](#cairns-own-files) ·
    [Coming from the Workbenches](#coming-from-the-workbenches)
- [Animated textures (ATX)](#animated-textures-atx)
  - [ATX frames list and menus](#atx-frames-list-and-menus) ·
    [ATX preview, source editor and problems](#atx-preview-source-editor-and-problems) · [ATX settings](#atx-settings)
- [Animations and meshes](#animations-and-meshes)
  - [Clip and mesh concepts](#clip-and-mesh-concepts) · [Clip and mesh tabs](#clip-and-mesh-tabs) ·
    [New Clip dialog](#new-clip-dialog) · [Animations library](#animations-library) ·
    [Animation viewport](#animation-viewport) · [Transport and layered preview](#transport-and-layered-preview) ·
    [Animation timeline](#animation-timeline) · [Clip, Bone and Key inspectors](#clip-bone-and-key-inspectors) ·
    [Pose editing tools](#pose-editing-tools) · [Clip tools](#clip-tools) ·
    [Versions and morph data](#versions-and-morph-data) · [Retarget dialog](#retarget-dialog) ·
    [Batch retarget](#batch-retarget) · [Mesh editing](#mesh-editing) ·
    [glTF options for clips and meshes](#gltf-options-for-clips-and-meshes) ·
    [Exporter and PS2 mesh conversion](#exporter-and-ps2-mesh-conversion) · [Table usage panel](#table-usage-panel) ·
    [Clip and mesh problem codes](#clip-and-mesh-problem-codes) ·
    [Animations and meshes settings](#animations-and-meshes-settings) ·
    [Saving clips and meshes](#saving-clips-and-meshes) ·
    [Animations and meshes keyboard shortcuts](#animations-and-meshes-keyboard-shortcuts)
- [Effects (VFX)](#effects-vfx)
  - [Effect format primer](#effect-format-primer) · [Effect menu and outliner](#effect-menu-and-outliner) ·
    [Effect inspectors](#effect-inspectors) ·
    [Effect viewport, timeline and shortcuts](#effect-viewport-timeline-and-shortcuts) ·
    [Effect settings](#effect-settings) · [Effect glTF exchange](#effect-gltf-exchange) ·
    [Effect preview accuracy](#effect-preview-accuracy) · [Effect problem checks](#effect-problem-checks)
- [Packfiles (VPP)](#packfiles-vpp)
  - [Packfile menus and toolbars](#packfile-menus-and-toolbars) ·
    [Packfile file list columns and Info](#packfile-file-list-columns-and-info) ·
    [Packfile preview and details panes](#packfile-preview-and-details-panes) ·
    [Packfile bars and status bar](#packfile-bars-and-status-bar) ·
    [Packfile saving and extracting](#packfile-saving-and-extracting) ·
    [Packfile problem codes](#packfile-problem-codes) · [Packfile settings](#packfile-settings) ·
    [Packfile limits](#packfile-limits) · [DDS conversion options](#dds-conversion-options) ·
    [PEG conversion rules](#peg-conversion-rules)
- [Tables (TBL)](#tables-tbl)
  - [Tables Cairn knows](#tables-cairn-knows) · [Table menus and shortcuts](#table-menus-and-shortcuts) ·
    [Table panels, status bar and settings](#table-panels-status-bar-and-settings) ·
    [How the game reads tables](#how-the-game-reads-tables) ·
    [Where the game accepts a table](#where-the-game-accepts-a-table)
- [Fonts (VF)](#fonts-vf)
  - [Font toolbar and menu](#font-toolbar-and-menu) · [Font facts and status bar](#font-facts-and-status-bar) ·
    [Font pixel formats](#font-pixel-formats) · [How the game spaces text](#how-the-game-spaces-text) ·
    [Font image sheet format](#font-image-sheet-format) · [Font problem codes](#font-problem-codes)
- [Volition bitmaps (VBM)](#volition-bitmaps-vbm)
  - [Bitmap settings](#bitmap-settings) · [Bitmap facts](#bitmap-facts) ·
    [Bitmap menu and shortcuts](#bitmap-menu-and-shortcuts) ·
    [Bitmap pixel formats and versions](#bitmap-pixel-formats-and-versions) ·
    [Bitmap problem codes](#bitmap-problem-codes)
- [Sounds (VSE, VMU, WAV, OGG)](#sounds-vse-vmu-wav-ogg)
  - [Sound details pane](#sound-details-pane) · [PS2 sound formats](#ps2-sound-formats) ·
    [Sound conversion details](#sound-conversion-details) · [Sound settings](#sound-settings) ·
    [Sound problem codes](#sound-problem-codes)

## Window and settings

### Main menus

| Menu | Entries |
|---|---|
| **File** | **New** (one entry per kind of file Cairn creates), **Open…** (**Ctrl+O**), **Open Recent**, **Import**, **Export**, **Save** (**Ctrl+S**), **Save As…** (**Ctrl+Shift+S**), **Save All**, **Close Tab** (**Ctrl+W**), **Close All Tabs**, **Reopen Closed Tab** (**Ctrl+Shift+T**), **Exit** (**Alt+F4**) |
| **Edit** | **Undo** (**Ctrl+Z**) and **Redo** (**Ctrl+Y**), named after the step, then the commands of the tab in front |
| Module menu | **Frames**, **Clip**, **Mesh**, **Effect**, **Packfile**, **Table**, **Font**, **Bitmap** or **Sound**, while a file of that type is in front |
| **View** | **Left Pane** (**Ctrl+Shift+L**), **Bottom Pane** (**Ctrl+Shift+M**), **Theme** (System, Light, Dark), then the tab's own view commands |
| **Tools** | **Settings…**, **File Associations…** |
| **Help** | One group per module with its topics, **Keyboard Shortcuts**, **About Cairn**. **F1** opens the topic for the tab in front (shown beside it in the menu), or the shortcut list |

The toolbar has **New** (a split button listing every kind of file), **Open**, **Save**, **Undo**, **Redo**, the pane
toggles and the buttons of the module in front.

### Settings pages

**Tools › Settings…** has a **General** page, a **File associations** page and one page per module.

| General page section | Settings |
|---|---|
| **Appearance** | **Follow Windows**, **Light**, **Dark**. The theme changes as you pick it; **Cancel** puts the old one back. |
| **Red Faction folder** | The game folder, **Auto-detect** (from Alpine Faction's or Dash Faction's settings), **Browse…**. The page says whether the folder looks like an install. |
| **Search folders** | Extra folders searched for referenced files, in order: **Add…**, **Remove**, **Move up**, **Move down**. |

Module pages: **Animated textures**, **Animations and meshes**, **Effects**, **Packfiles**, **Tables**, **Volition
bitmaps** and **Sounds**; each chapter of the guide describes its own.

### File search order

For previews and checks, Cairn finds a file a document names (a texture, mesh, clip, table) by its bare name, in
this order. Loose files count here so you can preview work in progress, but the game itself loads assets only from
packfiles (see [How the game finds your files](USAGE.md#how-the-game-finds-your-files)).

1. the open file's own folder, or for a file inside a packfile, that packfile;
2. the search folders, in their order: loose files first, then any `.vpp` directly in the folder;
3. the game folder: its root `.vpp` archives, then `user_maps\textures`, `user_maps\projects`, `user_maps\single` and
   `user_maps\multi` (loose files and packfiles).

A texture name also finds the other formats the game would take in its place: `.atx`, `.dds`, `.png`, `.jpg`,
`.jpeg`, `.vbm`, `.tga` (in that order of preference). Clip names written `.mvf` find the `.rfa`; mesh names written
`.v3d` or `.vcm` find the `.v3m` or `.v3c`.

The **Packfiles** browser lists the game folder's packfiles, its `user_maps` folders, each folder of `mods` that holds
packfiles (only the files directly inside it) and your search folders. A packfile that sits in two of these is listed
once, in the first group.

### File associations in detail

The **File associations** page lists one row per file type Cairn opens, grouped by module and including `.vpp`
packfiles:

- **Type** and **Description**: the extension and what it is.
- **Opens now with**: the program Windows opens the type with now (its tooltip says where Windows got it).
- **Open with Cairn**: tick to make Cairn open the type from Explorer; untick to give it back to the program Cairn
  replaced. **Select all** and **Select none** change every row; general formats such as `.wav` and `.ogg` are left
  out of **Select all**.

Nothing changes until **OK**, and then only the rows you changed. Changes apply to your Windows account and need no
administrator rights. A type whose default you chose in Windows yourself is protected: Cairn adds itself to the
type's **Open with** list, and **Choose default…** opens Windows' chooser (or use Windows Settings › Apps › Default
apps). **Remove old RFA/ATX Workbench associations…** removes only what those apps registered for your account; it
asks first and acts at once.

### Cairn's own files

| What | Where |
|---|---|
| Settings | `%APPDATA%\Cairn\settings.json` |
| Retarget profiles | `%APPDATA%\Cairn\profiles` |
| Recovery copies (every 30 seconds while a file has unsaved changes) | `%LOCALAPPDATA%\Cairn\recovery` |
| Crash log | `%LOCALAPPDATA%\Cairn\crash.log` |
| Caches (such as the library index) | `%LOCALAPPDATA%\Cairn` |
| Packfile work copies | `%LOCALAPPDATA%\Cairn\work`, or the folder on the **Packfiles** settings page; one sub-folder per open packfile, deleted when it closes |

If a second copy of Cairn runs (when opening a file could not reach the first), both use the same recovery folder.

### Coming from the Workbenches

On its first start (while Cairn has no `settings.json`), Cairn imports the theme, game folder, search folders and
recent files of ATX Workbench and RFA Workbench, RFA Workbench's other preferences and its retarget profiles. The old
apps' files are only read, so both keep working.

What moved:

- **File › New** has one entry per document type.
- Module menus appear only while a document of their type is in front, between **Edit** and **View**: **Frames** for
  an animated texture, **Clip** for a clip, **Mesh** for a mesh (and **Effect**, **Packfile**, **Table**, **Font**,
  **Bitmap**, **Sound** for the other modules).
- Importers and exporters are under **File › Import** and **File › Export**.
- One settings dialog, **Tools › Settings…**, with a shared **General** page and a page per module.
- File associations are a page of the settings dialog, with a check box per type.

## Animated textures (ATX)

### ATX frames list and menus

**Frames list.** Thumbnails of every frame in order. Multi-select with **Ctrl** and **Shift**; drag to reorder.
Right-click: **Add Frames** (**Browse…**, **From VPP…**), **Rename…** (**F2**), **Edit frame time…**, **Locate
File…**, **Cut** / **Copy** / **Paste** (**Ctrl+X** / **Ctrl+C** / **Ctrl+V**, also in the Edit menu as **Cut
Frames**, **Copy Frames**, **Paste Frames**), **Duplicate** (**Ctrl+D**), **Remove** (**Del**), **Move up** / **Move
down**, **Reverse selection**, **Sort selection by name**.

**Frames menu.** **Add Frames** (**Browse…** (**Insert**), **From VPP…** (**Ctrl+Shift+V**)), **Add Sequence…**,
**Locate File…**, **Duplicate**, **Remove**, **Move Up** (**Alt+Up**), **Move Down** (**Alt+Down**), **Reverse
Selection**, **Sort Selection by Name**, **Select All Frames** (**Ctrl+A**), **Bulk Frame Timing…** (**Ctrl+T**).

**Import and export.** **File › Import › Import VBM…** (**Ctrl+Shift+I**) and **Import VBM from VPP…** write a
`.vbm`'s frames as images and open an `.atx` that lists them.

**Bulk Frame Timing.** Set, clear, scale, offset, distribute or ramp the timing of the selected frames, with the
timing before and after; one undo step.

### ATX preview, source editor and problems

**Preview.** Plays the animation by the game's playback rules with the alpha mask applied. **View › Play / Pause
Preview** (**Space**). Options: target-format simulation (an approximation of the game's texture conversion), 3×3
tiling for seam checks, alpha-only view.

**Source editor.** Syntax highlighting, completion, hover documentation for every keyword, **Edit › Find…**
(**Ctrl+F**), **Edit › Find and Replace…** (**Ctrl+H**), **Edit › Toggle Comment** (**Ctrl+/**). **View › Focus Source
Editor** (**Ctrl+E**) moves the keyboard focus to it. Right-click a problem in the text for its quick fixes.

**Problems panel.** **View › Problems Panel** (**Ctrl+Shift+M**). Live checks: missing images, frames that do not match
frame 0, bad alpha masks, unknown tokens, names too long for the game, and more. Every entry says what is wrong and
how to fix it; most have a one-click fix. A file with errors asks once before it is saved.

**Where frames are found.** By bare name: the `.atx` file's folder (or its packfile), the search folders, then the
game's packfiles and its `user_maps\textures`, `user_maps\projects`, `user_maps\single` and `user_maps\multi` folders.

**Help.** **Help › Animated textures › ATX format reference** documents every keyword.

### ATX settings

**Tools › Settings… › Animated textures**:

| Setting | What it does |
|---|---|
| New files | **Minimal** (just the settings a new texture needs) or **With explanatory comments** (a comment above each). |
| Preview background | **Checkerboard**, **Black**, **White** or **Custom colour** (such as `#202020`). |

## Animations and meshes

Every window, option and problem code of the animation and mesh module. The walk-throughs are in the guide's
[Animations and meshes](USAGE.md#animations-and-meshes-rfa-v3c-v3m) chapter.

### Clip and mesh concepts

**Clip.** One animation file (`.rfa`). It holds one *track* per bone of the character it is made for. The tables
spell clips `.mvf` (`ult2_walk.mvf` in a table is `ult2_walk.rfa` on disk).

**Mesh, skeleton, bone index.** A character mesh (`.v3c`, spelt `.vcm` in the tables) has a skeleton of up to 50
bones, each with a number, its *index*. A clip does not store bone names: track 0 drives bone 0, track 1 drives
bone 1, and so on. So a clip only plays correctly on a mesh with exactly the same number of bones in the same
order. Cairn shows bone names by borrowing them from the mesh you preview the clip on. Meshes that share one
skeleton form a *skeleton family*.

**Static mesh.** A `.v3m` (spelt `.v3d` in the tables): items, props, level objects. No skeleton; Cairn opens it
read-only.

**Preview mesh / preview clip.** A clip tab plays its clip on a *preview mesh* you pick; a mesh tab plays a
*preview clip* you pick. Neither is saved into the file.

**Key, rotation key, position key.** Clips store poses at chosen moments, *keys*, and the game fills in between.
A *rotation key* says how a bone is turned relative to its parent; a *position key* says where the bone sits
relative to its parent (which sets the bone's length). Every bone needs position keys, or it collapses onto its
parent's joint.

**Tick, frame, start and end.** Clip time is counted in *ticks*, 4800 per second. Cairn shows *frames* of 160
ticks (30 per second) by default; stock clips start at frame 1 (tick 160). A clip plays from its *start* time to
its *end* time. Change the unit by clicking the time readout or in Settings.

**State and action.** The tables give each character named slots. A *state* is a base animation that loops (stand,
walk, run, crouch, seated in a jeep). An *action* is a one-off played on top (fire, reload, flinch, death). States
and actions use the same file format; they differ in how the game uses weights and ramps.

**Weight.** Every bone track has a weight from 0 to 10. When a clip plays as an action, a bone with weight 10
replaces the state completely on that bone, 5 shares it half and half, and 0 leaves it to the state. That is how an
upper-body reload plays over a walk: the arms are at 10, the legs at 0.

**Ramp in and ramp out.** When a clip plays as an action, its weights fade in from zero over the *ramp in* time
after the start, and fade out over the *ramp out* time before the end. States ignore ramps.

**Ease.** Each rotation key can *ease in* (arrive gently) and *ease out* (leave gently), from 0 % (linear) to
100 %.

**Control points.** Between two position keys the bone moves along a smooth curve; the two *control points* of each
key shape that curve.

**Bind pose and rest pose.** The *bind pose* is the pose the mesh's skin was attached to the skeleton in, stored in
the mesh. Retargeting talks about a *rest pose*: the pose each skeleton is measured from, ideally a T-pose.

**LOD (level of detail).** A mesh can hold up to three versions of its geometry, from most detailed (LOD 0) to
least; the game switches by camera distance.

**Prop point.** A named point on the mesh, usually following a bone, where the game attaches things: a held weapon,
a muzzle flash, a thruster.

**Collision sphere.** A named sphere following a bone, used for hit detection and collisions (`head`, `torso`…). The
tables refer to them by name.

**Morph data (vertex animation).** Some clips also move individual vertices of the one mesh they were made for,
mostly talking faces. It only fits that mesh; it plays on LOD 0 only.

**Retarget.** Transferring a clip from one skeleton onto a different one (another character type), keeping the
motion but giving it the target's proportions.

**IK (inverse kinematics).** Instead of turning the shoulder and elbow yourself, you say where the hand should be
and the arm bends to reach it. Cairn uses two-bone IK on arms (upper arm, forearm, hand) and legs (thigh, shin,
foot).

### Clip and mesh tabs

**Window parts.** With a clip or mesh in front, the menu bar gains **Clip** and **Mesh** menus. The left pane holds
the **Animations** library, the centre the viewport with the preview picker in its header and the transport under
it, the right the inspector (**Clip**, **Bone** and **Key** tabs for a clip, **Structure** for a mesh), and the
bottom panel the **Timeline**, **Problems** and **Table usage** tabs. The status bar shows the error and warning
counts (click to show Problems), messages, the bone count, the clip's duration, the playhead time and the playback
speed.

**Opening files.** **File › Open…** (**Ctrl+O**, or the **+** at the end of the tab strip) opens `.rfa`, `.v3c` and
`.v3m` files, several at once, and the exporter and PS2 meshes (`.v3d`, `.vcm`, `.rfm`, `.rfc`). You can also drop
files on the window, open them from the library or **File › Open Recent**, double-click them in Explorer once the
file association is set, or pass them on the command line. Opening or dropping a `.gltf` or `.glb` starts a glTF
import instead (the Open dialog also lists them on their own under **glTF to import**).

**Kinds of tab.**

- A **clip tab** (`.rfa`) has the **Preview mesh** picker in the viewport header, the transport, the **Clip**,
  **Bone** and **Key** inspector tabs, and uses the **Timeline**.
- A **mesh tab** (`.v3c`) has the **Preview clip** picker (with a **Bind pose** entry for no clip) and the
  **Structure** inspector tab. You can watch clips on it but not edit them there; open the clip in its own tab.
- A **static mesh tab** (`.v3m`) opens read-only, with a note saying so, and has no transport. You can view it,
  read its structure and problems, and **Save As** a copy.
- An **exporter or PS2 mesh tab** shows the converted mesh, read-only, with a **Convert…** banner; see
  [Exporter and PS2 mesh conversion](#exporter-and-ps2-mesh-conversion).

**The preview mesh picker** (clip tabs) lists the meshes the tables play the clip on first ("tables"), then meshes
with the same bone count, then the rest marked "does not fit". **Clip › Choose Preview Mesh…** opens it. Your choice
is remembered for that clip. If the bone counts differ, the viewport says so. When you open a clip, Cairn picks: the
mesh you picked for that clip before, else one the tables play it on, else the mesh you last used for clips with
that many bones, else any mesh with the same bone count.

**The preview clip picker** (mesh tabs) lists **Bind pose**, then the tables' clips for this mesh, then every clip
with the same bone count. **Mesh › Choose Preview Clip…** opens it (it needs a mesh with bones).

**Banners** at the top of a tab:

- "This file changed on disk while you were editing it." with **Reload** (throw away your changes) and **Keep
  mine**.
- "The file this tab came from was deleted or renamed on disk." with **Keep editing**.
- For a file from an archive: which `.vpp` it came from, and that Save asks where to write a copy.
- For a `.v3m`: that static meshes open read-only.

**Edit menu** (with a clip or mesh in front).

- **Undo** (**Ctrl+Z**) and **Redo** (**Ctrl+Y**, or **Ctrl+Shift+Z**) name the step they act on ("Undo Set ramp
  in").
- **Select All Bones** and **Clear Bone Selection** act on the active tab's skeleton.
- **Copy Keys**, **Cut Keys**, **Paste Keys at Playhead**, **Paste Keys Mirrored**, **Delete Keys**, **Select All
  Keys** and **Key Selected Bones at Playhead** are the timeline's; their shortcuts work while the timeline has
  focus.

**View menu.**

- **Inspector** (**Ctrl+Shift+I**), besides the shell's left pane (**Ctrl+Shift+L**) and bottom pane
  (**Ctrl+Shift+M**) toggles.
- **Play / Pause** (**Ctrl+Shift+P**, works from anywhere) and **Frame Selection** (**Ctrl+Shift+F**).
- **Time Display**: **Frames**, **Seconds** or **Ticks**.

**Tools menu.** **Batch Retarget…** and **Refresh Library**.

### New Clip dialog

**File › New › Animation clip…** (**Ctrl+N** with a clip or mesh tab in front, or from the welcome view), **Mesh ›
New Clip for This Mesh…**, or the library's right-click **New Clip for This Mesh…**. Every bone holds a starting
pose, keyed at the start and the end. Walk-through: [Make a new animation](USAGE.md#make-a-new-animation).

| Section | Options |
|---|---|
| CHARACTER MESH | The mesh the clip is made for, which fixes its bones and their order: open tabs first (the tab in front, a clip tab's preview mesh), then the library's characters; a filter; **Browse…** for a `.v3c`. Default: the mesh tab in front, else the preview mesh of the clip in front, else the mesh of your last new clip. |
| KIND AND LENGTH | **State (loops)**: no ramps (the game ignores them on states). **Action (plays once)**: ramps of 3 frames in and out, the most common stock action ramps. **Length** in your time unit, with the same length in frames, seconds and ticks under it. |
| STARTING POSE | **A reference clip's pose (usually the character's stand clip)**: a clip with the mesh's bone count, and **Pose at**, the moment of that clip to take (its start by default). The default is the stand state the tables give a class using the mesh, else the stock rig's own stand clip; a line under the list says which. Starting from it gives the new clip the bone lengths every other clip of the character carries. **The mesh's bind pose**: the default when no stand clip is known; its bone lengths can differ from the character's clips by a few centimetres. |
| Advanced | **RFA version** (**8** or **7**), **Bone weight** (10), **Ramp in** and **Ramp out** (set by the kind), **Start at** (frame 1). |
| NAME | The new clip's file name, checked as you type: not empty, characters the game can load, at most 59 characters with `.rfa`, and not the name of a clip the game already has. |

The preview on the right shows the starting pose. **Create** opens the clip as a new, unsaved tab with the Timeline
in front and the playhead at the start. **Save** asks where to write it (never the game folder). **Cancel**
(**Esc**).

Messages you may see:

- "No stand clip is known for this mesh (no table names one), so the bind pose is the default." The mesh is your
  own or the sample figure. If it has clips of its own, pick its stand or idle clip in the list; otherwise the bind
  pose is the right start.
- "The library has no clip with this mesh's bone count": there is nothing to pick; set the game folder or add the
  folder holding the character's clips.
- "… cannot be used: … has no bones": clips only play on character meshes with a skeleton.

### Animations library

The library lists every clip and mesh Cairn can find, in search order: the active file's folder, your search
folders (loose files, then `.vpp` archives in them), then the game (its `.vpp` archives, then the `user_maps`
folders). A clip from inside an archive opens read-only at its origin: saving asks where to write a copy.

**Meshes tab.** Characters grouped by skeleton family, largest family first; a family is named after its shortest
member ("miner family (9)") and its badge gives the bone count. Expanding a character lists the clips the tables
give it, grouped by class and table, each with its slot as a badge ("state stand", "action fire (12mm)"). A clip a
table names but no folder has is shown in grey italics. Static meshes and unreadable meshes are grouped under
**Static meshes**.

**Clips tab.** Every clip, with a **morph** badge for clips with vertex animation, its duration in frames, bone
count and version. **Compatible with the active mesh** shows only clips with the active document's bone count. The
count at the top right says how many are shown.

**Filter box.** Part of a name, or a wildcard such as `ult2_*.rfa`. **Esc** or the ✕ clears it. It filters both
tabs.

**Double-click (or Enter).** By default it previews on the tab in front instead of opening a tab:

| Tab in front | Double-click on | Result |
|---|---|---|
| A mesh with bones | A clip with the same bone count | The clip plays on the mesh. |
| A clip | A mesh with the clip's bone count | It becomes the clip's preview mesh. |
| A clip | Another clip | It opens in a new tab, previewed on the same mesh when its bone count fits. |
| Anything else, or the bone counts differ | Anything | It opens in a new tab (the status bar says why it could not preview). |

The line under the library and every entry's tooltip say what double-click will do right now. **Ctrl+double-click**,
**Ctrl+Enter** and **middle-click** always open a new tab. **Always open in a new tab** in
[Settings](#animations-and-meshes-settings) › **LIBRARY** makes double-click always open a tab.

**Dragging** an entry onto the viewport previews it there (a mesh onto a clip tab, a clip onto a mesh tab).

**Right-click menu.** **Open in new tab**; **Preview on this mesh** / **Preview this clip** (sets the preview partner
of the tab in front); **Open containing folder** (shows the file, or the `.vpp` holding it, in Explorer); **Extract
to…** (copies the file out of its archive to a folder you choose, asking before replacing); **Copy name**;
**Retarget…** (clips); **New Clip for This Mesh…** (character meshes).

**Refresh.** The ⟳ button in the library header, or **Tools › Refresh Library**, looks through the folders and
archives again. The bottom of the pane shows progress while the library builds; the first build reads every archive
the game has and can take a while, later starts use a cache.

**Empty states.** With no game folder and no search folder: **Set game directory…** and **Add search folder…**.
With folders set but nothing found: "No clips or meshes were found in the game directory or the search folders."
and **Add search folder…**; check the game folder is the one holding `RF.exe`.

### Animation viewport

**Camera.**

| Do | To |
|---|---|
| Left-drag | Orbit |
| Middle-drag, or Shift+left-drag | Pan |
| Mouse wheel | Zoom towards the cursor |
| **F** (viewport focused), **Ctrl+Shift+F**, or the frame button | Frame the selected bones (with their children), or everything |
| **1** / **3** / **7** (main row or numpad) | Front view (the character's face), side view, top view |
| **5** | Switch between perspective and orthographic |

**Selecting bones.** Click a joint to select its bone, **Ctrl+click** to add or remove one, **Esc** to clear. The
picked bone's editor comes forward: the **Bone** inspector in a clip tab (unless you are working in the **Key** tab
with keys selected) and the **Structure** node in a mesh tab. Its timeline row is scrolled into view.

**Selecting collision spheres and prop points** (mesh tabs). While they are shown, click a sphere's outline or
inside, or a prop point's diamond: its **Structure** node and editor come forward, and the bone selection goes.
Where several things are under the cursor, the click takes the one nearest the cursor, then the nearest the camera;
click the same spot again to take the next one. A click on empty space lets go of the selection. **Ctrl+click**
only ever adds or removes bones. Selecting a sphere or prop point in the tree highlights it, even with its toggle
off.

**Viewport keys** (when it has focus; click it, or Tab to it and a coloured frame shows): **Space** play/pause,
**Left**/**Right** step a frame, **Home**/**End** first and last frame, the camera keys above and the gizmo keys
**Q**, **E**, **W**.

**Toolbar** (top left of the viewport):

| Control | What it does |
|---|---|
| Display menu (eye button) | **Textured**, **Flat** (one neutral colour) or **Hidden** mesh; **Full bright** (the textures' own colours, unlit); **Background** (Theme colour, Black, Dark grey, Mid grey, Light grey, White); **Ghost of the saved clip**; **Front view**, **Side view**, **Top view**. |
| Skeleton | Bones and joints. |
| **Aa** | Bone names (and sphere and prop point names). |
| Grid | Ground grid and axes (X red, Y green, Z blue). |
| Spheres | Collision spheres. |
| Prop points | Prop points. |
| Root path | The root bone's path over the whole clip. |
| Bind pose | Shows the mesh's bind pose instead of the clip; a "Bind pose" badge appears. |
| LOD box | Which level of detail to draw (also **Mesh › Level of Detail**). |
| Perspective | Perspective on, orthographic off (**5**). |
| Frame | Frame the selection or everything. |
| Gizmo tools | Select, Rotate, Move and the space box. In clip tabs also **Key** and **IK** ([pose editing tools](#pose-editing-tools)); in mesh tabs also **Children** ([moving things in the viewport](#moving-things-in-the-viewport)). |

These toggles are shared by every viewport and remembered; [Settings](#animations-and-meshes-settings) ›
**VIEWPORT** edits the same values. **Ghost of the saved clip** draws the clip as last saved as a dimmed skeleton,
and only shows while the clip has unsaved changes.

**Notices** at the bottom of the viewport explain what it cannot show:

- "No preview mesh: set the game directory (Tools › Settings) or add a search folder so a mesh can be found." Set
  the game folder or add a folder with the mesh, or open the mesh and use its preview clip picker instead.
- "No mesh in the library has *N* bones." Pick any mesh in the **Preview mesh** box to see it anyway (it plays
  wrongly), or add the folder holding the right mesh.
- "This clip has *N* bones but *mesh* has *M*." The preview mesh is the wrong skeleton; pick another.
- A mesh still loading, or a file that could not be loaded.

**Morph data** plays in the preview on LOD 0, as in the game.

### Transport and layered preview

The transport sits under the viewport (hidden for static meshes).

**Time bar.** Shows the clip from start to end with frame ticks, a mark for every key time, and the ramps shaded.
Click or drag to move the playhead (playback pauses); hold **Alt** to land between frames. With it focused:
**Left**/**Right** step a frame, **Shift+Left**/**Shift+Right** step ten, **Home**/**End** jump to the ends,
**Space** plays.

**Buttons.** First frame, Previous key (any bone), Step back, Play/Pause, Step forward, Next key, Last frame, and
**Loop** (start again after the last frame; on by default).

**Time readout.** The playhead and end time in the current unit; its tooltip gives all three units. Click it to
switch between frames, seconds and ticks (the same setting as **TIME DISPLAY** in Settings).

**Speed box.** 0.1× to 4×. The status bar repeats the speed.

**Play as action over state** (clip tabs). Turns on the layered preview: the clip plays as an action, with its bone
weights and ramps, over a looping state clip, blended as the game does. Pick the state in the box beside it: the
preview mesh's own table states come first ("state stand · entity.tbl"), then every clip with the same bone count.
A badge over the viewport says it is on ("Layered: this clip as an action over the state …"), and a note beside the
box reports a bone-count mismatch or a load failure. Only the preview changes; clip tool previews also play layered
while it is on. Walk-through:
[Check how an action blends over a state](USAGE.md#check-how-an-action-blends-over-a-state).

### Animation timeline

The **Timeline** tab of the bottom panel is the dope sheet of the clip in front. It opens by default for clip tabs.

**Layout.**

- **Rows**: one per bone, in the skeleton's hierarchy with expand arrows, named from the preview mesh. Without a
  fitting preview mesh, rows are "Bone 0", "Bone 1"… in index order and the summary line says "no fitting mesh:
  bones by index"; then Paste Mirrored, Mirror pairs, Conform, the body weight presets, bone lengths and pose
  editing are unavailable.
- **All keys** row, pinned at the top: a mark at every key time. Click a mark to select every key at that time.
- On each bone row: rotation keys are diamonds (upper half), position keys squares (lower half). At the right of the
  row name: a coloured dot when the bone has a problem (hover for the message) and the bone's **weight**.
- **Ruler**: times in the current unit, the clip's **start and end handles** (triangles), the ramps shaded and, when
  keys are selected, **scale grips** at the ends of the selection.
- Drag the edge of the name column to widen it.

**Toolbar.** **Filter bones** (by name), **With keys** (only bones that have keys), **Selected** (only selected
bones), Expand all, Collapse all, **Key** (key the selected bones at the playhead), Delete, **Fit**. On the right:
bone and key counts and how many keys are selected.

**Mouse.**

| Do | To |
|---|---|
| Click a key / Ctrl+click / Shift+click | Select it / toggle it / add it |
| Drag on empty space (Ctrl or Shift: add) | Box-select |
| Drag a selected key | Move the selection, snapping the grabbed key to frames (**Alt**: free) |
| Ctrl+drag a selected key | Copy the selection to the new time |
| Drag a scale grip on the ruler, or Alt+drag the first or last selected key | Scale the selected keys in time about the playhead |
| Double-click a key | Jump to it and open it in the **Key** inspector |
| Click or drag the ruler (Alt: between frames), or drag the playhead line | Move the playhead |
| Drag a start or end triangle (Alt: free) | Change the clip's start or end |
| Click a row name (Ctrl: toggle, Shift: range) | Select bones (the viewport follows) |
| Double-click a row name | Select all keys of that bone |
| Click a row's arrow | Expand or collapse its children |
| Ctrl+wheel / Shift+wheel or middle-drag / wheel | Zoom about the cursor / pan in time / scroll the rows |
| Right-click | Context menu (below) |

Moved keys snap to the clip's frame grid, never pass an unselected key (the drag stops just short), and when two
land on the same tick the later one wins. **Esc** during a drag cancels it.

**Keyboard** (timeline focused): **Del** or **Backspace** delete; **Ctrl+C**, **Ctrl+X**, **Ctrl+V** copy, cut,
paste at the playhead; **Ctrl+Shift+V** paste mirrored; **K** key the selected bones at the playhead; **Ctrl+A**
select all keys; **Esc** clear the key selection; **Home** fit the clip into view; **Ctrl+Plus**/**Ctrl+Minus**
zoom; **Space** play; **Left**/**Right** step a frame; **Up**/**Down** select the previous or next bone.

**Right-click menus.**

- On a row name: **Select keys of** the bones, **Key at playhead**, **Delete all keys of** the bones,
  **Expand**/**Collapse**, **Expand all**, **Collapse all**, **Open in the Bone inspector**.
- On keys or empty space: **Open in the Key inspector** (on a key), **Delete**, **Cut**, **Copy**, **Paste at
  playhead**, **Paste mirrored**, **Scale about playhead** (50 %, 75 %, 150 %, 200 %, Reverse (−100 %)), **Key
  selected bones at playhead**, **Select all**, **Select none**, **Fit the clip**.

**Copy and paste.** Copied keys go to the Windows clipboard, so they paste into any clip in any tab (or another
Cairn window). Pasting puts the earliest key at the playhead and matches bones by name (by index when either clip
has no fitting preview mesh); copied bones with no match are skipped and the status bar lists them ("found no bone
here and were skipped": the other clip is another rig, so retarget instead). If pasted keys land after the clip's
end, the clip grows to include them. **Paste mirrored** pastes each bone's keys onto its left/right partner,
reflected; it needs a fitting preview mesh. If another program holds the clipboard, Cairn says "The system
clipboard is busy; the keys were copied inside Cairn only." and the keys still paste inside this window.

**Key at playhead** (**K**) adds keys that hold the current pose on the selected bones. The motion does not change.

**Deleting** every position key of a bone makes it collapse onto its parent; the status bar and Problems tab point
this out.

### Clip, Bone and Key inspectors

Number boxes work the same everywhere: type and press **Enter** (or **Esc** to put the old value back), or step
with **Up**/**Down**, the mouse wheel or the small arrows (**Shift**: ten times as much). A run of steps is one
undo step. A box shows *mixed* when the selected items disagree; typing a value sets them all.

#### Clip inspector

The clip's header fields. Every field has a tooltip saying what the game does with it. A ↶ button next to a changed
field puts back the value from when the file was opened or last saved. A coloured line under a field shows a
problem with it.

| Section | Field | Meaning |
|---|---|---|
| FORMAT | **Version** | 7 or 8. Choosing the other converts the clip (they differ only in morph data). |
| TIMING | **Start**, **End** | Where the clip starts and ends on its own timeline. |
| | **Duration** | End minus start, in frames and seconds (read only). |
| | **Ramp in**, **Ramp out** | Blend-in and blend-out times when played as an action. |
| EXPORTER TOLERANCES | **Position reduction**, **Rotation reduction** | The original exporter's settings, kept as a record. Not read by the game. |
| Advanced (not read by the game) | **Total rotation X/Y/Z/W**, **Total translation X/Y/Z** | Exporter leftovers the game never reads. Collapsed by default. |
| FACTS | Bones, Keys, Morph, File size, Origin | Read-only; Bones also gives the preview mesh's count when it differs. |

#### Bone inspector

The bones selected in the viewport or timeline. With nothing selected the tab says how to select bones.

- **Index**, **Parent** (from the preview mesh) and **Keys** (rotation and position key counts).
- **WEIGHT IN THIS CLIP**: the weight box (*mixed* when the selected bones differ) and buttons **0**, **5**, **10**.
  **Apply to children** makes weight edits also apply to every bone below the selected ones.
- **Give the weight above to:** **Upper body** (the spine and everything above it), **Lower body** (the root,
  pelvis and legs) or **All bones**. Upper and lower body need a fitting preview mesh.
- **OFFSET FROM PARENT**: the bone's offset in this clip (its first position key), in the mesh's rest pose, and the
  length difference. A clip whose bone lengths differ from the mesh stretches the character, because the game uses
  the clip's lengths.
- **KEYS**: **Key at playhead**, **Select all keys** (in the timeline), **Delete all keys**.

#### Key inspector

The keys selected in the timeline. Edits apply to every selected key of the kind they concern, as one undo step.

- **TIME**: the time of the selected keys (the earliest when several; the others keep their spacing). **Go to**
  moves the playhead there.
- **ROTATION** (rotation keys): angles **X pitch**, **Y yaw**, **Z roll** in degrees, and the quaternion **qX qY qZ
  qW**. Editing either updates the other; changing one quaternion component adjusts the rest. The angles are the
  bone's turn relative to its parent; the tooltip gives the exact convention.
- **EASES**: **Ease in** and **Ease out**, as a slider and a percentage. A curve preview draws the segments arriving
  at and leaving the key. A negative ease (never found in stock clips) makes the game jump instead of easing; the
  inspector explains it in orange and draws the jump dotted. Cairn never writes negative eases (typing one sets 0)
  but keeps an existing one unless you change it.
- **POSITION** (position keys): **X**, **Y**, **Z** in metres relative to the parent bone (the control points move
  with it).
- **CONTROL POINTS**: **In X/Y/Z** and **Out X/Y/Z**. **Auto (linear)** puts them a third of the way to the
  neighbouring keys: straight segments at even speed. **Auto (smooth)** makes the curve pass smoothly through each
  key.
- **JOINT IN MODEL SPACE**: where the joint is at the key's time (read only; needs a fitting preview mesh).

### Pose editing tools

Pose editing works in clip tabs on a fitting preview mesh. The tools are at the right of the viewport toolbar. In
mesh tabs the same tools move bind poses, collision spheres and prop points: see
[Moving things in the viewport](#moving-things-in-the-viewport).

| Control | What it does |
|---|---|
| Select (**Q**) | Click joints to select bones; no gizmo. |
| Rotate (**E**) | Rings turn the selected bones about the X, Y and Z axes of the chosen space; the outer ring turns about the view; dragging inside turns freely. Every selected bone gets the same turn in its own space. |
| Move (**W**) | Arrows move the bone along the space's axes; the square moves it in the view plane. Only the root and bones whose position is animated (more than one distinct position key) can move. With IK on, drags a hand or foot. |
| Space box | **Local** (the bone's own axes), **Parent** (its parent's axes), **Model** (the character's: X right, Y up, Z forward). |
| **Key** toggle | On (auto-key): a drag writes a key for the selected bones at the playhead, replacing any key there; the motion elsewhere is unchanged. Off (layer edit): the same offset is added to every key of the bone. |
| Arrow next to **Key** | Layer edit options: **Only in a time range**, with **From** and **To** (the ▶ buttons take the playhead's time) and **Falloff**, the time the offset takes to fade in before the range and out after it. Keys are added at the range ends so the motion outside stays as it was. |
| **IK** | With the move tool on a hand or foot, drag it and the arm or leg follows (two-bone IK; the shoulder or hip stays put, the hand keeps its orientation). Keys the three bones at the playhead, so it needs auto-key on ("The IK drag keys the limb at the playhead: turn auto-key on (or IK off)."). |

While dragging: **Ctrl** snaps to 5° (rotate) or 1 cm (move); **Esc** cancels. Each drag is one undo step. A readout
shows the angle, distance or, for IK, the distance to the target ("(out of reach)" when the limb cannot get there).

IK needs a skeleton whose limbs are known: the four stock humanoid rigs, and any skeleton whose bone names say
upper arm / forearm (lower arm) / hand and thigh (upper leg) / shin or calf (lower leg) / foot on each side, such as
the sample figure.

The badge at the bottom left of the viewport says what a drag will do, or why there is no gizmo: no fitting preview
mesh, the bind pose showing, a clip tool preview showing, no bone selected, a bone that cannot move, or IK with
auto-key off. If an edit changed the whole animation rather than one moment, auto-key was off: undo, turn **Key** on
and drag again.

Auto-key, the tool, the space and IK are remembered between sessions; the layer range is per tab. The tool and the
space are shared with mesh tabs: pressing **W** in one tab gives every tab the move tool.

### Clip tools

The **Clip** menu's tools each open a dialog with the same layout:

- A heading and a sentence on what the tool does.
- **BONES** (Reduce Keys and Set Bone Lengths): **Every bone**, the **Selected bones** (selected in the viewport
  before opening), or the **Bones with selected keys** (selected in the timeline before opening).
- The tool's own settings.
- **WHAT WILL CHANGE**: a summary of the result, or in red why it cannot be applied.
- A play button and time bar to watch the preview, which plays in the main viewport (a **Preview** badge shows in
  the viewport header).
- **OK** applies the change as one undo step (disabled when nothing would change); **Cancel** (**Esc**) closes
  without changing anything.

| Menu item | Shortcut | Settings | What it does |
|---|---|---|---|
| **Trim / Crop to Range…** | Ctrl+Alt+T | **From**, **To**; **From playhead**, **To playhead**, **Whole clip**, **Selected keys** | Keeps only the motion between From and To; start and end become the range. Keys outside are dropped and a key is added at each end where the motion carries on, so what stays plays as before. Ramps that no longer fit are scaled down together. |
| **Shift in Time…** | Ctrl+Alt+H | **Amount** (negative: earlier); **Start at 1 f** | Moves every key, the start and the end by the same amount. Use it to line a clip up with frame 1, as stock clips are. |
| **Retime…** | Ctrl+Alt+R | **Scale by** % or **Set length to**; **SCALE ABOUT**: **Start**, **Playhead**, **End** | Speeds the clip up or slows it down. Times, start, end and ramps scale; key values do not. Keys landing on the same tick merge. |
| **Reverse…** | Ctrl+Alt+V | none | Plays the clip backwards; eases and control points swap sides, so reversing twice gives back the same clip. Version 7 morph data runs one keyframe step early when reversed (convert to version 8 first when that matters). |
| **Recompute Start/End…** | | none | Sets start and end to the earliest and latest key. Use it after edits left keys outside the range or the range too long. |
| **Make Loopable…** | | **BLEND**: **The end blends into the first pose** or **The start blends out of the last pose**; **Window**; **ROOT TRAVEL**: **Keep X (sideways)**, **Keep Y (up)**, **Keep Z (forward)** | Cross-fades one end into the other end's pose over the window, so the last frame matches the first. The summary gives the seam before and after. Root travel options need a fitting preview mesh (to know the root). |
| **Resample (Bake)…** | Ctrl+Alt+B | **Keys** per second (30 = one per frame); **Rotations**, **Positions** | Replaces the keys with evenly spaced samples of the current motion. Eases are baked in. The summary measures the largest difference. Use it to tidy a messy import before editing. |
| **Reduce Keys…** | Ctrl+Alt+D | **TOLERANCE**: **Rotations** (degrees, default 0.1), **Positions** (metres, default 0.0005); bone scope | Drops keys while the motion stays within the tolerances. Kept keys are unchanged; the error shown is measured on the result. |
| **Mirror Left/Right…** | | **Plane**: Left ↔ right (flip X), Up ↔ down (flip Y), Front ↔ back (flip Z); **Use the preview mesh's rest pose (recommended)**; **Detect from names**, **Unpair all**; the **BONE PAIRS** table | Every bone takes its partner's animation, reflected; centre bones are reflected in place. Pairs come from names (`-l`/`-r`, `left`/`right`); change any partner in the table. Bone lengths stay the rig's own. Without a fitting preview mesh there are no names, so pair bones by hand. |
| **Offset Bone…** | Ctrl+Alt+F | **ROTATE** Pitch (X), Yaw (Y), Roll (Z); **MOVE** X, Y, Z; **Axes** (Local, Parent, Model); **WHEN**: **The whole clip** or **A time range** with **From**, **To**, **Falloff** | Adds a rotation and/or move to every key of the bones selected in the viewport, over the clip or a range that fades in and out. Children follow. Select the bones before opening. Model axes need a fitting preview mesh. |
| **Remove / Scale Root Motion…** | | **ROOT** bone; **CHANGE**: **Remove (play in place)** with **X sideways**, **Y up**, **Z forward**, **Turning**, or **Scale the travel** with X, Y, Z factors | Remove makes the clip play in place on the chosen axes (or removes the root's turning); Scale changes how far it travels. The root is found from the preview mesh (bone 0 is assumed without one: check it). |
| **Set Bone Lengths…** | | **REFERENCE**: **A reference clip (usually the character's stand clip)** with a filter and list, or **The preview mesh's bind pose**; bone scope | Makes each chosen bone's position keys constant at the reference's length. Root bones keep their motion. Use it when a clip stretches the character (RFA023). Needs a fitting preview mesh. |
| **Conform to Skeleton…** | | **TARGET MESH** with a filter and list | Re-lays the clip out for another mesh's bone list by name: matched tracks move to their new index unchanged, bones the mesh lacks are dropped, and its bones the clip lacks get a rest-pose track. Use it when two skeletons have the same bones in a different order, or a few extra or missing bones; for a really different rig, use Retarget. Needs a fitting preview mesh. Morph data is dropped when conforming to a different mesh. |
| **Set Weights…** | | **BONES**: **All**, **Upper body**, **Lower body**, **Selected bones**, **Selected and children**; **WEIGHT** with **0**, **2**, **5**, **10** | Sets how strongly the clip drives bones when it plays as an action. Keys are untouched. Body sets and children need a fitting preview mesh. |
| **Normalise…** | | **REPAIRS**, each with what it alone would change: **Sort keys by time, drop duplicates**; **Keep keys inside start–end**; **Unit-length rotations**; **Sign continuity**; **Clear pad words**; **Fix missing control points**; **Minimum keys per bone** | Repairs structural problems and leaves everything else as it was. Use it on clips from other tools, or to clear several Problems at once. |
| **Retarget…** | Ctrl+R | see [Retarget dialog](#retarget-dialog) | Moves the clip onto another skeleton. |
| **Compare With…** | Ctrl+Alt+G | see below | Plays another clip as a ghost. |
| **Clear Comparison** | | | Removes the compared clip's ghost. |
| **Convert to Version 7** / **Convert to Version 8** | | | See [Versions and morph data](#versions-and-morph-data). |
| **Strip Morph Data** | | | See [Versions and morph data](#versions-and-morph-data). |

If the clip changes while a tool is open, OK refuses and asks you to open the tool again.

#### Compare with another clip

**Clip › Compare With…** (**Ctrl+Alt+G**) plays another library clip as a ghost skeleton in step with the clip in
front: both start together and the ghost loops over its own length. Use it to compare an edit with the stock
original, or a retarget with the target rig's own clip.

1. Pick a clip in **CLIP TO COMPARE WITH** (clips with the same bone count come first; others are marked "does not
   fit" and may look wrong). The ghost appears at once.
2. Press **Compare** to keep it, or **Cancel** to put back what was there before.

A chip in the viewport header ("Compare: ult2_run.rfa") shows and hides the ghost (eye button) and stops comparing
(✕). **Clip › Clear Comparison** also stops it. Comparing is never an undo step and never changes the clip.

### Versions and morph data

**Clip › Convert to Version 7** and **Convert to Version 8** (or the **Version** box in the Clip inspector) change
the clip's format version. Bone animation is identical in both; only morph data differs (version 8 stores a time per
morph keyframe, version 7 spreads them evenly). Most stock clips are version 8. A 7 to 8 conversion plays the same
in game; 8 to 7 keeps the keyframe count, which can move a talking mouth by a few millimetres.

**Clip › Strip Morph Data** removes the clip's vertex animation. Morph data only fits the one mesh it was made for;
strip it when a clip will play on other meshes (RFA010 tells you when it reaches beyond the preview mesh).
Retargeting, glTF export and conforming to another mesh always leave it out, so a talking face does not animate
after those.

### Retarget dialog

**Clip › Retarget…** (**Ctrl+R**, needs a clip tab in front) or right-click a library clip › **Retarget…**. A
resizable window with a live preview on the right. Nothing is written until you save. Walk-through:
[Retarget animations to another character](USAGE.md#retarget-animations-to-another-character).

#### Source & target tab

- **CLIP TO RETARGET**: open clip tabs first, then the library, with a filter.
- **KIND OF CLIP (PRESET)**: **Seated / fixed controls**, **Standing / locomotion**, **Rotation only**, and
  **Custom** (appears when you change an option behind the preset). The dialog suggests a preset for each clip, from
  the tables (seated for the `jeep_drive`, `jeep_gun` and `on_turret` states, rotation only for `swim_stand` and
  `swim_walk`, standing otherwise) or, when no table names the clip, from words in its name (sit, seat, chair, jeep,
  driver, drive, gunner, turret, pilot, cockpit → seated; swim → rotation only), and says why ("Suggested for
  park_jeep_driver.rfa: Seated / fixed controls (the tables play it as the jeep_drive state)"). The suggestion
  applies until you pick a preset or change an option.

  | Preset | Use it for | What it keeps in place |
  |---|---|---|
  | **Seated / fixed controls** | Drivers, gunners, turret operators: anything sitting with hands on controls. | Hips on the seat; hands and feet exactly where the source's were. |
  | **Standing / locomotion** | Standing, walking, running, crouching, jumping, dying, almost everything else. | The target's hips at its own height, feet on its own ground; arms swing as the source's; a two-handed weapon stays gripped. |
  | **Rotation only** | Swimming and anything with no contact with the ground or a control. | Nothing: every joint turns as the source's, hands and feet land where the target's proportions put them. |

- **Keep the off hand on a two-handed weapon**: holds the left hand where the source's held the weapon relative to
  the right hand while the source's hands are close together (within 45 cm). On by default with Standing /
  locomotion; once you tick or clear it, your choice holds for every preset.
- **SOURCE RIG**: **Reference clip** (the source rig's stand clip, used to find where its feet meet the ground),
  **Source mesh** (the mesh the clip was made for; its bones must match the clip; **Browse…** picks a `.v3c`),
  **Rest mesh** (a mesh of the same skeleton whose bind pose, ideally a T-pose, the source is measured against),
  **Profile**.
- **TARGET RIG**: **Target mesh** (with its skeleton family; **Browse…** for a file), **Rest mesh** (defaults to the
  built-in rig's T-pose mesh, with a hint when the target's own bind is not a T-pose), **Reference clip** (the target
  rig's stand clip: it supplies bone lengths and the pose of bones the source lacks), **Profile**. For the four stock
  humanoid rigs these fill in by themselves.
- **Save profile…** / **Load profile…**: save both rig profiles, the bone map and the options as a JSON file (default
  folder `%APPDATA%\Cairn\profiles`), or load one. A bare rig profile file loads as the target's profile. A saved
  bone map that does not fit the current skeletons falls back to the automatic map, and Cairn says so.
- **OUTPUT**: **File name** (checked against the game's 59-character limit and against clips the game can already
  see; by default `af_{rig}_{clip}.rfa` for a stock target rig, such as `af_female_jeep_driver.rfa`, else
  `{source}_{target}.rfa`) and **Save folder** (where **Save as…** starts; never the game folder; **Browse…** to
  change).

#### Rig profiles

A *rig profile* tells the retargeter how a skeleton's bone names map to common names, which bone is the root and the
pelvis, and which bones form IK chains. Built-in profiles cover the four stock humanoid rigs: rig A (the miner1
type: miner, guards, Parker), the female rig (nurse, admin, Masako, Eos), the merc rig and the civilian rig
(technicians, scientists). Any other skeleton gets a generated profile; it has IK chains only when its bone names
say arm and leg parts.

#### Bone map tab

One row per target bone, in hierarchy order: its index, name, the source bone that drives it (pick another, or none;
**Alt+Down** opens the list), a status chip, and how it was matched. **Auto-map** pairs every bone automatically
(exact names, then common names through the profiles, then the stock rig tables, then similar body words,
cautiously). **Clear** sets every bone to none.

| Status | Meaning |
|---|---|
| mapped | Follows its source bone one for one: key times and eases are kept. |
| reparented | Hangs off a different parent than its source bone: its track is resampled from the source chain. |
| cross-branch | Its parent follows a source bone on another branch: eases are lost. Check this pairing. |
| unmapped | Not mapped although the automatic map would pair it: it holds a still pose. |
| extra | A bone the source has no counterpart for: it holds a still pose (the reference clip's or the bind). |
| parent unmapped | Cannot run: the bone has a source but its parent has none. |
| root mismatch | Cannot run: the target root must follow the source's root. |

Problems are listed under the table; errors stop the retarget ("Fix the bone map first": map the parent, set the
bone to none, or press **Auto-map**). Source bones nothing uses are listed too; their motion is dropped. "The bone
map was made for other skeletons. Press Auto-map." means you changed a mesh after editing the map.

#### Options tab

The preset group again, then every option behind it:

| Option | Choices | What it does | Set by preset |
|---|---|---|---|
| **Rest alignment** | on / off | Removes the difference between the two rest poses, so a limb pointing forward on the source points forward on the target. Leave on unless both rests are identical. | on in all three |
| **Limb IK (hands and feet held in place)** | on / off, with **Arms** and **Legs** | After the transfer, bends arms and legs so hands and feet land where they should. | Seated: arms and legs; Standing: legs only; Rotation only: off |
| IK chain rows | on / off and **pole** X, Y, Z | Run IK on that chain; the pole bias (metres) keeps a longer limb from folding sideways (arms: 0.15 m down). | |
| **Root motion** | **Anchor the pelvis** / **Hip height above the ground** / **Copy the source root** / **Keep in place** | Anchor: the target's pelvis lands where the source's was (a rider stays on the seat). Hip height: the target's hips stand as high above its own ground as the source's above theirs, scaled by leg length; held feet stay on the target's ground. Copy: the root's keys are copied as they are. Keep in place: no travel. | Seated: anchor; Standing and Rotation only: hip height |
| **Scale strides with the leg length** | on / off | With hip height, scales the travel and foot positions by the leg ratio too. Off keeps the source's ground speed, which matches how the game moves the character; on suits a much longer- or shorter-legged target that also moves faster or slower. | off |
| **Bone lengths** | **Target reference clip** / **Target bind pose** / **Source clip** | Where the target's bone lengths come from. The reference clip gives the character type's own proportions, which its other clips use. | target reference clip |
| **Extra bones** | **Reference clip pose** / **Bind pose** | The still pose of target bones the source has no counterpart for. | reference clip pose |
| **Resample rotations at** | off / N fps | Replaces every rotation track by evenly spaced samples (eases become 0). Off (recommended) keeps the source's key times and eases where possible; IK tracks are always resampled. | not part of a preset |
| **Rounding** | **Within unit length (recommended)** / **Reference (original tool)** | How rotation keys are rounded when saved. The recommended choice makes the game turn smoothly through every segment; Reference matches the files of the original retarget tool, and a few slow segments then snap in game. | not part of a preset |

The off-hand grip option sits under the preset group. The result recomputes a moment after any change.

#### Report tab

- **OUTPUT CHECKLIST**: Round trip (the result saves and reads back identically), Bone count (as the target mesh),
  Keys present, Key times, Unit quaternions, Sign continuity. Every line should be ticked.
- **PINNED CONTACTS (WHAT IK HELD, WORST DISTANCE)**: each hand or foot IK held, what it was held to, and the worst
  distance. A foot is only measured while the source's foot is on the ground, an off hand only while gripping. About
  0 cm is right; more means the limb was "fully stretched" (the target's limb is too short to reach, often in deaths
  lying flat with legs out: try another preset or fix the moment with IK afterwards). With no IK the tab says
  nothing was pinned.
- **HANDS, FEET AND HEAD FROM THE SOURCE'S (MODEL SPACE)**: how far unpinned joints land from the source's. These are
  proportions, not errors (a shorter character's head sits lower).
- **JOINTS**: per joint, the largest and mean difference relative to the pelvis, the largest in model space and the
  largest change of segment direction. IK moves elbows and knees on purpose, so limb directions differ by the
  proportions.
- **WHAT THE RETARGET DID**: notes (still bones, root placement, IK) and warnings (morph data dropped, IK chains
  skipped, a missing reference clip; without a stand clip as reference the ground is guessed and the character may
  float or sink).

#### Preview and buttons

The preview plays the result on the target mesh, with the source as a thin coloured skeleton in step (moved onto the
target's ground with hip height; the caption says by how much). **Side by side** shows the source mesh playing the
source clip next to it instead. Orbit and zoom as in the main viewport.

- **Retarget**: opens the result as a new, unsaved clip tab on the target mesh.
- **Save as…**: writes the result to a file now and opens it. It refuses a file that is open in a tab, and asks
  before writing into the game folder.
- **Cancel** (**Esc**): closes without making anything.

### Batch retarget

**Tools › Batch Retarget…** retargets many clips from one source rig onto one target rig with the same bone map,
and writes them to a folder.

**Clips & output tab.**

- The preset group, with **Automatic (per clip)** first: each clip gets its suggested preset, and the results say
  which.
- **ADD CLIPS FROM THE LIBRARY**: **Only clips for the source mesh** (only clips with its bone count), a filter, a
  list to tick, **Add checked**.
- **Every clip the tables give** an `entity.tbl` class, **Add class**: every state, action and weapon-specific clip,
  once each.
- **Add files…**, **Remove** (selected rows), **Clear**.
- **QUEUE AND OUTPUT NAMES**: each clip, its bone count and output name. A warning glyph marks a name over 59
  characters, another visible clip's name, a duplicate within the batch (the later one is written as `…_2.rfa`; add
  `{source}` to the pattern to keep them apart) or a wrong bone count. An ⓘ note marks a name already in the output
  folder.
- **OUTPUT**: **Folder** (never the game folder; **Browse…**) and **Name pattern**, default `af_{rig}_{clip}.rfa`.

| Token | Becomes |
|---|---|
| `{rig}` | The target profile's name (`A`, `female`, `merc`, `civilian`, or `generic`). |
| `{clip}` | The clip name after its first underscore (`park_jeep_driver` → `jeep_driver`). |
| `{source}` | The source clip's base name. |
| `{target}` | The target mesh's name. |
| `{sourcerig}` | The source profile's name. |

**Rigs**, **Bone map** and **Options** tabs: as in the [Retarget dialog](#retarget-dialog). The source mesh defaults
to the active clip's preview mesh, or `ult2_guard.v3c`.

**Running.** **Run** retargets every queued clip and writes the results. If any output already exists, it asks once
for the whole batch: **Replace**, **Keep existing** (those clips are skipped) or **Cancel**. **Stop** stops after the
clip in progress; what is done stays written. **Close** (**Esc**) closes, stopping a running batch first. A clip that
fails does not stop the others.

**Results.** Each clip's **Status**, **Preset**, **Output**, **Pinned contacts** ("0.02 cm (foot-l)", "nothing
pinned", or how many limbs were fully stretched) and warnings; a summary line counts them. Double-click a result to
open it on the target mesh. **Copy table lines** copies `entity.tbl` lines giving the new clips the slots their
sources have: for the class you last added with **Add class**, otherwise each clip's first `entity.tbl` use,
otherwise plain `+State:` lines named after the files. **Save report…** writes the settings and results (and the
table lines) as Markdown or text.

### Mesh editing

Character meshes (`.v3c`) are edited in the **Structure** inspector tab, the **Mesh** menu and, for bind poses,
collision spheres and prop points, with the viewport's gizmos. Every change is one undo step. Static meshes (`.v3m`)
show the same tree with the editors disabled, and no gizmos.

#### Structure tree

- **Header**: kind, version, counts and origin.
- **Submesh N: name** → **LOD n** (flags, vertex and triangle counts, batches, prop points) → **Batch b: texture**
  (one draw call with one texture) and **Textures (n)** (the LOD's texture list, with where each texture is found);
  **Materials (n)**.
- **Prop points (n)**, **Bones (n)** (as a hierarchy), **Collision spheres (n)**.

Selecting a node shows its facts and, for editable nodes, its editor in the pane below the tree (drag the splitter
to resize). Selecting a submesh, material, LOD, batch or texture highlights its geometry in the viewport; a LOD,
batch or texture node also switches the viewport to its LOD; selecting a sphere or prop point highlights it.
Clicking a joint, a collision sphere or a prop point in the viewport selects its node. **Del** removes the selected
sphere or prop point, **Alt+Up**/**Alt+Down** move the selected bone, **F2** renames (on a LOD texture entry, it goes
to the material's texture name).

#### Mesh editors

| Node | Fields | Notes |
|---|---|---|
| Bone | **Name**, **Parent**; **BIND POSE** in **Local** or **World** terms, **Children follow**, **Position (metres)**, **Rotation (degrees)**; **INDEX ORDER**: **Move up**, **Move down**, **Reorder…** | Renaming changes no clip (clips use indexes), but retarget, conform and glTF see the new name. A parent that would make a loop is refused. Moving the bind pose moves the skinned mesh; with Children follow off, children stay where they are in the model. **Children follow** is the same setting as the viewport toolbar's **Children** toggle. Index changes go through the reorder warning. |
| Collision sphere | **Name**, **Bone**, **CENTRE (METRES, IN THE BONE'S FRAME)**, **Radius**; **Add**, **Duplicate**, **Remove** | Names hold 23 characters. |
| Prop point | **Name**, **Bone**, **POSITION (METRES)**, **ORIENTATION (DEGREES)**; **Add**, **Remove** | Names hold 67 characters. Edits and removal apply to every LOD that has the point. |
| Material | **Texture** with **Browse…**, **All LOD entries**, **Emissive**; **STORED, NOT USED BY THE ENGINE**: **Reflection**, **Reflection map**, **Flags**, **Unknown 0**, **Unknown 1** | Texture names hold 31 characters. The LOD texture entries that carried the old name follow; with **All LOD entries** ticked, every entry of the material does (including names like `foo-mip1.tga`). Emissive 1 renders the material full bright. |
| LOD | **Distance** | The camera distance at which this LOD starts; each must be larger than the previous one's (LOD 0 is 0 in stock meshes). |
| Submesh | **Name** | Its trailer entry follows. |

Names accept Latin-1 characters only. **Esc** in a name box puts the stored name back; **Enter** commits.

#### Moving things in the viewport

The viewport toolbar's Select (**Q**), Rotate (**E**) and Move (**W**) tools and the space box work in mesh tabs
too. Select what to move (in the viewport or the tree), pick a tool, and drag a gizmo handle; the handles behave as
in [pose editing](#pose-editing-tools).

| Selected | Move (**W**) | Rotate (**E**) |
|---|---|---|
| A bone (a joint) | Moves the bone's bind (rest) pose. | Turns the bone's bind pose about its joint. |
| A collision sphere | Moves its centre; drag its outline, or the round grip on it, to change the radius. | Nothing: a sphere has no orientation (the badge says so). |
| A prop point | Moves it. | Turns it. |

**The space box.** **Local** uses the selected thing's own axes (a sphere has none and uses its bone's), **Parent**
the axes of what it is stored relative to (a bone's parent; a sphere's or prop point's bone), and **Model** the
character's axes. The bone editor's own **Local** / **World** switch only changes how its numbers are shown.

**Bone binds and the bind pose.** A bind edit changes where the skeleton sits inside the skin, not the skin itself.
So while a bone is selected with the move or rotate tool, the viewport shows the bind pose and the mesh stays still
while you drag. A preview clip is paused and hidden meanwhile and a "Bind pose" badge shows; pick Select (**Q**), or
select a sphere or prop point, and the clip comes back. The **Children** toggle (the bone editor's **Children
follow**) decides whether the bones below move and turn with the bone (on) or stay where they are (off).

**Several bones.** With several bones selected, a drag edits all of them: each turns by the same amount in its own
axes, and all move by the same distance. With **Children** on and one selected bone below another, only the bone
you selected last is edited; the badge says so.

**Spheres and prop points in a clip's pose.** They are drawn and dragged where they are in the pose the viewport
shows (the bind pose, or the preview clip at the playhead). Cairn works out the stored value so they end where you
dragged them; in other poses they follow the bone. A prop point's change goes into every LOD that has it.

**While dragging.** **Ctrl** snaps to 5° (rotate) or 1 cm (move and radius); **Esc** cancels and puts everything
back. A readout shows the distance, angle or radius. The editor pane's numbers follow the drag; the tree, its facts
and the Problems list update when you let go. Each drag is one undo step, named for what it did ("Move bone hand-l
bind", "Rotate prop point 'muzzle_1'", "Resize collision sphere 'head'").

**The badge** at the bottom left says what a drag will do (and what the stored values are relative to), or why there
is no gizmo: nothing movable selected, a collision sphere with the rotate tool, or a read-only `.v3m`.

The tool and space are shared with clip tabs and remembered; **Children** is kept for the session. Typing in the
editor pane does all of this from the keyboard.

#### Mesh menu

| Item | What it does |
|---|---|
| **Choose Preview Clip…** | Opens the preview clip picker. |
| **New Clip for This Mesh…** | Opens the [New Clip dialog](#new-clip-dialog) for the mesh in front. |
| **Level of Detail** | Which LOD the viewport draws. |
| **Add Collision Sphere** | Adds a sphere (named `sphere`, radius 0.2 m, at the bone's origin) on the selected bone, or the selected sphere's bone, or the root; selects it. |
| **Duplicate Collision Sphere** | Adds a copy of the selected sphere. |
| **Add Prop Point** | Adds a prop point (named `prop`) on the selected bone (or the root), in every LOD; selects it. |
| **Remove Selected** (Del in the tree) | Removes the selected sphere or prop point. |
| **Rename Selected** (F2) | Puts the cursor in the selected node's name box: a bone's, sphere's, prop point's or submesh's **Name**, or a material's **Texture**. On a LOD texture entry it selects the entry's material and its **Texture** box. Greyed out on nodes without a name (header, LOD, batch). |
| **Reorder Bones…** | Opens the reorder dialog. |
| **Move Bone Up** / **Move Bone Down** (Alt+Up / Alt+Down in the tree) | Swaps the selected bone with its neighbour in the index order, through the reorder dialog. |

#### Reorder bones

Clips address bones by index, so changing a mesh's bone order makes every clip made for it play its tracks on the
wrong bones until the clip is conformed. The mesh itself looks and skins the same: parents, vertex weights, spheres
and prop points are remapped.

The dialog shows the warning, the **NEW ORDER** list (**Move up**, **Move down**, **Reset**; **Alt+Up**/**Alt+Down**
in the list) with each moved bone's old index, and **OPEN CLIPS PREVIEWING THIS MESH** with a tick box each. Ticked
clips are re-laid out for the new order in the same action (one undo step in each tab) and then preview the
reordered mesh, so they play as before. The button reads **Reorder** or **Reorder and conform**. Clips on disk that
are not open must be conformed separately: open each, then **Clip › Conform to Skeleton…** with the saved mesh.

#### Texture browser

**Browse…** in the material editor lists every texture in the mesh's folder, your search folders and the game's
archives. Type in **Filter** (**Down** moves into the list), pick one and press **Use texture** (**Enter**). The
material stores the name only; the game finds the file the same way.

### glTF options for clips and meshes

Cairn reads and writes glTF 2.0 using REDUX's conventions, so files move between the two tools and into Blender and
other glTF tools.

**What carries over.**

- An exported clip imported back onto the same skeleton, with the key extras, comes back exactly as it was. Mesh
  geometry, skeleton, collision spheres and prop points come back within floating-point precision.
- If another tool re-saved the file (the extras are gone), the motion comes back very closely, but the clip starts
  at frame 1 (or the **Start at** value), ramps and weights take the import defaults, and eased segments come back as
  extra keys.
- Morph (vertex) animation is never exported; such clips export their bone animation with a warning.

#### Export to glTF

**File › Export › glTF…** (**Ctrl+E**) for a mesh tab, or a clip tab (the clip is exported on its preview mesh; a
mismatched bone count is warned about but still exports).

| Section | Options |
|---|---|
| INCLUDE | **Skeleton only (no geometry)**; one tick box per LOD; **Collision spheres (N)**; **Prop points (N)**; **Textures (as PNG)**: each texture, found as the game finds it, written as a PNG (needs the game folder or a search folder). |
| ANIMATIONS | Clips with the mesh's bone count: open tabs first, then the library (the tables' clips for the mesh first); a filter, **None**, **Tick shown**. A clip export always includes itself. A static mesh has none. |
| FORMAT | **.gltf + .bin + PNG textures**: readable by REDUX, Blender and every viewer; keep the files together. **Single .glb**: one file with the textures inside; Blender and viewers read it, REDUX does not. |
| KEY EXTRAS | **Write RF key extras (rf_keys)**: stores the original keys so an unedited round trip is exact. REDUX ignores them either way. |
| OUTPUT | The file path (`.gltf` or `.glb`) and **Browse…**. Never the game folder. |

**Export** writes the file and keeps the dialog open with a **RESULT** list: the files written, what went in,
whether key extras were written, clips whose morph data was left out, textures not found, and other warnings.
Replacing an existing file (and its `.bin` and PNGs) is asked about first. **Stop** stops a running export; nothing
is written once stopped. **Close** (**Esc**).

#### Import Animation from glTF

**File › Import › Animation from glTF…** (**Ctrl+I**), or open or drop a `.gltf`/`.glb` (a file with both
animations and meshes asks **Import animation**, **Import mesh** or **Cancel**).

- **ANIMATIONS**: tick the ones to import (one clip each); the selected one plays in the preview.
- **TARGET MESH**: open tabs first (a clip tab offers its preview mesh), then library characters; a filter;
  **Browse…** for a `.v3c`.
- **OPTIONS**. A note says when the animation carries RF timing (a Cairn or REDUX export); then its start, end,
  ramps, weights and version come from the file and these are only fallbacks.

| Option | Default | Meaning |
|---|---|---|
| **Reference clip** | the character's stand clip | Bones the file has no node for hold this clip's first pose, or the bind pose. |
| **Bone weight** | 10 | Each bone's weight where the file has none. |
| **Start at** | frame 1 | Where the first key lands without a start time in the file. If an import looks offset in time, change this. |
| **Sample every** | 1 frame | The step for bones that must be resampled. |
| **RFA version** | **8** | The version where the file has none. |
| **Ramp in** / **Ramp out** | 0 | Fade times where the file has none. |
| **Reduce keys** | off | With **Rotation within** and **Position within**: drops keys that change the pose by less than this. Tracks restored from key extras are never reduced. |
| **Use RF key extras when present** | on | Restores a Cairn export's original keys exactly while they still match the glTF motion. Off converts the glTF keys instead. |

- **Bone map** tab: the same table as the [retarget bone map](#bone-map-tab), with the glTF node per bone. Names are
  matched exactly first, then with the stock rigs' naming rules, then by body part and side (`thigh_l` finds an
  upper-leg bone on the left). Bones with no node do not move: pick the right node, or rename the bones in Blender.
- **Report** tab: per bone, how its track was made: **Restored** from the key extras, glTF keys **converted** one
  for one, **resampled** (its node hangs off a different parent or has a different rest frame, or the file uses
  curves RF cannot store), or holding the bind or reference pose (no node). Notes list animated nodes no bone
  follows (their motion is dropped) and channels that were ignored (such as scale).
- **Import** opens each ticked animation as a new, unsaved clip tab on the target mesh, named after the animation.
  **Stop** stops (no clips are opened). **Cancel** (**Esc**). An import started while another dialog is open is not
  queued.

#### Import Mesh from glTF

**File › Import › Mesh from glTF…**.

- **BUILD**: **Character (.v3c)**, skinned to the glTF's skeleton, with collision spheres and prop points from
  `rf_csphere::` / `rf_prop::` nodes; or **Static mesh (.v3m)**, no bones, any skin ignored.
- **KEEP AN EXISTING SKELETON**: **Replace the geometry but keep the skeleton, spheres and prop points of** an open
  character mesh. The glTF's joints map to its bones by name (the `__rfbi` number an export adds to each name may
  stay), and clips made for that mesh keep working. The existing bind pose is kept for every bone and your vertices
  are taken as they are.
- **OPTIONS**: **Scale** (1 keeps the size; glTF and RF both use metres; 0.01 for centimetres); **Texture names**:
  **Name them .tga (recommended)** or **Keep the glTF's names** (the texture name comes from the image's file name,
  without its folder); **LOD distances** for **LOD 0**, **LOD 1**, **LOD 2** (defaults 0, 10, 50) where a node
  carries none.
- A preview of the built mesh and a summary (LOD 0 size, LOD distances).
- **PRE-FLIGHT**: every finding with its code and a fix hint ([MI codes](#gltf-mesh-import-findings)). "No findings"
  means nothing was converted, assumed or out of limits. Errors disable **Import**.

Batches are made per material, vertex weights are reduced to the 4 strongest bones and normalised, and LODs come
from REDUX extras or `_LOD0`/`_LOD1`/`_LOD2` object names (objects with the same name before `_LOD` form one
submesh).

**Import** opens the mesh as a new, unsaved tab (a static mesh saves as `.v3m`). **Cancel** (**Esc**).

### Exporter and PS2 mesh conversion

Walk-through: [Convert exporter and PS2 meshes](USAGE.md#convert-exporter-and-ps2-meshes). **Help › Exporter and
PS2 meshes** has the same facts in the app.

**Opening.** A file is recognised by its contents: an exporter mesh named `.v3m` still opens as one. Besides
**File › Open**, you can select one in a packfile and use **Open in Cairn**; the packfile preview pane shows it in
3D and the **Info** column summarises it. A file that could not be read opens with the reason in Problems, and
**Save** and **Save As** are off.

**Where the converted file goes.** **Next to the source** (for a packfile entry: beside the packfile), **into a
folder**, or **into the packfile** the mesh came from as a new entry (one undo step in the packfile's tab). **Next to
the source** is off when the source (or its packfile) is in the game folder: choose a folder. **Save As** on the tab
also writes the converted mesh, never over the file the tab was read from. **Convert meshes…** in a packfile
converts many in one window and one undo step, with a report of what could not be converted and each remark with
the meshes it concerns. A PS2 mesh converts from its exporter twin when the packfile holds one, selected or not.

**What the conversion does** (checked against stock meshes made from the same exporter files):

- **Exporter meshes** convert the way the game's mesh compiler did: one smooth normal per position (hard edges are
  smoothed; files without usable normals get computed ones), each triangle's UVs moved by whole tiles into 0..1,
  corners whose UVs differ by less than 0.03 joined, materials naming the same texture drawn as one batch, LOD
  submeshes folded into the submesh that names them (at most three levels), bone names in lower case, a character's
  weights kept as they are. The report names each of these that applied.
- **PS2 meshes** lost their submesh names and their welded vertices in the PS2 build, and their weights are coarse
  (1/16 steps). When the same-named `.v3d`/`.vcm` is beside a `.rfm`/`.rfc`, Cairn converts that instead and says so.
  Texture names are kept; they name `.tga` files, which the packfile's **Convert to .tga…** makes from the PEG
  texture packs.
- **Red Faction II** `.rfm`/`.rfc` files and damaged files open with the reason in Problems (LEG001) and cannot be
  converted.
- The "texture has alpha" material flag, which the old compiler set by reading the texture file, is not set; the game
  does not read it.

### Table usage panel

The **Table usage** tab of the bottom panel reads the game's `entity.tbl`, `weapons.tbl`, `pc_multi.tbl` and
`fpgun.tbl` (a copy in an earlier search location is read before the stock one). To edit
tables, use the [Tables](USAGE.md#tables-tbl) module.

**For a clip**: **Table lines** (every line naming the clip: table, class, state or action, slot, weapon block, line
number, sound) and **Meshes that play it** (with a warning badge when a mesh's bone count differs). Double-click (or
**Enter**) a mesh to preview the clip on it.

**For a mesh**: each class that uses it, with its clip list (states and actions, line numbers, a badge with each
clip's bone count or "not in the library"). Double-click a clip (or right-click › **Open**) to open it; right-click
› **Preview on this mesh** plays it on the mesh without opening it.

**Copy table line** (button, right-click, or **Ctrl+C** on a row) copies the selected line, or a whole list for a
group row, or, for a clip no table uses, a new `+State:` line. Choose the **Layout**: **entity.tbl** (tab indent,
aligned columns) or **weapons.tbl** (no indent, columns sized to the values). Clips are written as `name.mvf`.

**Empty states** explain themselves: no game folder or search folder (with a **Settings…** button), no tables
found, tables that could not be read, a clip no table plays ("as it stands this clip is never used"), a mesh no
table gives clips, a static mesh.

### Clip and mesh problem codes

The **Problems** tab lists what the checker found in the file in front, errors first. Checks run after every change;
some need the preview mesh, the library or the tables, and appear once those have loaded. Each row shows the
message, the help text, a location link (click to select the bone, jump to the key's time, or focus the field or
mesh node), the code and any quick-fix buttons (each one undo step). The filter buttons show or hide errors,
warnings and suggestions; the status bar's counts open this tab (click again to hide the panel); **Up**/**Down**/
**Enter** on a row select what it points at. With nothing to report it says "No problems — *file* will load as
intended."

Quick fixes that need a dialog open it: for a wrong preview mesh, **Choose another preview mesh…**; for a clip that
does not fit a skeleton, **Conform to skeleton…** (with the mesh the problem names, or else the preview mesh,
already picked); for a missing texture, **Locate the file…** (the material's texture browser: the texture you pick
becomes the material's texture name; not on a read-only `.v3m`); for name problems, **Save as…**.

#### Clip errors

The game will misbehave.

| Code | Problem | Quick fix |
|---|---|---|
| RFA001 | The version is not 7 or 8; the game plays only those. | Convert to version 8 |
| RFA002 | The clip's bone count differs from the preview mesh's; the game matches by index and plays it wrong. | Choose another preview mesh…; Conform to skeleton…; or use Retarget |
| RFA003 | A bone has no position keys, so it collapses onto its parent's joint. | Add the missing keys (from the rest pose when the preview mesh fits) |
| RFA005 | Key times are not strictly increasing (out of order or duplicated). | Sort the keys and drop duplicate times |
| RFA006 | A key lies outside the clip's start–end range. | Set start and end to the first and last key; or Drop the keys outside the range |
| RFA007 | More than 50 bones. | Conform to skeleton… |
| RFA008 | The end is before the start. | Set start and end to the first and last key |
| RFA009 | The clip cannot be saved as it is (usually morph data that does not match the version). | Strip the morph (vertex) animation |
| RFA010 | The morph data moves a vertex the preview mesh's LOD 0 does not have (Alpine Faction refuses such a clip). | Choose another preview mesh…; Strip the morph (vertex) animation |
| RFA011 | A value is not a number. | Set the weight to 10 (for a weight); Strip the morph data (for morph); otherwise retype it in the Key inspector |
| RFA012 | A rotation key is all zeros: the bone and everything below it collapse. | Give the key a rotation in the Key inspector, or delete it |
| RFA013 | A table plays this clip on a mesh with a different bone count. | Conform to skeleton… (that mesh picked); or change the table, or retarget the clip |
| RFA014 | The file name is longer than 59 characters and can crash the game. | Save as… a shorter name |
| RFA015 | A bone has exactly one rotation key; the game reads past it and can snap to garbage. | Hold the rotation with a key at the start and the end |
| RFA016 | Morph data the game cannot time (no keyframes, or a version 7 clip with no length). | Strip the morph (vertex) animation |

#### Clip warnings

The clip loads, but not as intended.

| Code | Problem | Quick fix |
|---|---|---|
| RFA004 | A bone has no rotation keys, so it gets no rotation relative to its parent. | Add the missing keys |
| RFA020 | A position key's control point is at (0, 0, 0): the bone swings through its parent's joint between keys. | Set the control points to Auto (Linear) |
| RFA021 | A rotation key is not unit length, so the game scales the bone's mesh too. | Normalise the quaternions |
| RFA022 | Ramp in plus ramp out is longer than the clip: as an action it never reaches full weight. | Set the ramps to *n* in, *m* out (values that fit) |
| RFA023 | Bone lengths differ from the character's other clips (more than 2 cm and 10 %): it will stretch the character. A new clip started from the bind pose often shows this. | Set the bone lengths from the reference clip |
| RFA024 | Another clip with the same name is visible to the game; only one is ever loaded. | Save as… a new name |
| RFA025 | A bone weight above 10 removes every state from that bone. | Set the weight to 10 |
| RFA026 | The clip starts and ends at the same time although it has keys. | Set start and end to the first and last key |
| RFA027 | A ramp is negative, so the game never ramps that end. | Set the ramps to *n* in, *m* out (the negative one becomes 0) |
| RFA028 | Two rotation keys are visibly apart but stored so that the game snaps between them instead of turning. | Re-quantise the keys within unit length |

#### Clip suggestions

Harmless tidy-ups.

| Code | Problem | Quick fix |
|---|---|---|
| RFA030 | Consecutive rotation keys point opposite ways (opposite hemispheres). The game turns the short way anyway; other tools may not. | Make the keys sign-continuous |
| RFA031 | A rotation key's unused padding is not zero. | Clear the pad words |

#### Mesh errors

| Code | Problem | Quick fix |
|---|---|---|
| V3C001 | The format version is not the one the game reads. | Re-export |
| V3C002 | More than 50 bones. | Merge or remove bones in the source and re-export |
| V3C003 | A bone's parent is not a bone of the mesh, or itself. | Make the bone a root |
| V3C004 | Bone parents form a loop. | Reparent one bone of the loop |
| V3C005 | A collision sphere follows a bone that does not exist. | Attach it to the root bone |
| V3C006 | A prop point is attached to a bone that does not exist. | Attach it to the root bone |
| V3C007 | A vertex is weighted to a bone that does not exist. | Re-skin (re-export or rebuild through glTF import) |
| V3C008 | A submesh has no LODs or more than 3. | Re-export with 1 to 3 |
| V3C009 | More than 7 textures in one LOD. | Combine textures or split the object |
| V3C010 | A triangle uses a vertex its batch does not have. | Re-export |
| V3C011 | A batch uses a texture slot the LOD does not have. | Re-export |
| V3C012 | The header's submesh or sphere counts disagree with the file. | Set the header counts to the sections' |
| V3C013 | A value is not a number. | Retype it, or re-export |
| V3C014 | The mesh cannot be saved (a count or size the format cannot hold). | Split into more batches or submeshes |
| V3C015 | A morph map entry points outside its batch. | Re-export |

#### Mesh warnings

| Code | Problem | Quick fix |
|---|---|---|
| V3C020 | A character vertex's weights do not add up to a full weight. | Normalise the weights in the source and re-export (glTF import normalises them) |
| V3C021 | A texture was not found in the mesh's folder, the search folders or the game (it shows grey). | Open search settings…; Locate the file… |
| V3C022 | A name fills its field with no room for the terminator. | Shorten the name |
| V3C023 | Two bones share a name (retarget, conform and glTF cannot tell them apart). | Rename bone *n* to *name*_2 |
| V3C024 | A collision sphere's radius is zero or negative. | Set the radius to 0.1 m |
| V3C025 | LOD distances are not increasing. | Sort the LOD distances |
| V3C026 | The file name is longer than 59 characters. | Save as… |
| V3C027 | The mesh has no submeshes: it holds no geometry, so the game draws nothing for it. | Re-export the mesh |

#### glTF mesh import findings

Shown in the **PRE-FLIGHT** list of **Import Mesh from glTF**. Errors disable **Import**.

| Code | Kind | Finding | What to do |
|---|---|---|---|
| MI001 | Error | More than 50 bones (or more than 255). | Remove or merge helper bones, or keep an existing mesh's skeleton. |
| MI002 | Error | A name is too long or not plain Latin letters (bones and spheres 23 characters, submeshes 23, textures 31, prop points 67). | Rename it in your 3D tool. |
| MI003 | Error | More than 3 LODs. | Keep at most three; name them `_LOD0`, `_LOD1`, `_LOD2`. |
| MI004 | Error | More than 7 textures in one LOD (or more than 255 materials). | Merge materials or bake them onto a shared texture. |
| MI005 | Info | A material's geometry was split into several batches. | Nothing; it is automatic. |
| MI006 | Warning | Vertices with no bone weights were bound to the mesh node's bone or the root (they will not bend). | Weight-paint them if they should follow another bone. |
| MI007 | Warning | Vertices weighted to more than 4 bones were reduced to the 4 strongest. | Limit influences to 4 in your 3D tool (Blender: Weights › Limit Total). |
| MI008 | Warning / Info | Points or lines were skipped; strips or fans were converted. | Only triangles are imported. |
| MI009 | Info | Missing normals were generated. | Export normals if the smoothing looks wrong. |
| MI010 | Warning | Vertices have no texture coordinates. | Add a UV map. |
| MI011 | Warning | A joint matches no bone of the kept mesh; its vertices go to the nearest matched parent. | Rename the joint, or weight its vertices to an existing bone. |
| MI012 | Info | Geometry replaced; the existing skeleton, spheres and prop points are kept. | Nothing. |
| MI013 | Error | No triangle geometry. | Export the mesh objects too, not only the armature. |
| MI014 | Warning | LODs were renumbered. | Check the order; name objects `_LOD0`… to control it. |
| MI015 | Warning | Weights to joints that are not bones were dropped. | Weight only to deforming bones. |
| MI016 | Info | A skinned file was built as a static mesh; the skin is ignored. | Choose Character to keep the skin. |
| MI017 | Warning | A character was asked for but there is no skeleton. | Add an armature, or build a static mesh. |
| MI018 | Warning | The bind matrices do not fit; the bind pose was rebuilt from the nodes. | Apply transforms in your 3D tool and re-export. |
| MI019 | Info | Vertex weights that do not add up exactly were kept as stored. | Usually harmless. |
| MI099 | Error | The mesh could not be built. | The message names the limit; fix it and import again. |

#### Exporter and PS2 mesh codes

| Code | Kind | Meaning |
|---|---|---|
| LEG001 | Error | The file is a Red Faction II mesh or is damaged; it cannot be converted. |
| LEG100 | Info | What converting this mesh approximates (shown on every exporter or PS2 mesh tab). |

### Animations and meshes settings

**Tools › Settings…**. APPEARANCE, RED FACTION FOLDER and SEARCH FOLDERS are on the **General** page and apply to
every module (see [The Cairn window](USAGE.md#the-cairn-window)); the sections below are on the **Animations and
meshes** page.

| Section | Options |
|---|---|
| LIBRARY | What double-click (or Enter) on a library entry does: **Preview on the current document (default)** or **Always open in a new tab**. |
| VIEWPORT | What every viewport shows (the viewport toolbar changes the same settings): **Mesh** (**Textured**, **Flat**, **Hidden**), **Skeleton**, **Bone names**, **Grid and axes**, **Collision spheres**, **Prop points**, **Root motion path**, **Full bright**, **Perspective**, **Bind pose**, **Background**. |
| TIME DISPLAY | **Frames** (30 fps), **Seconds**, **Ticks**. |
| RESOLVED SEARCH ORDER | Where the active file (or any open file) looks for meshes, tables and textures, in order. |

**OK** applies the settings (and rebuilds the library when the game folder or search folders changed); **Cancel**
discards them. File associations for `.rfa`, `.v3c`, `.v3m` and the exporter and PS2 meshes are set with the other
types under **Tools › File Associations…**.

### Saving clips and meshes

- Each tab keeps an undo history of its last 200 steps. Drags and runs of number-box steps are one step each.
- A file opened from inside a `.vpp` is never written back there: **Save** asks where to write a copy, and the
  suggested folder is never the game folder.
- A file with errors asks once before saving ("Save with errors": **Save anyway** or **Cancel**).
- A file that breaks a limit of the format cannot be written; the message says which.
- Saving onto a file open in another tab is refused (the two tabs would overwrite each other).
- New documents (a new clip, a retarget result, an import) stay marked unsaved until you save them, even after
  undoing back to their first state.
- **Save All** (**Ctrl+Alt+S**) saves every changed tab and lists any that could not be saved.
- No save, export, extract or retarget output defaults to the game folder, and writing a retargeted clip there asks
  first: that folder holds the game's own files (and the game loads assets only from packfiles, not loose files).
- Static meshes (`.v3m`) open read-only; **Save As** writes a copy.
- If a file open in a tab changes on disk, an unchanged tab reloads it; a tab with unsaved changes shows **Reload**
  and **Keep mine**. If the file is deleted or renamed, the tab keeps its contents and shows **Keep editing**.
- Unsaved clips and meshes are covered by Cairn's crash recovery like any other tab (see
  [The Cairn window](USAGE.md#the-cairn-window)). Retarget profiles are kept in `%APPDATA%\Cairn\profiles`.

### Animations and meshes keyboard shortcuts

Shortcuts marked "(viewport)", "(timeline)" and so on work while that area has keyboard focus; click it first.
**F1** in a clip or mesh tab opens the RFA & V3C format reference; **Help › Keyboard Shortcuts** lists every
shortcut Cairn binds.

**Files and tabs**

| Keys | Action |
|---|---|
| Ctrl+N | New clip for a character mesh |
| Ctrl+O | Open clips or meshes |
| Ctrl+S / Ctrl+Shift+S / Ctrl+Alt+S | Save / Save as / Save all |
| Ctrl+W, or middle-click a tab | Close the tab |
| Ctrl+Shift+T | Reopen the last closed tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+E | Export to glTF |
| Ctrl+I | Import animation from glTF |

**Editing**

| Keys | Action |
|---|---|
| Ctrl+Z | Undo |
| Ctrl+Y or Ctrl+Shift+Z | Redo |
| Up / Down, wheel (Shift: ×10) | Step a number box |
| Enter / Esc | Commit a number box / put the old value back |
| F2 | Rename the selected bone, sphere, prop point, submesh or material texture |
| Del (structure tree) | Remove the selected sphere or prop point |
| Alt+Up / Alt+Down (structure tree) | Move the selected bone up / down in the index order |

**View and playback**

| Keys | Action |
|---|---|
| Ctrl+Shift+L / I / M | Show or hide the library / inspector / bottom panel |
| Ctrl+Shift+P | Play or pause from anywhere |
| Space (viewport, time bar, timeline) | Play or pause |
| Left / Right (viewport, time bar, timeline) | Step one frame |
| Shift+Left / Shift+Right (time bar) | Step ten frames |
| Home / End (viewport, time bar) | First / last frame |
| Alt+drag on the time bar | Scrub without snapping to frames |
| F1 | RFA & V3C format reference |

**Viewport**

| Keys or mouse | Action |
|---|---|
| Left-drag / middle-drag or Shift+left-drag / wheel | Orbit / pan / zoom towards the cursor |
| Click a joint / Ctrl+click | Select a bone / add or remove one |
| Click a collision sphere or prop point (mesh tabs, shown) | Select it; click the same spot again for the next thing under the cursor |
| Esc | Clear the bone selection (or cancel a gizmo drag) |
| F, or Ctrl+Shift+F | Frame the selection or everything |
| 1 / 3 / 7 | Front / side / top view |
| 5 | Perspective or orthographic |
| Q / E / W | Select / rotate / move tool (clip tabs: bones; mesh tabs: bone binds, spheres, prop points) |
| Drag a sphere's outline or grip (mesh tabs, move tool) | Change its radius |
| Ctrl while dragging the gizmo | Snap to 5° or 1 cm |
| Drag a library entry onto the viewport | Preview it |

**Timeline**

| Keys or mouse | Action |
|---|---|
| Click / Ctrl+click / Shift+click a key | Select / toggle / add |
| Drag on empty space | Box-select (Ctrl or Shift: add) |
| Drag a selected key (Alt: free) / Ctrl+drag | Move / copy the selected keys |
| Drag a ruler grip, or Alt+drag the first/last selected key | Scale the selection about the playhead |
| Double-click a key | Open it in the Key inspector |
| Click a mark in the "All keys" row | Select every key at that time |
| Drag a start/end triangle | Change the clip's start or end |
| Del or Backspace | Delete the selected keys |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste at the playhead |
| Ctrl+Shift+V | Paste mirrored |
| K | Key the selected bones at the playhead |
| Ctrl+A / Esc | Select every key / clear the selection or cancel a drag |
| Home | Fit the clip into view |
| Ctrl+Plus / Ctrl+Minus, Ctrl+wheel | Zoom |
| Shift+wheel or middle-drag | Pan in time |
| Up / Down | Previous / next bone |
| Click a row name (Ctrl, Shift) / its arrow | Select bones / expand a branch |
| Double-click a row name | Select every key of that bone |

**Clip tools and retarget**

| Keys | Action |
|---|---|
| Ctrl+Alt+T | Trim / crop to range |
| Ctrl+Alt+H | Shift in time |
| Ctrl+Alt+R | Retime |
| Ctrl+Alt+V | Reverse |
| Ctrl+Alt+B | Resample (bake) |
| Ctrl+Alt+D | Reduce keys |
| Ctrl+Alt+F | Offset the selected bones |
| Ctrl+Alt+G | Compare with another clip |
| Ctrl+R | Retarget the active clip |
| Alt+Down on a bone map row's source box | Change that bone's source |

**Library, table usage and problems**

| Keys or mouse | Action |
|---|---|
| Double-click or Enter | Preview on the tab in front (see [Animations library](#animations-library)) |
| Ctrl+double-click, Ctrl+Enter or middle-click | Open in a new tab |
| Esc (filter box) | Clear the filter |
| Double-click or Enter (Table usage row) | Preview the clip on a mesh row / open a clip row |
| Ctrl+C (Table usage row) | Copy the table line (on a list row, the whole list) |
| Click a row, or Up / Down / Enter (Problems) | Select what the problem points at |

## Effects (VFX)

### Effect format primer

- **Sections.** An effect is a list of sections: meshes, particle systems, dummies (named points other things attach
  to), lights, spacewarps (forces that act on particles) and materials. Objects may name a parent; the hierarchy is
  for authoring and is what the outliner shows, but the game does not combine parent and child transforms: every
  stored transform is absolute.
- **Time.** Effects run at 15 frames per second. Times are stored in ticks, 320 per frame and 4800 per second. The
  header's end frame should match the longest object.
- **Mesh animation.** A mesh can be static, keyframed (translation, rotation and scale keys), per-frame transforms,
  or morph (a full vertex set per frame). Meshes appear at their start frame and disappear after their end frame.
- **Loop and one-shot.** The file does not say how it plays; the game code that spawns it decides. Effects spawned by
  explosions and impacts play once; glows and other attached effects usually loop. A looping effect restarts from the
  first frame; a one-shot effect plays once and its meshes disappear; some callers hold the last frame.
- **Format versions.** Stock files use several versions of the format. Cairn opens, plays and saves all of them
  unchanged; editing needs the current version, and new effects are written in it.

### Effect menu and outliner

**Effect menu.**

- **Add** › **Primitive…** (plane, facing quad, facing rod, disc, ring, cylinder, cone, sphere or box), **Particle
  system**, **Dummy**, **Light**, **Spacewarp**, **Material**.
- **New from template** › Additive flash, Scrolling beam, Ring shockwave, Particle fountain.
- **Loop**, **One-shot**, **Hold last frame** (preview playback mode).
- **Convert to current format**.
- **Edit** › **Rename** (**F2**), **Duplicate** (**Ctrl+D**), **Delete** (**Del**), **Move up** (**Alt+Up**), **Move
  down** (**Alt+Down**), **Select all of this type**, **Isolate in preview**, **Show all in preview**.
- **Vertex mode** › **Vertex mode** (**Tab**), **Select all** (**Ctrl+A**), **Invert selection** (**Ctrl+I**),
  **Select connected** (**L**), **Grow selection** (**Ctrl+Plus**), **Shrink selection** (**Ctrl+Minus**), **Delete
  vertices** (**Ctrl+Delete**), **Merge to centre** (**M**).
- **Auto-key**, **Local axes**, **Pivot mode** (toggles).
- **Keys** › **Insert key (all tracks)** (**K**), **Insert translation key**, **Insert rotation key**, **Insert scale
  key**, **Delete keys at playhead**.

**File menu.** **File › Import › Effect** › **glTF as effect…**, **Geometry from mesh (V3M/V3C)…**, **Objects from
another effect…**; **File › Export › Effect as glTF…**.

**Outliner.** **Scene Root** with the objects by parent, then **Materials**. A check box shows or hides each object in
the preview. Right-click: **Rename**, **Duplicate**, **Delete**, **Parent**, **Move up**, **Move down**, **Select all of
this type**, **Isolate in preview**, **Show all in preview**, **Add**.

**Effects library** (left pane). Every `.vfx` in the game's packfiles and your search folders, with its source, the
tables that use it, frame count, object count and version. The filter matches names, sources and tables.

### Effect inspectors

| Tab | Fields, in order |
|---|---|
| **Effect** | Format version, End frame, Length, Playback mode; Contents: Meshes, Particle systems, Dummies / lights, Space warps, Materials, Sections. |
| **Object** (mesh) | Name, Parent; Flags; Timing (Frame rate, Start time, Frame count, Lengthen by, Animation with **Static** / **Per-frame** / **Keyframed** / **Morph** conversions); Facing (at the playhead): Width, Height, Apply size to all frames, Rod up (facing quads and rods); Material slots; Geometry (Vertices / faces, Bounds); Pivot. |
| **Object** (particle system) | Name, Parent; Emission (Start, Particle count, Lifetime, Lifetime variation, Emitter); Particle flags; Tail distance, Shrink and Fade at birth and death; Space warps; per-frame values (width, height, drop size, speed, speed variation, birth rate). |
| **Object** (dummy, light, spacewarp) | Name, Parent; Animation (Frame count); At the playhead (Position and the type's values). |
| **Material** | Materials (Add, Duplicate, Select users, Remove with reassignment); Appearance: Type, Additive, Texture 1 (and 2) with start frame, rate and playback, Fps, Colour, Specular, Gloss, Reflection; Opacity, Self-illumination (and Mix) tracks. |
| **Keys** | The keys selected in the timeline: time (frames or ticks) and value. |
| **Vertices** | In vertex mode: selection, positions, faces, UV offset / scale / scroll and rotation, Make morph mesh. |

### Effect viewport, timeline and shortcuts

**Viewport.** Orbit camera and grid. The bar above it has **Auto-key**, **Local axes**, **Pivot mode** and **Vertex
mode**. Click to select objects (or vertices in vertex mode); the gizmo moves (**W**), rotates (**E**) or scales (**R**)
the selection, **Q** hides it. With auto-key on, a gizmo drag on a keyframed mesh adds a key at the current frame.
Meshes are drawn with alpha-blended, additive and fullbright materials, animated textures, facing quads and rods
turned to the camera, and particles as sprites.

**Transport.** First / previous key / previous frame / play / next frame / next key / last, the loop toggle, the frame
readout and playback speed.

**Timeline** (bottom pane). One row per object with a bar for its active range; expand a row for its tracks (frame
transforms or translation, rotation and scale keys, and the opacity and self-illumination of its material). The
toolbar has Move / Rotate / Scale, **Key**, delete selected keys, expand all, **Auto-key** and **Local axes**; the
right-click menu inserts and deletes keys at the playhead. **Shift** at the start of a drag edits every frame of
per-frame data; **Alt** drags keys between frames.

**Shortcuts.** **W** / **E** / **R** / **Q** tools; **K** insert key; **,** and **.** previous and next frame;
**Shift+,** and **Shift+.** previous and next key; **Home** / **End** first and last frame; **F2** rename; **Ctrl+D**
duplicate; **Del** delete; **Alt+Up** / **Alt+Down** reorder; **Tab** vertex mode, **Esc** leaves it; **Shift+F**
vertex scope (current frame or all frames).

**Status bar.** Format version, frame count, object count, playback mode and the auto-key / axes state.

### Effect settings

**Tools › Settings… › Effects.** Editing: **Auto-key**, **Local axes** and **Pivot mode** (the same switches as the menu
and bars). Layout: reset the object list and inspector widths.

### Effect glTF exchange

- A current-format effect exported by Cairn loads in REDUX, and importing that file again gives back the identical
  `.vfx`.
- Files written by REDUX import identically.
- `.gltf` + `.bin` is the form REDUX reads; `.glb` is also offered.
- A plain glTF from Blender imports as new objects: node animation is sampled at 15 frames per second, morph targets
  become morph frames, and materials are matched by name, with their textures renamed to `.tga`. Skins, cameras and
  PBR material values are not carried.

### Effect preview accuracy

Exact: geometry, morph frames, keyframe and per-frame motion, visibility windows, facing quads and rods, material
tracks and animated textures, and particle births, lifetimes, sizes, fades and gravity (particles are simulated with
a fixed seed, so they look the same every time but not exactly like any one run in the game).

Approximate:

- Forces from spacewarps: particles under a spacewarp move only roughly as in the game.
- Streak (drop) particles are drawn as round sprites, not stretched along their motion.
- All particles are drawn fullbright, and a particle system sorts as one against meshes.
- Particle speeds: the game's speed unit is not fully confirmed, so travel distances may differ.
- Blending approximates the game's renderer; check additive and alpha materials in the game.

### Effect problem checks

The **Problems** tab reports: format versions the game cannot load; faces that refer to missing vertex records (the
game crashes on these); indices out of range; textures that cannot be found; parents that are not in the file (shown
as information, since they may be bones or props outside the effect); timing that does not add up, including an end
frame that does not match the longest object; duplicate or empty names; particle systems naming spacewarps that do not
exist; unused materials. Double-click an entry (or press **Enter**) to select what it refers to; **Fix** applies a
quick fix as one undo step.

## Packfiles (VPP)

### Packfile menus and toolbars

**Packfile menu.** **Add files…**, **Add folder…**; **Extract selected…** (**Ctrl+E**), **Extract all…**, **Extract as
PNG…** (the selected images, and every texture of selected `.peg` entries, as PNG files; animations as numbered
frames); **Remove** (**Del**), **Rename** (**F2**), **Replace…**, **Select all of type** (every entry with the selected
entry's extension); **Sort by** › **Name**, **Type**, **Size** or **Original order** (reorders the entries inside the
packfile, one undo step); **Convert images to DDS…** (**Ctrl+Shift+D**); **Convert to .tga…** (selected `.peg`
entries); **Convert meshes…**; **Convert sounds…**; **Validate** (checks the packfile now, including whether added
files changed on disk).

**Right-click menu.** **Open** (**Enter**), **Open with…**, **Open in Cairn**; **Extract to…** (**Ctrl+E**), **Extract
here** (once the packfile has been saved somewhere), **Copy name** (the selected names as text); **Rename** (**F2**),
**Replace…**, **Convert to DDS…**, **Convert to .tga…** (when the selection holds a `.peg`), **Convert meshes…**,
**Convert sounds…**, **Remove** (**Del**); **Select all of this type**. **Open in Cairn** is offered for the types Cairn
opens.

**Toolbars.** Above the list: **Add files…**, **Add folder…**, **Extract…** (the selection, or everything when nothing
is selected), **Remove**, **Rename**, the filter box and the type filter. With a packfile in front, the main toolbar
adds add-files and extract buttons and **Autoplay sounds**.

**File types panel** (left pane). Every group and type in the packfile with its count and a check box, **Show all
types**, and how many entries are shown. A **Problems** group appears while the packfile has a problem it can filter
by: **Names longer than 31 characters (n)** lists the textures, sounds and fonts whose names the game cuts off. It
disappears (and is unticked) once no entry has the problem.

**Keyboard** (file list focused). **Enter** opens the selection in its Windows programs, **Ctrl+E** extracts,
**Ctrl+C** copies as files, **Del** removes, **F2** renames, **Ctrl+A** selects every entry shown; typing letters
jumps to the first entry whose name starts with them. Double-click opens an entry.

**Filter box.** Matches part of a name, ignoring case; `*` and `?` work as wildcards. **Esc** clears it. While a filter
is on, the toolbar and the File types panel say how many entries are shown ("120 of 2,568 shown").

### Packfile file list columns and Info

| Column | Shows |
|---|---|
| **Name** | The entry name. |
| **Type** | A plain description, such as "Targa image" or "Character mesh". PS2 types: **PS2 texture pack**, **PS2 static mesh**, **PS2 character mesh**, **PS2 sound effect**, **PS2 music**. |
| **Size** | The size; the tooltip has the exact byte count. |
| **State** | Empty for an entry as saved; **added**, **replaced** or **renamed** for a pending change. The tooltip says where new data comes from, or the old name. |
| **Info** | One line read from the file's header, filled in shortly after the packfile opens. The tooltip shows the whole line; sorting by Info compares numbers by value. Pending entries are read from their source files. |

| Type | Info, for example |
|---|---|
| Level (`.rfl`) | `Regicide by --ReWiReD-- (Tuesday, August 06, 2002 at 01:30:23)`: name, author and save time in your time zone |
| Targa image (`.tga`) | `256x256, 24-bit, RLE compressed` (or `uncompressed`; `8-bit paletted`, `8-bit greyscale`) |
| DDS image (`.dds`) | `512x512, DXT1, 10 mipmaps` (`cube map` when it is one) |
| Volition bitmap (`.vbm`) | `128x128, 8 frames, 15 fps`; a single frame shows its pixel format |
| PNG, JPEG, Photoshop | `256x256, 24-bit`; `.psd` adds its channels |
| Sound (`.wav`, `.aif`, `.ogg`, `.mp3`) | `22,050 Hz, 16-bit mono, 1.2 s`; `44,100 Hz stereo, 3:05` (MP3 adds its bitrate) |
| PS2 sound (`.vse`, `.vmu`) | `22,050 Hz mono, 1.4 s, PS ADPCM`, with `, loops` for a looping sound |
| Mesh (`.v3m`, `.v3c`) | `3 submeshes, 1,234 triangles`; `30 bones, 560 triangles, 3 LODs` (triangles of the most detailed LOD) |
| Animation clip (`.rfa`) | `79 frames, 2.6 s, 45 bones` (`morph` when it carries vertex animation) |
| Effect (`.vfx`) | `16 frames, 1.1 s, 7 objects` |
| Animated texture (`.atx`) | `8 frames, 100 ms each, loop` |
| Table (`.tbl`) | `weapons table, 30 entries`; `level info, 3 settings`; otherwise its kind and line count |
| Text, font, editor group | `67 lines`; `69 glyphs, 49 px high`; `3 groups` |
| PS2 texture pack (`.peg`) | `PEG v6: 28 textures (6 animated, 3 MPEG-2 compressed)` |
| A file converted from a `.peg` | `64x64, from PS2 8-bit indexed, 32-bit palette, 3 mips (level 0 kept)`; a frame adds `frame 2 of 12`; a background `640x448, from PS2 MPEG-2 (6 tiles)` (adds `black below 25 transparent` when black was made transparent) |

Types Cairn does not read leave the cell empty. Data that cannot be read as its type shows a dim *unreadable* (the
tooltip says why); an entry whose content is another type than its name says names it first (`DDS data: 512x512,
DXT1, 10 mipmaps` for a `.tga` that holds a DDS texture).

### Packfile preview and details panes

| Type | Preview |
|---|---|
| `.tga`, `.dds`, `.png`, `.jpg`, `.vbm` | Image on a checkerboard: **Fit**, **100%**, wheel zoom, **Alpha**; mip level for `.dds`; play, pause and frame steps for an animated `.vbm`. |
| `.wav`, `.ogg`, `.aif`, `.mp3`, `.vse`, `.vmu` | **Play**, **Stop**, position slider, time, clickable waveform (none for `.mp3`). Compressed sounds (Ogg Vorbis, ADPCM `.wav`, the stock `.aif` files, PS ADPCM) play too. |
| `.vf` | A summary line, the sample text and every character drawn with the font. |
| `.tbl` | The table, read-only, highlighted and foldable. |
| `.txt`, `.log`, `.gltf` and other text | Numbered lines, the detected encoding, **Wrap** (on by default), **Find** (**Ctrl+F**, **F3** / **Shift+F3**), **Copy** (**Ctrl+C**: the selected lines, or everything). |
| `.v3m`, `.v3c`, `.v3d`, `.vcm`, `.rfm`, `.rfc` | The mesh with its textures, display toggles and orbit camera (exporter and PS2 meshes as they convert). |
| `.rfa` | The clip on a character mesh with the same bone count (from the packfile first, then the game). |
| `.vfx` | The effect playing. |
| `.atx` | The animated texture playing, with its alpha mask. |
| `.peg` | The pack's textures (size, format, mip levels, frames) above the selected one; an animated texture plays; MPEG-2 backgrounds show with black as transparent when that setting is on, or are marked as not converted when decoding is off. |
| `.rfl` | No preview: one line pointing to the details. |
| anything else | The first 4 KB in hex. |

Mesh, clip, effect, animated texture, PEG, table, font, bitmap and sound (all but `.mp3`) previews have **Open in
Cairn**. Entries over 48 MB wait for **Preview anyway**, because previewing reads the whole file into memory.

**Details pane.** **Name**, **Original name** (after a rename), **Type**, **Size**, **Game** (**Loaded by the game** or
**Not loaded by the game**), **Offset** in the packfile or **Source** (the file a pending entry comes from), **State**
and the entry's **Problems**; then the facts for its type: image size, format, mipmaps and alpha; sound codec, rate,
channels and duration; a mesh's submeshes, LODs, bones and materials; a clip's bones, frames and duration; an effect's
version, frames, objects and materials; a table's lines and encoding; a level's groups (Level, Properties,
Statistics, Alpine Faction, References, Preloads, Notes, Sections). A banner warns when an entry's content does not
match its name (for example a `.wav` that is really a web page). **Copy all** copies everything as text.

**Level references** say **in this packfile**, **in the game data**, **missing**, or, without a game folder or search
folders, **not checked**.

### Packfile bars and status bar

- **The packfile was changed on disk by another program**: **Reload** reads it again (pending changes are lost);
  **Keep mine** hides the bar, but saving stays blocked until you reload.
- **The packfile was deleted or renamed on disk**: use **File › Save As…**.
- **A work copy was changed outside the packfile**: **Update packfile** or **Ignore** (the bar returns if the copy
  changes again).
- **This packfile is from the PlayStation 2 version**: says what can be converted; **Dismiss** hides it. A `.peg`
  opened on its own says what saving writes instead.

**Status bar.** Entries, the size when saved (exact bytes in the tooltip), pending changes, the problem count (click
to open the problems) and, while saving or extracting, the progress (click to cancel).

### Packfile saving and extracting

**Saving.** Cairn checks the packfile (errors stop the save, warnings do not), writes the whole new packfile to a
temporary file in the target's folder (copying every entry from the original packfile, your added files or memory),
flushes it to disk, reads its entry list back to check it, and only then swaps it in for the old file. A cancelled or
failed save removes the temporary file. The packfile tab waits while it saves; the rest of Cairn stays usable. The undo
history is cleared after a save, because older steps refer to data in the replaced file.

**Extracting.** When files of the same name exist, Cairn asks once: **Overwrite**, **Skip existing** or **Cancel**.
Characters Windows does not allow in file names are written as `_`.

**Name clashes when adding.** **Replace**, **Keep both** (a free name such as `name (2).tga`), **Skip**; with several
clashes **Replace all**, **Keep both for all**, **Skip all** or **Decide for each…**; **Cancel** adds nothing. Names
are compared ignoring case, as the game does. An added file that changes or disappears before saving is reported, and
saving waits until you add it again or remove the entry.

### Packfile problem codes

**Errors** stop saving:

| Code | Meaning |
|---|---|
| VPP001 | An empty name. |
| VPP002 | A name longer than 59 characters. |
| VPP003 | Characters a packfile cannot store. |
| VPP004 | A `\` or `/` in a name. |
| VPP005 | The same name twice, also when only the case differs (identical copies, as in the stock `ui.vpp`, are only a note). |
| VPP006 | An entry over 1.5 GB. |
| VPP007 | A packfile too large for the format. |
| VPP008 | An entry that would start beyond 2 GB. |
| VPP010 | An added file that changed or vanished. |
| VPP011 | An entry whose data is missing from a damaged packfile. |
| VPP012 | The packfile changed on disk. |
| VPP016 | More entries than Alpine Faction accepts. |

**Warnings** do not stop saving:

| Code | Meaning |
|---|---|
| VPP009 | A type the game does not load (including PlayStation 2 types). |
| VPP013 | An empty packfile. |
| VPP014 | A name Windows cannot use as a file name, so it cannot be extracted as it is. |
| VPP015 | A name without an extension. |
| VPP017 | A 0-byte entry. |
| VPP020 | A name with letters outside plain ASCII. |
| VPP021 | An animation name with extra dots. |
| VPP023 | An upper-case `.OGG` extension. |
| VPP024 | A packfile file name longer than 31 characters. |
| VPP027 | A texture, sound or font name longer than 31 characters: the game cuts it off, so the file is not found. |

**Notes:**

| Code | Meaning |
|---|---|
| VPP025 | A full path longer than 127 characters. |
| VPP026 | A header that records a different size than the file has (saving rewrites it). |

### Packfile settings

**Tools › Settings… › Packfiles**:

| Setting | What it does |
|---|---|
| **Keep a .bak copy when saving over a packfile** | Keeps the previous version as `name.vpp.bak`. |
| **Ask before removing entries** | Asks before **Remove**. |
| **Folder for work copies** | Empty means `%LOCALAPPDATA%\Cairn\work`; each open packfile gets its own sub-folder, deleted when it closes. |
| **Decode PS2 MPEG-2 backgrounds** | On by default. Off lists the backgrounds as "MPEG-2 compressed background — not converted" and leaves them out of previews, conversions and PNG extracts. |
| **Treat black as transparent** | Off by default; with **Threshold** (0 to 64, 25 by default) and **Soft edge**. See [PEG conversion rules](#peg-conversion-rules). |

**Help › Packfiles › Packfiles: format and limits** summarises the format and the limits below.

### Packfile limits

These are the game's rules; Cairn checks them so you find out before the game does.

- **Entry names**: at most 59 characters, no `\` or `/`, and only characters of the Western European (Latin-1) set.
  Textures, sounds and fonts are cut off after 31 characters when the game looks them up.
- **The packfile's own file name**: at most 31 characters including `.vpp`, and at most 127 characters for the full
  path. The game does not load a packfile with a longer name.
- **Number of entries**: up to 1,048,576 in one packfile; no limit on the number of packfiles.
- **Size**: every entry must start within the first 2 GB, so keep packfiles under 2 GB. The game also refuses a
  packfile that holds a single file larger than 1.5 GB.
- **How the game finds a file**: by bare name, ignoring case (A to Z only). When two loaded packfiles hold the same
  name, the one loaded last wins, and inside one packfile the later entry wins, within Alpine Faction's rules on which
  folders may replace the game's own files.
- **The `.rfa` dot rule**: the game finds an animation by cutting its name at the first dot and adding `.rfa`. A table
  that names `walk.mvf` loads `walk.rfa`, and an entry named `walk.v2.rfa` can never be loaded as an animation.
- **Types**: textures may be `.tga`, `.vbm`, `.dds`, `.png`, `.jpg` or `.atx` (a `.dds` is found in place of a
  requested `.tga` of the same name); sounds `.wav` or `.ogg`, and a sound is treated as Ogg Vorbis only when its name
  ends in lower-case `.ogg`. `.aif`, `.mp3`, `.mvf`, `.rfg`, `.psd`, `.gltf`, `.txt` and `.log` appear in some
  packfiles but the game never loads them, nor the PS2 version's `.peg`, `.rfm`, `.rfc`, `.vse` and `.vmu`.

### DDS conversion options

**Packfile › Convert images to DDS…** (**Ctrl+Shift+D**) converts the selected `.tga`, `.png` and `.jpg` entries, or
all of them when none is selected; other entries are skipped and listed. The window shows each image's size and alpha,
a before-and-after preview of the selected image (with zoom, mip level and **Alpha only**) and a size summary. The
choices are remembered.

| Option | Choices |
|---|---|
| **Format** | **Auto** (DXT1, DXT1 with 1-bit alpha or DXT5 from each image's alpha), **DXT1 (opaque)**, **DXT1 (1-bit alpha)**, **DXT3 (4-bit alpha)**, **DXT5 (smooth alpha)**, **A8R8G8B8 (32-bit)**, **R5G6B5 (16-bit)**, **A1R5G5B5 (16-bit)**, **A4R4G4B4 (16-bit)**. Only formats the game loads are offered. |
| **Mipmaps** | **Full chain**, **None**, or **At most N levels**. |
| **Mip filter** | **Box**, **Triangle**, **Lanczos (sharper)**; also used to resize. |
| **Quality** | **Fast**, **Balanced**, **Best (slow)** (block-compressed formats). |
| **Resize to a power of two** | **When the format needs it** (DXT needs sides that are multiples of 4), **Whenever a side is not a power of two**, **Never**. |
| **Rounding** | **Nearest**, **Next larger**, **Next smaller** power of two. |
| **Premultiply colour by alpha** | Off by default: the game expects straight alpha. Only for textures drawn additively. |
| **Output** | **Replace the originals in the packfile**, **Keep the originals too (add the .dds files)**, or **Write to a folder**. |
| **When a .dds exists** | **Replace it** or **Skip that image**. |

Converting into the packfile is one undo step; save the packfile to keep it.

### PEG conversion rules

**Convert to .tga…** (selected `.peg` entries) converts the ticked textures and puts the results where the `.peg` was,
as one undo step. With **Keep the .peg entries in the packfile** off, each `.peg` is replaced by its files; on, the
files are added after it so you can convert more of it later. The dialog's **Treat black as transparent**,
**Threshold** and **Soft edge** start as set in **Settings › Packfiles** and apply to that conversion only. A `.peg`
opened on its own is converted with the settings when it opens (every texture, no dialog); after the first save a
summary lists the textures left out.

- **A texture with one frame** becomes `name.tga`: 32-bit with alpha, full size (smaller mip levels are left out). A
  texture named `.vbm` becomes a `.tga` too; the game looks for a `.vbm` and then a `.tga` of the same name, so nothing
  that names it needs changing.
- **An animated texture** (most are named `.vbm`) becomes its frames, `name_00.tga`, `name_01.tga` and so on, plus
  `name.atx` listing them (Alpine Faction 1.4.0+). The game looks for an `.atx` before any other texture of that name.
  The PS2 files store no frame rate: Cairn uses the rate of the PC game's own `.vbm` of that name when the game folder
  has one, else 15 frames a second. Frame names are shortened to fit the game's 31 characters; when two long names
  shorten alike, the second animation's frames get a number (`explosion_big_fire_anim1_00.tga`) and its `.atx` lists
  them.
- **Colours** come out as the PS2 shows them: its alpha runs from 0 to 128 (128 is opaque) and becomes 0 to 255.
- **MPEG-2 compressed backgrounds** (full-screen menu pictures, the legal screen, the Extras pages, multiplayer
  previews, HUD portraits) are decoded into 24-bit `name.tga` files with no alpha, so the few UI strips and portraits
  come out as opaque rectangles. A background that cannot be decoded is left out and the summary says why. Only
  intra-coded MPEG-2 pictures (what every known PEG holds) are decoded.
- **Treat black as transparent** gives the backgrounds alpha the way the PS2 could when it decoded them: every pixel
  whose red, green and blue are all below the **Threshold** (0 to 64; 25 is the PS2 game's value) becomes transparent,
  and the backgrounds become 32-bit. **Soft edge** makes pixels up to twice the threshold half transparent. It applies
  to the preview, conversion and **Extract as PNG…**. The PS2 game only does this for a background whose PEG entry asks
  for it, which no known file does; a background that does ask is keyed at 25 even with the setting off.
- **The main menu's spinning planet** (150 numbered backgrounds, `plan-0001` to `plan-0150`) stays as 150 `.tga` files,
  and Cairn also writes `interface-bg-mm.atx` (named after the `.peg`; `plan.atx` when that name is taken) looping them
  at 30 frames a second, the rate the pictures declare; the PS2's real rate is not known. Numbered backgrounds count
  as frames only when there are at least 8 of one size, numbered one after another with at least 3 digits, so pages
  such as `extras01` to `extras26` stay stills. Nothing in the PC game asks for these frames.
- **Name clashes.** Only one texture of a name can stay. When several packs converted together hold a texture of the
  same name, or the packfile already has an entry of that name, identical copies are converted once; copies that
  differ keep the one with the most pixels (the one already in the packfile, or the first, when they are the same
  size). A larger texture replaces the entry already there (for an animation, its `.atx` and the frames it listed; the
  new frames get a number, such as `boom1_00.tga`). An old frame that another `.atx` still names (as a frame or an
  alpha mask) stays, and so does every old frame when another `.atx` cannot be read. An entry whose size cannot be read
  is kept, and animation frames never take a name an entry already has. The summary lists each case, for example
  "envirohand.tga: 2 different versions, kept 128×128 from a.peg, skipped 64×64 from b.peg".
- **PEG versions.** Version 6 (and version 4, from early PS2 builds) are read; any other variant is reported as an
  "unsupported PEG variant".

## Tables (TBL)

### Tables Cairn knows

The stock tables `weapons.tbl`, `entity.tbl`, `items.tbl`, `clutter.tbl`, `ammo.tbl`, `pc_multi.tbl`, `fpgun.tbl`,
`effects.tbl`, `emitters.tbl`, `explosion.tbl`, `vclip.tbl`, `sounds.tbl`, `foley.tbl`, `materials.tbl`, `hud.tbl`,
`hud_personas.tbl`, `personas.tbl`, `game.tbl`, `movemodes.tbl`, `ponr.tbl`, `credits.tbl`, `endgame.tbl`,
`strings.tbl` and `events.tbl`; a level's `<level>_text.tbl`; and Alpine Faction's own tables `<level>_info.tbl`,
`af_game.tbl`, `af_ui.tbl`, `af_client*.tbl` and `af_level_quirks.tbl`. Any other name opens as **Unknown table**,
checked for syntax only.

A new table (**File › New › Table**) opens as `Untitled.tbl` with a comment at the top, in the game's usual encoding
and line endings (ANSI, CRLF).

### Table menus and shortcuts

**Table menu.** **Go to Definition** (**F12**), **Find Usages** (**Shift+F12**), **Go to Entry…** (**Ctrl+Shift+O**),
**Compare with Stock**.

**Edit menu additions.** **Find…** (**Ctrl+F**), **Replace…** (**Ctrl+H**), **Go to Line…** (**Ctrl+G**), **Toggle
Comment** (**Ctrl+/**, adds or removes `//` on the selected lines), **Quick Fixes…** (**Ctrl+.**), **Complete**
(**Ctrl+Space**). Right-click in the editor for **Go to definition**, **Find usages**, **Cut**, **Copy** and **Paste**.

| Keys | Action |
|---|---|
| **Ctrl+Space** | Complete fields, values, names and files |
| **Ctrl+.** | Quick fixes for the problem at the caret |
| **F8** / **Shift+F8** | Next / previous problem |
| **F12**, **Ctrl+click** | Go to definition |
| **Shift+F12** | Find usages |
| **Ctrl+Shift+O** | Go to entry |
| **Ctrl+G** | Go to line |
| **Ctrl+F** / **Ctrl+H** | Find / replace bar |
| **F3** / **Shift+F3** | Find next / previous |
| **Ctrl+/** | Toggle `//` comment on the selected lines |
| **Ctrl+Z**, **Ctrl+Y** | Undo, redo (a quick fix or **Replace all** is one step) |

**Find bar.** **Enter** finds the next match, **Shift+Enter** the previous, **Esc** closes; **Match case**;
**Replace** replaces and finds the next; **Replace all** is one undo step.

**Highlighting.** Comments (`//`, `/* */`) italic; section headers bold; field names, strings, numbers, `true` /
`false`, keywords such as `XSTR` and brackets each coloured; file names underlined; names of entries in other tables
dotted-underlined; problems wavy-underlined in the colour of their kind.

### Table panels, status bar and settings

**Outline** (left pane). Sections with their entry counts, entries with error and warning marks, **Filter entries**,
the caret's entry highlighted; click or **Enter** jumps.

**Reference preview** (right). The file or name at the caret: preview above (the packfile module's previews), details
below (the name asked for, where Cairn found it, size and type, which tables use it), with **Go to definition** and
**Open in Cairn**. Its split is remembered. A table opened from a packfile looks up the files it names in that
packfile first.

**Problems** (bottom pane). One row per problem: kind, **Ln** and **Col**, message, a button per quick fix.

**Usages** (bottom pane). Columns **Table**, **Entry**, **Line**, **Field**, **Text**, **Location**. A table open in a
tab is listed once, as the tab.

**Compare** (bottom pane). Columns **Change**, **Section**, **Entry**, **Field**, **Stock**, **This table**; **Only
changes**; **Compare again**. A table the game does not have says it looks like a custom table; without a game folder
there is nothing to compare with.

**Status bar.** The table's title (**Weapons**, **Entities**…, or **Unknown table**; the tooltip describes it), the
entry count, error and warning counts (click to open **Problems**), **Ln** and **Col**, the encoding (**ANSI
(Windows-1252)**, **Latin-1**, **UTF-8**, **UTF-8 with a byte-order mark**) and the line endings (**CRLF**, **LF**,
**CR**, with **(mixed)** when the file mixes them; they stay mixed). Both are kept on save.

**Bars above the text.** **The file was changed outside Cairn.**: **Reload** (undoable) or **Keep mine**. **The file
was deleted or renamed on disk. Save writes it again.**: **Dismiss**.

**Settings › Tables.** PROBLEMS: **Report errors**, **Report warnings**, **Report information**. EDITOR: **Open
completion while typing**, **Wrap long lines**.

**Help › Tables › Table syntax** summarises how tables are written and read.

### How the game reads tables

These are the game's rules; Cairn checks them so you find out before the game does.

- **Fields are read in a fixed order.** For each table the game asks for its sections and fields one after another,
  always in the same order, and each field only at its own place. A field in the wrong place, misspelt or unknown is
  not skipped: the game stops loading with an error box ("Expected … but found …") naming the table and the line.
  Optional fields may be left out, but those you write must keep the order. Field names ignore case, but every space
  counts: `$Fire  Wait:` with two spaces is a different, unknown field.
- **Some fields are read only when another allows it.** The lines under `$Glow:` are read only when it is `true`, and
  `$Weapon Icon:` only after `$Weapon Type:`. Written when they are not read, they stop the game like any misplaced
  field.
- **Where the game skips.** `entity.tbl` and `clutter.tbl` search for the start of the next entry, so text between
  entries is ignored there. Most other tables need their `#End`: without it the game stops. Anything after the last
  `#End` is never read.
- **Comments.** `//` runs to the end of the line, and `/* */` encloses a block. The game ends a `//` comment only at a
  carriage return, so in a file with Unix (LF) line endings a `//` comment swallows everything after it. Keep tables
  in CRLF.
- **No byte-order mark.** The game reads tables as plain bytes; a UTF-8 byte-order mark sits in front of the first
  header and the game stops. Save tables as ANSI (or UTF-8 without the mark). The game reads every byte as one
  character.
- **Strings** are in double quotes, on one line, with no escapes, at most 254 bytes (some fields allow less). A bare
  word where a string is expected stops the game.
- **Numbers** are plain decimals: `-1`, `0.5`, `.5`. No exponents (`1e3`) and no leading `+`: the game reads up to
  that point and stops on the rest. Booleans are `true` or `false` (also `yes` / `no`), never `1` or `0`.
- **Lists and flags** go in brackets with quoted items separated by spaces, not commas: `("alt_fire" "underwater")`.
  An unknown flag name stops the game; an unknown weapon, sound or effect name does not, but the game then has
  nothing to use.
- **One file, by name.** The game finds a table by its bare file name and reads the whole file: there is no merging.
  A modded `weapons.tbl` replaces the stock one entirely.
- **Alpine Faction's own tables are line based.** `<level>_info.tbl` (per-level options, read when that level loads)
  and the `af_*.tbl` files are read line by line between `#Start` and `#End`. Each line is `$Option: value`; names must
  match exactly, including case; order is free and a later line wins; quotes are optional; `//` is a comment only at
  the start of a line. A value Alpine Faction cannot read is ignored, not fatal.
- **Level text** (`<level>_text.tbl`) holds a level's voice lines and subtitles, numbered 0 to 63, with no section
  headers. `events.tbl` is read by the level editor, never by the game.

### Where the game accepts a table

- A mod's packfiles (`mods\<name>`, started with `-mod <name>`) may replace any table.
- Packfiles in `client_mods` may replace only `strings.tbl`, `hud.tbl`, `hud_personas.tbl`, `personas.tbl`,
  `credits.tbl`, `endgame.tbl`, `ponr.tbl` and HUD message files (`*_text.tbl`). Level packfiles in `user_maps` may
  replace the same tables only when the player turns on **Allow clientside mods in legacy directories** in the
  Alpine Faction launcher. The other tables need a mod.
- A table with a name the game does not already have (such as a level's `<level>_info.tbl`) is not a replacement and
  loads from any of these folders.
- With a language other than English, Alpine Faction reads `localized_credits.tbl`, `localized_endgame.tbl` and
  `localized_strings.tbl` instead of the plain names.

## Fonts (VF)

### Font toolbar and menu

| Control | What it does |
|---|---|
| **Glyphs** | Zoom of the glyph grid (1× to 6×). |
| **Backdrop** | What glyphs are drawn on: **Dark** (default: the stock fonts are white), **Checker** or **Light**. |
| **Sample** | The text drawn below; characters are taken in the game's code page (Windows-1252). |
| **All characters** | Draws every character of the font, 16 to a line, instead of the sample. |
| **Zoom** | Zoom of the sample (1× to 8×). |

The **Font** menu and the **Replace Glyph…**, **Export Sheet…** and **Import Sheet…** toolbar buttons appear while a
font is in front.

| Command | Shortcut | What it does |
|---|---|---|
| **Replace Glyph from Image…** | **Ctrl+R** | Replaces the selected glyph with a picture file. |
| **Paste Glyph Image** | **Ctrl+Shift+V** | The same with the picture on the clipboard. |
| **Add or Remove Characters…** | | Changes the first and last character. |
| **Change Height…** | | Adds or cuts rows of every glyph. |
| **Pixel Format** | | Converts to 4-bit monochrome, 8-bit indexed or RGBA 4444. |
| **Export Image Sheet…** | **Ctrl+Shift+E** | Writes the PNG and JSON sheet. |
| **Import Image Sheet…** | **Ctrl+Shift+I** | Reads an edited sheet back. |

**Pixel format conversions.** Monochrome to indexed or RGBA 4444 looks the same in the game (an indexed font gets a
white palette like the stock fonts); colours to monochrome keep only the transparency; a colour font with more than
256 colours loses some when it becomes indexed. A version 0 font becomes version 1 when it stops being monochrome.

**Replace Glyph from Image.** **Coverage from** applies to monochrome fonts and indexed fonts whose palette is white
with transparency (like the stock ones). **Threshold** 0 keeps soft edges; 1 to 255 makes each pixel solid or clear.
RGBA 4444 rounds each channel to 4 bits; an indexed font takes the nearest palette colour.

**Export Image Sheet.** When the `.json` (or the `.png`) is already there, Cairn asks before replacing it, since
metrics you edited in it by hand would be lost.

### Font facts and status bar

**Inspector facts.** **Version** (0: the older header, monochrome only; 1), **Format**, **Height**, **Glyphs**,
**Characters** (first and last code), **Default spacing** (the gap for characters the font lacks), **Widest glyph**,
**Kerning pairs**, **Pixel data** and **Texture**: the size of the texture the game builds for the font when it loads
it, and how much of it the glyphs use. Indexed fonts also show their **Palette**, 256 colours in a 16 × 16 grid (hover
a colour for its number and value; unused colours have a thin border).

**Glyph details.** **Character**, **Code**, **Width**, **Spacing**, **Pixel offset**, **Kerning index**, **User data**,
and every **Kerning** pair the glyph takes part in.

**Status bar.** Format version and pixel format, glyph count, height, the selected character, and the number of
errors and warnings (click to open **Problems**).

### Font pixel formats

| Format | Stored per pixel | How the game shows it |
|---|---|---|
| 4-bit monochrome | 1 byte, coverage 0 (clear) to 14 (solid) | White, more or less see-through. Values above 14 count as 14. |
| 8-bit indexed | 1 byte, a palette index | The palette colour (`0xAARRGGBB`), keeping the low hex digit of each channel. |
| RGBA 4444 | 2 bytes, 4 bits each of alpha, red, green, blue | As stored. |

The game turns every font into a texture with 4 bits per channel; Cairn shows the colours that texture has.

### How the game spaces text

Each character is drawn at the pen position with its own width, then the pen moves right by the character's
**spacing**. A **kerning pair** adds an offset (usually negative) when one particular character follows another. A
character the font does not have moves the pen by the default spacing and draws nothing; a line feed starts a new
line one font height lower. The game finds a kerning pair only when the pairs are sorted by first, then second
character, and the first character's kerning index points at its first pair. It never applies a pair that involves
glyph number 128 or later.

### Font image sheet format

The PNG is a grid of 16 cells per row with a 1-pixel line around every cell. Every cell is as wide as the widest glyph
plus the extra width, and as high as the font; cell *n* (glyph *n*, counting from 0) starts at
x = 1 + (n mod 16) × (cell width + 1), y = 1 + (n div 16) × (cell height + 1). Each glyph is drawn at the top left of
its cell, its width × the font height; the rest of the cell and the lines are the guide colour (opaque magenta) or
clear.

| Format | Pixel colour |
|---|---|
| 4-bit monochrome | Grey on black, coverage *v* as grey *v* × 255 / 14 (or white with that transparency). |
| 8-bit indexed | The palette colour as stored; a palette of white with transparency (the stock fonts') is written like monochrome. |
| RGBA 4444 | Each 4-bit channel widened to 8 bits (`A` becomes `AA`). |

The JSON file (`"kind": "cairn-vf-sheet"`) holds `font` (`version`, `format`: `mono`, `indexed` or `rgba4444`,
`height`, `firstCharacter`, `defaultSpacing`), `layout` (`columns`, `cellWidth`, `cellHeight`, `gap`, `monoStyle`,
`guide`), `glyphs` (one per character in order: `code`, `char` for reading only, `width`, `spacing`, `userData`),
`kerning` (`left` and `right` character codes, `offset`) and, for indexed fonts, `palette` (256 colours as hex
`AARRGGBB`). On import every value comes from the JSON file: widths up to the cell width, spacings, user data, kerning
(sorted again), the default spacing, the palette and the character range (the glyphs must run from `firstCharacter`
without gaps). A pixel is converted to the font's format as in **Replace Glyph from Image**; a pixel whose colour is
exactly what the export wrote keeps its value, so an exported sheet imports back unchanged.

### Font problem codes

| Code | Meaning |
|---|---|
| VF000 | The file cannot be read (not a font, an unsupported version or pixel format, cut short, or impossible sizes: a height over 1,024 pixels, a glyph over 4,096 pixels wide, or far more pixels than the file holds). |
| VF001–VF006 | Header values the game cannot use: height 0, no characters, a first character outside 0–255, no default spacing, a version 0 font that is not monochrome, a palette of the wrong size. |
| VF010 | A glyph's pixels lie outside the pixel data; the game would draw whatever lies beyond. |
| VF020 | Bytes after the end of the font (ignored by the game, kept by Cairn). |
| VF030, VF031, VF033 | Character range notes: a first character other than space, glyphs past code 255 that can never be drawn, characters that draw nothing. |
| VF040–VF045 | Kerning: a pair names a glyph the font lacks, a pair the game never applies (out of order, or a glyph number of 128 or more), a wrong kerning index, a duplicate pair. |
| VF046 | Kerning the game applies to two characters that have no pair: its lookup runs past the first character's last pair and uses the next pair in the table. The message names both pairs and how far the text moves. Give the two characters a pair of their own. |
| VF050–VF055 | Glyph metrics: negative width (read as 0) or spacing, spacing 0, a glyph wider than the texture, pixels of the wrong size, a spacing of thousands of pixels (text with it is too wide to draw). |
| VF060 | **Font too big**: the glyphs do not fit the font's texture (at most 256 × 256); the game stops with "Font too big!" when it loads the font. |
| VF061 | The font is taller than its texture but its glyphs fit one row: the game's check does not catch it, writes past the texture, draws the characters wrongly and can crash. |
| VF070–VF073 | Pixel and layout notes: coverage above 14, palette colours that change in the game, unused pixel data, unused header fields that do not match. |

## Volition bitmaps (VBM)

### Bitmap settings

**Tools › Settings… › Volition bitmaps**:

| Setting | What it does |
|---|---|
| **Frame rate** | The frame rate **New VBM** starts with (15 fps until changed). |
| **Pixel format** | The pixel format **New VBM** starts with: **Suggested from the images** (565 for opaque images, 1555 for on/off transparency, 4444 for soft transparency) or always 1555, 4444 or 565. |
| **Make mipmaps for new bitmaps** | **New VBM** starts with mip levels down to 16 pixels on the shorter side, as the game's own textures have (off: no mipmaps). Frames added to or replaced in an existing bitmap always get that bitmap's mip levels, made again from the new image. |
| **Filter** and **Fit** | How images of another size are resized: **Nearest (sharp pixels)**, **Bilinear (smooth)** or **High quality** (Lanczos); **Stretch to the frame**, **Keep aspect (transparent padding)** or **Crop the centre**. Also what the resize window last used. |
| **Ask each time, with a preview** | Off: images of another size are resized with the filter and fit above without asking. |

### Bitmap facts

**Size**, **Format** (pixel format), **Version**, **Frames**, **Length** (frames divided by the frame rate), **Mip
levels** (and the smallest level's size), **Pixel data** and **File size**. The status bar shows the size, pixel
format, frames and frame rate, and the number of errors and warnings (click to open **Problems**).

### Bitmap menu and shortcuts

| Command | Shortcut |
|---|---|
| **Play / Pause** | **Space** |
| Previous / next frame | **,** / **.** |
| **Replace Frame…** | **Ctrl+R** |
| **Add Frames…** | **Insert** |
| **Copy Frames** / **Paste Frames** | **Ctrl+C** / **Ctrl+V** |
| **Duplicate Frames** | **Ctrl+D** |
| **Remove Frames** | **Delete** |
| **Move Earlier** / **Move Later** | **Alt+Left** / **Alt+Right** |
| **Reverse Frame Order**, **Pixel Format**, **Mip Levels** | |
| **Export Frames…** | **Ctrl+Shift+E** |
| **Convert to ATX…** | |

Also **File › New › Volition bitmap**, **File › Export › VBM Frames as Images…** and **File › Export › VBM as ATX…**.

**Copying between bitmaps.** Between bitmaps of the same size, pixel format and mip levels, pasted frames keep their
exact data; otherwise they are resized and converted like any image. If the Windows clipboard is busy or cannot be
read, frames copied in Cairn still paste.

**Export Frames.** Names are `stem_00.tga`, `stem_01.tga`… numbered from 0, the way **Import VBM** names frames. TGAs
are 32-bit when the bitmap has transparency and 24-bit otherwise; PNGs always keep the alpha channel.

**Convert to ATX.** A bitmap opened from a packfile offers your last import folder, never the work copy's folder.

### Bitmap pixel formats and versions

| Format | Bits per pixel | Transparency |
|---|---|---|
| 1555 | 5 bits each of red, green and blue | 1 bit: each pixel is opaque or clear |
| 4444 | 4 bits each of red, green, blue and alpha | 16 levels |
| 565 | 5 bits red, 6 green, 5 blue | none |

Version 1 bitmaps store the 1555 transparency bit inverted (set means clear); version 2 store it the standard way.
Every stock 1555 bitmap is version 1, so Cairn writes new 1555 bitmaps as version 1 and 4444 and 565 bitmaps as
version 2. Edits keep a bitmap's version. Converting a pixel to 16 bits keeps the top bits of each channel, as the game
does. Mip levels are made with a box filter.

### Bitmap problem codes

| Code | Meaning |
|---|---|
| VBM001 | The file cannot be read (not a VBM, an unknown pixel format, or not even one frame complete). It opens read-only and cannot be saved. |
| VBM002 | The file stops early: the complete frames are shown, and saving keeps only those. |
| VBM003 | Bytes after the last frame (ignored by the game, kept by Cairn). |
| VBM004 | The header's mip count does not fit the size; it is read as the most the size holds and corrected when saved. |
| VBM005 | More than 255 frames: the game keeps the frame count in one byte. |
| VBM006 | A side is not a power of two: fine for interface images, not for textures on level geometry and meshes. |
| VBM007 | An animated bitmap with a frame rate of 0. |
| VBM008 | A version other than 1 or 2. |
| VBM009 | The header's frame count is 0 or negative (read as 1). |
| VBM010 | A version 2 1555 bitmap: no stock file uses it, so check it in the game. |

## Sounds (VSE, VMU, WAV, OGG)

### Sound details pane

| Row | What it shows |
|---|---|
| Format | The file type (PS2 sound effect, PS2 music, WAVE, Ogg Vorbis, AIFF). |
| Codec | PS ADPCM, PCM, Microsoft or IMA ADPCM, Vorbis, IMA4. |
| Sample rate | The rate the sound plays at; for a PS2 sound also the exact rate the console plays (its pitch value). |
| Channels | 1 (mono) or 2 (stereo). |
| Bit depth | The stored sample size: 16-bit, 8-bit, 4-bit ADPCM (decoded to 16-bit). |
| Duration, Samples | The length, and the number of samples per channel. |
| Loop, Loop from | The loop region and where it comes from (the PS2 header and frame flags, a `smpl` chunk, Ogg comments). |
| Size | The file's size. |
| Header and layout | Every header field of a PS2 sound (sound time, key-off time, envelope, data size, pitch, flags, blocks). |
| About this format | What is special about the format, for example how a `.vmu` stores its two channels. |

**Sound menu and shortcuts.** **Play** / **Pause** (**Space**), **Stop**, go to start (**Home**), **Loop** (**L**),
**Convert…** (**Ctrl+Shift+E**). The waveform zooms with the mouse wheel (**Shift**+wheel scrolls); the toolbar shows
how many samples one pixel holds.

### PS2 sound formats

- **`.vse`** (sound effect): mono, 11,025, 22,050 or 44,100 Hz. The current 24-byte header holds the sound time and
  key-off time in milliseconds, an envelope, the data size, a loop start, the pitch and flags (bit 1: looping); an older
  12-byte header (duration, data size, pitch) pads its data with silence, which Cairn drops when it lies past the
  header's duration. The sound data is PS ADPCM: 16-byte frames of 28 samples, each with a filter, a shift and loop
  flags. A one-shot sound ends on a frame flagged "end" followed by a never-played end marker; a looping one ends on a
  "loop end" frame. Sounds longer than 64 KB carry loop flags every 4,096 frames for the console's streaming buffer;
  Cairn reads them as such, not as loops.
- **`.vmu`** (music): stereo, 44,100 Hz, a 12-byte header (block count, pitch with bit 15 set for music that loops,
  block time, loop start). Left and right alternate in 16 KB blocks; the last, shorter block is split in half.
- **Pitch**: the console stores a rate as a pitch value (rate × 4096 / 48,000, rounded down), so a 22,050 Hz sound
  plays at 22,043 Hz. Cairn plays and converts at the standard rate and says so.

The PS2 version's `RF_PS2.VPP` holds about a thousand `.vse` files and two `.vmu` pieces of music.

### Sound conversion details

- **WAV** is 16-bit PCM. **Keep loop points** writes the loop into the WAV's `smpl` chunk.
- **Ogg Vorbis** is encoded with the Xiph.Org reference encoder (libvorbis 1.3.7, variable bitrate), quality q-1 to
  q10 (default q5, about 160 kbit/s for 44.1 kHz stereo). **Keep loop points** writes `LOOPSTART` and `LOOPLENGTH`
  comments (in samples). The file always gets a lower-case `.ogg`.
- A format the sound is in already is not offered (a 16-bit WAV to WAV, an Ogg Vorbis to Ogg Vorbis). A WAV in another
  coding (24-bit, ADPCM) converts to a 16-bit WAV, named `name (converted).wav` so it never takes its source's place.
- **Next to the source** means the sound's own folder, or for a packfile entry the packfile's folder; it is off when
  that is the game folder.
- **What the conversion changes** lists the source's lossy coding (and, for Ogg Vorbis, the re-encoding), the
  console's exact playing rate, the loop, and header fields WAV and Ogg cannot hold.
- **Batch conversion (Convert sounds…)** leaves out sounds already in the chosen format, sounds whose converted name
  another selected sound also makes (`x.wav` and `x.aif` both give `x.ogg`: the `.wav` is converted), and sounds whose
  converted name the packfile or folder already has, unless **Replace** is ticked; then Cairn lists them and asks
  first. A damaged sound is listed and the others go on; each kind of remark is reported once with the number of sounds
  it concerns.

### Sound settings

**Tools › Settings… › Sounds**:

| Setting | What it does |
|---|---|
| Format | WAV (16-bit PCM) or Ogg Vorbis: what **Convert** offers first. |
| Ogg Vorbis quality | q-1 to q10 (default q5), with the nominal bitrate for 44.1 kHz stereo beside it. |
| Where the files go | Into the packfile, next to the source, or a folder: what **Convert** offers first. A sound not from a packfile falls back to the next choice. |
| Keep loop points | Writes the loop into a WAV's `smpl` chunk, or as `LOOPSTART`/`LOOPLENGTH` comments in an Ogg. |

Replacing files of the same name is not a setting: tick **Replace** in the Convert window each time.

### Sound problem codes

| Code | Meaning |
|---|---|
| SND002 | The header's data size differs from the data in the file (cut short, or extra bytes). |
| SND003 | Frames with an out-of-range filter or shift, decoded as the console does. |
| SND004 | Bytes at the end that do not make a whole 16-byte frame (ignored). |
| SND005 | A one-shot sound without an end flag: it may be cut short. |
| SND006 | An unusual envelope value (information). |
| SND007 | An implausible pitch value: the sound plays at 22,050 Hz. |
| SND008 | The header's time and the data's length differ by more than 50 ms (information). |
| SND009, SND010 | A `.vmu` whose block count or last block does not match its data. |
| SND011, SND012 | The older 12-byte header; silent padding dropped (information). |
| SND013 | The file holds no sound data. |
| SND014 | Streaming-buffer loop flags in a sound that does not loop (information). |
| SND015 | The two channels of a `.vmu` end at different frames. |
| SND016 | The looping flag and the sound time disagree (information). |
| SND020, SND021 | Samples wider than 16 bits; loop points outside the sound. |
