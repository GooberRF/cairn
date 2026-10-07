using Cairn.Vfx.Editing;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Inspectors;

/// <summary>Effect tab: version, header end frame (editable, fit to longest object), counts, playback mode.</summary>
public sealed class VfxEffectTab(VfxDocument doc) : VfxInspectorPage(doc)
{
    protected override string StructureKey() => "effect";

    protected override void Build()
    {
        Header("Effect");
        Fact("Format version", () => Doc.VersionText + (Doc.IsOlderVersion ? " (older: convert to edit)" : " (current)"), "vfx.header.version");
        Number("End frame", Tip("vfx.header.end_frame", "Last frame of the effect (15 fps); 0 = the game uses the longest object"),
            () => [File.EndFrame], (f, v) => VfxEdit.SetEndFrame(f, (int)v), 0, 1, 0, 100000);
        Buttons(("Fit to longest object", "Set the end frame to the end of the longest animated object (meshes, particles, dummies, lights)",
            () => VfxEditing.Apply(Doc, "Fit end frame to longest object", VfxEdit.RecomputeEndFrame)));
        Fact("Length", () => $"{Doc.EndFrame} frames, {Doc.EndFrame / 15.0:0.##} s at 15 fps", "vfx.header.end_frame");
        Fact("Playback mode", () => Doc.ModeText + " (Effect menu; how the game plays the effect depends on the caller)");
        Header("Contents");
        Fact("Meshes", () => Doc.Sampler.Meshes.Count.ToString());
        Fact("Particle systems", () => Doc.Sampler.ParticleSystems.Count.ToString());
        Fact("Dummies / lights", () => $"{Doc.Sampler.Dummies.Count} / {Doc.Sampler.Lights.Count}");
        Fact("Space warps", () => Doc.Sampler.Spacewarps.Count.ToString());
        Fact("Materials", () => Doc.Sampler.Materials.Count.ToString());
        Fact("Sections", () => File.Sections.Length.ToString());
    }
}
