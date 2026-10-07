using Cairn.Formats.Vpp;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Tests;

/// <summary>Byte-for-byte comparison of two packfiles with the same layout, classifying every difference by region.</summary>
internal sealed record PackfileDiff(long LengthA, long LengthB, IReadOnlyDictionary<string, long> BytesByRegion)
{
    public bool Identical => LengthA == LengthB && BytesByRegion.Count == 0;

    /// <summary>True when the only differences are in places a writer fills with zeros (padding, bytes after a name's NUL).</summary>
    public bool OnlyPaddingOrNameTails => LengthA == LengthB
        && BytesByRegion.Keys.All(k => k is "header padding" or "name tail" or "directory padding" or "data padding");

    public override string ToString() =>
        (LengthA != LengthB ? $"length {LengthA:N0} vs {LengthB:N0}; " : "")
        + string.Join(", ", BytesByRegion.Select(kv => $"{kv.Value:N0} bytes differ in {kv.Key}"));
}

internal static class PackfileComparer
{
    public static PackfileDiff Compare(string pathA, string pathB, VppPackage layout)
    {
        var regions = new Dictionary<string, long>();
        using var a = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var b = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long directoryEnd = VppArchive.BlockSize + VppPackage.AlignUp((long)layout.Count * VppArchive.EntryBytes);
        var bufA = new byte[1 << 20];
        var bufB = new byte[1 << 20];
        long position = 0;
        long common = Math.Min(a.Length, b.Length);
        while (position < common)
        {
            int want = (int)Math.Min(bufA.Length, common - position);
            a.ReadExactly(bufA, 0, want);
            b.ReadExactly(bufB, 0, want);
            if (!bufA.AsSpan(0, want).SequenceEqual(bufB.AsSpan(0, want)))
            {
                for (int i = 0; i < want; i++)
                {
                    if (bufA[i] != bufB[i])
                    {
                        string region = Classify(position + i, directoryEnd, layout, bufA, bufB, i);
                        regions[region] = regions.GetValueOrDefault(region) + 1;
                    }
                }
            }
            position += want;
        }
        return new PackfileDiff(a.Length, b.Length, regions);
    }

    private static string Classify(long offset, long directoryEnd, VppPackage layout, byte[] bufA, byte[] bufB, int i)
    {
        if (offset < 16) return "header fields";
        if (offset < VppArchive.BlockSize) return "header padding";
        if (offset < directoryEnd)
        {
            long rel = offset - VppArchive.BlockSize;
            long entry = rel / VppArchive.EntryBytes;
            if (entry >= layout.Count) return "directory padding";
            int within = (int)(rel % VppArchive.EntryBytes);
            if (within >= VppArchive.NameBytes) return "directory sizes";
            // After the name's NUL (the writer leaves zeros there).
            return within > layout.Items[(int)entry].Name.Length ? "name tail" : "names";
        }
        // Opened packages list entries in offset order: binary search for the last entry starting at or before the offset.
        int lo = 0, hi = layout.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (((ArchiveSource)layout.Items[mid].Source).Offset <= offset) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        _ = bufA; _ = bufB; _ = i;
        if (found < 0) return "outside any entry";
        var s = (ArchiveSource)layout.Items[found].Source;
        if (offset < s.Offset + s.Length) return "data";
        if (offset < s.Offset + VppPackage.AlignUp(s.Length)) return "data padding";
        return "outside any entry";
    }
}
