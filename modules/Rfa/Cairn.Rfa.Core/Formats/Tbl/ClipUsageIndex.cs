using System.Collections.Immutable;
using Cairn.Assets;

namespace Cairn.Rfa.Formats.Tbl;

/// <summary>Whether a table line plays its clip as a state (looping base pose) or an action (one-shot layer).</summary>
public enum ClipUsageKind
{
    /// <summary>A <c>+State:</c> line: a looping base animation, weights used as stored.</summary>
    State,
    /// <summary>An <c>+Action:</c> line: played over the states, weights ramped by ramp_in / ramp_out.</summary>
    Action,
}

/// <summary>One table line that names a clip.</summary>
/// <param name="ClipBaseName">The clip's identity in the engine: base file name, no folder or extension.</param>
/// <param name="Clip">The clip as the table spells it (usually <c>.mvf</c>).</param>
/// <param name="Table">The table file name, e.g. "entity.tbl" or "weapons.tbl".</param>
/// <param name="ClassName">The entity class or weapon whose line this is.</param>
/// <param name="Kind">State or action.</param>
/// <param name="SlotName">The state or action name, e.g. "stand", "fire_stand".</param>
/// <param name="WeaponBlock">For an entity class's <c>+Weapon Specific:</c> override, the weapon it applies to; otherwise null.</param>
/// <param name="Sound">An action's foley sound, if the line names one.</param>
/// <param name="Line">1-based line in <paramref name="Table"/>.</param>
public sealed record ClipUsage(
    string ClipBaseName,
    TblFileName Clip,
    string Table,
    string ClassName,
    ClipUsageKind Kind,
    string SlotName,
    string? WeaponBlock,
    string? Sound,
    int Line)
{
    /// <summary>The clip's file name on disk (<c>.rfa</c>).</summary>
    public string DiskName => Clip.DiskName;

    /// <summary>A one-line description, e.g. <c>miner1 (Shotgun) state "stand"</c>.</summary>
    public string Describe() =>
        $"{ClassName}{(WeaponBlock is null ? "" : $" ({WeaponBlock})")} {(Kind == ClipUsageKind.State ? "state" : "action")} \"{SlotName}\"";

    /// <inheritdoc />
    public override string ToString() => $"{Table}:{Line} {Describe()} -> {Clip}";
}

/// <summary>The clips the tables give one mesh through one route.</summary>
/// <param name="MeshName">
/// The mesh's most likely disk name. Table spellings are normalised (<c>.vcm</c> is <c>.v3c</c>), and
/// a <c>.v3d</c> becomes <c>.v3c</c> here because only character meshes play clips.
/// </param>
/// <param name="ClassName">The entity class or weapon whose clips play on the mesh.</param>
/// <param name="Table">The table that ties the mesh to those clips ("entity.tbl", "pc_multi.tbl", "weapons.tbl", "fpgun.tbl").</param>
/// <param name="Relation">Plain-language reason, e.g. "entity class mesh".</param>
/// <param name="Clips">The usages, in table order (default lines first, then each weapon block).</param>
public sealed record MeshClipList(string MeshName, string ClassName, string Table, string Relation, ImmutableArray<ClipUsage> Clips)
{
    /// <summary>The mesh as the table spells it.</summary>
    public TblFileName Mesh { get; init; }

    /// <summary>For a pc_multi.tbl route, the multiplayer character's name; otherwise null.</summary>
    public string? Character { get; init; }
}

/// <summary>
/// Which classes use which clips, and which clips each mesh plays, from entity.tbl, weapons.tbl,
/// pc_multi.tbl and fpgun.tbl. Clip names are compared as the engine does: base file name only,
/// case-insensitive, global (<c>x</c>, <c>x.rfa</c>, <c>x.mvf</c> and <c>folder\X.RFA</c> are one clip).
/// Mesh names are compared through <see cref="TblFileName.Candidates"/>, case-insensitive. Immutable
/// and safe to share between threads.
/// </summary>
public sealed class ClipUsageIndex
{
    /// <summary>The entity classes table.</summary>
    public const string EntityTable = "entity.tbl";

    /// <summary>The weapons table (first-person clips).</summary>
    public const string WeaponsTable = "weapons.tbl";

    /// <summary>The multiplayer characters table.</summary>
    public const string MultiTable = "pc_multi.tbl";

    /// <summary>The first-person model variants table.</summary>
    public const string FpgunTable = "fpgun.tbl";

