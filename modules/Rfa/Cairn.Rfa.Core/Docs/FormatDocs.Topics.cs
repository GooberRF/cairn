using System.Collections.Immutable;

namespace Cairn.Rfa.Docs;

public static partial class FormatDocs
{
    private static ImmutableArray<FormatTopic> BuildTopics() =>
    [
        new("engine.identity", "How the game finds a clip",
            "A clip is identified by its base file name, case-insensitively, in one global table of 800 slots: "
            + "\"x.mvf\" in a table, \"x.rfa\" on disk and \"X\" are the same clip, whichever folder or archive it is in. "
            + "Two different files called x.rfa cannot both be loaded; the first one found in the search order wins.\n\n"
            + "The tables still use the exporter's extensions: .mvf is .rfa, .vcm is .v3c and .v3d is .v3m (weapons.tbl "
            + "also writes first-person character meshes as .v3d).\n\n"
            + "The clip's bone tracks are matched to the mesh's bones by INDEX, with no count or name check: a clip must "
            + "carry exactly the mesh's bone count, in the mesh's bone order."),
        new("engine.states_actions", "States and actions",
            "entity.tbl and weapons.tbl give every class named slots. A +State: is a base animation (stand, walk, run, "
            + "crouch); an +Action: is a one-off played on top (fire, reload, flinch, death), with an optional foley sound. "
            + "A +Weapon Specific: block overrides slots while the character holds that weapon.\n\n"
            + "States and actions play the same file format; what differs is how their bone weights are used."),
        new("engine.weights", "Bone weights",
            "Each bone track has a weight. Below 0.00001 the clip leaves that bone alone.\n\n"
            + "A state uses its weight as stored. While an action plays, every state's weight on a bone is multiplied by "
            + "(10 - w) / 10, where w is the playing (primary) action's current weight on that bone. Weights therefore run "
            + "from 0 to 10: an action bone at 10 completely replaces the states on that bone, 5 blends half and half, and "
            + "0 leaves the states alone. Stock clips use 2, 4, 5 and 10, which is how an upper-body fire or reload action "
            + "plays over a walking or seated state.\n\n"
            + "The final rotation of a bone is a running slerp over the contributing clips by weight; its position is "
            + "their weighted mean."),
        new("engine.ramps", "Ramp in and ramp out",
            "An action's weights are not applied at full strength at once: they ramp linearly from 0 at the start time up "
            + "to the stored weight over ramp_in ticks, and back down to 0 over the last ramp_out ticks before the end "
            + "time. Where the two ramps overlap (a short clip), the ramp-in wins. Outside the clip the weight is 0.\n\n"
            + "States ignore both ramps."),
        new("engine.rotation_sampling", "How rotations are sampled",
            "Between two rotation keys the engine eases the segment's progress (the earlier key's ease out and the later "
            + "key's ease in, each a byte over 127; if they sum past 1 they are scaled to sum to 1), then slerps the int16 "
            + "quaternions along the short arc and stores the result back to int16, truncating.\n\n"
            + "Two keys less than about 0.16 degrees apart (1 - dot <= 1e-6) snap to the later key for the whole segment. "
            + "A stored w of 0 becomes 1. Before the first key and after the last the end keys hold; a bone with no "
            + "rotation keys gets the identity rotation."),
        new("engine.position_sampling", "How positions are sampled",
            "Between two position keys the engine evaluates a cubic Bézier through the first key, its out control point, "
            + "the second key's in control point and the second key. Control points are absolute positions, and time runs "
            + "linearly (there is no easing on positions).\n\n"
            + "Outside the keyed range the end keys hold. A bone with no position keys sits at (0, 0, 0) in its parent's "
            + "frame, collapsing onto its parent, so every bone needs at least one. Position keys, not the mesh's bind "
            + "pose, set the bone lengths the game renders."),
        new("engine.morph", "Morph (vertex) animation",
            "Some clips also move individual vertices of the one mesh they were made for (faces talking). Morphs apply to "
            + "LOD 0 only, from the first playing clip that has morph vertices, at that clip's own time; weights and ramps "
            + "play no part and morph clips never blend.\n\n"
            + "Morph positions replace the vertices in the submesh's own space. Each morph index goes through the batch's "
            + "morph map when LOD 0 has flag 0x01, then same-position offsets copy the result to duplicate vertices.\n\n"
            + "Version 7 stores no times: keyframe k sits at start + k * (end - start) / keyframe count, positions are "
            + "interpolated linearly between consecutive keyframes and the last holds until the end. Version 8 stores a "
            + "time per keyframe, clamped at both ends and linear between, and decodes each byte as "
            + "min + (max - min) * b / 255.\n\n"
            + "Alpine Faction refuses a clip whose largest morph index is not below LOD 0's vertex count."),
        new("engine.time", "Time units",
            "Clip time is in ticks: 4800 per second. Cairn shows frames of 160 ticks (30 fps); every stock clip "
            + "starts at tick 160."),
    ];
}
