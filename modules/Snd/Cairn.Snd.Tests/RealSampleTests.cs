using Cairn.Formats;
using Cairn.Formats.Audio;
using Cairn.Vpp.Model;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Snd.Tests;

/// <summary>
/// Every .vse/.vmu of the local PlayStation 2 files (loose files under <see cref="LocalPaths.Ps2Directory"/> and
/// <see cref="LocalPaths.MeshesStuffDirectory"/>, and the entries of the PS2 packfiles), when present; passes trivially
/// without them. Files are only read.
/// </summary>
public class RealSampleTests(ITestOutputHelper output)
{
    private static IEnumerable<(string Name, Func<byte[]> Read)> Samples()
    {
        foreach (var root in new[] { LocalPaths.Ps2Directory, LocalPaths.MeshesStuffDirectory })
        {
            if (root is null || !Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (Ps2Sound.IsPs2SoundName(file)) yield return (file, () => File.ReadAllBytes(file));
                else if (file.EndsWith(".vpp", StringComparison.OrdinalIgnoreCase))
                {
                    var package = VppPackage.Open(file);
                    foreach (var item in package.Items.Where(i => Ps2Sound.IsPs2SoundName(i.Name)))
                        yield return ($"{Path.GetFileName(file)}:{item.Name}", () => item.Source.ReadAll());
                }
            }
        }
    }

    [Fact]
    public void EverySampleDecodesSanely()
    {
        int vse = 0, vmu = 0, refused = 0, looped = 0, oldLayout = 0, timeMismatch = 0;
        var failures = new List<string>();
        var codes = new Dictionary<string, int>();
        foreach (var (name, read) in Samples())
        {
            byte[] bytes = read();
            DecodedSound sound;
            try { sound = Ps2Sound.Decode(bytes, name); }
            catch (AssetFormatException ex)
            {
                refused++;
                if (bytes.Length >= 12) failures.Add($"{name}: refused ({ex.Message})");
                continue;
            }
            bool isVmu = name.EndsWith(".vmu", StringComparison.OrdinalIgnoreCase);
            if (isVmu) vmu++; else vse++;
            foreach (var p in sound.Problems) codes[p.Code] = codes.GetValueOrDefault(p.Code) + 1;
            if (sound.Loop is not null) looped++;
            if (sound.Problems.Any(p => p.Code == "SND011")) oldLayout++;
            if (sound.Problems.Any(p => p.Code == "SND008")) timeMismatch++;

            // sane: mono effects at a standard rate, stereo music, some sound, no out-of-range frames, rarely clipped
            if (sound.FrameCount == 0) failures.Add($"{name}: no samples");
            if (sound.SampleRate is not (11025 or 22050 or 44100)) failures.Add($"{name}: {sound.SampleRate} Hz");
            if (sound.Channels != (isVmu ? 2 : 1)) failures.Add($"{name}: {sound.Channels} channels");
            if (sound.Problems.Any(p => p.Code == "SND003")) failures.Add($"{name}: out-of-range frames");
            if (sound.Duration.TotalSeconds > (isVmu ? 600 : 120)) failures.Add($"{name}: {sound.Duration}");
            long clipped = sound.Samples.Count(s => s is short.MaxValue or short.MinValue);
            if (clipped > sound.Samples.Length / 100) failures.Add($"{name}: {clipped} clipped samples of {sound.Samples.Length}");
            var info = Ps2Sound.Probe(bytes, name);
            if (Math.Abs(info.Duration!.Value.TotalSeconds - sound.Duration.TotalSeconds) > 1e-6) failures.Add($"{name}: probe {info.Duration} vs decode {sound.Duration}");
        }
        output.WriteLine($"vse {vse}, vmu {vmu}, refused {refused}, looped {looped}, old layout {oldLayout}, header time off by >50 ms {timeMismatch}");
        output.WriteLine("problem codes: " + string.Join(", ", codes.OrderBy(c => c.Key).Select(c => $"{c.Key} x{c.Value}")));
        foreach (var f in failures.Take(40)) output.WriteLine(f);
        Assert.Empty(failures);
        if (vse + vmu > 0) Assert.True(refused <= 4, $"{refused} files refused");
    }
}
