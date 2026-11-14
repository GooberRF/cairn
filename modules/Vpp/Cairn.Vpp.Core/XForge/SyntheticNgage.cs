using System.Numerics;
using Cairn.Formats.Imaging;
using Cairn.Formats.XForge;

namespace Cairn.Vpp.XForge;

/// <summary>
/// Small Red Faction for N-Gage files for tests and self-tests, built in code: a level scene, a character scene, a
/// particle system and an archive holding them with some of the textures they name (one deliberately missing).
/// </summary>
internal static class SyntheticNgage
{
    /// <summary>The texture the level names that the archive leaves out.</summary>
    public const string MissingTexture = "txtr_enviro_missing.tga";

    /// <summary>The particle system's frame the archive leaves out.</summary>
    public const string MissingFrame = "spr_rlexplosion_06.pcx";

    private static readonly Vector3[] Quad = [new(0, 0, 0), new(4, 0, 0), new(0, 0, 4), new(4, 0, 4)];
    private static readonly Vector3[] QuadNormals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY];
    private static readonly ushort[] QuadIndices = [0, 2, 1, 1, 2, 3];
    private static readonly Vector2[] QuadUvs = [new(0, 0), new(0, 1), new(1, 0), new(1, 0), new(0, 1), new(1, 1)];

    /// <summary>
    /// A level: an area with a floor (two triangles, textured <c>lvl0_floor_01.pcx</c>) and a wall (textured
    /// <see cref="MissingTexture"/>), a room collider, a script trigger, an enemy spawn, a health pack and an explosion.
    /// </summary>
    public static byte[] Level()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint area = x.Marker(XffMarkers.Area, root, "starthall");
        x.MeshObject(area, "floor01", Quad, QuadNormals, QuadIndices, null, QuadUvs, "lvl0_floor_01.pcx");
        x.MeshObject(area, "wall01", Quad, QuadNormals, QuadIndices, null, QuadUvs, MissingTexture,
            new XffTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), Vector3.One));
        uint room = x.CollisionBox(area, "col_room01", new Vector3(-2, -1, -2), new Vector3(2, 1, 2));
        x.Property(room, XffClassifier.RoomProperty);
        uint trigger = x.Marker(XffMarkers.ScriptTrigger, area, "gr_entrig");
        x.CollisionBox(trigger, "col_entrig", -Vector3.One, Vector3.One);
        x.Marker(4202, area, "enemy01");
        x.Prefab(area, "health01", XffTransform.Identity, "item_healthpack.xff");
        x.Prefab(area, "health02", XffTransform.Identity, "item_healthpack.xff");
        uint fx = x.Group(area, "gr_fx");
        x.Property(fx, "w:1");
        x.Emitter(fx, "PS_explosion", "explosion.pr3");
        x.Sprite(area, "SP_flare", "flare.pcx");
        return x.ToArray();
    }

    /// <summary>An enemy (marker 3202) with one animated part: a 533 ms looping track of 17 keys at 30 keys per second.</summary>
    public static byte[] Character()
    {
        var x = new SyntheticXff();
        uint marker = x.Marker(3202, XffFile.NoReference, "player");
        var keys = Enumerable.Range(0, 17).Select(i => new XffKey(new Vector3(0, 0.05f * MathF.Sin(i * MathF.PI / 8), 0), Quaternion.Identity)).ToList();
        uint track = x.Track(marker, "anim_cfo_body", 0, 533, 30, keys);
        x.MeshObject(track, "cfo_body", Quad, QuadNormals, QuadIndices, null, QuadUvs, "txtr_ug_body.pcx");
        x.Emitter(marker, "PS_gib", "gib.pr3");
        return x.ToArray();
    }

    /// <summary>The explosion particle system (<see cref="SyntheticPr3"/>'s defaults).</summary>
    public static byte[] Explosion() => new SyntheticPr3().Write();

    /// <summary>A small 8-bit PCX.</summary>
    public static byte[] Pcx(int width, int height) =>
        SyntheticPcx.Write(width, height, 8, 1, (px, py) => (px * 7 + py * 3) % 256, [.. Enumerable.Range(0, 768).Select(i => (byte)(i * 5))]);

    /// <summary>
    /// An archive like <c>redfaction.cfl</c>: the level (<c>level_01a.xff</c>), the character (<c>enemy_ug.xff</c>), the
    /// explosion (<c>explosion.pr3</c>), the textures they name except <see cref="MissingTexture"/> and
    /// <see cref="MissingFrame"/>, the health pack, and an odd-sized HUD image.
    /// </summary>
    public static byte[] Archive()
    {
        var entries = new List<SyntheticCflEntry>
        {
            new("level_01a.xff", Level()),
            new("enemy_ug.xff", Character()),
            new("explosion.pr3", Explosion()),
            new("lvl0_floor_01.pcx", Pcx(32, 32)),
            new("txtr_ug_body.pcx", Pcx(32, 32)),
            new("flare.pcx", Pcx(16, 16)),
            new("pic_textbox_red.pcx", Pcx(151, 37)),
            new("item_healthpack.xff", Item()),
        };
        entries.AddRange(Enumerable.Range(1, 5).Select(i => new SyntheticCflEntry($"spr_rlexplosion_{i:00}.pcx", Pcx(16, 16))));
        return SyntheticCfl.Build([.. entries]);
    }

    /// <summary>The health pack: a model with its item marker.</summary>
    private static byte[] Item()
    {
        var x = new SyntheticXff();
        uint marker = x.Marker(2040, XffFile.NoReference, "item_healthpack");
        x.MeshObject(marker, "box", Quad, QuadNormals, QuadIndices, null, QuadUvs, "lvl0_floor_01.pcx");
        return x.ToArray();
    }
}
