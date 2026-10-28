using Cairn.Formats;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Tests;

/// <summary>Truncated and mutated files: the readers may refuse them, but only with <see cref="AssetFormatException"/>.</summary>
public class LvlFuzzTests
{
    private static int Scale => int.TryParse(Environment.GetEnvironmentVariable("CAIRN_FUZZ_SCALE"), out int s) && s > 0 ? s : 1;

    private static void ReadAny(byte[] data, string name)
    {
        try
        {
            if (name.EndsWith(".rfg", StringComparison.Ordinal))
            {
                var g = RfgReader.Read(data, name);
                foreach (var group in g.Groups) _ = group.Objects.Count;
            }
            else
            {
                var level = RflLevelReader.Read(data, name);
                LvlSamples.CheckModel(level);
            }
        }
        catch (AssetFormatException)
        {
        }
    }

    /// <summary>Reads copies of <paramref name="data"/> with random bytes and ints overwritten.</summary>
    internal static void Mutate(byte[] data, string name, int iterations, int seed)
    {
        var rng = new Random(seed);
        var copy = new byte[data.Length];
        for (int i = 0; i < iterations; i++)
        {
            data.CopyTo(copy, 0);
            int edits = 1 + rng.Next(8);
            for (int e = 0; e < edits; e++)
            {
                int at = rng.Next(8, Math.Max(9, copy.Length - 4));
                switch (rng.Next(4))
                {
                    case 0: copy[at] = (byte)rng.Next(256); break;
                    case 1: BitConverter.GetBytes(rng.Next(int.MinValue, int.MaxValue)).CopyTo(copy, at); break;
                    case 2: BitConverter.GetBytes(-1).CopyTo(copy, at); break;
                    default: BitConverter.GetBytes(0x7FFF_FFF0).CopyTo(copy, at); break;
                }
            }
            ReadAny(copy, name);
        }
    }

    public static TheoryData<string> Synthetic => ["stock200.rfl", "stock180.rfl", "alpine306.rfl", "alpine300.rfl", "group.rfg"];

    private static byte[] Build(string name) => name switch
    {
        "stock200.rfl" => RflReaderTests.StockLevel(0xC8),
        "stock180.rfl" => RflReaderTests.StockLevel(0xB4),
        "alpine306.rfl" => RflReaderTests.AlpineLevel(306),
        "alpine300.rfl" => RflReaderTests.AlpineLevel(300, alpineEventMatrix: false),
        _ => GroupFile(),
    };

    private static byte[] GroupFile()
    {
        var w = new LvlBytes(300);
        w.U32(RfgReader.Magic).I32(300).I32(1);
        RfgReaderTests.Group(w, "g", moving: true, brushes: 2, lights: l => l.I32(1).Light(1, Vector3.One, 0x18, 3));
        w.Section(0x0AFBAE03, s => s.U32(1).Corona(2, Vector3.Zero, "v.tga"));
        return w.ToArray();
    }

    [Theory]
    [MemberData(nameof(Synthetic))]
    public void Every_truncation_reads_or_is_refused_cleanly(string name)
    {
        var data = Build(name);
        for (int n = 0; n < data.Length; n++) ReadAny(data[..n], name);
    }

    [Theory]
    [MemberData(nameof(Synthetic))]
    public void Random_mutations_read_or_are_refused_cleanly(string name) =>
        Mutate(Build(name), name, 400 * Scale, seed: name.Length);

    [Fact]
    public void Truncated_levels_keep_what_was_before_the_cut()
    {
        var data = RflReaderTests.StockLevel(0xC8);
        // Cut in the middle of the file: the header survives, so the reader returns a truncated level.
        var level = RflLevelReader.Read(data[..(data.Length / 2)], "half.rfl");
        Assert.True(level.IsTruncated);
        Assert.NotEmpty(level.Sections);
    }
}
