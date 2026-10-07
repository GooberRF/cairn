using Cairn.Rfa.Docs;

namespace Cairn.Vfx.Docs;

/// <summary>
/// The field-by-field reference for VFX (VSFX) effect files, in the same records as the RFA/V3C
/// reference (<see cref="FormatReference"/>, <see cref="FormatSection"/>, <see cref="FormatField"/>,
/// <see cref="FormatTopic"/>) so the same Help page builder renders it. Ids are <c>vfx.section.field</c>.
/// </summary>
public static class VfxFormatDocs
{
    private const string Fmt = "VFX";

    /// <summary>The VFX reference: header and every section type.</summary>
    public static FormatReference Reference { get; } = Build();

    /// <summary>Help topics: time units, versions, playback, flags.</summary>
    public static ImmutableArray<FormatTopic> Topics { get; } = BuildTopics();

    private static readonly ImmutableDictionary<string, FormatField> ById =
        Reference.Fields.ToImmutableDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The field ids the effect inspector attaches tooltips to; every one exists.</summary>
    public static ImmutableArray<string> InspectorIds { get; } =
    [
        "vfx.header.version", "vfx.header.end_frame", "vfx.header.flags",
        "vfx.object.name", "vfx.object.parent", "vfx.object.save_parent",
        "vfx.mesh.num_vertices", "vfx.mesh.flags", "vfx.mesh.fps", "vfx.mesh.start_time", "vfx.mesh.end_time",
        "vfx.mesh.num_frames", "vfx.mesh.material_indices", "vfx.mesh.is_keyframed", "vfx.mesh.pivot",
        "vfx.face.vertices", "vfx.face.material", "vfx.face.face_vertices",
        "vfx.material.type", "vfx.material.additive", "vfx.material.texture", "vfx.material.fps",
        "vfx.material.self_illumination", "vfx.material.opacity", "vfx.material.solid_color",
        "vfx.particle.flags", "vfx.particle.warps", "vfx.particle.material", "vfx.particle.count",
        "vfx.particle.lifetime", "vfx.particle.emitter_type", "vfx.particle.frames",
        "vfx.spacewarp.type", "vfx.light.params", "vfx.dummy.frames",
    ];

    /// <summary>The field with this id (ignoring case), or null.</summary>
    public static FormatField? Find(string id) => id is not null && ById.TryGetValue(id, out var f) ? f : null;

    /// <summary>The section with this id (ignoring case), or null.</summary>
    public static FormatSection? FindSection(string id) =>
        Reference.Sections.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The topic with this id (ignoring case), or null.</summary>
    public static FormatTopic? FindTopic(string id) =>
        Topics.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private static FormatSection Section(string id, string title, string summary, params FormatField[] fields) =>
        new(id, Fmt, title, summary, [.. fields]);

    private static FormatField F(string section, string id, string name, string type, string? units, string summary,
        string engine, string? limits = null, bool? read = true) =>
        new(id, Fmt, section, name, type, units, summary, engine, limits, read);

