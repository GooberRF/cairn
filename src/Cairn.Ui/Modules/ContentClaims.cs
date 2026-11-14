namespace Cairn.Ui.Modules;

/// <summary>
/// How the shell and the modules choose between modules that take the same file name, by what the file holds: the
/// meshes and the levels modules both open <c>.xff</c> scenes, and only the content tells a level from a prop.
/// </summary>
/// <remarks>
/// <para>
/// Candidates are found by name as always (a kind's <see cref="IDocumentKind.Extensions"/>, a provider's
/// <see cref="IAssetPreviewProvider.CanPreview"/>, a batch converter's <see cref="IArchiveBatchConverter.CanConvert"/>).
/// One candidate is used as it is, without reading the file. Only when several match is the file read (up to
/// <see cref="MaxBytes"/>) and each asked for its claim (<see cref="IDocumentKind.Claim"/> and the others):
/// <see cref="NotMine"/> rules a candidate out, the highest claim wins, and a tie goes to the first in module order.
/// When every candidate says <see cref="NotMine"/>, the choice is the one by name (the first), which then reports
/// the file it cannot read as it always did. The default claim is <see cref="ByName"/>, so modules that never look
/// at the content keep today's behaviour.
/// </para>
/// <para>
/// Convert to… lists every module's targets rather than choosing one, so a converter tells by the content through
/// <see cref="IAssetConverter.TargetsFor(string, ReadOnlySpan{byte})"/> (no targets: not its file) and says how much of
/// the file it needs with <see cref="IAssetConverter.HeadLengthFor"/> (<see cref="WholeFile"/> for a scene).
/// </para>
/// </remarks>
public static class ContentClaims
{
    /// <summary>The claim of a candidate the file is not for.</summary>
    public const int NotMine = 0;

    /// <summary>The default claim: the name matches and the content was not looked at.</summary>
    public const int ByName = 1;

    /// <summary>The usual claim of a candidate that recognised the content as its own.</summary>
    public const int Recognised = 10;

    /// <summary>The most bytes read to ask the claims; a larger file is asked about its start.</summary>
    public const int MaxBytes = 64 << 20;

    /// <summary>The leading bytes a converter's <see cref="IAssetConverter.TargetsFor(string, ReadOnlySpan{byte})"/> gets by default.</summary>
    public const int DefaultHeadLength = 16;

    /// <summary>What <see cref="IAssetConverter.HeadLengthFor"/> returns when the whole file decides (read up to <see cref="MaxBytes"/>).</summary>
    public const int WholeFile = int.MaxValue;

    /// <summary>The kinds that take <paramref name="name"/>'s extension (ignoring case), in order.</summary>
    public static IReadOnlyList<IDocumentKind> KindsFor(IEnumerable<IDocumentKind> kinds, string name)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        string ext = Path.GetExtension(name ?? string.Empty);
        if (ext.Length == 0) return [];
        return [.. kinds.Where(k => k.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// The kind that opens <paramref name="name"/>: the only one that takes its extension, or, when several do, the one
    /// with the highest claim on the bytes <paramref name="read"/> returns (called only then; null when the file cannot
    /// be read, and the first by name is used). Null when no kind takes the extension.
    /// </summary>
    public static IDocumentKind? KindFor(IEnumerable<IDocumentKind> kinds, string name, Func<byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var candidates = KindsFor(kinds, name);
        if (candidates.Count <= 1) return candidates.FirstOrDefault();
        return read() is { } data ? Pick(candidates, k => k.Claim(name, data)) : candidates[0];
    }

    /// <summary>The kind among <paramref name="kinds"/> that opens <paramref name="name"/> holding <paramref name="data"/>, as <see cref="KindFor(IEnumerable{IDocumentKind}, string, Func{byte[]})"/>.</summary>
    public static IDocumentKind? KindFor(IEnumerable<IDocumentKind> kinds, string name, byte[] data) => KindFor(kinds, name, () => data);

    /// <summary>The preview providers among <paramref name="modules"/> whose <see cref="IAssetPreviewProvider.CanPreview"/> takes <paramref name="name"/>, in module order (a provider that throws is skipped).</summary>
    public static IReadOnlyList<(IModule Module, IAssetPreviewProvider Provider)> PreviewProvidersFor(IEnumerable<IModule> modules, string name)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var found = new List<(IModule, IAssetPreviewProvider)>();
        foreach (var module in modules)
        {
            if (module is not IAssetPreviewProvider provider) continue;
            try
            {
                if (provider.CanPreview(name)) found.Add((module, provider));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* a broken provider is skipped */ }
        }
        return found;
    }

    /// <summary>
    /// The provider among <paramref name="candidates"/> that previews <paramref name="name"/> holding
    /// <paramref name="data"/>: the only one, or the highest claim (the first when <paramref name="data"/> is null).
    /// Null for no candidates. Any thread.
    /// </summary>
    public static (IModule Module, IAssetPreviewProvider Provider)? PreviewProviderFor(IReadOnlyList<(IModule Module, IAssetPreviewProvider Provider)> candidates,
        string name, byte[]? data)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1 || data is null) return candidates[0];
        return Pick(candidates, c => c.Provider.Claim(name, data));
    }

    /// <summary>
    /// True when <paramref name="converter"/> keeps the archive entry <paramref name="name"/> holding
    /// <paramref name="data"/> against <paramref name="rivals"/> (the other batch converters that take the name): its
    /// claim is the highest (ties keep it), or every claim is <see cref="NotMine"/>.
    /// </summary>
    public static bool BatchKeeps(IArchiveBatchConverter converter, IReadOnlyList<IArchiveBatchConverter> rivals, string name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(rivals);
        int own = Safe(() => converter.Claim(name, data));
        int best = rivals.Select(r => Safe(() => r.Claim(name, data))).DefaultIfEmpty(NotMine).Max();
        return own >= best || (own <= NotMine && best <= NotMine);
    }

    /// <summary>
    /// The leading bytes the converters among <paramref name="modules"/> want for <paramref name="name"/>
    /// (<see cref="IAssetConverter.HeadLengthFor"/>): at least <see cref="DefaultHeadLength"/>, at most <see cref="MaxBytes"/>.
    /// </summary>
    public static int HeadLengthFor(IEnumerable<IModule> modules, string name)
    {
        ArgumentNullException.ThrowIfNull(modules);
        int length = DefaultHeadLength;
        foreach (var converter in modules.OfType<IAssetConverter>())
            length = Math.Max(length, Safe(() => converter.HeadLengthFor(name)));
        return Math.Min(length, MaxBytes);
    }

    /// <summary>The first <see cref="MaxBytes"/> of the file at <paramref name="path"/> (all of a smaller one), or null when it cannot be read.</summary>
    public static byte[]? ReadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(stream.Length, MaxBytes)];
            int read = 0;
            while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is int n and > 0) read += n;
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The candidate with the highest claim (the first on a tie); the first candidate when every claim is
    /// <see cref="NotMine"/>. A claim that throws counts as <see cref="NotMine"/>.
    /// </summary>
    public static T Pick<T>(IReadOnlyList<T> candidates, Func<T, int> claim)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(claim);
        if (candidates.Count == 0) throw new ArgumentException("There is nothing to choose from.", nameof(candidates));
        T best = candidates[0];
        int bestClaim = NotMine;
        foreach (var candidate in candidates)
        {
            int value = Safe(() => claim(candidate));
            if (value > bestClaim) (best, bestClaim) = (candidate, value);
        }
        return best;
    }

    private static int Safe(Func<int> claim)
    {
        try { return claim(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return NotMine; }
    }
}
