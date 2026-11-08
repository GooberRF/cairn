using System.Windows;
using System.Windows.Documents;

namespace Cairn.Lvl.Ui;

/// <summary>The help topic of the levels and groups module.</summary>
internal static class LvlHelp
{
    public static FlowDocument Build()
    {
        var doc = new FlowDocument { PagePadding = new Thickness(16), FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13 };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "App.Text");
        doc.SetResourceReference(FlowDocument.BackgroundProperty, "App.PaneBackground");
        void Heading(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        void Para(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 6) });
        doc.Blocks.Add(new Paragraph(new Run("Levels and groups")) { FontSize = 20, FontWeight = FontWeights.SemiBold });
        Para("The Levels & groups module opens Red Faction levels (.rfl: Red Faction, Alpine Faction and Red Faction II) and the " +
             "level editor's group files (.rfg) in read-only tabs. A tab lists the file's facts on the left and shows its geometry in 3D " +
             "on the right. Cairn never changes a level or a group file: Save is off, and File > Convert to… (or Level > Convert…, " +
             "Ctrl+Shift+E) makes other files from it. Alpine Faction's level editor is the tool for editing levels.");
        Heading("Facts");
        Para("For a level: its name, author, format version and the game it loads in, the level's properties (ambient light, fog, " +
             "geomod), statistics and object classes, Alpine Faction's settings, every file the level refers to with where it is " +
             "found (beside the level, in the game data, or missing), the preload lists, the brushes, movers and groups, and how each " +
             "section of the file was read. For a group file: its groups with their brushes, objects and movement, the objects by " +
             "type, and the textures with where each is found. Anything that did not read cleanly is listed and flagged. A Red Faction " +
             "II level gets the same facts, with its sun, hardlight and lightmap multiplier among the properties and its textures " +
             "found in its .peg packs; files it names that Cairn cannot look up are listed without a location, never as missing.");
        Heading("The 3D preview");
        Para("Compiled shows the geometry the game draws (the rooms and the movers); Brushes shows the editor's brushes. Group files " +
             "only have brushes. Faces chooses which face classes are drawn: ordinary faces, portal, detail, sky, invisible, holes, " +
             "liquid surface, alpha, full bright and scrolling. Portal and invisible faces start hidden because the game does not draw " +
             "them. Liquid surfaces are drawn see-through. The game draws the front of each face only, so a room seen from outside " +
             "shows its inside; Faces > Both sides of each face draws the backs too.");
        Para("Lighting: Full bright draws every face at its texture's colours; Level ambient light draws them at the level's ambient " +
             "colour; Shaded adds a light from the camera's side to show the shapes. Lightmaps are not drawn. Textures are found " +
             "beside the file first (the rest of its packfile for a level opened from one), then in the game data; a face whose " +
             "texture is missing is drawn in a flat colour, and the toolbar counts the missing textures.");
        Para("A Red Faction II level's textures come from the .peg texture packs beside it (in its folder or its packfile), then " +
             "from the shared packs of the Red Faction II install it belongs to, when Cairn can tell which one that is.");
        Para("Objects chooses the object markers: lights, the player start and respawn points, and items at first; entities, clutter, " +
             "triggers and events, and the other objects (sounds, emitters, regions, nav points, cameras, Alpine meshes) on request. " +
             "Walls hide the markers behind them. Click a face to see what it belongs to (room, brush or mover, with its uid), its " +
             "texture, classes, flags, smoothing groups and scroll speed; click a marker to see the object. Esc clears the selection. " +
             "The preview in a packfile's list starts without markers.");
        Para("Camera: drag with the left button to orbit, the middle button (or Shift and the left button) to pan, and the wheel to " +
             "zoom. F frames the whole level, Home resets the view, and 1, 3 and 7 look from the front, side and top. Fly (V) turns " +
             "on the fly camera: drag with either button to look around, W A S D to move, Q and E to go down and up, Shift to go " +
             "faster and Ctrl slower; the wheel steps forward and back. Tools › Settings… › General › 3D viewport mouse inverts " +
             "the orbit and the fly-mode mouse look along either axis.");
        Para("A level with more triangles than the preview's limit (300,000 at first) asks before it is built: building a very large " +
             "level takes longer and uses more memory.");
        Heading("Converting");
        Para("A level or group file converts to glTF (.gltf with a .bin, or one .glb), Wavefront OBJ (.obj with a .mtl) or a static " +
             "mesh (.v3m). The Convert window chooses the geometry (compiled or brushes), the face classes to include (portal and " +
             "invisible faces are left out at first), the objects to carry (glTF), whether textures are written as PNG files (inside " +
             "a .glb), and each format's own options: glTF keeps each brush's position and rotation on its node or moves the vertices " +
             "into world space and can write the lightmap coordinates; OBJ can write triangles and Cairn's comment lines (face and " +
             "brush flags that Cairn reads back and other tools ignore); a static mesh has one submesh per brush, room or mover, or " +
             "one for the whole level. The output goes into the packfile the file came from (as new entries, one undo step in the " +
             "packfile's tab), next to the source, or into a folder. A name that is taken gets a free name unless Replace is ticked; " +
             "Cairn asks before replacing anything. Texture images are named after the textures, so an image of the same name beside " +
             "the output is written over.");
        Para("A level also converts to an editor group file (.rfg) for Alpine Faction's level editor: its brushes with their flags, " +
             "textures and scrolling, its groups and moving groups, and the objects chosen. The level's own settings, its player " +
             "start and its compiled geometry have no place in a group file; the report lists what was left out.");
        Para("Red Faction II levels add their own choices: texture names translated to Red Faction's (the default) or kept, with " +
             "the Red Faction II textures written beside a static mesh or group file; and, folded below, how its lights and coronas " +
             "become Red Faction lights, whether its clutter becomes Alpine meshes, the jump pad strength and what happens to its " +
             "special brushes. The defaults are REDUX's; Defaults puts them back.");
        Para("Meshes (.v3m, .v3c), glTF (.gltf, .glb) and OBJ files convert to group files too: File > Convert to… on a mesh tab, " +
             "Convert to on such a packfile entry, or File > Import > Group file from glTF, OBJ or mesh… for a file on disk (the group " +
             "file then opens in a tab). Each mesh node, OBJ object or submesh becomes a brush. A closed mesh whose faces point " +
             "outward becomes a solid brush and anything else a detail brush, unless the window says always solid or always detail; " +
             "brushes Cairn wrote to glTF or OBJ keep their own flags. For a mesh, the level of detail can be chosen.");
        Para("After a conversion the report lists the files written, the counts, and what was approximated, dropped or split, and " +
             "any engine limit the output breaks (for example a static mesh's texture limit), each with a code. In a packfile, " +
             "select levels and group files and use Convert levels... to convert them all with the same options.");
        Heading("Where levels load from");
        Para("Red Faction and Alpine Faction load levels from packfiles (.vpp). A converted or extracted file on disk is for other " +
             "tools; to play a level, it has to be in a packfile the game loads.");
        return doc;
    }
}