    private static FormatReference Build()
    {
        const string h = "vfx.header", o = "vfx.object", m = "vfx.mesh", fc = "vfx.face", fv = "vfx.face_vertex",
            fr = "vfx.mesh_frame", mt = "vfx.material", p = "vfx.particle", d = "vfx.dummy", l = "vfx.light", w = "vfx.spacewarp";
        return new FormatReference(Fmt, "VFX effect files",
            "A VFX file holds a small animated scene: meshes, materials, particle systems, dummies, lights and spacewarps, each stored as a tagged section after a header. "
            + "The game plays it at 15 frames per second, either once (explosions, hits) or looping (thrusters, items, clutter); the referencing table decides which.",
        [
            Section(h, "Header", "The file starts with the VSFX signature, the version, a few independent values and a list of totals. Cairn recomputes the totals on save.",
                F(h, "vfx.header.version", "Version", "int32", null, "The format version. Stock files use 0x30008 to 0x30012 and 0x40006.", "The game loads 0x30000-0x40006 but refuses 0x40000-0x40004.", "0x30000-0x40006, not 0x40000-0x40004"),
                F(h, "vfx.header.flags", "Flags", "int32", null, "Header flags, present from 0x30008. Zero in every stock file.", "No meaning is known."),
                F(h, "vfx.header.end_frame", "End frame", "int32", "frames (1/15 s)", "When the effect ends.", "A one-shot effect stops drawing meshes here (particles finish their lives); a looping effect wraps back to 0. Usually the end of the longest mesh."),
                F(h, "vfx.header.totals", "Totals", "derived", null, "Counts of sections, faces, frames and keys across the file, used by the game to size its buffers.", "Must match the sections exactly; Cairn rewrites them on every save.")),
            Section(o, "Object names", "Meshes, dummies, lights, particle systems and spacewarps share a name and a parent.",
                F(o, "vfx.object.name", "Name", "string", null, "The object's name.", "Used to find spacewarps and parents; should be unique."),
                F(o, "vfx.object.parent", "Parent", "string", null, "The object this one was linked to in the authoring tool, \"Scene Root\" when none. It may name a bone or prop point of the host model.", "The game does not apply this hierarchy inside the effect: positions are already baked."),
                F(o, "vfx.object.save_parent", "Save parent", "uint8", null, "Exporter flag; always 0 in stock files. Spacewarps do not have it.", "Not used.", read: false)),
            Section(m, "Mesh section (SFXO)", "A mesh: faces, face-vertex records, material slots, frames and optional keys.",
                F(m, "vfx.mesh.num_vertices", "Vertex count", "int32", null, "How many vertices each frame's position block holds.", "Face corners and face-vertex records index into it; an index beyond it crashes the game."),
                F(m, "vfx.mesh.flags", "Flags", "uint32", null, "Render flags: see the mesh flags topic.", "Facing and rod meshes turn to the camera; morph meshes store positions in every frame; fullbright ignores lighting."),
                F(m, "vfx.mesh.fps", "Frame rate", "int32", "frames per second", "The rate the mesh's own frames advance at (15 in most stock meshes). Present from 0x30009.", "Local frame = (effect time - start time) x fps."),
                F(m, "vfx.mesh.start_time", "Start time", "float", "seconds", "When the mesh appears (from 0x40004; older files store a start frame).", "Before it the mesh is hidden."),
                F(m, "vfx.mesh.end_time", "End time", "float", "seconds", "The time of the last frame (from 0x40004; older files store an end frame).", "Frame count = floor((end - start) x fps) + 1."),
                F(m, "vfx.mesh.num_frames", "Frame count", "int32", null, "How many frame records follow.", "After the last frame the mesh is hidden; the last record is held until then."),
                F(m, "vfx.mesh.material_indices", "Material slots", "int32[]", null, "From 0x40000: each slot names a material section. Older files store the materials inline.", "Faces pick a slot by index."),
                F(m, "vfx.mesh.is_keyframed", "Keyframed", "uint8", null, "Whether the mesh moves by TCB/Bezier keys instead of a transform per frame. Morph meshes never are.", "Keys are sampled at 4800 ticks per second."),
                F(m, "vfx.mesh.pivot", "Pivot", "transform", null, "Pivot offset applied with the keys (from 0x3000A).", "Combined with the keyed transform.")),
            Section(fc, "Face", "A triangle of a mesh.",
                F(fc, "vfx.face.vertices", "Vertices", "int32[3]", null, "The three vertex indices.", "Must be below the vertex count."),
                F(fc, "vfx.face.material", "Material slot", "int32", null, "Which material slot of the mesh the face uses.", "Must be below the slot count."),
                F(fc, "vfx.face.face_vertices", "Face-vertex records", "int32[3]", null, "The face-vertex record of each corner (it repeats the vertex index).", "A missing record crashes the game."),
                F(fc, "vfx.face.normal", "Normal, centre, radius", "float[7]", null, "Precomputed face plane and bounds.", "Used for lighting and culling.")),
            Section(fv, "Face-vertex record", "Per-corner record used for smoothing.",
                F(fv, "vfx.face_vertex.vertex", "Vertex", "int32", null, "The vertex this record belongs to.", "Must be below the vertex count."),
                F(fv, "vfx.face_vertex.adjacent", "Adjacent faces", "int32[]", null, "Every face that uses this record, in ascending order.", "Used to average normals.")),
            Section(fr, "Mesh frame", "One frame of a mesh. Which parts are present depends on the version, the flags and whether the mesh is keyframed.",
                F(fr, "vfx.mesh_frame.positions", "Positions", "int16[3] x n", null, "Quantised positions: centre + raw x multiplier per axis. In frame 0 always, every frame for morph meshes.", "Interpolated linearly between frames unless no-interp is set."),
                F(fr, "vfx.mesh_frame.facing_size", "Facing size", "float[2]", "metres", "Width and height of a facing or rod mesh.", "Interpolated per frame."),
                F(fr, "vfx.mesh_frame.uvs", "UVs", "float[2] x 3 per face", null, "Texture coordinates, in frame 0 and in every frame with the dump-UVs flag.", "Animates UVs per frame."),
                F(fr, "vfx.mesh_frame.transform", "Transform", "TRS", null, "Translation, rotation, scale for non-morph meshes that are not keyframed.", "Interpolated between frames."),
                F(fr, "vfx.mesh_frame.opacity", "Opacity", "float", "0-1", "Per-frame opacity before 0x40005; later files keep opacity in the material.", "Multiplies the material alpha.")),
            Section(mt, "Material section (MATL)", "From 0x40000 materials are separate sections shared by mesh slots and particle systems.",
                F(mt, "vfx.material.type", "Type", "int32", null, "0 image, 1 vmix (two textures blended over time), 2 solid colour.", "Stock files use image and solid colour only."),
                F(mt, "vfx.material.additive", "Additive", "uint8", null, "Additive blending instead of alpha blending.", "Additive materials brighten what is behind them."),
                F(mt, "vfx.material.texture", "Texture", "string + start frame + rate + anim type", null, "A .tga or .vbm name; $original_map / $original_map_rgb stand for the host object's texture.", "Animated .vbm textures start at the start frame and play at the rate; anim type 2 plays once."),
                F(mt, "vfx.material.fps", "Frame rate", "int32", "frames per second", "Rate of the self-illumination, opacity and mix tracks (from 0x40003).", "Tracks shorter than the effect hold their last value."),
                F(mt, "vfx.material.self_illumination", "Self-illumination", "float[]", "0-1", "Per-frame self-illumination.", "1 renders at full brightness."),
                F(mt, "vfx.material.opacity", "Opacity", "float[]", "0-1", "Per-frame opacity (from 0x40005).", "Fades the material."),
                F(mt, "vfx.material.solid_color", "Solid colour", "int32[3]", "0-255", "Colour of a solid-colour material.", "Used instead of a texture.")),
            Section(p, "Particle system (PART)", "An emitter with per-frame emission parameters.",
                F(p, "vfx.particle.flags", "Flags", "uint32", null, "Behaviour bits (from 0x30010); 0x100 marks a drops system.", "Drops render as coloured streaks."),
                F(p, "vfx.particle.warps", "Spacewarps", "string[]", null, "Spacewarps (by name) that push the particles.", "A name not in the file binds nothing."),
                F(p, "vfx.particle.material", "Material", "int32", null, "Material section index (from 0x40000; older files inline it).", "Must name an existing material."),
                F(p, "vfx.particle.count", "Particle count", "int32", null, "Maximum live particles.", "Stock effects use 2-100.", "2-100 in stock effects"),
                F(p, "vfx.particle.lifetime", "Lifetime", "int32", "ticks (1/4800 s)", "How long each particle lives, with a random variation.", "Stock effects use 1600-32000."),
                F(p, "vfx.particle.emitter_type", "Emitter type", "int32", null, "0 point, 1 area (stock values).", "Picks where particles are born."),
                F(p, "vfx.particle.frames", "Frames", "record x n", "frames (1/15 s)", "Per frame: position, orientation, width, height, drop size, speed, speed variation, birth rate.", "Interpolated; after a one-shot effect ends particles stop being born but live out their lives.")),
            Section(d, "Dummy (DMMY)", "A named point with a position per frame, used as an attachment marker.",
                F(d, "vfx.dummy.frames", "Frames", "(float[3], quat) x n", null, "Position and orientation per frame.", "Not drawn.")),
            Section(l, "Light (ALGT)", "A dynamic light with parameters per frame.",
                F(l, "vfx.light.params", "Parameters", "position, radius, multiplier, colour, on", null, "Initial values, then one set per frame.", "Lights the level around the effect.")),
            Section(w, "Spacewarp (WARP)", "A force field that particle systems can name.",
                F(w, "vfx.spacewarp.type", "Type", "int32", null, "The kind of force (0 and 1 in stock files).", "Applied to particles of systems that name it."),
                F(w, "vfx.spacewarp.frames", "Frames", "record x n", null, "Position, orientation, strength, decay, turbulence, frequency, scale per frame.", "Interpolated per frame.")),
        ]);
    }

