using System.Collections.Immutable;

namespace Cairn.Formats.Tbl;

/// <summary>One <c>+State:</c> or <c>+Action:</c> line: the slot name and the clip that fills it.</summary>
/// <param name="Name">The state or action name, e.g. "stand", "fire_stand".</param>
/// <param name="Clip">The clip as written (usually <c>.mvf</c>; <see cref="TblFileName.DiskName"/> is the <c>.rfa</c>).</param>
/// <param name="Sound">An action's foley sound, when the line names one (empty strings become null).</param>
/// <param name="Line">1-based line in the table.</param>
public sealed record TblAnimation(string Name, TblFileName Clip, string? Sound, int Line);

/// <summary>An entity class's <c>+Weapon Specific:</c> block: the clips it uses while holding that weapon.</summary>
/// <param name="WeaponName">The weapon, as named in weapons.tbl.</param>
/// <param name="SpineAdjustment">The block's <c>+Spine Adjustment:</c> in degrees, if given.</param>
/// <param name="States">The block's state overrides.</param>
/// <param name="Actions">The block's action overrides.</param>
/// <param name="Line">1-based line of the <c>+Weapon Specific:</c> key.</param>
public sealed record EntityWeaponAnimations(
    string WeaponName, float? SpineAdjustment, ImmutableArray<TblAnimation> States, ImmutableArray<TblAnimation> Actions, int Line);

/// <summary>An entity.tbl class: its mesh and the clips its states and actions use.</summary>
/// <param name="Name">The class name (<c>$Name:</c>).</param>
/// <param name="Mesh">The <c>$V3D Filename:</c> (usually <c>.vcm</c>, i.e. a <c>.v3c</c>).</param>
/// <param name="States">Default <c>+State:</c> lines, in order.</param>
/// <param name="Actions">Default <c>+Action:</c> lines, in order.</param>
/// <param name="WeaponAnimations">The <c>+Weapon Specific:</c> blocks, in order.</param>
/// <param name="Line">1-based line of <c>$Name:</c>.</param>
public sealed record EntityClass(
    string Name,
    TblFileName Mesh,
    ImmutableArray<TblAnimation> States,
    ImmutableArray<TblAnimation> Actions,
    ImmutableArray<EntityWeaponAnimations> WeaponAnimations,
    int Line);

/// <summary>A pc_multi.tbl <c>+Custom Fpgun:</c> line.</summary>
/// <param name="WeaponName">The weapon (weapons.tbl name).</param>
/// <param name="FpgunName">The fpgun.tbl entry used for it.</param>
public sealed record CustomFpgun(string WeaponName, string FpgunName);

/// <summary>A multiplayer character from pc_multi.tbl.</summary>
/// <param name="Name">The character name (<c>$Name:</c>).</param>
/// <param name="EntityType">The entity.tbl class whose mesh it uses (<c>$EntityType:</c>).</param>
/// <param name="EntityAnimType">The entity.tbl class whose clips it uses (<c>$EntityAnimType:</c>).</param>
/// <param name="SkinName">The <c>$SkinName:</c>, if any.</param>
/// <param name="ImageName">The selection image, if any.</param>
/// <param name="CustomFpguns">First-person arms per weapon.</param>
/// <param name="Line">1-based line of <c>$Name:</c>.</param>
public sealed record MultiCharacter(
    string Name, string EntityType, string EntityAnimType, string? SkinName, string? ImageName,
    ImmutableArray<CustomFpgun> CustomFpguns, int Line);

/// <summary>A weapons.tbl entry: its meshes and its first-person states and actions.</summary>
/// <param name="Name">The weapon name.</param>
/// <param name="FirstPersonMesh">The <c>$1st Person Mesh:</c> (the animated first-person model), if any.</param>
/// <param name="ThirdPersonMesh">The <c>$3rd Person V3D:</c>, if any.</param>
/// <param name="ProjectileMesh">The <c>$V3D Filename:</c> (projectile model), if not empty.</param>
/// <param name="States">First-person <c>+State:</c> lines (clips for the first-person mesh).</param>
/// <param name="Actions">First-person <c>+Action:</c> lines.</param>
/// <param name="Line">1-based line of <c>$Name:</c>.</param>
public sealed record WeaponClass(
    string Name,
    TblFileName? FirstPersonMesh,
    TblFileName? ThirdPersonMesh,
    TblFileName? ProjectileMesh,
    ImmutableArray<TblAnimation> States,
    ImmutableArray<TblAnimation> Actions,
    int Line);

