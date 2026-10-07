using Cairn.Vfx.SampleGen;

// Regenerates samples/vfx from scratch:
//     dotnet run --project tools/Cairn.Vfx.SampleGen -- samples/vfx
// With no argument it writes to samples/vfx/ in the repository the tool was built in (the folder holding
// Cairn.sln above the binary), or to ./samples/vfx when it cannot find one.

string target = args.Length > 0 ? args[0] : Path.Combine(RepositoryRoot() ?? ".", "samples", "vfx");

target = Path.GetFullPath(target);
var written = VfxSampleSet.WriteAll(target);

Console.WriteLine($"Wrote {written.Count} sample file(s) to {target}:");
foreach (string path in written) Console.WriteLine("  " + Path.GetFileName(path));

static string? RepositoryRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cairn.sln"))) dir = dir.Parent;
    return dir?.FullName;
}
