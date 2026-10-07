using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Ui.Documents;

/// <summary>The kinds of section the outliner and inspectors distinguish.</summary>
public enum VfxSectionKind { Mesh, Particles, Dummy, Light, Spacewarp, Material, Other }

/// <summary>Name/parent/kind accessors over the section records (they share no base members).</summary>
public static class VfxSections
{
    public static VfxSectionKind KindOf(VfxSection s) => s switch
    {
        VfxMesh => VfxSectionKind.Mesh,
        VfxParticleSystem => VfxSectionKind.Particles,
        VfxDummy => VfxSectionKind.Dummy,
        VfxLight => VfxSectionKind.Light,
        VfxSpacewarp => VfxSectionKind.Spacewarp,
        VfxMaterial => VfxSectionKind.Material,
        _ => VfxSectionKind.Other,
    };

    public static string NameOf(VfxSection s) => s switch
    {
        VfxMesh m => m.Name, VfxParticleSystem p => p.Name, VfxDummy d => d.Name,
        VfxLight l => l.Name, VfxSpacewarp w => w.Name, _ => string.Empty,
    };

    public static string ParentOf(VfxSection s) => s switch
    {
        VfxMesh m => m.Parent, VfxParticleSystem p => p.Parent, VfxDummy d => d.Parent,
        VfxLight l => l.Parent, VfxSpacewarp w => w.Parent, _ => string.Empty,
    };

    /// <summary>Segoe Fluent/MDL2 glyph per kind.</summary>
    public static string GlyphOf(VfxSectionKind k) => k switch
    {
        VfxSectionKind.Mesh => "", VfxSectionKind.Particles => "", VfxSectionKind.Dummy => "",
        VfxSectionKind.Light => "", VfxSectionKind.Spacewarp => "", VfxSectionKind.Material => "",
        _ => "",
    };

    public static string KindText(VfxSectionKind k) => k switch
    {
        VfxSectionKind.Particles => "Particle system", VfxSectionKind.Spacewarp => "Space warp", VfxSectionKind.Other => "Other data",
        _ => k.ToString(),
    };

    /// <summary>Index of the n-th section of type <typeparamref name="T"/> in the file, or -1.</summary>
    public static int SectionIndex<T>(VfxFile f, int ordinal) where T : VfxSection
    {
        for (int i = 0, n = 0; i < f.Sections.Length; i++)
            if (f.Sections[i] is T && n++ == ordinal) return i;
        return -1;
    }

    /// <summary>Ordinal among sections of the same record type, or -1.</summary>
    public static int OrdinalOf(VfxFile f, int section)
    {
        if (section < 0 || section >= f.Sections.Length) return -1;
        var t = f.Sections[section].GetType();
        int n = 0;
        for (int i = 0; i < section; i++) if (f.Sections[i].GetType() == t) n++;
        return n;
    }
}
