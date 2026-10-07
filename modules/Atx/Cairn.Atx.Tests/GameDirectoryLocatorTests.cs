using Cairn.Workspace;
using Microsoft.Win32;

namespace Cairn.Atx.Tests;

/// <summary>
/// The detection order, which matters because Alpine Faction's own setting is right far more often
/// than anything else on the list. Every probe runs against a fake registry and real temporary
/// folders, so the assertions hold whether or not this machine has Red Faction installed.
/// </summary>
public sealed class GameDirectoryLocatorTests : IDisposable
{
    private const string AlpineKey = @"SOFTWARE\Volition\Red Faction\Alpine Faction";
    private const string DashKey = @"SOFTWARE\Volition\Red Faction\Dash Faction";
    private const string RetailKey = @"SOFTWARE\Volition\Red Faction";
    private const string SteamKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 20530";
    private const string GogKey = @"SOFTWARE\Nordic Games\Red Faction";

    private readonly string _root = Directory.CreateTempSubdirectory("cairn-atx-gamedir-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Creates a folder that <see cref="GameDirectoryLocator.LooksLikeGameDirectory"/> accepts.</summary>
    private string MakeInstall(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tables.vpp"), "not really, but the probe only looks");
        File.WriteAllText(Path.Combine(dir, "RF.exe"), "nor is this");
        return dir;
    }

    /// <summary>A folder that exists but is plainly not an RF install.</summary>
    private string MakeEmpty(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GameDirectoryDetection? Detect(IRegistryReader registry) =>
        GameDirectoryLocator.DetectDetailed(registry, []);

    [Fact]
    public void AlpineFactionKeyWinsOverEveryOtherProbe()
    {
        string alpine = MakeInstall("alpine");
        var registry = new FakeRegistry()
            .With(RegistryHive.CurrentUser, AlpineKey, "Executable Path",
                Path.Combine(alpine, "RF.exe"))
            .With(RegistryHive.CurrentUser, DashKey, "Executable Path",
                Path.Combine(MakeInstall("dash"), "RF.exe"))
            .With(RegistryHive.LocalMachine, RetailKey, "InstallPath", MakeInstall("retail"))
            .With(RegistryHive.LocalMachine, SteamKey, "InstallLocation", MakeInstall("steam"))
            .With(RegistryHive.LocalMachine, GogKey, "INSTALL_DIR", MakeInstall("gog"));

        var found = Detect(registry);

        Assert.NotNull(found);
        Assert.Equal(GameDirectorySource.AlpineFaction, found!.Source);
        Assert.Equal(Path.GetFullPath(alpine), found.Directory);
        Assert.Equal("Found via Alpine Faction's settings.", found.Hint);
    }

    [Fact]
    public void ExecutablePathResolvesToItsParentDirectory()
    {
        string install = MakeInstall("exe-parent");
        var registry = new FakeRegistry().With(
            RegistryHive.CurrentUser, AlpineKey, "Executable Path", Path.Combine(install, "RF.exe"));

        Assert.Equal(Path.GetFullPath(install), Detect(registry)?.Directory);
    }

    [Fact]
    public void QuotedAndPaddedValueIsAccepted()
    {
        string install = MakeInstall("quoted");
        var registry = new FakeRegistry().With(
            RegistryHive.CurrentUser, AlpineKey, "Executable Path",
            "  \"" + Path.Combine(install, "RF.exe") + "\"  ");

        Assert.Equal(Path.GetFullPath(install), Detect(registry)?.Directory);
    }

    [Fact]
    public void ValueThatIsAlreadyADirectoryIsAccepted()
    {
        string install = MakeInstall("directory-value");
        var registry = new FakeRegistry().With(
            RegistryHive.CurrentUser, AlpineKey, "Executable Path", install);

        Assert.Equal(Path.GetFullPath(install), Detect(registry)?.Directory);
    }

    [Fact]
    public void EnvironmentVariablesAreExpanded()
    {
        string install = MakeInstall("expanded");
        // %TEMP% is not where the folder lives, so expand something that certainly resolves: build
        // the value out of a variable this process definitely has.
        string variable = "CAIRN_ATX_TEST_GAME_DIR";
        Environment.SetEnvironmentVariable(variable, install);
        try
        {
            var registry = new FakeRegistry().With(
                RegistryHive.CurrentUser, AlpineKey, "Executable Path", $"%{variable}%\\RF.exe");
            Assert.Equal(Path.GetFullPath(install), Detect(registry)?.Directory);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void MissingAlpineKeyFallsThroughToDashFaction()
    {
        string dash = MakeInstall("dash-only");
        var registry = new FakeRegistry().With(
            RegistryHive.CurrentUser, DashKey, "Executable Path", Path.Combine(dash, "RF.exe"));

        var found = Detect(registry);

        Assert.Equal(GameDirectorySource.DashFaction, found?.Source);
        Assert.Equal(Path.GetFullPath(dash), found?.Directory);
    }

    [Fact]
    public void AlpinePathThatIsNotAnInstallFallsThroughToTheNextProbe()
    {
        string retail = MakeInstall("retail-fallback");
        var registry = new FakeRegistry()
            .With(RegistryHive.CurrentUser, AlpineKey, "Executable Path",
                Path.Combine(MakeEmpty("not-an-install"), "RF.exe"))
            .With(RegistryHive.LocalMachine, RetailKey, "InstallPath", retail);

        var found = Detect(registry);

        Assert.Equal(GameDirectorySource.Retail, found?.Source);
        Assert.Equal(Path.GetFullPath(retail), found?.Directory);
    }

    [Fact]
    public void PathThatDoesNotExistAtAllFallsThrough()
    {
        string steam = MakeInstall("steam-fallback");
        var registry = new FakeRegistry()
            .With(RegistryHive.CurrentUser, AlpineKey, "Executable Path",
                Path.Combine(_root, "gone", "RF.exe"))
            .With(RegistryHive.LocalMachine, SteamKey, "InstallLocation", steam);

        var found = Detect(registry);

        Assert.Equal(GameDirectorySource.Steam, found?.Source);
        Assert.Equal(Path.GetFullPath(steam), found?.Directory);
    }

    [Fact]
    public void GogEntryIsProbed()
    {
        string gog = MakeInstall("gog-only");
        var registry = new FakeRegistry()
            .With(RegistryHive.LocalMachine, GogKey, "INSTALL_DIR", gog);

        Assert.Equal(GameDirectorySource.Gog, Detect(registry)?.Source);
    }

    [Fact]
    public void SteamIsReadFromTheSixtyFourBitViewFirst()
    {
        string sixtyFour = MakeInstall("steam-64");
        string thirtyTwo = MakeInstall("steam-32");
        var registry = new FakeRegistry()
            .With(RegistryHive.LocalMachine, SteamKey, "InstallLocation", sixtyFour,
                RegistryView.Registry64)
            .With(RegistryHive.LocalMachine, SteamKey, "InstallLocation", thirtyTwo,
                RegistryView.Registry32);

        Assert.Equal(Path.GetFullPath(sixtyFour), Detect(registry)?.Directory);
    }

    [Fact]
    public void VolitionKeysAreReadFromBothRegistryViews()
    {
        // Alpine Faction is a 32-bit process, so from this 64-bit one its HKLM keys live under
        // WOW6432Node. A value that only exists in the 32-bit view must still be found.
        string install = MakeInstall("wow6432");
        var registry = new FakeRegistry().With(
            RegistryHive.LocalMachine, RetailKey, "InstallPath", install, RegistryView.Registry32);

        Assert.Equal(Path.GetFullPath(install), Detect(registry)?.Directory);
    }

    [Fact]
    public void NothingFoundReturnsNullRatherThanThrowing()
    {
        Assert.Null(Detect(new FakeRegistry()));
        Assert.Null(Detect(new ThrowingRegistry()));
    }

    [Fact]
    public void EmptyAndWhitespaceValuesAreIgnored()
    {
        string retail = MakeInstall("retail-after-blank");
        var registry = new FakeRegistry()
            .With(RegistryHive.CurrentUser, AlpineKey, "Executable Path", "   ")
            .With(RegistryHive.LocalMachine, RetailKey, "InstallPath", retail);

        Assert.Equal(GameDirectorySource.Retail, Detect(registry)?.Source);
    }

    [Fact]
    public void CommonPathsAreOnlyTriedAfterEveryRegistryProbe()
    {
        string registryHit = MakeInstall("from-registry");
        string commonHit = MakeInstall("from-common-path");
        var registry = new FakeRegistry()
            .With(RegistryHive.LocalMachine, GogKey, "INSTALL_DIR", registryHit);

        var found = GameDirectoryLocator.DetectDetailed(registry, [commonHit]);
        Assert.Equal(Path.GetFullPath(registryHit), found?.Directory);

        var fallback = GameDirectoryLocator.DetectDetailed(new FakeRegistry(), [commonHit]);
        Assert.Equal(Path.GetFullPath(commonHit), fallback?.Directory);
        Assert.Equal(GameDirectorySource.CommonPath, fallback?.Source);
    }

    [Fact]
    public void LooksLikeGameDirectoryRejectsNonsense()
    {
        Assert.False(GameDirectoryLocator.LooksLikeGameDirectory(null));
        Assert.False(GameDirectoryLocator.LooksLikeGameDirectory("   "));
        Assert.False(GameDirectoryLocator.LooksLikeGameDirectory(MakeEmpty("plain")));
        Assert.False(GameDirectoryLocator.LooksLikeGameDirectory(Path.Combine(_root, "missing")));
        Assert.True(GameDirectoryLocator.LooksLikeGameDirectory(MakeInstall("real")));
    }

    /// <summary>A registry that answers from a dictionary and never touches the real one.</summary>
    private sealed class FakeRegistry : IRegistryReader
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Adds a value, in both views unless one is named.</summary>
        public FakeRegistry With(
            RegistryHive hive, string key, string value, string data, RegistryView? view = null)
        {
            if (view is { } only) _values[Key(hive, only, key, value)] = data;
            else
            {
                _values[Key(hive, RegistryView.Registry32, key, value)] = data;
                _values[Key(hive, RegistryView.Registry64, key, value)] = data;
            }
            return this;
        }

        public string? Read(RegistryHive hive, RegistryView view, string key, string value) =>
            _values.TryGetValue(Key(hive, view, key, value), out string? data) ? data : null;

        private static string Key(RegistryHive hive, RegistryView view, string key, string value) =>
            $"{hive}|{view}|{key}|{value}";
    }

    /// <summary>A registry that fails every read, as a locked-down machine's would.</summary>
    private sealed class ThrowingRegistry : IRegistryReader
    {
        public string? Read(RegistryHive hive, RegistryView view, string key, string value) =>
            throw new System.Security.SecurityException("no");
    }
}
