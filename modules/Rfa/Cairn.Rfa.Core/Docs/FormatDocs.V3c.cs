namespace Cairn.Rfa.Docs;

public static partial class FormatDocs
{
    private static FormatReference BuildV3c()
    {
        const string F = "V3C";

        var header = new SectionBuilder(F, "v3c.header", "Header",
            "The fixed 40 bytes at the start of every .v3c / .v3m file. Counts here are stored as written; Cairn "
            + "never recomputes them on a plain save.")
            .Field("v3c.signature", "Signature", "int32", null,
                "\"RFCM\" (0x5246434D) for a character mesh (.v3c, spelt .vcm in the tables) or \"RF3D\" (0x52463344) for "
                + "a static mesh (.v3m, spelt .v3d).",
                "Selects character or static loading; only characters have bones, collision spheres and clips.",
                "One of the two values.", true, "0x00")
            .Field("v3c.version", "Version", "int32", null,
                "Format version, 0x40000 in every known file.",
                "The only version the engine loads.", "0x40000.", true, "0x04")
            .Field("v3c.num_submeshes", "Submesh count", "int32", null,
                "Declared number of SUBM sections.",
                "Matches the SUBM sections that follow.", null, null, "0x08")
            .Field("v3c.num_all_vertices", "Total vertices", "int32", null,
                "Reset to 0 by the ccrunch tool in game files.", "Not needed by the game.", null, null, "0x0C")
            .Field("v3c.num_all_triangles", "Total triangles", "int32", null,
                "Reset to 0 by ccrunch in game files.", "Not needed by the game.", null, null, "0x10")
            .Field("v3c.unknown0", "Unknown 0", "int32", null,
                "Reset to 0 by ccrunch (a normals count in some notes).", "No known use.", null, null, "0x14")
            .Field("v3c.num_all_materials", "Total materials", "int32", null,
                "Sum of every submesh's material count.", "Sizes the mesh's material table.", null, null, "0x18")
            .Field("v3c.unknown1", "Unknown 1", "int32", null,
                "0 in game files.", "No known use.", null, null, "0x1C")
            .Field("v3c.unknown2", "Unknown 2", "int32", null,
                "0 in game files (a dumb-section count before ccrunch).", "No known use.", null, null, "0x20")
            .Field("v3c.num_colspheres", "Collision sphere count", "int32", null,
                "Declared number of CSPH sections.", "Matches the CSPH sections that follow.", null, null, "0x24");

        var section = new SectionBuilder(F, "v3c.section", "Section header",
            "After the header the file is a list of sections, each {int32 type; int32 size; body}, ended by type 0 (END).")
            .Field("v3c.section.type", "Section type", "int32", null,
                "SUBM 0x5355424D (submesh), CSPH 0x43535048 (collision sphere), BONE 0x424F4E45 (reads \"ENOB\" on disk), "
                + "DUMB 0x44554D42 (exporter groups, removed by ccrunch), 0 = END.",
                "Unknown section types are kept byte for byte by Cairn.", null, true, "+0x00")
            .Field("v3c.section.size", "Section size", "int32", "bytes",
                "Size of the body after this header. 0 for every SUBM after ccrunch, so a submesh must be fully parsed "
                + "to find its end. CSPH is 44, BONE 4 + 56 per bone, END 0 in stock files.",
                "Unusable for SUBM.", null, null, "+0x04");

        var submesh = new SectionBuilder(F, "v3c.submesh", "Submesh (SUBM)",
            "One exported object: names, LOD distances, bounds, then its LODs, materials and a trailing list. The game "
            + "renders every submesh of a mesh.")
            .Field("v3c.submesh.name", "Name", "char[24]", null,
                "The object's name in the exporter.", "Identifies the submesh.", "23 characters plus the terminator.", null)
            .Field("v3c.submesh.parent_name", "Parent name", "char[24]", null,
                "\"None\", or the 3ds Max group name.", "No known use in game.", "23 characters plus the terminator.", null)
            .Field("v3c.submesh.version", "Submesh version", "int32", null,
                "7 in every known file.", "Values below 7 do not work.", "7.", true)
            .Field("v3c.submesh.num_lods", "LOD count", "int32", null,
                "Number of levels of detail.",
                "The engine's VifLodMesh holds at most 3 levels. Alpine Faction keeps that limit (1-3 levels); it only "
                + "scales the switch distances by r_lodscale (default 10) and can force LOD 0 for multiplayer characters.", "1 to 3.", true)
            .Field("v3c.submesh.lod_distances", "LOD distances", "float[num_lods]", "metres",
                "Camera distance at which each LOD starts, ascending (e.g. 0, 10, 100).",
                "LOD i is used from distance[i]; Alpine Faction divides the apparent distance by r_lodscale (default 10), "
                + "so levels switch ten times farther than authored.", "Ascending; the first is normally 0.", true)
            .Field("v3c.submesh.offset", "Offset", "float[3]", "metres",
                "The submesh's bounding-sphere centre / offset.", "Used for culling and LOD distance.", null, true)
            .Field("v3c.submesh.radius", "Bounding radius", "float", "metres",
                "Radius of the bounding sphere.", "Used for culling; shared by every LOD.", null, true)
            .Field("v3c.submesh.aabb_min", "Bounding box min", "float[3]", "metres",
                "Minimum corner of the axis-aligned bounding box.", "Shared by every LOD.", null, true)
            .Field("v3c.submesh.aabb_max", "Bounding box max", "float[3]", "metres",
                "Maximum corner of the axis-aligned bounding box.", "Shared by every LOD.", null, true)
            .Field("v3c.submesh.num_materials", "Material count", "int32", null,
                "Number of 84-byte materials after the LODs.", "Sizes the material list.", null, true)
            .Field("v3c.submesh.trailer", "Trailing list", "int32 + {char[24], float}[]", null,
                "A count (1 in game files) and entries of a 24-byte name (usually the submesh name) and a float (0).",
                "No known use.", null, null);

        var lod = new SectionBuilder(F, "v3c.lod", "Level of detail",
            "One LOD of a submesh: flags, vertex count, the geometry data block (batch headers, batch streams, prop "
            + "points), the batch info table and the texture list. Most detailed first.")
            .Field("v3c.lod.flags", "Flags", "uint32", "bitfield",
                "0x01 batches carry a morph map (characters); 0x02 character; 0x04 reflective materials; 0x10 collide with "
                + "the most detailed LOD instead of the least; 0x20 batches carry triangle planes (static meshes).",
                "0x01 also decides whether RFA morph indices go through the morph map. 0x10 is read off the last LOD by the "
                + "collision code.", null, true)
            .Field("v3c.lod.num_vertices", "Vertex count", "uint32", null,
                "The LOD's ORIGINAL vertex count, before splitting into batches; also the length of every batch's morph "
                + "map. Not the sum of batch vertex counts in most stock files.",
                "Bounds RFA morph indices (Alpine Faction refuses a clip whose largest index is not below LOD 0's count).",
                null, true)
            .Field("v3c.lod.num_batches", "Batch count", "uint16", null,
                "Number of geometry batches (one draw call and one texture each).", "Sizes the batch tables.", "0..65535.", true)
            .Field("v3c.lod.data_size", "Data size", "uint32", "bytes",
                "Size of the geometry data block: batch headers, then each batch's streams, then the prop points, each "
                + "part aligned to 16 bytes relative to the block start.", "Read as one block.", null, true)
            .Field("v3c.lod.unknown1", "Unknown", "int32", null,
                "-1 in most files, 0 in some.", "No known use.", null, null)
            .Field("v3c.lod.num_prop_points", "Prop point count", "uint32", null,
                "Number of prop points at the end of the data block.", "Sizes the prop point list.", null, true)
            .Field("v3c.lod.textures", "Textures", "uint32 + {uint8, char[]}[]", null,
                "The LOD's texture list: a count, then per texture a material index and a zero-terminated name (a copy of "
                + "that material's diffuse map name). Batch texture indices point into this list.",
                "Textures resolve by name through the game's file system (with the .dds/.png/.jpg supersede chain).",
                "At most 7 textures per LOD.", true);

        var batch = new SectionBuilder(F, "v3c.batch", "Batch",
            "One draw call with one texture. Its 0x38-byte header sits in the LOD data block, its counts and stream sizes "
            + "in the batch info table after the block, and its streams in the block, each aligned to 16 bytes.")
            .Field("v3c.batch.header_reserved", "Header reserved bytes", "char[0x20] + char[0x14]", null,
                "Header bytes 0x00-0x1F and 0x24-0x37. Stock files hold leftovers here.",
                "Overwritten in memory by the engine; kept as stored.", null, false)
            .Field("v3c.batch.texture_index", "Texture index", "int32", null,
                "Index into the LOD's texture list.", "Selects the batch's texture.", "Below the LOD's texture count.", true)
            .Field("v3c.batch.num_vertices", "Vertex count", "uint16", null,
                "Vertices in this batch (batch info).", "Sizes the vertex streams.", "0..65535 (16-bit).", true)
            .Field("v3c.batch.num_triangles", "Triangle count", "uint16", null,
                "Triangles in this batch (batch info).", "Sizes the triangle stream.", "0..65535 (16-bit).", true)
            .Field("v3c.batch.stream_sizes", "Stream sizes", "uint16[5]", "bytes",
                "Positions, triangles, same-position offsets, bone links and UV allocation sizes (batch info). Stock files "
                + "round positions, UVs and bone links up to 16 bytes and pad the same-position offsets; normals reuse the "
                + "position size. A bone-link size of 0 means no bone links.",
                "Locate the streams inside the data block.", "Each at least what its element count needs; 16-bit.", true)
            .Field("v3c.batch.render_flags", "Render flags", "uint32", "bitfield",
                "Render state flags from the batch info (e.g. 0x518C41 normal, 0x110C21 additive).",
                "Selects the batch's render mode.", null, true)
            .Field("v3c.batch.positions", "Positions", "float[3][num_vertices]", "metres, submesh space",
                "Vertex positions.", "Skinned by the bone links on characters; replaced by morph positions on LOD 0.", null, true)
            .Field("v3c.batch.normals", "Normals", "float[3][num_vertices]", null,
                "Vertex normals.", "Used for lighting (most stock meshes render unlit).", null, true)
            .Field("v3c.batch.tex_coords", "UVs", "float[2][num_vertices]", null,
                "Diffuse texture coordinates.", "Map the batch's texture.", null, true)
            .Field("v3c.batch.triangles", "Triangles", "{uint16[3], uint16 flags}[num_triangles]", null,
                "Three vertex indices into the batch and a flags word (0x20 = double-sided).",
                "0x20 disables back-face culling for that triangle.", null, true)
            .Field("v3c.batch.planes", "Triangle planes", "{float[3], float}[num_triangles]", null,
                "One plane per triangle, present when the LOD has flag 0x20 (static meshes).",
                "Back-face culling.", null, true)
            .Field("v3c.batch.same_pos_offsets", "Same-position offsets", "int16[num_vertices]", null,
                "When positive, this vertex has the same position as the vertex that many places earlier.",
                "Used by clipping, and by the morph apply to copy a morphed position to duplicate vertices.", null, true)
            .Field("v3c.batch.bone_links", "Bone links", "{uint8[4] weights, uint8[4] bones}[num_vertices]", null,
                "Up to four bone influences per vertex: weights 0..255 and bone indices (0xFF = unused slot).",
                "Skinning; weights sum to 255 in stock characters.",
                "4 influences per vertex, weights summing to 255.", true)
            .Field("v3c.batch.morph_map", "Morph map", "int16[lod num_vertices]", null,
                "Present when the LOD has flag 0x01: maps an RFA morph vertex index (original numbering) to a vertex of "
                + "this batch.", "Used by the morph apply on LOD 0.", null, true);

        var material = new SectionBuilder(F, "v3c.material", "Material",
            "84 bytes per material, after a submesh's LODs.")
            .Field("v3c.material.diffuse_map", "Diffuse map", "char[32]", null,
                "Texture file name.", "Resolved through the game's file system (supersede chain .dds, .png, .jpg, .jpeg first).",
                "31 characters plus the terminator.", true)
            .Field("v3c.material.emissive", "Emissive", "float", "0..1",
                "Self-illumination.", "Maxed with the ambient light; 1 renders the material full bright.", "0 to 1.", true)
            .Field("v3c.material.unknown", "Unknown", "float[2]", null,
                "0 in game files (specular level and glossiness in some notes).", "Not used by the engine.", null, false)
            .Field("v3c.material.ref_coefficient", "Reflection coefficient", "float", "0..1",
                "Reflection strength.", "Not used by the engine.", null, false)
            .Field("v3c.material.ref_map", "Reflection map", "char[32]", null,
                "Reflection texture name (non-empty when the coefficient is above 0).", "Not used by the engine.", null, false)
            .Field("v3c.material.flags", "Flags", "uint32", "bitfield",
                "Values 0x1, 0x9, 0x11, 0x19 seen in game files.", "Not used by the engine.", null, false);

        var prop = new SectionBuilder(F, "v3c.prop", "Prop point",
            "0x64 bytes each, at the end of a LOD's data block: a named attachment point the game code looks up by name "
            + "(muzzle flashes, thrusters, held weapons).")
            .Field("v3c.prop.name", "Name", "char[0x44]", null,
                "Zero-terminated name, e.g. \"muzzle_1\". Many stock names keep leftover bytes after the terminator.",
                "Game code finds the point by this name.", "67 characters plus the terminator.", true)
            .Field("v3c.prop.rotation", "Rotation", "float[4]", "quaternion (x y z w)",
                "Orientation of the point.", "Orients attached props and effects.", null, true)
            .Field("v3c.prop.position", "Position", "float[3]", "metres",
                "Position of the point (model space for static meshes).", "Places attached props and effects.", null, true)
            .Field("v3c.prop.parent", "Parent bone", "int32", null,
                "Bone index the point follows, or -1.", "Moves the point with that bone.", "-1 or a valid bone index.", true);

        var csphere = new SectionBuilder(F, "v3c.csphere", "Collision sphere (CSPH)",
            "One section per sphere: the hit and collision volumes of a character, each following a bone.")
            .Field("v3c.csphere.name", "Name", "char[24]", null,
                "Sphere name, e.g. \"head\".", "Referred to by name, e.g. by entity.tbl's $Collision Sphere: lines.",
                "23 characters plus the terminator.", true)
            .Field("v3c.csphere.bone", "Bone", "int32", null,
                "Bone index the sphere follows, or -1.", "Moves the sphere with that bone's animated transform.",
                "-1 or a valid bone index.", true)
            .Field("v3c.csphere.position", "Position", "float[3]", "metres, bone space",
                "Centre relative to the bone.", "Placed in the bone's frame each frame.", null, true)
            .Field("v3c.csphere.radius", "Radius", "float", "metres",
                "Sphere radius.", "Collision and hit detection.", "Greater than 0.", true);

        var bone = new SectionBuilder(F, "v3c.bone", "Bone (BONE)",
            "The skeleton: a count, then 56 bytes per bone. Bone ORDER is the contract with every clip, which addresses "
            + "bones by index only.")
            .Field("v3c.bone.num_bones", "Bone count", "int32", null,
                "Number of bones.", "Every clip played on the mesh must have exactly this many tracks, in this order.",
                "At most 50.", true)
            .Field("v3c.bone.name", "Name", "char[24]", null,
                "Bone name.", "Used by game code that looks bones up by name; clips never use names.",
                "23 characters plus the terminator.", true)
            .Field("v3c.bone.rotation", "Rotation", "float[4]", "quaternion (x y z w)",
                "The INVERSE bind rotation (model space to bone space), stored as the conjugate of the active quaternion, "
                + "the same conjugation clips use.",
                "Skinning only: the rendered pose comes from the clips. normalize(rotation) is the bone-to-model rotation "
                + "in the bind pose.", null, true)
            .Field("v3c.bone.position", "Position", "float[3]", "metres",
                "The inverse bind translation (model to bone). The bone origin in model space is rotate(bind rotation, -position).",
                "Skinning only; bone lengths at runtime come from the clips' position keys.", null, true)
            .Field("v3c.bone.parent", "Parent", "int32", null,
                "Parent bone index, -1 for the root. A parent may come AFTER its child in index order.",
                "Forward kinematics composes each bone with its parent.", "-1 or a valid bone index; no cycles.", true);

        return new FormatReference(F, "V3C character mesh",
            "A .v3c (spelt .vcm in the tables) is a character mesh: submeshes with 1-3 LODs of batched geometry, "
            + "materials, prop points, collision spheres and a skeleton of at most 50 bones. A .v3m (spelt .v3d) is a "
            + "static mesh with the same submesh layout and no bones or spheres. Fixed-size names hold one byte fewer "
            + "characters than their size (the terminator); limits the engine enforces: 50 bones, 24-byte bone and "
            + "sphere names, 32-byte texture names, 7 textures per LOD, 16-bit vertex and triangle counts per batch, "
            + "4 bone weights per vertex summing to 255, 3 LODs.",
            [header.Build(), section.Build(), submesh.Build(), lod.Build(), batch.Build(), material.Build(),
             prop.Build(), csphere.Build(), bone.Build()]);
    }
}