/// <summary>
/// An fpgun.tbl entry: a first-person model variant (hands/arms per character skin). The clips are
/// the weapon's (weapons.tbl); this only swaps the mesh and its arm textures.
/// </summary>
/// <param name="Name">The entry name pc_multi.tbl refers to.</param>
/// <param name="Model">The <c>$Model:</c> mesh (a <c>.v3c</c>).</param>
/// <param name="HandTexture">The <c>$Hand Texture:</c>.</param>
/// <param name="ForearmTexture">The <c>$Forearm Texture:</c>.</param>
/// <param name="BicepTexture">The <c>$Bicep Texture:</c>.</param>
/// <param name="Line">1-based line of <c>$Name:</c>.</param>
public sealed record FpgunEntry(
    string Name, TblFileName Model, string? HandTexture, string? ForearmTexture, string? BicepTexture, int Line);

/// <summary>
/// Readers for the tables that tie clips to meshes. Each takes the table's text and returns plain
/// data in file order; unknown keys are skipped and nothing throws on odd input. The usage index
/// built from these is a later phase.
/// </summary>
public static class TableReaders
{
    /// <summary>
    /// entity.tbl: every class's name, <c>$V3D Filename:</c>, default states and actions, and its
    /// <c>+Weapon Specific:</c> blocks. A weapon block collects the <c>+State:</c>/<c>+Action:</c>
    /// lines after it until the next <c>+Weapon Specific:</c>, any <c>$</c> key or the next class.
    /// </summary>
    public static IReadOnlyList<EntityClass> ReadEntities(string text)
    {
        var result = new List<EntityClass>();
        EntityBuilder? current = null;
        foreach (var f in TblParser.ParseFields(text))
        {
            if (f.Is('$', "Name"))
            {
                if (current is not null) result.Add(current.Build());
                current = new EntityBuilder(f.String(0) ?? string.Empty, f.Line);
                continue;
            }
            if (current is null) continue;
            if (f.Prefix == '$')
            {
                current.Weapon = null;
                if (f.Is('$', "V3D Filename")) current.Mesh = new TblFileName(f.String(0) ?? string.Empty);
                continue;
            }
            if (f.Is('+', "Weapon Specific"))
            {
                current.Weapon = new WeaponBuilder(f.String(0) ?? string.Empty, f.Line);
                current.Weapons.Add(current.Weapon);
            }
            else if (f.Is('+', "Spine Adjustment") && current.Weapon is not null)
            {
                current.Weapon.Spine = f.Number();
            }
            else if (f.Is('+', "State") && Animation(f) is { } state)
            {
                (current.Weapon?.States ?? current.States).Add(state);
            }
            else if (f.Is('+', "Action") && Animation(f) is { } action)
            {
                (current.Weapon?.Actions ?? current.Actions).Add(action);
            }
        }
        if (current is not null) result.Add(current.Build());
        return result;
    }

    /// <summary>pc_multi.tbl: every character's name, EntityType, EntityAnimType, skin and custom fpguns.</summary>
    public static IReadOnlyList<MultiCharacter> ReadMultiCharacters(string text)
    {
        var result = new List<MultiCharacter>();
        string? name = null, entityType = null, animType = null, skin = null, image = null;
        int line = 0;
        var fpguns = ImmutableArray.CreateBuilder<CustomFpgun>();

        void Flush()
        {
            if (name is null) return;
            result.Add(new MultiCharacter(name, entityType ?? string.Empty, animType ?? string.Empty, skin, image,
                fpguns.ToImmutable(), line));
            fpguns.Clear();
        }

        foreach (var f in TblParser.ParseFields(text))
        {
            if (f.Is('$', "Name"))
            {
                Flush();
                name = f.String(0) ?? string.Empty;
                entityType = animType = skin = image = null;
                line = f.Line;
            }
            else if (name is null) continue;
            else if (f.Is('$', "EntityType")) entityType = f.String(0);
            else if (f.Is('$', "EntityAnimType")) animType = f.String(0);
            else if (f.Is('$', "SkinName")) skin = f.String(0);
            else if (f.Is('$', "ImageName")) image = f.String(0);
            else if (f.Is('+', "Custom Fpgun") && f.String(0) is { } weapon)
                fpguns.Add(new CustomFpgun(weapon, f.String(1) ?? string.Empty));
        }
        Flush();
        return result;
    }

