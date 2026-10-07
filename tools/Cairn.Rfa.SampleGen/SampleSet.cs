using System.Text;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.SampleGen;

/// <summary>
/// Builds the contents of <c>samples/</c>: a small skinned character (<see cref="SampleFigure"/>) with
/// its texture, three clean clips for it — a looping idle, a looping walk and an eased wave — one clip
/// that trips a spread of lint rules, and a README describing them. Everything is generated here from
/// code; no game data is involved, and two runs write byte-identical files.
/// </summary>
public static class SampleSet
{
    /// <summary>The character mesh.</summary>
    public const string MeshFile = "sample_figure.v3c";

    /// <summary>Its texture (the material's diffuse map).</summary>
    public const string TextureFile = "sample_figure.tga";

    /// <summary>The looping idle.</summary>
    public const string IdleFile = "sample_figure_idle.rfa";

    /// <summary>The looping walk.</summary>
    public const string WalkFile = "sample_figure_walk.rfa";

    /// <summary>The eased wave, meant to play as an action.</summary>
    public const string WaveFile = "sample_figure_wave.rfa";

    /// <summary>The deliberately broken clip.</summary>
    public const string BrokenFile = "sample_figure_broken.rfa";

    /// <summary>What each file is.</summary>
    public const string ReadmeFile = "README.md";

    /// <summary>The submesh name stored in the mesh.</summary>
    public const string SubmeshName = "sample_figure";

    /// <summary>The clips that lint clean against the mesh.</summary>
    public static IReadOnlyList<string> CleanClips { get; } = [IdleFile, WalkFile, WaveFile];

    /// <summary>Writes every sample file into <paramref name="directory"/>, creating it if needed.</summary>
    public static IReadOnlyList<string> WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();

        Write(directory, MeshFile, V3dWriter.Write(V3dBuilder.Build(SampleFigure.Description(SubmeshName, TextureFile))), written);
        Write(directory, TextureFile, TinyTgaWriter.Tga24(SampleFigure.TextureSize, SampleFigure.TextureSize, SampleFigure.Texture()), written);
        Write(directory, IdleFile, RfaWriter.Write(SampleClips.Idle()), written);
        Write(directory, WalkFile, RfaWriter.Write(SampleClips.Walk()), written);
        Write(directory, WaveFile, RfaWriter.Write(SampleClips.Wave()), written);
        Write(directory, BrokenFile, RfaWriter.Write(SampleClips.Broken()), written);
        Write(directory, ReadmeFile, Encoding.UTF8.GetBytes(Readme()), written);
        return written;
    }

    private static void Write(string directory, string name, byte[] bytes, List<string> written)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        written.Add(path);
    }

    /// <summary>The README, with LF line endings and no byte-order mark.</summary>
    public static string Readme()
    {
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');

        Line("# Samples");
        Line();
        Line("Small, fully synthetic files to try Cairn on without the game's data. Every file here,");
        Line("this README included, is written by `tools/Cairn.Rfa.SampleGen`; nothing comes from the game.");
        Line("To regenerate them (the output is byte-for-byte the same every time):");
        Line();
        Line("```");
        Line("dotnet run --project tools/Cairn.Rfa.SampleGen -- samples/rfa");
        Line("```");
        Line();
        Line("`SampleTests` in the test project regenerates the set and checks that these files match it, that");
        Line("the clean files have no errors or warnings, and that the broken clip trips the rules listed below.");
        Line();
        Line("| File | What it is |");
        Line("| --- | --- |");
        Line($"| `{MeshFile}` | A blocky 15-bone biped about 1.77 m tall, facing +Z with its feet on the ground: pelvis, spine, head, and upper arm, forearm, hand, thigh, shin and foot on each side. Each box is skinned to one bone. One material, three collision spheres (`head`, `torso`, `legs`) and a prop point (`hand_grip`, in the right hand). |");
        Line($"| `{TextureFile}` | Its 64x64 24-bit texture: one tile per material (shirt, skin, trousers, boots, belt, hair, gloves), with a face on the head's front and a zip on the shirt's front so you can tell which way the figure faces. |");
        Line($"| `{IdleFile}` | A two-second breathing idle that loops: the weight sways from foot to foot and the head looks around. |");
        Line($"| `{WalkFile}` | A walk cycle in place (32 frames) that loops: legs, knees, feet, arms and pelvis all keyed, with smooth Bezier control points on the pelvis bob. |");
        Line($"| `{WaveFile}` | The right arm rises, waves twice and comes back down, with ease-in/ease-out on every key. Made to play as an action: the arm's bones have weight 10 and the rest 0, with 480/640-tick ramps. |");
        Line($"| `{BrokenFile}` | A one-second clip that is wrong on purpose. Open it to see the Problems panel and its quick fixes. |");
        Line();
        Line("The clips address the figure's 15 bones by index, so preview them on it: add this folder as a search");
        Line($"folder (Settings) and choose `{MeshFile}` as a clip's preview mesh.");
        Line();
        Line($"## What `{BrokenFile}` gets wrong");
        Line();
        Line("| Code | Severity | Problem |");
        Line("| --- | --- | --- |");
        foreach (var (code, what) in SampleClips.BrokenRules)
        {
            var info = ClipRules.Find(code) ?? throw new InvalidOperationException($"Unknown rule {code}.");
            Line($"| {code} | {info.Severity} | {what} |");
        }
        return text.ToString();
    }
}
