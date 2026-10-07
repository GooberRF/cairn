using System.Numerics;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Ui.Dialogs;

/// <summary>Primitive shapes offered by Effect &gt; Add &gt; Primitive.</summary>
public enum VfxPrimitiveKind { Plane, FacingQuad, FacingRod, Disc, Cylinder, Sphere, Box }

/// <summary>What the primitive dialog collects. Size A/B: width/depth, size/-, width/length, outer/inner, radius/top radius, radius/-, size.</summary>
public sealed record VfxPrimitiveRequest(VfxPrimitiveKind Kind, string Name, float SizeA = 1, float SizeB = 1, float Height = 1, int Segments = 16,
    int ExistingMaterial = -1, string? Texture = null, bool Additive = true, bool Fullbright = true, int Frames = 0);

/// <summary>Pure "add an object" edits (each is applied as one undo step by the caller).</summary>
public static class VfxCreation
{
    /// <summary>Default length (frames) when the effect has none yet.</summary>
    public const int DefaultFrames = 30;

    public static int EffectFrames(VfxFile file) => file.EndFrame > 0 ? file.EndFrame + 1 : DefaultFrames;

    public static int MaterialCount(VfxFile file) => file.Sections.Count(s => s is VfxMaterial);

    public static string[] PrimitiveLabels { get; } =
        ["Plane", "Camera-facing quad", "Facing rod", "Disc / ring", "Cylinder / cone", "Sphere", "Box"];

    /// <summary>Adds a material (image when <paramref name="texture"/> is set, else grey colour). Returns the material index.</summary>
    public static (VfxFile File, int Material) AddMaterial(VfxFile file, string? texture, bool additive, bool fullbright)
    {
        int index = MaterialCount(file);
        var material = string.IsNullOrWhiteSpace(texture)
            ? VfxBuilder.ColorMaterial(null, additive)
            : VfxBuilder.ImageMaterial(texture.Trim(), additive, fullbright ? 1f : 0f);
        return (VfxEdit.AddSection(file, material), index);
    }

    public static VfxMesh BuildPrimitive(VfxPrimitiveRequest r, int material) => r.Kind switch
    {
        VfxPrimitiveKind.Plane => VfxPrimitives.Plane(r.Name, r.SizeA, r.SizeB, material),
        VfxPrimitiveKind.FacingQuad => VfxPrimitives.FacingQuad(r.Name, r.SizeA, material),
        VfxPrimitiveKind.FacingRod => VfxPrimitives.FacingRod(r.Name, r.SizeA, r.SizeB, null, material),
        VfxPrimitiveKind.Disc => VfxPrimitives.Disc(r.Name, r.SizeA, r.SizeB, Math.Max(3, r.Segments), material),
        VfxPrimitiveKind.Cylinder => VfxPrimitives.Cylinder(r.Name, r.SizeA, r.SizeB, r.Height, Math.Max(3, r.Segments), material),
        VfxPrimitiveKind.Sphere => VfxPrimitives.Sphere(r.Name, r.SizeA, Math.Max(3, r.Segments), Math.Max(2, r.Segments / 2), material),
        _ => VfxPrimitives.Box(r.Name, new Vector3(r.SizeA, r.Height, r.SizeB), material),
    };

    /// <summary>Adds a primitive mesh (and its material when none is chosen). Returns the new file and the mesh's section index.</summary>
    public static (VfxFile File, int Section) AddPrimitive(VfxFile file, VfxPrimitiveRequest r)
    {
        int material = r.ExistingMaterial;
        if (material < 0 || material >= MaterialCount(file)) (file, material) = AddMaterial(file, r.Texture, r.Additive, r.Fullbright);
        var name = VfxEdit.UniqueName(file, string.IsNullOrWhiteSpace(r.Name) ? r.Kind.ToString() : r.Name.Trim());
        file = VfxEdit.AddSection(file, BuildPrimitive(r with { Name = name }, material));
        int index = VfxEdit.FindByName(file, name);
        int frames = r.Frames > 0 ? r.Frames : EffectFrames(file);
        if (frames > 1) file = VfxEdit.SetFrameCount(file, index, frames);
        return (Lengthen(file, frames), index);
    }

