using Cairn.Formats.Vpp;
using Cairn.Workspace;

namespace Cairn.Tbl.Tests;

/// <summary>A table's bytes and where they came from.</summary>
public sealed record TableSample(string Origin, string FileName, byte[] Bytes, bool IsStock);

/// <summary>Stock tables from the research folder and every .tbl inside the game folder's packfiles.</summary>
internal static class TestData
{
    private static readonly Lazy<IReadOnlyList<TableSample>> LazyGame = new(LoadGame);

    /// <summary>The decompiled stock tables folder, or null.</summary>
    public static string? StockFolder =>
        LocalPaths.Research is { } r && Path.Combine(r, "rfa_workbench", "rf_decomp", "tables") is var p && Directory.Exists(p) ? p : null;

    public static string? GameDirectory => LocalPaths.GameDirectory is { } g && Directory.Exists(g) ? g : null;

    /// <summary>The stock *.tbl files (empty when the research data is absent).</summary>
    public static IReadOnlyList<TableSample> Stock() => StockFolder is { } f
        ? [.. Directory.GetFiles(f, "*.tbl").Order(StringComparer.OrdinalIgnoreCase).Select(p => new TableSample(p, Path.GetFileName(p), File.ReadAllBytes(p), true))]
        : [];

    /// <summary>Every .tbl entry of every .vpp under the game folder (read only), stock = the install folder's own packfiles.</summary>
    public static IReadOnlyList<TableSample> Game() => LazyGame.Value;

    private static List<TableSample> LoadGame()
    {
        var result = new List<TableSample>();
        if (GameDirectory is not { } game) return result;
        foreach (string vpp in Directory.EnumerateFiles(game, "*.vpp", SearchOption.AllDirectories))
        {
            VppArchive archive;
            try { archive = VppArchive.Open(vpp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Cairn.Formats.AssetFormatException) { continue; }
            bool stock = string.Equals(Path.GetDirectoryName(vpp), game.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".tbl", StringComparison.OrdinalIgnoreCase)) continue;
                try { result.Add(new TableSample(vpp + "|" + entry.Name, entry.Name, archive.ReadEntry(entry), stock)); }
                catch (Exception ex) when (ex is IOException or Cairn.Formats.AssetFormatException) { }
            }
        }
        return result;
    }
}