    /// <summary>Every table <see cref="Load"/> reads, in the order it reads them.</summary>
    public static IReadOnlyList<string> TableNames { get; } = [EntityTable, WeaponsTable, MultiTable, FpgunTable];

    private readonly Dictionary<string, List<ClipUsage>> _byClip;
    private readonly Dictionary<string, ImmutableArray<ClipUsage>> _byClass;
    private readonly Dictionary<string, List<MeshClipList>> _byMeshName;
    private readonly Dictionary<string, List<MeshClipList>> _byMeshBase;
    private readonly Dictionary<string, List<string>> _meshesByClip;
    private readonly Dictionary<MeshClipList, int> _listOrder;

    private ClipUsageIndex(
        ImmutableArray<ClipUsage> usages,
        ImmutableArray<MeshClipList> meshLists,
        Dictionary<string, ImmutableArray<ClipUsage>> classes,
        ImmutableDictionary<string, AssetLocation> sources,
        ImmutableDictionary<string, string> errors)
    {
        Usages = usages;
        MeshClipLists = meshLists;
        TableSources = sources;
        TableErrors = errors;
        _byClass = classes;

        _byClip = new Dictionary<string, List<ClipUsage>>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in usages) Add(_byClip, u.ClipBaseName, u);

        _byMeshName = new Dictionary<string, List<MeshClipList>>(StringComparer.OrdinalIgnoreCase);
        _byMeshBase = new Dictionary<string, List<MeshClipList>>(StringComparer.OrdinalIgnoreCase);
        _meshesByClip = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _listOrder = new Dictionary<MeshClipList, int>(ReferenceEqualityComparer.Instance);
        foreach (var list in meshLists)
        {
            _listOrder[list] = _listOrder.Count;
            // Both the table's spelling and the normalised name find the list (a .v3d finds it as
            // .v3m or .v3c; the .v3c chosen for MeshName is always among them).
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { list.MeshName };
            if (!list.Mesh.IsEmpty) keys.UnionWith(MeshCandidates(list.Mesh.Original));
            foreach (string key in keys) Add(_byMeshName, key, list);
            Add(_byMeshBase, Path.GetFileNameWithoutExtension(list.MeshName), list);
            foreach (var u in list.Clips)
            {
                if (!_meshesByClip.TryGetValue(u.ClipBaseName, out var meshes)) _meshesByClip[u.ClipBaseName] = meshes = [];
                if (!meshes.Contains(list.MeshName, StringComparer.OrdinalIgnoreCase)) meshes.Add(list.MeshName);
            }
        }
        ClipBaseNames = [.. _byClip.Keys];
    }

    /// <summary>An index with no tables at all.</summary>
    public static ClipUsageIndex Empty { get; } = new([], [], new(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<string, AssetLocation>.Empty, ImmutableDictionary<string, string>.Empty);

    /// <summary>Every clip line, in table order (entity.tbl first, then weapons.tbl).</summary>
    public ImmutableArray<ClipUsage> Usages { get; }

    /// <summary>Every mesh-to-clips route, in build order.</summary>
    public ImmutableArray<MeshClipList> MeshClipLists { get; }

    /// <summary>
    /// Where each table came from when the index was <see cref="Load">loaded through a resolver</see>
    /// (a loose <c>.tbl</c> wins over the copy in a VPP, as in the game). Keyed by table file name;
    /// a table that was not found is absent. Empty for an index built from data.
    /// </summary>
    public ImmutableDictionary<string, AssetLocation> TableSources { get; }

    /// <summary>Tables that were found but could not be read, with a plain-language reason.</summary>
    public ImmutableDictionary<string, string> TableErrors { get; }

    /// <summary>Every clip identity (base name) some table line names.</summary>
    public IReadOnlyCollection<string> ClipBaseNames { get; }

    /// <summary>
    /// The engine's identity for a clip name: the base file name without folder or extension.
    /// Compare results ignoring case.
    /// </summary>
    public static string ClipKey(string clipName)
    {
        ArgumentNullException.ThrowIfNull(clipName);
        string bare = Path.GetFileName(clipName.Trim().Replace('/', '\\'));
        return Path.GetFileNameWithoutExtension(bare);
    }

    /// <summary>
    /// The disk name of a mesh a table names, for clip playback: <c>.vcm</c> and <c>.v3d</c> become
    /// <c>.v3c</c> (only character meshes animate); anything else is kept.
    /// </summary>
    public static string CharacterMeshName(TblFileName mesh)
    {
        var candidates = mesh.Candidates;
        foreach (string c in candidates)
        {
            if (c.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase)) return c;
        }
        return candidates[0];
    }

    /// <summary>
    /// Builds the index from table data. Entity classes give their meshes their clips; each pc_multi
    /// character gives its <c>$EntityType:</c>'s mesh the clips of its <c>$EntityAnimType:</c>; each
    /// weapon gives its <c>$1st Person Mesh:</c> its clips; and each pc_multi <c>+Custom Fpgun:</c>
    /// gives that fpgun.tbl entry's <c>$Model:</c> the weapon's clips.
    /// </summary>
    public static ClipUsageIndex Build(
        IReadOnlyList<EntityClass> entities,
        IReadOnlyList<WeaponClass> weapons,
        IReadOnlyList<MultiCharacter> multiCharacters,
        IReadOnlyList<FpgunEntry>? fpguns = null) =>
        Build(entities, weapons, multiCharacters, fpguns,
            ImmutableDictionary<string, AssetLocation>.Empty, ImmutableDictionary<string, string>.Empty);

    /// <summary>Builds the index from table texts (any may be null when that table is missing).</summary>
    public static ClipUsageIndex FromTexts(string? entityText, string? weaponsText, string? multiText, string? fpgunText) =>
        Build(
            entityText is null ? [] : TableReaders.ReadEntities(entityText),
            weaponsText is null ? [] : TableReaders.ReadWeapons(weaponsText),
            multiText is null ? [] : TableReaders.ReadMultiCharacters(multiText),
            fpgunText is null ? [] : TableReaders.ReadFpguns(fpgunText));

    /// <summary>
    /// Reads the four tables through <paramref name="resolver"/> (so a loose <c>.tbl</c> overrides
    /// the one in a VPP, as in the game) and builds the index. A missing table contributes nothing;
    /// an unreadable one is listed in <see cref="TableErrors"/>. Never throws for bad input.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static ClipUsageIndex Load(AssetResolver resolver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var sources = ImmutableDictionary.CreateBuilder<string, AssetLocation>(StringComparer.OrdinalIgnoreCase);
        var errors = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string table in TableNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = resolver.Resolve(table, cancellationToken);
            if (location is null) continue;
            sources[table] = location;
            try
            {
                texts[table] = TblTokenizer.DecodeText(location.ReadAllBytes());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException)
            {
                errors[table] = $"{table} ({location.DisplayLocation}) could not be read: {ex.Message}";
            }
        }

        string? Text(string name) => texts.TryGetValue(name, out var t) ? t : null;
        cancellationToken.ThrowIfCancellationRequested();
        return Build(
            Text(EntityTable) is { } e ? TableReaders.ReadEntities(e) : [],
            Text(WeaponsTable) is { } w ? TableReaders.ReadWeapons(w) : [],
            Text(MultiTable) is { } m ? TableReaders.ReadMultiCharacters(m) : [],
            Text(FpgunTable) is { } f ? TableReaders.ReadFpguns(f) : [],
            sources.ToImmutable(), errors.ToImmutable());
    }

    /// <summary><see cref="Load"/> on a worker thread.</summary>
    public static Task<ClipUsageIndex> LoadAsync(AssetResolver resolver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return Task.Run(() => Load(resolver, cancellationToken), cancellationToken);
    }

    /// <summary>Every line naming <paramref name="clipName"/> (any spelling), in table order.</summary>
    public IReadOnlyList<ClipUsage> UsagesOf(string clipName) =>
        string.IsNullOrWhiteSpace(clipName) ? [] : _byClip.TryGetValue(ClipKey(clipName), out var list) ? list.AsReadOnly() : [];

    /// <summary>True when any table line names <paramref name="clipName"/> (any spelling).</summary>
    public bool IsUsed(string clipName) => !string.IsNullOrWhiteSpace(clipName) && _byClip.ContainsKey(ClipKey(clipName));

    /// <summary>
    /// Every line of one entity class (<paramref name="table"/> "entity.tbl", the default) or weapon
    /// ("weapons.tbl"), default lines first, then each <c>+Weapon Specific:</c> block. Names ignore case.
    /// </summary>
    public IReadOnlyList<ClipUsage> UsagesOfClass(string className, string table = EntityTable) =>
        string.IsNullOrWhiteSpace(className) ? [] : _byClass.TryGetValue(ClassKey(table, className), out var list) ? list : [];

    /// <summary>
    /// Every route by which the tables give <paramref name="meshName"/> clips (entity class meshes,
    /// multiplayer characters, weapon first-person meshes, fpgun models). Any table spelling works
    /// (<c>miner.vcm</c>, <c>miner.v3c</c>, <c>fp_glock.v3d</c>); a name without an extension matches
    /// by base name.
    /// </summary>
    public IReadOnlyList<MeshClipList> ClipListsForMesh(string meshName)
    {
        if (string.IsNullOrWhiteSpace(meshName)) return [];
        string bare = Path.GetFileName(meshName.Trim().Replace('/', '\\'));
        if (Path.GetExtension(bare).Length == 0)
            return _byMeshBase.TryGetValue(bare, out var byBase) ? byBase.AsReadOnly() : [];

        var found = new HashSet<MeshClipList>(ReferenceEqualityComparer.Instance);
        foreach (string candidate in MeshCandidates(bare))
        {
            if (_byMeshName.TryGetValue(candidate, out var lists)) found.UnionWith(lists);
        }
        return [.. found.OrderBy(l => _listOrder[l])];
    }

    /// <summary>
    /// The meshes (<see cref="MeshClipList.MeshName"/>) the tables play <paramref name="clipName"/> on,
    /// in build order: entity classes first, then multiplayer characters, weapons and fpgun models.
    /// </summary>
    public IReadOnlyList<string> MeshesForClip(string clipName) =>
        string.IsNullOrWhiteSpace(clipName) ? [] : _meshesByClip.TryGetValue(ClipKey(clipName), out var list) ? list.AsReadOnly() : [];

    // ── Building ─────────────────────────────────────────────────────────────

    private static ClipUsageIndex Build(
        IReadOnlyList<EntityClass> entities,
        IReadOnlyList<WeaponClass> weapons,
        IReadOnlyList<MultiCharacter> multiCharacters,
        IReadOnlyList<FpgunEntry>? fpguns,
        ImmutableDictionary<string, AssetLocation> sources,
        ImmutableDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(weapons);
        ArgumentNullException.ThrowIfNull(multiCharacters);
        fpguns ??= [];

        var usages = ImmutableArray.CreateBuilder<ClipUsage>();
        var classes = new Dictionary<string, ImmutableArray<ClipUsage>>(StringComparer.OrdinalIgnoreCase);

        // The first class of a name is the one lookups by name find (pc_multi.tbl's references,
        // UsagesOfClass); a later duplicate still contributes its lines and its own mesh list.
        var entityOwn = new List<ImmutableArray<ClipUsage>>(entities.Count);
        var entityUsages = new Dictionary<string, ImmutableArray<ClipUsage>>(StringComparer.OrdinalIgnoreCase);
        var entityByName = new Dictionary<string, EntityClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            var own = ImmutableArray.CreateBuilder<ClipUsage>();
            AddLines(own, EntityTable, entity.Name, null, entity.States, entity.Actions);
            foreach (var block in entity.WeaponAnimations)
                AddLines(own, EntityTable, entity.Name, block.WeaponName, block.States, block.Actions);
            var ordered = own.OrderBy(u => u.Line).ToImmutableArray();
            usages.AddRange(ordered);
            entityOwn.Add(ordered);
            entityUsages.TryAdd(entity.Name, ordered);
            entityByName.TryAdd(entity.Name, entity);
            classes.TryAdd(ClassKey(EntityTable, entity.Name), ordered);
        }

        var weaponUsages = new Dictionary<string, ImmutableArray<ClipUsage>>(StringComparer.OrdinalIgnoreCase);
        var weaponByName = new Dictionary<string, WeaponClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var weapon in weapons)
        {
            var own = ImmutableArray.CreateBuilder<ClipUsage>();
            AddLines(own, WeaponsTable, weapon.Name, null, weapon.States, weapon.Actions);
            var ordered = own.OrderBy(u => u.Line).ToImmutableArray();
            usages.AddRange(ordered);
            weaponUsages.TryAdd(weapon.Name, ordered);
            weaponByName.TryAdd(weapon.Name, weapon);
            classes.TryAdd(ClassKey(WeaponsTable, weapon.Name), ordered);
        }

        var lists = ImmutableArray.CreateBuilder<MeshClipList>();
        for (int i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            var clips = entityOwn[i];
            if (entity.Mesh.IsEmpty || clips.IsEmpty) continue;
            lists.Add(new MeshClipList(CharacterMeshName(entity.Mesh), entity.Name, EntityTable, "entity class mesh", clips)
            {
                Mesh = entity.Mesh,
            });
        }

        foreach (var character in multiCharacters)
        {
            if (!entityByName.TryGetValue(character.EntityType, out var meshClass) || meshClass.Mesh.IsEmpty) continue;
            if (!entityUsages.TryGetValue(character.EntityAnimType, out var clips) || clips.IsEmpty) continue;
            string animName = entityByName[character.EntityAnimType].Name;
            lists.Add(new MeshClipList(
                CharacterMeshName(meshClass.Mesh), animName, MultiTable,
                $"multiplayer character {character.Name} (EntityType {meshClass.Name} uses EntityAnimType {animName}'s clips)",
                clips)
            {
                Mesh = meshClass.Mesh,
                Character = character.Name,
            });
        }

        foreach (var weapon in weapons)
        {
            if (weapon.FirstPersonMesh is not { IsEmpty: false } fp) continue;
            var clips = weaponUsages[weapon.Name];
            if (clips.IsEmpty) continue;
            lists.Add(new MeshClipList(CharacterMeshName(fp), weapon.Name, WeaponsTable, "weapon first-person mesh", clips) { Mesh = fp });
        }

        // fpgun models: one list per (model, weapon), naming the fpgun entries that lead there.
        var fpgunByName = new Dictionary<string, FpgunEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fpguns) fpgunByName.TryAdd(f.Name, f);
        var routes = new List<(string MeshKey, FpgunEntry Entry, WeaponClass Weapon, List<string> Names)>();
        foreach (var character in multiCharacters)
        {
            foreach (var custom in character.CustomFpguns)
            {
                if (!fpgunByName.TryGetValue(custom.FpgunName, out var entry) || entry.Model.IsEmpty) continue;
                if (!weaponByName.TryGetValue(custom.WeaponName, out var weapon) || weaponUsages[weapon.Name].IsEmpty) continue;
                string meshKey = CharacterMeshName(entry.Model);
                // The weapon's own first-person mesh already has this list.
                if (weapon.FirstPersonMesh is { IsEmpty: false } own
                    && string.Equals(CharacterMeshName(own), meshKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int at = routes.FindIndex(r => string.Equals(r.MeshKey, meshKey, StringComparison.OrdinalIgnoreCase)
                    && ReferenceEquals(r.Weapon, weapon));
                if (at < 0) routes.Add((meshKey, entry, weapon, [entry.Name]));
                else if (!routes[at].Names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) routes[at].Names.Add(entry.Name);
            }
        }
        foreach (var (meshKey, entry, weapon, names) in routes)
        {
            lists.Add(new MeshClipList(meshKey, weapon.Name, FpgunTable,
                $"fpgun model for weapon {weapon.Name} (fpgun.tbl {string.Join(", ", names)})", weaponUsages[weapon.Name])
            {
                Mesh = entry.Model,
            });
        }

        return new ClipUsageIndex(usages.ToImmutable(), lists.ToImmutable(), classes, sources, errors);
    }

    private static void AddLines(
        ImmutableArray<ClipUsage>.Builder into, string table, string className, string? weaponBlock,
        ImmutableArray<TblAnimation> states, ImmutableArray<TblAnimation> actions)
    {
        foreach (var s in states)
        {
            if (s.Clip.IsEmpty) continue;
            into.Add(new ClipUsage(ClipKey(s.Clip.Original), s.Clip, table, className, ClipUsageKind.State, s.Name, weaponBlock, s.Sound, s.Line));
        }
        foreach (var a in actions)
        {
            if (a.Clip.IsEmpty) continue;
            into.Add(new ClipUsage(ClipKey(a.Clip.Original), a.Clip, table, className, ClipUsageKind.Action, a.Name, weaponBlock, a.Sound, a.Line));
        }
    }

    private static IReadOnlyList<string> MeshCandidates(string name) =>
        TblFileName.Normalize(Path.GetFileName(name.Trim().Replace('/', '\\')));

    private static string ClassKey(string table, string className) => table + "|" + className;

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }
}
