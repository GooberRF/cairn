namespace Cairn.Assets;

/// <summary>One <c>.peg</c> texture pack a <see cref="PegTextureSet"/> may search.</summary>
/// <param name="Name">The pack's file name (no folder).</param>
/// <param name="Read">Reads the whole pack; null (or an exception) when it is gone or cannot be read.</param>
public sealed record PegPack(string Name, Func<byte[]?> Read);

/// <summary>A texture found in a <see cref="PegTextureSet"/>.</summary>
/// <param name="Pack">The pack it is in.</param>
/// <param name="Texture">The texture's directory entry.</param>
public sealed record PegTextureHit(string Pack, PegTexture Texture);

/// <summary>
/// The textures inside a list of <c>.peg</c> texture packs, searched in the given order (the first pack holding a
/// name wins), so a mesh or level that keeps its pixels in packs beside it (Red Faction II, the PlayStation 2 game)
/// previews and converts with them. Packs are read in order, each once, only as far as a lookup needs: a texture in the
/// first pack never reads the others. Safe to use from any thread.
/// <para>
/// A texture is found by its stem whatever the extension asked for: <c>flag.tga</c>, <c>flag.vbm</c> and <c>flag</c>
/// all find a stored <c>flag.tga</c> or <c>flag.vbm</c>. <see cref="Layer"/> offers each texture to a resolver as a
/// <c>.tga</c> of its first frame (an animated texture shows its first frame).
/// </para>
/// </summary>
public sealed class PegTextureSet
{
    private readonly IReadOnlyList<PegPack> _packs;
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Pack, PegTexture Texture)> _byStem = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[]?[] _bytes;
    private readonly List<(string Pack, string Reason)> _unreadable = [];
    private int _read;

    /// <param name="label">Where the packs are, for messages ("beside burke.rfc").</param>
    /// <param name="packs">The packs in search order.</param>
    public PegTextureSet(string label, IEnumerable<PegPack> packs)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(packs);
        Label = label;
        _packs = [.. packs];
        _bytes = new byte[]?[_packs.Count];
    }

    /// <summary>Where the packs are, for messages.</summary>
    public string Label { get; }

    /// <summary>The packs' names in search order.</summary>
    public IReadOnlyList<string> PackNames => [.. _packs.Select(p => p.Name)];

    /// <summary>True when there are no packs to search.</summary>
    public bool IsEmpty => _packs.Count == 0;

    /// <summary>Packs that could not be read, with the reason (of those read so far).</summary>
    public IReadOnlyList<(string Pack, string Reason)> Unreadable
    {
        get { lock (_gate) return [.. _unreadable]; }
    }

    /// <summary>The texture <paramref name="textureName"/> (bare name, any extension, case-insensitive), or null.</summary>
    public PegTextureHit? Find(string textureName) =>
        Lookup(textureName) is { } hit ? new PegTextureHit(_packs[hit.Pack].Name, hit.Texture) : null;

    /// <summary>True when a pack holds <paramref name="textureName"/> (see <see cref="Find"/>).</summary>
    public bool Contains(string textureName) => Lookup(textureName) is not null;

    /// <summary>Decodes the first frame of <paramref name="textureName"/>, or null when no pack holds it or it cannot be decoded.</summary>
    public BgraImage? Decode(string textureName)
    {
        if (Lookup(textureName) is not { } hit) return null;
        byte[]? bytes;
        lock (_gate) bytes = _bytes[hit.Pack];
        if (bytes is null) return null;
        try
        {
            return PegCodec.DecodeFrame(bytes, hit.Texture);
        }
        catch (ImageDecodeException)
        {
            return null;
        }
    }

    /// <summary>Decodes every frame of <paramref name="textureName"/> (one for a still), or null when no pack holds it or it cannot be decoded.</summary>
    public IReadOnlyList<BgraImage>? DecodeFrames(string textureName)
    {
        if (Lookup(textureName) is not { } hit) return null;
        byte[]? bytes;
        lock (_gate) bytes = _bytes[hit.Pack];
        if (bytes is null) return null;
        try
        {
            return PegCodec.DecodeFrames(bytes, hit.Texture);
        }
        catch (ImageDecodeException)
        {
            return null;
        }
    }

    /// <summary>The first pack's entry for a name, reading further packs in order until one holds it (or none is left).</summary>
    private (int Pack, PegTexture Texture)? Lookup(string textureName)
    {
        if (string.IsNullOrWhiteSpace(textureName) || _packs.Count == 0) return null;
        string stem = Stem(textureName);
        lock (_gate)
        {
            while (true)
            {
                if (_byStem.TryGetValue(stem, out var hit)) return hit;
                if (_read >= _packs.Count) return null;
                ReadNext();
            }
        }
    }

    /// <summary>Reads the next pack into the index (under the lock); an earlier pack's texture of a name wins.</summary>
    private void ReadNext()
    {
        int p = _read++;
        byte[]? data;
        PegFile file;
        try
        {
            data = _packs[p].Read();
            if (data is null) { _unreadable.Add((_packs[p].Name, "it could not be read")); return; }
            file = PegCodec.Read(data, _packs[p].Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException or InvalidDataException)
        {
            _unreadable.Add((_packs[p].Name, ex.Message));
            return;
        }
        bool used = false;
        foreach (var texture in file.Textures)
        {
            if (texture.CanDecode && _byStem.TryAdd(Stem(texture.Name), (p, texture))) used = true;
        }
        // only packs that provide a texture stay in memory
        if (used) _bytes[p] = data;
    }

    /// <summary>
    /// The first frame of <paramref name="textureName"/> as a TGA file (32-bit when any pixel is not opaque, else 24-bit),
    /// or null when no pack holds it or it cannot be decoded.
    /// </summary>
    public byte[]? ReadAsTga(string textureName) => Decode(textureName) is { } image ? TgaWriter.Write(image, HasAlpha(image)) : null;

    /// <summary>True when any pixel of <paramref name="image"/> is not fully opaque.</summary>
    public static bool HasAlpha(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var px = image.Pixels;
        for (int i = 3; i < px.Length; i += 4)
        {
            if (px[i] != 255) return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a name <see cref="Layer"/> offers: a <c>.tga</c> name (or a bare stem)
    /// whose texture a pack holds.
    /// </summary>
    public bool Offers(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        string ext = Path.GetExtension(candidate);
        return (ext.Length == 0 || ext.Equals(".tga", StringComparison.OrdinalIgnoreCase)) && Contains(candidate);
    }

    /// <summary>Opens a name <see cref="Offers"/> as a TGA stream, or null.</summary>
    public Stream? Open(string candidate) => Offers(candidate) && ReadAsTga(candidate) is { } tga ? new MemoryStream(tga, writable: false) : null;

    /// <summary>
    /// A resolver that finds the packs' textures first (as <c>.tga</c> files: a request for <c>flag.vbm</c> or
    /// <c>flag.tga</c> ends at <c>flag.tga</c> in the supersede chain), then resolves as <paramref name="resolver"/>
    /// does. A resolver keeps one in-memory layer, so a caller that also has other in-memory files (the rest of a
    /// packfile) builds one layer with <see cref="Offers"/> and <see cref="Open"/> and its own files instead.
    /// </summary>
    public AssetResolver Layer(AssetResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return resolver.WithOverlay(Label, Offers, Open);
    }

    /// <summary>
    /// The packs to search for an asset's textures, in order, among <paramref name="available"/> pack names (the
    /// <c>.peg</c> files of its folder or packfile): <c>&lt;stem&gt;&lt;suffix&gt;.peg</c> for each preferred suffix
    /// (<c>_v3d</c> for a static mesh, <c>_vcm</c> for a character, then none), the same with the stem cut at its last
    /// <c>_</c> again and again (<c>burke_b</c>, then <c>burke</c>); then the packs whose names start with one of those
    /// stems and an underscore (skins such as <c>military_grunt_a.peg</c>); then every other pack in the order given,
    /// unless there are more than <paramref name="maxOtherPacks"/> of them (a whole game's packfile: searching it all
    /// for a texture that is not there would read hundreds of packs). Names compare case-insensitively; each pack is
    /// listed once.
    /// </summary>
    /// <param name="assetName">The mesh or level's file name.</param>
    /// <param name="available">The pack names that exist.</param>
    /// <param name="suffixes">The pack suffixes to try for each stem, best first (for example "_vcm", "").</param>
    /// <param name="maxOtherPacks">The most unrelated packs searched at the end.</param>
    public static IReadOnlyList<string> SearchOrder(string assetName, IEnumerable<string> available, IReadOnlyList<string> suffixes, int maxOtherPacks = 24)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(suffixes);
        var packs = available.Where(n => n.EndsWith(".peg", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byName = packs.ToDictionary(n => n, StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stems = new List<string>();
        string stem = Path.GetFileNameWithoutExtension(assetName);
        while (stem.Length > 0)
        {
            stems.Add(stem);
            foreach (string suffix in suffixes)
            {
                if (byName.TryGetValue(stem + suffix + ".peg", out var hit) && taken.Add(hit)) order.Add(hit);
            }
            int cut = stem.LastIndexOf('_');
            if (cut <= 0) break;
            stem = stem[..cut];
        }
        foreach (string s in stems.Where(s => s.Length >= 3))
        {
            foreach (string pack in packs)
            {
                if (pack.StartsWith(s + "_", StringComparison.OrdinalIgnoreCase) && taken.Add(pack)) order.Add(pack);
            }
        }
        var others = packs.Where(p => !taken.Contains(p)).ToList();
        if (others.Count <= maxOtherPacks) order.AddRange(others);
        return order;
    }

    /// <summary>The packs beside a file on disk (its folder's <c>.peg</c> files), in <see cref="SearchOrder"/>.</summary>
    /// <param name="label">Where the packs are, for messages.</param>
    /// <param name="path">The file's full path.</param>
    /// <param name="suffixes">See <see cref="SearchOrder"/>.</param>
    public static PegTextureSet ForFolder(string label, string path, IReadOnlyList<string> suffixes)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? folder = Path.GetDirectoryName(path);
        IEnumerable<string> names = [];
        try
        {
            if (folder is not null && Directory.Exists(folder))
                names = [.. Directory.EnumerateFiles(folder, "*.peg").Select(f => Path.GetFileName(f)).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { names = []; }
        var order = SearchOrder(Path.GetFileName(path), names, suffixes);
        return new PegTextureSet(label, order.Select(n => new PegPack(n, () => File.ReadAllBytes(Path.Combine(folder!, n)))));
    }

    private static string Stem(string name)
    {
        string bare = Path.GetFileName(name.Trim());
        string ext = Path.GetExtension(bare);
        return ext.Length > 0 && AssetResolver.IsTextureName(bare) ? bare[..^ext.Length] : bare;
    }
}
