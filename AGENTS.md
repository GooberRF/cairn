# Agent guide for Cairn

This file is intended to help Codex, Claude, and other assistants (and other automated tools) work in this repository efficiently.

## Basic information
- Cairn is a Windows desktop suite of modding tools for Red Faction (C#, .NET 9, WPF): one app (`Cairn.exe`)
  with a module per file format, each opening files in tabs with previews, editing, linting and conversion.
- Alpine Faction is the baseline: files are judged by how Alpine Faction loads them.
- Red Faction II formats and legacy formats (.vse, .vmu, .v3d, .rfm, .rfc, .vcm, .peg, RF2 .rfl/.rfa/.rfe,
  .toc_group) are read, previewed and converted, never written.
- The game and editor source is not available; formats come from reverse engineering, and some fields are
  unknown. Label uncertain fields rather than guessing.
- The game loads assets only from packfiles; loose files never override packfile contents.

## Repository overview
- `src/Cairn.Shell/`: the app (startup, menus, Convert to…, diagnostics/self-tests under `Probe/`).
- `src/Cairn.Ui/`: shared UI and the module contracts (`Modules/Contracts.cs`).
- `src/Cairn.Formats/`: shared format readers and writers (RFL, RF2 packfiles, Wavefront OBJ, …).
- `src/Cairn.Assets/`: asset resolution (game folder, search folders, packfiles), textures, PEG.
- `src/Cairn.Viewport/`, `src/Cairn.Previews/`, `src/Cairn.Workspace/`: 3D viewport, preview pane, settings/recovery.
- `modules/<Name>/Cairn.<Name>.{Core,Ui,Tests}`: one module per area: Atx (animated textures), Lvl (levels and
  group files), Rfa (animations and meshes), Snd (sounds), Tbl (tables), Vbm, Vf (fonts), Vfx (effects), Vpp (packfiles).
- `tools/`: sample generators and the icon generator. `native/vorbis/`: prebuilt Vorbis DLL and its build script.
- `samples/`: small sample files. `docs/`: `USAGE.md`, `REFERENCE.md`, `CHANGELOG.md`.

## Build and run
- Requires the .NET 9 SDK on Windows.
- `dotnet build Cairn.sln -c Release`; `.\publish.ps1` makes the portable single-file build in `dist\`.
- Warnings are errors. Files use CRLF line endings.

## Testing and validation
- Unit tests: `dotnet test` (or one project, e.g. `dotnet test modules/Rfa/Cairn.Rfa.Tests`). Timing-sensitive
  tests can fail under heavy load; rerun a failing project on its own before treating it as a regression.
- In-app self-tests: `Cairn.exe --probe-module --selftest-only <name or prefix*>` (e.g. `rfa.*`); `--selftest`
  runs all. Some tests also use local sample data (paths in the git-ignored `research/local-paths.json` or
  environment variables); without it, unit tests leave those checks out and self-tests report them as skipped.
- Changes should always be rigorously reviewed before a merge is considered:
  - As surgical as possible to achieve the goal
  - Effective, necessary, safe, and properly/robustly written
  - Reuses shared code (`src/Cairn.Formats`, `src/Cairn.Assets`, the `Cairn.Ui` contracts) instead of duplicating it
  - Matches the style and naming of nearby code
  - No unexpected regressions affecting existing behaviour

## Change guidelines
- Keep changes minimal and focused on the requested task.
- Match existing formatting and naming in nearby code.
- Update documentation when behaviour or usage changes: `docs/CHANGELOG.md`, `docs/USAGE.md`, `docs/REFERENCE.md`,
  and the module's in-app help (`HelpDocuments.cs` / `*Help.cs`).
- Never commit machine-specific paths, personal data or game-owned files.