    /// <summary>Adds a particle system, dummy, light or spacewarp with stock-like defaults, lasting the effect length.</summary>
    public static (VfxFile File, int Section) AddObject(VfxFile file, string kind)
    {
        int frames = EffectFrames(file);
        if (kind == "Particle system" && MaterialCount(file) == 0) file = AddMaterial(file, null, true, true).File;
        string name = VfxEdit.UniqueName(file, kind switch { "Particle system" => "Particles", _ => kind });
        VfxSection section = kind switch
        {
            // births happen only while 0 < frame < frame count: span the effect
            "Particle system" => VfxBuilder.ParticleSystem(name, MaterialCount(file) - 1, Math.Max(frames, 30)),
            "Dummy" => VfxBuilder.Dummy(name, frames),
            "Light" => VfxBuilder.Light(name, frames),
            _ => VfxBuilder.Spacewarp(name, 0, frames),
        };
        file = VfxEdit.AddSection(file, section);
        return (Lengthen(file, frames), VfxEdit.FindByName(file, name));
    }

    /// <summary>A glTF without materials gives meshes that point at material 0: add a grey colour material for them.</summary>
    private static VfxFile Lengthen(VfxFile file, int frames) =>
        frames - 1 > file.EndFrame ? VfxEdit.SetEndFrame(file, frames - 1) : file;

    /// <summary>Ready-made starting points.</summary>
    public static string[] TemplateNames { get; } = ["Additive flash", "Scrolling beam", "Ring shockwave", "Particle fountain"];

    /// <summary>Builds a template effect (texture names are placeholders the user replaces).</summary>
    public static VfxFile Template(string name, string texture = "cairn_placeholder.tga")
    {
        var file = VfxBuilder.NewFile();
        switch (name)
        {
            case "Additive flash":
            {
                (file, int i) = AddPrimitive(file, new(VfxPrimitiveKind.FacingQuad, "Flash", 2, Texture: texture, Frames: 15));
                file = VfxEdit.SetFacingSize(file, i, new Vector2(4, 4), 14);
                file = VfxEdit.FillTrack(file, VfxCreation.MaterialSection(file, 0), VfxMaterialTrack.Opacity, 15, [(0, 1f), (14, 0f)]);
                break;
            }
            case "Scrolling beam":
            {
                (file, int i) = AddPrimitive(file, new(VfxPrimitiveKind.FacingRod, "Beam", 0.3f, 6, Texture: texture, Frames: 30));
                file = VfxEdit.GenerateUvScroll(file, i, new Vector2(0, 0.05f));
                break;
            }
            case "Ring shockwave":
            {
                (file, int i) = AddPrimitive(file, new(VfxPrimitiveKind.Disc, "Shockwave", 1, 0.5f, Segments: 32, Texture: texture, Frames: 20));
                // Grows from 1x to 6x with an ease-out (fast at first, slowing down), stored as keyframes.
                var start = ((VfxMesh)file.Sections[i]).Frames[0].Transform!;
                for (int k = 0; k < 20; k++)
                {
                    float u = k / 19f, s = 1 + 5 * (1 - (1 - u) * (1 - u));
                    file = VfxEdit.SetStaticTransform(file, i, start with { Scale = new Vector3(s, s, s) }, k);
                }
                file = VfxEdit.ToKeyframes(file, i);
                file = VfxEdit.FillTrack(file, VfxCreation.MaterialSection(file, 0), VfxMaterialTrack.Opacity, 20, [(0, 1f), (19, 0f)]);
                break;
            }
            default:
                file = AddMaterial(file, texture, true, true).File;
                file = VfxEdit.SetEndFrame(file, 59);
                (file, int p) = AddObject(file, "Particle system");
                // The rectangle emitter fires along local -Y: turn it over so the spray goes up.
                var up = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
                // 40 particles/s (birth rate is per 1/4800 s tick), 4 m/s up under gravity (apex ~0.8 m), 1.2 s life.
                if (file.Sections[p] is VfxParticleSystem ps)
                    file = file with { Sections = file.Sections.SetItem(p, ps with
                    {
                        Flags = 0x2 | 0x10, ParticleCount = 80, Lifetime = 5760,
                        Frames = [.. ps.Frames.Select(f => f with { Orientation = up, Speed = 4, SpeedVariation = 1f, BirthRate = 40f / Cairn.Vfx.Animation.VfxTime.TicksPerSecond, Width = 0.3f, Height = 0.3f, DropSize = 0.08f })],
                    }) };
                break;
        }
        return file;
    }

    /// <summary>Section index of material number <paramref name="material"/>.</summary>
    public static int MaterialSection(VfxFile file, int material) => VfxEdit.MaterialSectionIndex(file, material);
}
