using System.Windows;
using System.Windows.Documents;

namespace Cairn.Vpp.Ui.Dialogs;

/// <summary>Help > Packfiles > "Packfiles: format and limits".</summary>
internal static class VppHelp
{
    public const string TopicId = "vpp.format";

    public static FlowDocument Build()
    {
        var doc = new FlowDocument { PagePadding = new Thickness(16), FontSize = 13 };
        void H(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        void P(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0, 2, 0, 6) });
        void L(params string[] items)
        {
            var list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 2, 0, 6) };
            foreach (var item in items) list.ListItems.Add(new ListItem(new Paragraph(new Run(item)) { Margin = new Thickness(0, 1, 0, 1) }));
            doc.Blocks.Add(list);
        }

        H("Packfiles");
        P("A packfile (.vpp) is the archive Red Faction loads its levels, textures, sounds, meshes, animations and tables from. It holds a flat list of files: there are no folders inside a packfile.");
        P("Cairn opens a packfile by reading only its directory, so even the largest open instantly. Every change (add, replace, rename, remove) is a pending change you can undo; nothing is written until you save, and saving never writes over the packfile in place: Cairn writes a new file next to it, checks it, and then swaps it in.");
        H("Working with entries");
        L("Add files or folders with the Packfile menu, the toolbar, or by dropping them from Explorer onto the list. Folders are flattened: only the file names are kept.",
          "When a name is already taken you can replace the entry, keep both (the new one gets a free name such as \"name (2).tga\"), or skip it.",
          "Double-click or press Enter to open an entry in the program Windows uses for its type. Cairn works on a copy; when you save that copy, a bar offers to update the packfile.",
          "Drag entries out to Explorer, or press Ctrl+C and paste them in Explorer, to extract them. Extract (Ctrl+E) asks for a folder.",
          "F2 renames, Del removes, Ctrl+A selects everything shown. The filter box accepts parts of names and wildcards such as *.tga or lev??.rfl.");
        H("Format");
        P("A packfile starts with a 2,048-byte header (signature, version 1, the number of files, the total size). Then comes the directory: one 64-byte record per file, a 60-byte name and the file's size, padded to a multiple of 2,048 bytes. The files' data follow in the same order, each padded to a multiple of 2,048 bytes.");
        H("Limits the game imposes");
        L("Entry names: at most 59 characters (bytes), no \\ or / (the game could never find such an entry), Latin-1 characters only. Names are compared without regard to case, so two entries may not differ only in case.",
          "Packfile file name: at most 31 characters including .vpp, and the full path from the game folder at most 127 characters, or the game will not load it.",
          "Size: the whole packfile must stay below 2 GB (every file must start within the first 2 GB), and no single file may be larger than 1.5 GB.",
          "Entry counts: at most 1,048,576 entries per packfile; there is no limit on the number of packfiles or on the entries of all packfiles together.",
          "When two packfiles hold the same name, the one loaded last wins.");
        H("File types the game loads");
        P("Levels (.rfl), tables (.tbl), meshes (.v3m, .v3c), animations (.rfa), effects (.vfx), textures (.tga, .vbm, .dds, .png, .jpg and .atx), sounds (.wav, .ogg; the name must end in lower-case .ogg) and fonts (.vf). Cairn targets Alpine Faction: these are the types it loads. Types such as .aif, .mp3, .mvf, .rfg, .psd, .gltf, .txt and .log are kept in some packfiles but the game never loads them; Cairn warns about them. The game loads these assets only from packfiles, never from loose files.");
        H("PlayStation 2 packfiles");
        P("Packfiles from the PlayStation 2 version have the same layout, so they open like any other, but their textures are PEG texture packs (.peg) and their meshes (.rfm, .rfc), sounds (.vse, .vmu) and levels are the PS2 version's own; the PC game loads none of them. Select .peg entries and use Convert to .tga... (right-click, or the Packfile menu): a dialog lists their textures with a tick each, what each becomes and any name the packfile already has; the ticked ones become 32-bit .tga files (animations as numbered frames plus an .atx) in place of the .peg, as one undoable change. A texture whose name the packfile already has (from an earlier conversion, or its own) keeps the larger version: a larger one replaces it, an identical or smaller one is left out, and the summary lists each case. A .peg opened on its own shows its textures as a packfile, and saving writes a new PC packfile (.vpp). MPEG-2 compressed backgrounds become 24-bit .tga files (no alpha); the main menu's numbered frames (plan-0001 to plan-0150) also get an .atx that loops them at 30 fps (the PS2 rate is not known). Settings > Packfiles > Decode PS2 MPEG-2 backgrounds switches this off; Treat black as transparent makes every pixel whose red, green and blue are all below the threshold (25, as the PS2 game uses) transparent, and the backgrounds 32-bit .tga files with alpha. The convert dialog offers the same choice for each conversion.");
        H("Red Faction II packfile groups");
        P("Red Faction II keeps its files in packfile groups: a .toc_group file in the game's pc_media folder lists the files of one group. A packed group keeps their data in one .packfile (in the group's own folder); a loose group lists files that sit in folders of the install. Open the .toc_group with File > Open. Cairn reads only the list and checks it strictly (no signature, so every name, count, offset and size must make sense), and refuses a file that does not, with the reason.");
        P("A group is read only in Cairn: preview, extract, open and convert its files, but nothing can be added, removed, renamed or saved. Types use Red Faction II's names (RF2 texture pack, RF2 level, RF2 static mesh, RF2 animation and so on). Levels and meshes preview with their textures from the group's .peg packs, animations play on a Red Faction II character of the group, and Open in Cairn opens levels, meshes, animations, texture packs, tables and sounds in tabs. Convert to on an entry, and Convert meshes and animations..., Convert sounds... and Convert levels..., write into a folder (an animation becomes a Red Faction clip). The types Cairn does not read can only be extracted. With nothing selected, the details describe the group. Problems: RF2001 a listed file is missing on disk, RF2002 two names differ only in case, RF2003 a file's size differs from the list, RF2004 an empty file.");
        H("Alpine Faction cache packs");
        P("Alpine Faction keeps data it fetched or built in cache packs (.cache) in the game's alpine_cache folder; map_thumbs.cache holds the map browser's level previews, one JPEG per level. Open a .cache with File > Open. Cairn reads only the index and checks it as strictly as the game does (signature AFCP, version 1, unique names, sizes that add up to the file), and refuses a file that does not, with the reason; a .cache from another program is refused as not a cache pack.");
        P("A cache pack is read only in Cairn: preview, extract and convert its entries, but nothing can be added, removed, renamed or saved. An entry whose data does not match its name is listed with the data's type added, so the thumbnail stored as dm01.rfl is listed as dm01.rfl.jpg and previews and extracts as a JPEG; the details show the stored name, the entry's tag and its CRC. With nothing selected, the details describe the pack. The game rewrites the whole pack when it saves it; reload the tab if that happens while it is open. Problems: ACP003 a corrupt entry (its data does not match its CRC, so the game does not use it); as information, ACP001 two names differ only in case, ACP002 an empty entry.");
        H("Problems");
        P("The problem count in the status bar lists everything that would stop the game from loading the packfile (errors, which also block saving) and things worth knowing (warnings). Click it to see the list; double-click a problem to select its entry.");
        return doc;
    }
}
