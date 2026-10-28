using System.Windows.Controls;
using Cairn.Formats;
using Cairn.Formats.Gltf;
using Cairn.Formats.Obj;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Ui.Dialogs;
using Cairn.Rfa.Formats.V3d;
using Cairn.Ui.Modules;
using Cairn.Workspace;

namespace Cairn.Lvl.Ui;

/// <summary>
/// Meshes (.v3m, .v3c), glTF (.gltf, .glb) and OBJ files to editor group files (.rfg): Convert to… on a mesh tab or a
/// packfile entry, and File › Import › Group file from glTF, OBJ or mesh… for a file on disk (the group file opens in a
/// tab when it is written to a folder).
/// </summary>
public sealed partial class LvlModule
{
    /// <summary>The largest mesh, glTF or OBJ file read for a conversion.</summary>
    private const long MaxMeshSourceBytes = 512L << 20;

    /// <summary>True when <paramref name="name"/> is a mesh, glTF or OBJ file, which converts to a group file.</summary>
    internal static bool IsMeshSource(string name) => Path.GetExtension(name).ToLowerInvariant() is ".v3m" or ".v3c" or ".obj" or ".gltf" or ".glb";

    /// <summary>True when <paramref name="name"/> is a glTF file (the only mesh source with objects to carry).</summary>
    internal static bool IsGltfName(string name) => Path.GetExtension(name).ToLowerInvariant() is ".gltf" or ".glb";

    private static ConversionTarget MeshRfgTarget(string sourceName) => new(".rfg", "Group file (.rfg)",
        Path.GetExtension(sourceName).ToLowerInvariant() switch
        {
            ".obj" => "An editor group file for Alpine Faction's level editor: one brush per object or group (Cairn's comment lines restore its brush and face flags)",
            ".gltf" or ".glb" => "An editor group file for Alpine Faction's level editor: one brush per mesh node (Cairn's extras restore brushes, groups and objects)",
            _ => "An editor group file for Alpine Faction's level editor: one brush per submesh, from the chosen level of detail",
        }, true);

    private MenuItem ImportMenuItem()
    {
        var item = new MenuItem
        {
            Header = "_Group file from glTF, OBJ or mesh…",
            ToolTip = "Convert a glTF, OBJ, static mesh (.v3m) or character mesh (.v3c) into an editor group file (.rfg) for Alpine Faction's level editor, then open it",
            Command = new Cairn.Ui.Mvvm.RelayCommand(ImportAsGroupFile),
        };
        System.Windows.Automation.AutomationProperties.SetName(item, "Group file from glTF, OBJ or mesh");
        return item;
    }

    private void ImportAsGroupFile()
    {
        string? start = Settings.Folder is { Length: > 0 } f && Directory.Exists(f) ? f : null;
        var picked = base.Shell.Dialogs.OpenFiles(start, "Convert to a group file",
            "glTF, OBJ and meshes (*.gltf;*.glb;*.obj;*.v3m;*.v3c)|*.gltf;*.glb;*.obj;*.v3m;*.v3c|All files (*.*)|*.*", multiselect: false);
        if (picked.Length == 1) _ = ConvertFileToRfgAsync(picked[0]);
    }

