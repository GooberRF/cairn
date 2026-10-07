using Cairn.Rfa.SampleGen;

// Regenerates samples/ from scratch:
//     dotnet run --project tools/Cairn.Rfa.SampleGen -- samples/rfa
// With no argument it writes to samples/rfa/ in the repository the tool was built in (the folder holding
// Cairn.sln above the binary), or to ./samples when it cannot find one.

string target = args.Length > 0 ? args[0] : Path.Combine(RepositoryRoot() ?? ".", "samples", "rfa");

target = Path.GetFullPath(target);
var written = SampleSet.WriteAll(target);

Console.WriteLine($"Wrote {written.Count} sample file(s) to {target}:");
foreach (string path in written) Console.WriteLine("  " + Path.GetFileName(path));

static string? RepositoryRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cairn.sln"))) dir = dir.Parent;
    return dir?.FullName;
}
