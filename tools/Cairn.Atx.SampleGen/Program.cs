using Cairn.Atx.SampleGen;

// Regenerates samples/atx/ from scratch:
//     dotnet run --project tools/Cairn.Atx.SampleGen -- samples/atx
// With no argument it writes to samples/atx under the repository root (the folder holding Cairn.sln).

string target = args.Length > 0
    ? args[0]
    : Path.Combine(FindRoot(), "samples", "atx");

target = Path.GetFullPath(target);
var written = SampleSet.WriteAll(target);

Console.WriteLine($"Wrote {written.Count} sample file(s) to {target}:");
foreach (string path in written) Console.WriteLine("  " + Path.GetFileName(path));

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cairn.sln"))) dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("Cairn.sln not found above the tool; pass the output folder.");
}