    /// <summary>
    /// weapons.tbl: every weapon's meshes and first-person states and actions (the clips that play on
    /// its <c>$1st Person Mesh:</c>).
    /// </summary>
    public static IReadOnlyList<WeaponClass> ReadWeapons(string text)
    {
        var result = new List<WeaponClass>();
        string? name = null;
        int line = 0;
        TblFileName? fp = null, tp = null, projectile = null;
        var states = ImmutableArray.CreateBuilder<TblAnimation>();
        var actions = ImmutableArray.CreateBuilder<TblAnimation>();

        void Flush()
        {
            if (name is null) return;
            result.Add(new WeaponClass(name, fp, tp, projectile, states.ToImmutable(), actions.ToImmutable(), line));
            states.Clear();
            actions.Clear();
        }

        foreach (var f in TblParser.ParseFields(text))
        {
            if (f.Is('$', "Name"))
            {
                Flush();
                name = f.String(0) ?? string.Empty;
                line = f.Line;
                fp = tp = projectile = null;
            }
            else if (name is null) continue;
            else if (f.Is('$', "1st Person Mesh")) fp = NonEmpty(f.String(0));
            else if (f.Is('$', "3rd Person V3D")) tp = NonEmpty(f.String(0));
            else if (f.Is('$', "V3D Filename")) projectile = NonEmpty(f.String(0));
            else if (f.Is('+', "State") && Animation(f) is { } state) states.Add(state);
            else if (f.Is('+', "Action") && Animation(f) is { } action) actions.Add(action);
        }
        Flush();
        return result;
    }

    /// <summary>fpgun.tbl: every first-person model variant and its arm textures. It lists no clips.</summary>
    public static IReadOnlyList<FpgunEntry> ReadFpguns(string text)
    {
        var result = new List<FpgunEntry>();
        string? name = null, hand = null, forearm = null, bicep = null;
        TblFileName model = default;
        int line = 0;

        void Flush()
        {
            if (name is null) return;
            result.Add(new FpgunEntry(name, model, hand, forearm, bicep, line));
        }

        foreach (var f in TblParser.ParseFields(text))
        {
            if (f.Is('$', "Name"))
            {
                Flush();
                name = f.String(0) ?? string.Empty;
                line = f.Line;
                model = new TblFileName(string.Empty);
                hand = forearm = bicep = null;
            }
            else if (name is null) continue;
            else if (f.Is('$', "Model")) model = new TblFileName(f.String(0) ?? string.Empty);
            else if (f.Is('$', "Hand Texture")) hand = f.String(0);
            else if (f.Is('$', "Forearm Texture")) forearm = f.String(0);
            else if (f.Is('$', "Bicep Texture")) bicep = f.String(0);
        }
        Flush();
        return result;
    }

    private static TblAnimation? Animation(TblField f)
    {
        string? name = f.String(0);
        string? clip = f.String(1);
        if (name is null || clip is null) return null;
        string? sound = f.String(2);
        return new TblAnimation(name, new TblFileName(clip), string.IsNullOrEmpty(sound) ? null : sound, f.Line);
    }

    private static TblFileName? NonEmpty(string? name) => string.IsNullOrWhiteSpace(name) ? null : new TblFileName(name);

    private sealed class EntityBuilder(string name, int line)
    {
        public TblFileName Mesh { get; set; } = new(string.Empty);
        public List<TblAnimation> States { get; } = [];
        public List<TblAnimation> Actions { get; } = [];
        public List<WeaponBuilder> Weapons { get; } = [];
        public WeaponBuilder? Weapon { get; set; }

        public EntityClass Build() => new(name, Mesh, [.. States], [.. Actions], [.. Weapons.Select(w => w.Build())], line);
    }

    private sealed class WeaponBuilder(string name, int line)
    {
        public float? Spine { get; set; }
        public List<TblAnimation> States { get; } = [];
        public List<TblAnimation> Actions { get; } = [];

        public EntityWeaponAnimations Build() => new(name, Spine, [.. States], [.. Actions], line);
    }
}
