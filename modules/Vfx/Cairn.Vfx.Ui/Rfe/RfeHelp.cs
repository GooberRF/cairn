using System.Windows;
using System.Windows.Documents;
using Cairn.Vfx.Rfe;

namespace Cairn.Vfx.Ui.Rfe;

/// <summary>The help topic "Red Faction II effects".</summary>
internal static class RfeHelp
{
    public const string TopicId = "vfx.rfe";

    public static FlowDocument Build()
    {
        var doc = new FlowDocument();
        void Heading(string text, double size = 16) => doc.Blocks.Add(new Paragraph(new Run(text)) { FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        void Text(string text) => doc.Blocks.Add(new Paragraph(new Run(text)));
        void Bullets(IEnumerable<string> items)
        {
            var list = new List();
            foreach (string item in items) list.ListItems.Add(new ListItem(new Paragraph(new Run(item))));
            doc.Blocks.Add(list);
        }
        Heading("Red Faction II effects (.rfe)", 20);
        Text("A .rfe is Red Faction II's compiled effect: texture names, materials (blend bits and opacity and self-illumination tracks keyed in seconds), mesh objects (one static Red Faction II mesh each, moved by position, rotation and scale keys spread evenly over the effect) and particle emitters. Cairn reads the layout the PC game loads (\"FEFR\" version 3); six stock files use an older layout the PC game refuses, and Cairn refuses them too.");
        Text("Effects open read-only (File › Open, Open in Cairn on a packfile entry) with their facts and an animated preview, and play in the packfile preview pane. Nothing can be edited or saved; File › Convert to › Red Faction effect (.vfx), the banner's Convert… and the packfile's Convert effects… make Red Faction effects of them.");
        Heading("Textures");
        Text("Textures come from .peg packs, in this order: the effect's own <name>_vfx.peg, <name>.peg, the other packs of its folder or packfile that hold the texture, always_loaded.peg of the install's Permanent_data group, then any other pack of the install. The facts say where each one was found, or \"not found\".");
        Heading("Preview");
        Text("Mesh motion, the material tracks, the particle step (1/30 s), birth rate, launch speed, speed and life variance, gravity, the constant acceleration, the rectangle and sphere spawns, the launch cone and the fade and size envelopes follow the game's code. These are approximated:");
        Bullets(RfeFacts.PreviewApproximations);
        Heading("Conversion to a Red Faction effect");
        Text("Meshes and materials convert exactly where Red Faction has the same thing; particles as closely as Red Faction allows. The report (in the window, and in the converted tab's Problems as information) lists every approximation for that effect.");
        Bullets(RfeFacts.ConversionMapping.Select(m => $"{m.Rf2} → {m.Rf1} ({m.Kind switch { RfeNoteKind.Approximated => "approximated", RfeNoteKind.Dropped => "dropped", _ => "exact" }})"));
        Text("Converting never replaces the source or a Red Faction II file: the output takes a free name (\"name (2).vfx\") instead. Check converted particle effects in the game: units and behaviour without a Red Faction field differ.");
        return doc;
    }
}