    private static ImmutableArray<FormatTopic> BuildTopics() =>
    [
        new("vfx.time", "Time units",
            "The effect clock runs in frames of 1/15 second; the header end frame uses it.\n\n"
            + "Each mesh has its own frame rate: its local frame is (effect time - start time) x fps. Keys use ticks of 1/4800 second, so one 15 fps frame is 320 ticks.\n\n"
            + "Particle lifetimes are in ticks as well."),
        new("vfx.playback", "Playback",
            "Whether an effect loops is decided by whoever plays it, not by the file. Explosions, hits, tracers, sparks and warm-up effects play once; thrusters, cockpit effects, items, clutter and projectile models loop.\n\n"
            + "A mesh is visible only between its start time and its last frame. Between frames positions, sizes and transforms are interpolated linearly (unless no-interp is set); keyed meshes use their TCB or Bezier keys.\n\n"
            + "When a one-shot effect reaches the end frame its meshes disappear, particle systems stop emitting and the effect is removed once the last particle dies. A looping effect wraps to frame 0."),
        new("vfx.versions", "Versions",
            "0x30008 adds header flags; 0x30009 per-mesh frame rates and keys; 0x3000A pivots and compressed positions; 0x3000C inclusive frame ranges; 0x3000D per-frame UVs; 0x3000F selection sets; 0x30010 particle flags; 0x30012 texture animation.\n\n"
            + "0x40000 moves materials into their own sections; 0x40003 adds material frame rates; 0x40004 stores mesh times in seconds (the game refuses 0x40000-0x40004); 0x40005 moves opacity into materials; 0x40006 is the current version.\n\n"
            + "Cairn reads every version and saves 0x40006."),
        new("vfx.mesh_flags", "Mesh flags",
            "0x1 facing (turns to the camera), 0x2 no interpolation, 0x4 morph (positions every frame), 0x8 fire, 0x10 fullbright, 0x20 see-through, 0x40 corona, 0x80 sky, 0x100 UVs every frame, 0x800 facing rod (turns about its up axis).\n\n"
            + "Stock effects use facing, morph, fullbright, UVs every frame and facing rod."),
        new("vfx.unexercised", "Features stock effects do not use",
            "vmix materials, chains, cameras, selection sets and material modifiers parse but no stock effect uses them, so the preview of these is approximate."),
    ];
}
