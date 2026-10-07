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
        P("Levels (.rfl), tables (.tbl), meshes (.v3m, .v3c), animations (.rfa), effects (.vfx), textures (.tga, .vbm, .dds, .png, .jpg and .atx), sounds (.wav, .ogg; the name must end in lower-case .ogg) and fonts (.vf). Cairn targets Alpine Faction: these are the types it loads. Types such as .aif, .mp3, .mvf, .rfg, .psd, .gltf, .txt and .log are kept in some packfiles but the game never loads them; Cairn warns about them.");
        H("Problems");
        P("The problem count in the status bar lists everything that would stop the game from loading the packfile (errors, which also block saving) and things worth knowing (warnings). Click it to see the list; double-click a problem to select its entry.");
        return doc;
    }
}