    /// <summary>
    /// Converts a mesh, glTF or OBJ file on disk to a group file: with the Convert window (and the group file opened in a
    /// tab afterwards), or with <paramref name="choice"/> and no dialog. Returns the group file written, or null.
    /// </summary>
    public async Task<string?> ConvertFileToRfgAsync(string path, LvlConvertChoice? choice = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string name = Path.GetFileName(path);
        if (!IsMeshSource(name) || !RfgAvailable)
        {
            base.Shell.Dialogs.ShowError($"Could not convert {name}", "Only glTF (.gltf, .glb), OBJ and mesh (.v3m, .v3c) files convert to group files.");
            return null;
        }
        byte[] bytes;
        try { bytes = await Task.Run(() => AtomicFile.ReadAllBytes(path, MaxMeshSourceBytes)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException or ArgumentException or NotSupportedException)
        {
            base.Shell.Dialogs.ShowError($"Could not read {name}", ex.Message);
            return null;
        }
        var request = new AssetConversionRequest(name, bytes, ".rfg") { FilePath = Path.GetFullPath(path), Interactive = choice is null };
        return await ConvertMeshAsync(request, choice);
    }

    /// <summary>
    /// A mesh, glTF or OBJ source to a group file, with the Convert window when <paramref name="choice"/> is null. A
    /// glTF's buffers and an OBJ's material libraries are read beside the file, or from the same packfile.
    /// </summary>
    private async Task<string?> ConvertMeshAsync(AssetConversionRequest request, LvlConvertChoice? choice)
    {
        string name = request.FileName;
        string? archive = request.ArchivePath;
        string? folder = request.FilePath is { } file ? Path.GetDirectoryName(file) : archive is null ? null : Path.GetDirectoryName(archive);
        string? nextTo = IsGameFolder(folder) ? null : folder;
        string? packfile = request.AddToArchive is null ? null : archive is null ? "the packfile" : Path.GetFileName(archive);
        if (choice is null)
        {
            if (base.Shell.IsDiagnosticRun) return null;
            choice = AskChoice([name], packfile, nextTo, LvlConvertTarget.Rfg, NoNextToReason(folder), false);
            if (choice is null) return null;
        }
        choice = choice with { Options = choice.Options with { Target = LvlConvertTarget.Rfg } };
        var bytes = request.Bytes;
        string? path = request.FilePath;
        var sibling = request.ReadSibling;
        var job = new LvlJob(name, _ => throw new InvalidOperationException("A mesh is not read as a level."), _ => null)
        {
            Direct = (import, ct) => MeshToRfg(bytes, name, path, sibling, import, ct),
        };
        var add = request.AddToArchive is { } addToArchive ? (Func<string, IReadOnlyList<(string Name, byte[] Bytes)>, bool, IReadOnlyList<string>?>)addToArchive : null;
        var outcomes = await RunJobsAsync([job], choice, add, request.ArchiveEntryNames, packfile, nextTo, request.Interactive, null);
        var outcome = outcomes?.FirstOrDefault();
        if (outcome is not { Succeeded: true }) return null;
        // a group file written to a folder is a document this module opens: show it
        if (request.Interactive && !base.Shell.IsDiagnosticRun && choice.Place != LvlOutputPlace.IntoPackfile && File.Exists(outcome.Written[0]))
            base.Shell.OpenFile(outcome.Written[0]);
        return outcome.Written[0];
    }

    /// <summary>
    /// Reads a mesh (.v3m, .v3c), glTF (.gltf, .glb) or OBJ file and converts it to a group file. A .gltf's buffers and
    /// images and an OBJ's .mtl come from beside <paramref name="filePath"/>, else through <paramref name="sibling"/>
    /// (the rest of a packfile). Any thread.
    /// </summary>
    /// <exception cref="AssetFormatException">The file cannot be read.</exception>
    internal static LvlConversion MeshToRfg(byte[] bytes, string name, string? filePath, Func<string, byte[]?>? sibling, RfgImportOptions options, CancellationToken ct)
    {
        string? folder = filePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(filePath));
        byte[]? Beside(string relative)
        {
            if (folder is not null)
            {
                // a .gltf's URIs stay inside its folder (the reader has checked they are relative)
                string target = Path.GetFullPath(Path.Combine(folder, relative));
                string prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
                if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(target)) return AtomicFile.ReadAllBytes(target, MaxMeshSourceBytes);
                return null;
            }
            if (sibling is null) return null;
            // packfiles have no folders: a path inside one is looked up by its file name
            return sibling(relative) ?? sibling(Path.GetFileName(relative));
        }
        switch (Path.GetExtension(name).ToLowerInvariant())
        {
            case ".v3m":
            case ".v3c":
                return LvlConverter.MeshToRfg(V3dReader.Read(bytes, name), name, options, ct);
            case ".gltf":
            case ".glb":
                return LvlConverter.GltfToRfg(GltfReader.Read(bytes, name, Beside), name, options, ct);
            case ".obj":
            {
                var obj = ObjReader.Read(bytes, name);
                MtlDocument? materials = null;
                if (folder is not null) materials = ObjReader.ReadMaterialLibraries(obj, folder).Materials;
                else if (sibling is not null)
                {
                    materials = new MtlDocument();
                    foreach (string library in obj.MaterialLibraries)
                    {
                        if (Beside(library) is not { } mtlBytes) continue;
                        var mtl = ObjReader.ReadMtl(mtlBytes, library);
                        int offset = materials.Materials.Count;
                        materials.Materials.AddRange(mtl.Materials);
                        foreach (var comment in mtl.Comments)
                            materials.Comments.Add(comment with { MaterialIndex = comment.MaterialIndex < 0 ? offset - 1 : comment.MaterialIndex + offset });
                    }
                }
                return LvlConverter.ObjToRfg(obj, materials, name, options, ct);
            }
            default:
                throw new AssetFormatException($"'{name}' is not a glTF, OBJ or mesh file.");
        }
    }
}
