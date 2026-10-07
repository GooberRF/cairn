using System.Collections.Immutable;

namespace Cairn.Rfa.Docs;

public static partial class FormatDocs
{
    private const string Ticks = "ticks (1/4800 s)";

    private static FormatReference BuildRfa()
    {
        const string F = "RFA";

        var header = new SectionBuilder(F, "rfa.header", "Header",
            "The fixed 0x50 bytes at the start of every clip: version, timing, counts, ramps and the offsets of the "
            + "morph data. Little-endian, packed. The game uses the whole file image in place once loaded.")
            .Field("rfa.signature", "Signature", "char[4]", null,
                "\"VMVF\" (0x46564D56 read as a little-endian int32): identifies the file as an animation clip.",
                "Clips are found by name; the file is loaded whole when a class first needs it. Cairn refuses "
                + "any other signature.", offset: "0x00")
            .Field("rfa.version", "Version", "int32", null,
                "7 or 8. The two versions differ only in the morph (vertex animation) block: version 8 stores a time per "
                + "morph keyframe and byte-quantised positions in a bounding box; version 7 stores float positions and no times.",
                "Decides how the morph block is read. Bone tracks are identical in both versions.",
                "7 or 8 only.", true, "0x04")
            .Field("rfa.pos_reduction", "Position reduction", "float", "metres",
                "The exporter's position key-reduction tolerance, kept as a record of how the clip was made.",
                "Never read by the game.", null, false, "0x08")
            .Field("rfa.rot_reduction", "Rotation reduction", "float", null,
                "The exporter's rotation key-reduction tolerance, kept as a record of how the clip was made.",
                "Never read by the game.", null, false, "0x0C")
            .Field("rfa.start_time", "Start time", "int32", Ticks,
                "Where the clip starts on its own timeline. Every stock clip starts at 160 (one 30 fps frame).",
                "Playback runs from start to end. An action's weight ramps up over ramp_in ticks from here and is 0 "
                + "before it; version 7 morph keyframes are spread evenly from here to the end time.",
                "Must be less than the end time; keys outside [start, end] are never reached.", true, "0x10")
            .Field("rfa.end_time", "End time", "int32", Ticks,
                "Where the clip ends on its own timeline.",
                "The clip's timeline ends here. An action's weight ramps down over the last ramp_out "
                + "ticks before it and is 0 after it.",
                "Must be greater than the start time.", true, "0x14")
            .Field("rfa.num_bones", "Bone count", "int32", null,
                "Number of bone tracks. Track i animates the mesh's bone i: clips address bones by index only.",
                "Never compared with the mesh's bone count. A clip with fewer bones than the mesh makes the engine read "
                + "past its bone table; one with more drives the wrong bones. It must equal the mesh's count, in the mesh's order.",
                "At most 50 (the engine's bone limit); must equal the mesh's bone count.", true, "0x18")
            .Field("rfa.num_morph_vertices", "Morph vertex count", "int32", null,
                "Number of mesh vertices the morph block animates (0 for a clip without vertex animation).",
                "Morphs apply to LOD 0 of the mesh only. Alpine Faction refuses a clip whose largest morph vertex index "
                + "is not below LOD 0's vertex count.",
                "Indices must be below LOD 0's vertex count.", true, "0x1C")
            .Field("rfa.num_morph_keyframes", "Morph keyframe count", "int32", null,
                "Number of morph keyframes (vertex poses).",
                "Sets the size of the morph block; how keyframes are timed depends on the version (see the morph section).",
                null, true, "0x20")
            .Field("rfa.ramp_in", "Ramp in", "int32", Ticks,
                "Blend-in time when the clip plays as an action.",
                "An action's bone weights ramp linearly from 0 at start_time to full after ramp_in ticks. Where ramp-in "
                + "and ramp-out overlap, ramp-in wins. States ignore it. (The format header calling it unused is wrong.)",
                "0 or more; longer than the clip means the action never reaches full weight.", true, "0x24")
            .Field("rfa.ramp_out", "Ramp out", "int32", Ticks,
                "Blend-out time when the clip plays as an action.",
                "An action's bone weights ramp linearly down to 0 over the last ramp_out ticks before end_time. States ignore it.",
                "0 or more.", true, "0x28")
            .Field("rfa.total_rotation", "Total rotation", "float[4]", "quaternion (x y z w)",
                "An exporter summary rotation. (0, 0, 0, 1) in every stock clip.",
                "Only read by functions named Skeleton::get_total_*_unused, which nothing uses: not used by the game.",
                null, false, "0x2C")
            .Field("rfa.total_translation", "Total translation", "float[3]", "metres",
                "An exporter summary translation. Zero in every stock clip.",
                "Only read by functions named Skeleton::get_total_*_unused, which nothing uses: not used by the game.",
                null, false, "0x3C")
            .Field("rfa.morph_vertices_offset", "Morph vertices offset", "int32", "bytes from file start",
                "Where the morph vertex index list starts.",
                "Locates the morph block inside the loaded file image. Cairn always writes the canonical layout "
                + "(right after the bones) and accepts files that store 0 when there is no morph data.",
                null, null, "0x48")
            .Field("rfa.morph_keyframes_offset", "Morph keyframes offset", "int32", "bytes from file start",
                "Where the morph keyframes (times, box and positions) start.",
                "Locates the morph keyframes inside the loaded file image; written canonically after the indices and their padding.",
                null, null, "0x4C")
            .Field("rfa.bone_offsets", "Bone offsets", "int32[num_bones]", "bytes from file start",
                "One offset per bone track, in bone index order (stock files store the tracks contiguously, in order).",
                "The samplers read image + 0x50 + bone * 4 to find a bone's track; the bone count is never checked.",
                null, true, "0x50")
            .Field("rfa.duration", "Duration", "derived", Ticks,
                "End time minus start time; shown as frames (160 ticks each, 30 fps) and seconds (4800 ticks each).",
                "Not stored: the game plays from start_time to end_time.", null, null)
            .Field("rfa.file_size", "File size", "derived", "bytes",
                "Size of the clip file.",
                "The game reads the whole file into memory when the clip is first loaded and samples it in place.", null, null);

        var bone = new SectionBuilder(F, "rfa.bone", "Bone track",
            "One per mesh bone, at the offset the bone offset table gives: a weight, two key counts, then the rotation "
            + "keys (16 bytes each) and position keys (40 bytes each), each list in time order.")
            .Field("rfa.bone.weight", "Weight", "float", "0..10",
                "How strongly this clip drives this bone. Stock clips use 2, 4, 5 and 10.",
                "Below 0.00001 the clip ignores the bone. A state uses its weight as stored, but every state's weight on "
                + "a bone is multiplied by (10 - w) / 10, where w is the playing (primary) action's ramped weight on that "
                + "bone: so 10 means the action completely replaces the states on that bone and 5 blends half and half. "
                + "An action's weight is ramped by ramp_in / ramp_out and is 0 outside the clip. The blended rotation is "
                + "a running slerp over the contributing clips; positions are their weighted mean.",
                "0 to 10; below 0.00001 the bone is left to other clips.", true, "+0x00")
            .Field("rfa.bone.num_rot_keys", "Rotation key count", "int16", null,
                "Number of rotation keys in this track.",
                "0 keys leaves the bone at the identity rotation for this clip.", "0..32767", true, "+0x04")
            .Field("rfa.bone.num_pos_keys", "Position key count", "int16", null,
                "Number of position keys in this track.",
                "0 keys puts the bone at (0, 0, 0) in its parent's frame: it collapses onto its parent. Every bone needs at "
                + "least one position key.", "1..32767 in practice", true, "+0x06")
            .Field("rfa.bone.index", "Bone index", "derived", null,
                "The track's position in the clip, which is the mesh bone it animates. The bone's name comes from the mesh.",
                "Bones are matched to the mesh by index with no name or count check.", null, null);

        var rotKey = new SectionBuilder(F, "rfa.rotkey", "Rotation key",
            "16 bytes: time, an int16 quaternion, ease in, ease out and a pad word. The rotation is the bone's local "
            + "rotation relative to its parent, stored as the conjugate of the active rotation.")
            .Field("rfa.rotkey.time", "Time", "int32", Ticks,
                "When the bone reaches this rotation.",
                "Before the first key the first key's rotation holds; after the last key the last key's holds. 0 keys = identity.",
                "Keys must be in increasing time order inside [start, end].", true, "+0x00")
            .Field("rfa.rotkey.rotation", "Rotation", "int16[4]", "quaternion (x y z w) × 16383",
                "The local rotation as four int16 components over 16383, in the file's conjugated convention.",
                "Between keys the engine slerps the int16 quaternions along the short arc and stores the result back to "
                + "int16 (truncated). Two keys less than about 0.16 degrees apart (1 - dot <= 1e-6) snap to the later key "
                + "over the whole segment. A stored w of 0 becomes 1. Cairn re-quantises only keys an edit changes.",
                "Each component -32768..32767; unit length (16383) expected.", true, "+0x04")
            .Field("rfa.rotkey.ease_in", "Ease in", "int8", "÷ 127",
                "Easing into this key from the previous one: 0 linear, 127 full ease.",
                "Shapes the segment that ends at this key, together with the previous key's ease out. If the two sum past "
                + "1 they are scaled to sum to 1.", "-128..127 (0..127 meaningful)", true, "+0x0C")
            .Field("rfa.rotkey.ease_out", "Ease out", "int8", "÷ 127",
                "Easing out of this key towards the next one: 0 linear, 127 full ease.",
                "Shapes the segment that starts at this key, together with the next key's ease in.",
                "-128..127 (0..127 meaningful)", true, "+0x0D")
            .Field("rfa.rotkey.pad", "Pad", "int16", null,
                "Padding; 0 in every stock file.",
                "No known use. Kept as stored so files round-trip.", null, null, "+0x0E");

        var posKey = new SectionBuilder(F, "rfa.poskey", "Position key",
            "40 bytes: time, position, and the two absolute Bézier control points around it. Positions are in the "
            + "parent bone's frame (model space for the root) and set the bone lengths the game renders.")
            .Field("rfa.poskey.time", "Time", "int32", Ticks,
                "When the bone reaches this position.",
                "Clamped to the first / last key outside the keyed range.",
                "Increasing time order inside [start, end].", true, "+0x00")
            .Field("rfa.poskey.position", "Position", "float[3]", "metres, parent frame",
                "The bone's offset from its parent at this key (for the root, its position in model space).",
                "The renderer takes every bone translation from the clips' position keys; the mesh's bind offsets only "
                + "matter for skinning. All clips of one character type carry the same offsets (its proportions).",
                null, true, "+0x04")
            .Field("rfa.poskey.in_ctrl", "In control point", "float[3]", "metres, parent frame",
                "ABSOLUTE Bézier control point used on the segment that ends at this key.",
                "Between keys the engine evaluates a cubic Bézier through key, out control, next in control, next key, "
                + "with linear time (no easing). A constant track stores in = out = position.",
                null, true, "+0x10")
            .Field("rfa.poskey.out_ctrl", "Out control point", "float[3]", "metres, parent frame",
                "ABSOLUTE Bézier control point used on the segment that starts at this key.",
                "See the in control point. Control points left at (0, 0, 0) pull the bone towards its parent mid-segment.",
                null, true, "+0x1C");

        var morph = new SectionBuilder(F, "rfa.morph", "Morph (vertex animation)",
            "After the bones: the morphed vertex indices, padding to a multiple of 4, then the keyframes. Morph data "
            + "belongs to the one mesh the clip was made for; Cairn preserves it byte for byte, can strip it, "
            + "and drops it when retargeting.")
            .Field("rfa.morph.vertex_indices", "Vertex indices", "int16[num_morph_vertices]", null,
                "Which vertices move, in LOD 0's original vertex numbering.",
                "Applied to LOD 0 only, from the first playing clip that has morph vertices, at its own time; bone weights "
                + "and ramps play no part and morph clips never blend with each other. When LOD 0 has flag 0x01 each index "
                + "goes through the batch's morph map, otherwise straight to the batch vertex; same-position offsets then "
                + "copy the result to duplicate vertices.",
                "Alpine Faction refuses a clip whose largest index is not below LOD 0's vertex count.", true)
            .Field("rfa.morph.padding", "Padding", "bytes", null,
                "Zero bytes padding the index list to a multiple of 4.",
                "Skipped.", null, false)
            .Field("rfa.morph.times", "Keyframe times (v8)", "int32[num_morph_keyframes]", Ticks,
                "Version 8: when each morph keyframe applies.",
                "Clamped at both ends (the first keyframe holds before its time, the last after); positions are "
                + "interpolated linearly between the keyframes around the current time.",
                "Increasing.", true)
            .Field("rfa.morph.aabb", "Bounding box (v8)", "float[6]", "metres, submesh space",
                "Version 8: min xyz then max xyz of the quantisation box. Present only when there are keyframes and vertices.",
                "Byte 0 maps to min, byte 255 to max.", null, true)
            .Field("rfa.morph.positions_v8", "Positions (v8)", "uint8[keyframes][vertices][3]", "fraction of the box",
                "Version 8: each morphed vertex's position at each keyframe, quantised into the box.",
                "Decoded as min + (max - min) * b / 255. Positions are absolute and REPLACE the vertices, in the batch's "
                + "own (submesh-local) space.", null, true)
            .Field("rfa.morph.positions_v7", "Positions (v7)", "float[keyframes][vertices][3]", "metres, submesh space",
                "Version 7: each morphed vertex's position at each keyframe, as floats. No times are stored.",
                "Keyframe k sits at start + k * (end - start) / keyframe count; positions are interpolated linearly "
                + "between consecutive keyframes and the last keyframe holds until the end. They replace the vertices.",
                null, true);

        return new FormatReference(F, "RFA animation clip",
            "An .rfa clip (spelt .mvf in the tables) animates a character mesh's bones by index, with optional vertex "
            + "(morph) animation for the one mesh it was made for. Time is in ticks, 4800 per second; a 30 fps frame is "
            + "160 ticks. Quaternions are stored conjugated (file convention), positions in the parent bone's frame. The "
            + "engine identifies a clip by its base file name, case-insensitively, in one global table of 800 slots.",
            [header.Build(), bone.Build(), rotKey.Build(), posKey.Build(), morph.Build()]);
    }
}
