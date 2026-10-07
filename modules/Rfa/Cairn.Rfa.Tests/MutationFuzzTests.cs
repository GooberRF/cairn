using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Editing;
using Cairn.Formats;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Vpp;
using Cairn.Rfa.Interchange;
using Cairn.Rfa.Linting;
using Cairn.Rfa.Retarget;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Fixed-seed mutation fuzzing of everything the app reads from disk: clips, meshes, archives, tables,
/// glTF, and the JSON files (rig profiles, bone maps, retarget options, settings, recovery snapshots,
/// the library cache, clipboard data). Each input is mutated (truncation, bit flips, 0x00/0xFF/0x7F/0x80
/// runs, interesting int16/int32 values, splices, duplicated and removed chunks; JSON also structurally)
/// and must either load or fail with the documented exception only. Inputs that load are linted and
/// written back, and what is written must read back. Every input is bounded in time and allocation, and
/// the whole run sits under a watchdog so a hang fails the test instead of stalling it. The stock corpus
/// is sampled in place (a few files by stride) when present.
/// </summary>
public class MutationFuzzTests(ITestOutputHelper output)
{
    // ── Budgets (iteration counts are the knobs for runtime) ─────────────────

    private const int RfaMutationsPerSeed = 220;
    private const int RfaTruncationsPerSeed = 40;
    private const int CorpusClips = 12;
    private const int CorpusMorphClips = 3;
    private const int CorpusMutationsPerFile = 30;
    private const int CorpusMaxBytes = 48 * 1024;
    private const int V3dMutationsPerSeed = 300;
    private const int V3dTruncationsPerSeed = 40;
    private const int CorpusMeshesPerKind = 3;
    private const int VppMutations = 400;
    private const int VppOnDiskEvery = 20;
    private const int TblMutationsPerSeed = 60;
    private const int CorpusTblWindows = 2;
    private const int CorpusTblWindowChars = 3000;
    private const int GltfMutations = 300;
    private const int JsonMutationsPerSeed = 150;
    private const int FileJsonMutationsPerSeed = 20;

    /// <summary>
    /// Multiplies every mutation count: 1 in the normal test run; set <c>CAIRN_FUZZ_SCALE</c> (e.g. 50) for a
    /// deeper run of the same deterministic sequences, extended further.
    /// </summary>
    private static readonly int Scale =
        int.TryParse(Environment.GetEnvironmentVariable("CAIRN_FUZZ_SCALE"), out int scale) && scale > 0 ? scale : 1;

    /// <summary>Any single input taking longer than this fails the test.</summary>
    private static readonly TimeSpan InputTimeBudget = TimeSpan.FromSeconds(2);

    /// <summary>The whole fact must finish within this, or it fails naming the input it is stuck on.</summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(90 * Scale);

    /// <summary>Per-input allocation limit: this much, or this many bytes per input byte if more.</summary>
    private const long AllocFloorBytes = 64L * 1024 * 1024;
    private const long AllocPerInputByte = 512;

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static readonly V3dFile LintMesh = V3dFormatTests.SampleCharacter();
    private static readonly Skeleton LintSkeleton = Skeleton.FromFile(LintMesh);

    private static IEnumerable<(string Name, RfaClip Clip)> SyntheticClips()
    {
        yield return ("v8 morph 3 bones (0-key and 1-key tracks)", RfaFormatTests.SampleClip(8, true));
        yield return ("v7 morph 3 bones", RfaFormatTests.SampleClip(7, true));
        yield return ("v8 no morph", RfaFormatTests.SampleClip(8, false));
        yield return ("v8 50 bones", EditingTestClips.Make(50, 8));
        yield return ("v7 1 bone", EditingTestClips.Make(1, 7));
        yield return ("v7 1 bone + morph", EditingTestClips.Make(1, 7) with { Morph = EditingTestClips.MakeV7Morph(5) });
        yield return ("v8 keyframes without vertices", RfaFormatTests.SampleClip(8, false) with { Morph = new RfaMorph([], 3, [0, 0, 0], null, [], []) });
        yield return ("v8 empty bones", new RfaClip { Bones = [new RfaBoneTrack(1f, [], []), new RfaBoneTrack(0f, [], [])] });
        yield return ("v8 no bones", new RfaClip());
    }

    private static IEnumerable<(string Name, V3dFile Mesh)> SyntheticMeshes()
    {
        var character = V3dFormatTests.SampleCharacter();
        yield return ("character", character);
        var staticMesh = character with
        {
            Header = character.Header with { Signature = V3dHeader.StaticSignature },
            Sections = [.. character.Sections.Where(s => s is not V3dBoneSection)],
        };
        yield return ("static", staticMesh);
        yield return ("empty", new V3dFile { Header = new V3dHeader(V3dHeader.StaticSignature, V3dHeader.CurrentVersion, 0, 0, 0, 0, 0, 0, 0, 0), Sections = [] });
    }

    private const string EntityTbl = """
        // entity classes
        #Entity Classes
        $Name:                  "miner1"
        $V3D Filename:          "miner.vcm"
        $Mass:                  100
        +State:                 "stand"                 "ult2_stand.mvf"
        +State:                 "jeep_drive"            "ult2_jeep_drive.mvf"
        +Action:                "reload"                "ult2_reload.mvf"              "Ultor Reload"
        +Weapon Specific: "Shotgun"
        +Spine Adjustment: -17.0
        +State:                 "stand"                 "park_shotgun_stand.mvf"
        +Action:                "fire_stand"            "park_shotgun_firepump.mvf"    ""
        $Collision Sphere:      "head" 2.0 2.0
        $Name:                  "rat"
        $V3D Filename:          "rat.vcm"
        +State:                 "swim_stand"            "rat_swim.mvf"
        #End
        """;

    private const string WeaponsTbl = """
        #Primary
        $Name:                  "Shotgun"
        $1st Person Mesh:       "fp_shotgun.v3d"
        $3rd Person V3D:        "shotgun.v3m"
        $V3D Filename:          ""
        +State:                 "idle"                  "fp_shotgun_idle.mvf"
        +Action:                "fire"                  "fp_shotgun_fire.mvf"          "Shotgun Fire"
        #End
        """;

    private const string MultiTbl = """
        #Characters
        $Name:                  "Parker"
        $EntityType:            "miner1"
        $EntityAnimType:        "miner1"
        $SkinName:              "park_skin"
        $ImageName:             "park.tga"
        +Custom Fpgun:          "Shotgun"               "fp_park_shotgun"
        #End
        """;

    private const string FpgunTbl = """
        $Name:                  "fp_park_shotgun"
        $Model:                 "fp_park_shotgun.v3c"
        $Hand Texture:          "hand.tga"
        $Forearm Texture:       "forearm.tga"
        $Bicep Texture:         "bicep.tga"
        """;

    // ── Clips ────────────────────────────────────────────────────────────────

    [Fact]
    public void ClipsSurviveMutation()
    {
        Harness.Run(output, "rfa", h =>
        {
            var seeds = SyntheticClips().Select(s => (s.Name, s.Clip, Bytes: RfaWriter.Write(s.Clip))).ToList();
            foreach (var (name, path) in CorpusClipSample())
            {
                byte[] bytes = File.ReadAllBytes(path);
                seeds.Add(($"corpus {name}", RfaReader.Read(bytes, name), bytes));
            }

            // Crafted, not random: every bone-table entry points at the same big bone record, so a small
            // file would expand into (bones x keys) keys if the reader trusted the offsets.
            byte[] aliased = AliasedBones(bones: 4000, keys: 2000);
            h.Input("rfa crafted: 4000 bone offsets aliasing one 2000-key bone", aliased.Length,
                () => ClipPipeline(h, aliased, RfaFormatTests.SampleClip()));

            var m = new Mutator(0x5EED_0001);
            for (int s = 0; s < seeds.Count; s++)
            {
                var (name, reference, bytes) = seeds[s];
                byte[] other = seeds[(s + 1) % seeds.Count].Bytes;
                bool corpus = name.StartsWith("corpus", StringComparison.Ordinal);
                h.Input($"rfa '{name}' unmutated", bytes.Length, () => ClipPipeline(h, bytes, reference));
                int truncations = corpus ? RfaTruncationsPerSeed / 4 : RfaTruncationsPerSeed;
                for (int t = 1; t <= truncations; t++)
                {
                    int at = (int)((long)bytes.Length * t / (truncations + 1));
                    h.Input($"rfa '{name}' truncated to {at}", at, () => ClipPipeline(h, bytes[..at], reference));
                }
                int count = Scale * (corpus ? CorpusMutationsPerFile : RfaMutationsPerSeed);
                for (int i = 0; i < count; i++)
                {
                    byte[] input = m.Mutate(bytes, other, out string how);
                    h.Input($"rfa '{name}' #{i} [{how}]", input.Length, () => ClipPipeline(h, input, reference));
                }
            }
            Assert.True(h.Accepted > seeds.Count, $"only {h.Accepted} of {h.Inputs} mutated clips loaded; the mutations never get past the header");
        });
    }

    private static void ClipPipeline(Harness h, byte[] bytes, RfaClip reference)
    {
        try { _ = RfaProbe.Probe(bytes, "fuzz.rfa"); }
        catch (AssetFormatException) { }
        RfaClip clip;
        try { clip = RfaReader.Read(bytes, "fuzz.rfa"); }
        catch (AssetFormatException) { return; }
        h.Accepted++;
        AcceptedClip(clip, reference);
    }

    /// <summary>A clip some reader accepted: the linter must not throw, and what the writer writes must read back.</summary>
    private static void AcceptedClip(RfaClip clip, RfaClip? reference)
    {
        _ = ClipLinter.Analyze(clip, new ClipLintContext
        {
            FileName = "fuzz.rfa",
            Skeleton = LintSkeleton,
            PreviewMesh = LintMesh,
            PreviewMeshName = "sample.v3c",
            ReferenceClip = reference,
            ReferenceClipName = "reference.rfa",
        });
        byte[] written;
        try { written = RfaWriter.Write(clip); }
        catch (ArgumentException ex) when (ex.GetType() == typeof(ArgumentException)) { return; }
        var back = RfaReader.Read(written, "rewritten.rfa");
        if (!written.AsSpan().SequenceEqual(RfaWriter.Write(back)))
            throw new FuzzContractException("a written clip, read back and written again, came out different");
    }

    /// <summary>A clip whose <paramref name="bones"/> bone-table entries all point at one bone record of <paramref name="keys"/> rotation keys.</summary>
    private static byte[] AliasedBones(int bones, int keys)
    {
        var track = new RfaBoneTrack(1f, [.. Enumerable.Range(0, keys).Select(k => new RfaRotKey(160 + k, 0, 0, 0, 16383))], []);
        byte[] one = RfaWriter.Write(new RfaClip { Bones = [track] });
        int boneAt = BinaryPrimitives.ReadInt32LittleEndian(one.AsSpan(RfaClip.BoneTableOffset));
        byte[] record = one[boneAt..(boneAt + track.ByteSize)];
        int dataAt = RfaClip.BoneTableOffset + 4 * bones;
        var b = new byte[dataAt + record.Length];
        one.AsSpan(0, RfaClip.HeaderSize).CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x18), bones);
        for (int i = 0; i < bones; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(RfaClip.BoneTableOffset + 4 * i), dataAt);
        record.CopyTo(b, dataAt);
        return b;
    }

    private static IEnumerable<(string Name, string Path)> CorpusClipSample()
    {
        var files = CorpusFiles(".rfa");
        var picked = Stride(files, CorpusClips).ToList();
        int morph = 0;
        foreach (string path in files)
        {
            if (morph >= CorpusMorphClips) break;
            if (picked.Contains(path)) continue;
            try
            {
                var probe = RfaProbe.ProbeFile(path);
                if (probe.MorphVertexCount == 0) continue;
            }
            catch (AssetFormatException) { continue; }
            picked.Add(path);
            morph++;
        }
        return picked.Select(p => (Path.GetFileName(p), p));
    }

    // ── Meshes ───────────────────────────────────────────────────────────────

    [Fact]
    public void MeshesSurviveMutation()
    {
        Harness.Run(output, "v3d", h =>
        {
            var seeds = SyntheticMeshes().Select(s => (s.Name, Bytes: V3dWriter.Write(s.Mesh))).ToList();
            foreach (string path in Stride(CorpusFiles(".v3c"), CorpusMeshesPerKind).Concat(Stride(CorpusFiles(".v3m"), CorpusMeshesPerKind)))
            {
                seeds.Add(($"corpus {Path.GetFileName(path)}", File.ReadAllBytes(path)));
            }

            var m = new Mutator(0x5EED_0002);
            for (int s = 0; s < seeds.Count; s++)
            {
                var (name, bytes) = seeds[s];
                byte[] other = seeds[(s + 1) % seeds.Count].Bytes;
                bool corpus = name.StartsWith("corpus", StringComparison.Ordinal);
                h.Input($"v3d '{name}' unmutated", bytes.Length, () => MeshPipeline(h, bytes));
                int truncations = corpus ? V3dTruncationsPerSeed / 4 : V3dTruncationsPerSeed;
                for (int t = 1; t <= truncations; t++)
                {
                    int at = (int)((long)bytes.Length * t / (truncations + 1));
                    h.Input($"v3d '{name}' truncated to {at}", at, () => MeshPipeline(h, bytes[..at]));
                }
                int count = Scale * (corpus ? CorpusMutationsPerFile : V3dMutationsPerSeed);
                for (int i = 0; i < count; i++)
                {
                    byte[] input = m.Mutate(bytes, other, out string how);
                    h.Input($"v3d '{name}' #{i} [{how}]", input.Length, () => MeshPipeline(h, input));
                }
            }
            Assert.True(h.Accepted > seeds.Count, $"only {h.Accepted} of {h.Inputs} mutated meshes loaded");
        });
    }

    private static void MeshPipeline(Harness h, byte[] bytes)
    {
        try { _ = V3dProbe.Probe(bytes, "fuzz.v3c"); }
        catch (AssetFormatException) { }
        V3dFile mesh;
        try { mesh = V3dReader.Read(bytes, "fuzz.v3c"); }
        catch (AssetFormatException) { return; }
        h.Accepted++;
        AcceptedMesh(mesh);
    }

    private static void AcceptedMesh(V3dFile mesh)
    {
        _ = MeshLinter.Analyze(mesh, new MeshLintContext { FileName = "fuzz.v3c", TextureExists = _ => true });
        byte[] written;
        try { written = V3dWriter.Write(mesh); }
        catch (ArgumentException ex) when (ex.GetType() == typeof(ArgumentException)) { return; }
        var back = V3dReader.Read(written, "rewritten.v3c");
        if (!written.AsSpan().SequenceEqual(V3dWriter.Write(back)))
            throw new FuzzContractException("a written mesh, read back and written again, came out different");
    }

    // ── Archives ─────────────────────────────────────────────────────────────

    [Fact]
    public void ArchivesSurviveMutation()
    {
        using var temp = new TempFolder();
        Harness.Run(output, "vpp", h =>
        {
            byte[] clip = RfaWriter.Write(RfaFormatTests.SampleClip(8, true));
            byte[] mesh = V3dWriter.Write(V3dFormatTests.SampleCharacter());
            byte[] seed = VppWriter.Build([
                ("clip.rfa", clip), ("mesh.v3c", mesh), ("entity.tbl", Encoding.Latin1.GetBytes(EntityTbl)), ("empty.txt", []),
            ]);
            byte[] other = VppWriter.Build([("x.rfa", clip)]);
            string path = temp.File("fuzz.vpp");

            var m = new Mutator(0x5EED_0003);
            for (int i = 0; i < VppMutations * Scale; i++)
            {
                string how = "unmutated";
                byte[] input = i == 0 ? seed : m.Mutate(seed, other, out how);
                how = $"#{i} [{how}]";
                bool onDisk = i % VppOnDiskEvery == 0;
                h.Input($"vpp {how}", input.Length, () =>
                {
                    VppArchive archive;
                    try { archive = VppArchive.Read(new MemoryStream(input), "fuzz.vpp"); }
                    catch (VppFormatException) { return; }
                    h.Accepted++;
                    foreach (var e in archive.Entries) _ = archive.TryGetEntry(e.Name, out _);
                    if (!onDisk) return;
                    File.WriteAllBytes(path, input);
                    archive = VppArchive.Open(path);
                    foreach (var e in archive.Entries.Take(8))
                    {
                        try { _ = archive.ReadEntry(e); }
                        catch (VppFormatException) { }
                        using var stream = archive.OpenEntry(e);
                        try
                        {
                            if (e.Name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) AcceptedClip(RfaReader.Read(stream, e.Name), null);
                            else if (e.Name.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase)) AcceptedMesh(V3dReader.Read(stream, e.Name));
                        }
                        catch (AssetFormatException) { }
                    }
                });
            }
            Assert.True(h.Accepted > 10, $"only {h.Accepted} of {h.Inputs} mutated archives opened");
        });
    }

    // ── Tables ───────────────────────────────────────────────────────────────

    [Fact]
    public void TablesSurviveMutation()
    {
        Harness.Run(output, "tbl", h =>
        {
            var seeds = new List<(string Table, string Text)>
            {
                ("entity.tbl", EntityTbl), ("weapons.tbl", WeaponsTbl), ("pc_multi.tbl", MultiTbl), ("fpgun.tbl", FpgunTbl),
            };
            if (TestPaths.Tables is { } tables)
            {
                foreach (string table in new[] { "entity.tbl", "weapons.tbl", "pc_multi.tbl", "fpgun.tbl" })
                {
                    string path = Path.Combine(tables, table);
                    if (!File.Exists(path)) continue;
                    string text = TblTokenizer.ReadText(path);
                    // The whole stock table once, then small windows of it to mutate (whole tables are slow to re-tokenise thousands of times).
                    h.Input($"tbl corpus {table} whole", text.Length, () => TablePipeline(h, table, text));
                    for (int w = 0; w < CorpusTblWindows; w++)
                    {
                        int start = text.Length * (w + 1) / (CorpusTblWindows + 1);
                        start = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
                        int length = Math.Min(CorpusTblWindowChars, text.Length - start);
                        seeds.Add((table, text.Substring(start, length)));
                    }
                }
            }

            var m = new Mutator(0x5EED_0004);
            for (int s = 0; s < seeds.Count; s++)
            {
                var (table, text) = seeds[s];
                string other = seeds[(s + 1) % seeds.Count].Text;
                for (int i = 0; i < TblMutationsPerSeed * Scale; i++)
                {
                    string input = m.MutateText(text, other, out string how);
                    h.Input($"tbl {table} seed {s} #{i} [{how}]", input.Length * 2, () => TablePipeline(h, table, input));
                }
            }
        });
    }

    /// <summary>Table readers never throw (TblTokenizer, TableReaders, ClipUsageIndex document this).</summary>
    private static void TablePipeline(Harness h, string table, string text)
    {
        _ = TblTokenizer.Tokenize(text);
        var fields = TblParser.ParseFields(text);
        foreach (var f in fields)
        {
            _ = f.String(0);
            _ = f.Number();
        }
        var entities = TableReaders.ReadEntities(text);
        var weapons = TableReaders.ReadWeapons(text);
        var multi = TableReaders.ReadMultiCharacters(text);
        var fpguns = TableReaders.ReadFpguns(text);
        if (entities.Count + weapons.Count + multi.Count + fpguns.Count > 0) h.Accepted++;
        var index = ClipUsageIndex.FromTexts(
            table == "entity.tbl" ? text : EntityTbl,
            table == "weapons.tbl" ? text : WeaponsTbl,
            table == "pc_multi.tbl" ? text : MultiTbl,
            table == "fpgun.tbl" ? text : FpgunTbl);

        var names = new List<string> { "ult2_stand", "ult2_stand.mvf", "miner.v3c", "miner", "fp_shotgun.v3d", "", " " };
        foreach (var e in entities)
        {
            names.Add(e.Name);
            names.Add(e.Mesh.Original ?? string.Empty);
            _ = e.Mesh.Candidates;
            _ = e.Mesh.BaseName;
            _ = ClipUsageIndex.CharacterMeshName(e.Mesh);
            foreach (var a in e.States.Concat(e.Actions).Concat(e.WeaponAnimations.SelectMany(w => w.States.Concat(w.Actions))))
            {
                names.Add(a.Clip.Original);
                _ = a.Clip.DiskName;
                _ = a.Clip.BaseName;
            }
        }
        foreach (var w in weapons)
        {
            names.Add(w.Name);
            foreach (var mesh in new[] { w.FirstPersonMesh, w.ThirdPersonMesh, w.ProjectileMesh })
            {
                if (mesh is { } n) names.Add(n.DiskName);
            }
            foreach (var a in w.States.Concat(w.Actions)) names.Add(a.Clip.DiskName);
        }
        foreach (var c in multi) names.Add(c.EntityType);
        foreach (var f in fpguns) names.Add(f.Model.DiskName);

        foreach (string name in names.Take(64))
        {
            _ = index.UsagesOf(name);
            _ = index.IsUsed(name);
            _ = index.UsagesOfClass(name);
            _ = index.UsagesOfClass(name, ClipUsageIndex.WeaponsTable);
            _ = index.ClipListsForMesh(name);
            _ = index.MeshesForClip(name);
            _ = RetargetPresets.Suggest(name, index);
        }
    }

    // ── glTF ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GlbImportSurvivesMutation()
    {
        Harness.Run(output, "glb", h =>
        {
            var mesh = V3dFormatTests.SampleCharacter();
            var export = GltfExport.Export(mesh, [new GltfExportClip("clip", RfaFormatTests.SampleClip(8, false))]);
            byte[] glb = GltfWriter.ToGlb(export.Document, new GltfWriteOptions { Indented = false });
            var (json, bin) = SplitGlb(glb);
            var skeleton = Skeleton.FromFile(mesh);
            byte[] other = GltfWriter.ToGlb(GltfExport.Export(mesh, null, new GltfExportOptions { IncludeMesh = false }).Document,
                new GltfWriteOptions { Indented = false });

            // Crafted: a 5-day end time on an animation whose joints no longer match the target's rest
            // pose, so every bone is resampled at 30 fps over the whole range.
            var longJson = JsonNode.Parse(json)!;
            foreach (var anim in longJson["animations"]!.AsArray()) anim!["extras"]![GltfExtras.EndTime] = 2_000_000_000;
            foreach (var node in longJson["nodes"]!.AsArray())
            {
                if (node!["rotation"] is not null) node["rotation"] = new JsonArray(0.5, 0.5, 0.5, 0.5);
            }
            byte[] longGlb = JoinGlb(longJson.ToJsonString(), bin);
            h.Input("glb crafted: 2e9-tick animation, re-posed joints", longGlb.Length, () => GlbPipeline(h, longGlb, skeleton));

            var m = new Mutator(0x5EED_0005);
            for (int i = 0; i < GltfMutations * Scale; i++)
            {
                byte[] input;
                string how;
                if (i == 0) (input, how) = (glb, "unmutated");
                else if (i % 2 == 0) input = m.Mutate(glb, other, out how);
                else
                {
                    input = JoinGlb(m.MutateJson(json, out how), bin);
                    how = "json " + how;
                }
                h.Input($"glb #{i} [{how}]", input.Length, () => GlbPipeline(h, input, skeleton));
            }
            Assert.True(h.Accepted > GltfMutations / 10, $"only {h.Accepted} of {h.Inputs} mutated .glb files imported");
        });
    }

    private static void GlbPipeline(Harness h, byte[] bytes, Skeleton skeleton)
    {
        GltfDocument doc;
        try { doc = GltfReader.Read(bytes, "fuzz.glb", null); }
        catch (AssetFormatException) { return; }
        bool any = false;
        try
        {
            var result = GltfMeshImport.Import(doc, null, "fuzz.glb");
            if (result.Mesh is { } imported)
            {
                any = true;
                AcceptedMesh(imported);
            }
        }
        catch (AssetFormatException) { }
        try
        {
            foreach (var clip in GltfAnimationImport.Import(doc, skeleton, null, "fuzz.glb"))
            {
                any = true;
                AcceptedClip(clip.Clip, null);
            }
        }
        catch (AssetFormatException) { }
        if (any) h.Accepted++;
    }

    private static (string Json, byte[] Bin) SplitGlb(byte[] glb)
    {
        int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        string json = Encoding.UTF8.GetString(glb, 20, jsonLength);
        int binAt = 20 + jsonLength;
        int binLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(binAt));
        return (json, glb[(binAt + 8)..(binAt + 8 + binLength)]);
    }

    private static byte[] JoinGlb(string json, byte[] bin)
    {
        byte[] j = Encoding.UTF8.GetBytes(json);
        int jPad = (4 - j.Length % 4) % 4, bPad = (4 - bin.Length % 4) % 4;
        var w = new MemoryStream();
        void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            w.Write(b);
        }
        U32(0x46546C67);
        U32(2);
        U32((uint)(12 + 8 + j.Length + jPad + 8 + bin.Length + bPad));
        U32((uint)(j.Length + jPad));
        U32(0x4E4F534A);
        w.Write(j);
        for (int i = 0; i < jPad; i++) w.WriteByte(0x20);
        U32((uint)(bin.Length + bPad));
        U32(0x004E4942);
        w.Write(bin);
        for (int i = 0; i < bPad; i++) w.WriteByte(0);
        return w.ToArray();
    }

    // ── Retarget JSON and clipboard text ─────────────────────────────────────

    [Fact]
    public void RetargetJsonSurvivesMutation()
    {
        Harness.Run(output, "retarget json", h =>
        {
            var names = LintSkeleton.Names;
            var parents = LintSkeleton.Parents;
            var boneMap = BoneMap.Create(
                [new BoneMapBone("root", -1), new BoneMapBone("pelvis", 0), new BoneMapBone("spine", 1)],
                [new BoneMapBone("pelvis", 2), new BoneMapBone("spine", 0), new BoneMapBone("root", -1), new BoneMapBone("extra", 1)],
                [1, 2, 0, -1]);
            var seeds = new List<(string Kind, string Json)>
            {
                ("rig profile", RigProfiles.Female.ToJson()),
                ("rig profile", RigProfile.Generic(names, parents).ToJson()),
                ("bone map", boneMap.ToJson()),
                ("options", """{"rootMode":"ScaleByLegLength","ik":true,"ikArms":false,"ikLegs":true,"quantization":"WithinUnit","resampleStep":null,"disabledIkChains":["l_upperarm"]}"""),
                ("clipboard", KeyClipboard.CopyPose(RfaFormatTests.SampleClip(8, false), 320, ["a", "b", "c"]).ToJson()),
                ("bone pairs", new BonePairMap([2, 1, 0, 3]).ToJson()),
            };

            var m = new Mutator(0x5EED_0006);
            for (int s = 0; s < seeds.Count; s++)
            {
                var (kind, json) = seeds[s];
                h.Input($"{kind} seed {s} unmutated", json.Length * 2, () => JsonPipeline(h, kind, json, names, parents));
                for (int i = 0; i < JsonMutationsPerSeed * Scale; i++)
                {
                    string input = m.MutateJsonOrText(json, seeds[(s + 1) % seeds.Count].Json, out string how);
                    h.Input($"{kind} seed {s} #{i} [{how}]", input.Length * 2, () => JsonPipeline(h, kind, input, names, parents));
                }
            }
        });
    }

    private static void JsonPipeline(Harness h, string kind, string json, IReadOnlyList<string> names, IReadOnlyList<int> parents)
    {
        switch (kind)
        {
            case "rig profile":
            {
                // RigProfile.FromJson: FormatException for anything that is not a profile.
                RigProfile profile;
                try { profile = RigProfile.FromJson(json); }
                catch (FormatException) { return; }
                h.Accepted++;
                _ = profile.Validate();
                try
                {
                    // Canonical documents FormatException for a bad prefix pattern.
                    _ = profile.CanonicalNames(names);
                    _ = profile.IndexOf(names, profile.RootBone);
                    _ = profile.IndexOf(names, profile.PelvisBone);
                    _ = RigProfiles.Matches(profile, [.. Enumerable.Range(0, profile.Bones.Length).Select(i => "bone" + i)],
                        [.. Enumerable.Range(0, profile.Bones.Length).Select(i => i - 1)]);
                }
                catch (FormatException) { }
                foreach (var chain in profile.IkChains) _ = RigProfile.IsLegChain(chain);
                _ = RigProfile.FromJson(profile.ToJson());
                break;
            }
            case "bone map":
            {
                BoneMap map;
                try { map = BoneMap.FromJson(json); }
                catch (FormatException) { return; }
                h.Accepted++;
                _ = map.Validate();
                _ = map.Fits(names, names);
                _ = map.ToArray();
                _ = BoneMap.FromJson(map.ToJson());
                break;
            }
            case "options":
            {
                // The app parses the envelope itself and hands MigrateOptionsJson whatever object it found;
                // a repeated property is the one thing it reports (FormatException, which the app shows).
                JsonNode? node;
                try { node = JsonNode.Parse(json); }
                catch (JsonException) { return; }
                if (node is not JsonObject options) return;
                try { _ = RetargetPresets.MigrateOptionsJson(options); }
                catch (FormatException) { return; }
                h.Accepted++;
                break;
            }
            case "clipboard":
            {
                KeyClipboard clipboard;
                try { clipboard = KeyClipboard.FromJson(json); }
                catch (FormatException) { return; }
                h.Accepted++;
                _ = KeyClipboard.FromJson(clipboard.ToJson());
                break;
            }
            case "bone pairs":
            {
                BonePairMap pairs;
                try { pairs = BonePairMap.FromJson(json); }
                catch (FormatException) { return; }
                h.Accepted++;
                for (int i = 0; i < pairs.Partner.Length; i++) _ = pairs.PartnerOf(i);
                _ = BonePairMap.FromJson(pairs.ToJson());
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    // ── Workspace files ──────────────────────────────────────────────────────

    [Fact]
    public void WorkspaceFilesSurviveMutation()
    {
        using var temp = new TempFolder();
        Harness.Run(output, "workspace files", h =>
        {
            byte[] clipBytes = RfaWriter.Write(RfaFormatTests.SampleClip(8, true));
            byte[] meshBytes = V3dWriter.Write(V3dFormatTests.SampleCharacter());

            // settings.json
            var settings = new AppSettings
            {
                Theme = AppTheme.Dark,
                GameDirectory = @"C:\Games\RF",
                SearchFolders = [@"C:\mods", @"D:\more"],
                RecentFiles = [@"C:\mods\a.rfa"],
                Layout = new() { ["left"] = 240.5 },
                Panels = new() { ["lint"] = true },
                Window = new WindowPlacement { Left = 10, Top = 20, Width = 800, Height = 600, IsSet = true },
                LastSaveFolder = @"C:\out",
            };
            settings.Set("viewport", new Dictionary<string, double> { ["fov"] = 60 });
            settings.Set("names", new[] { "a", "b" });
            settings.Set("theme2", AppTheme.Light);
            string settingsPath = temp.File("settings.json");
            Assert.True(SettingsStore.Save(settings, settingsPath));
            byte[] settingsSeed = File.ReadAllBytes(settingsPath);

            // recovery snapshot
            string recoveryDir = temp.SubDirectory("recovery");
            var store = new RecoveryStore(recoveryDir);
            Assert.True(store.Save(new RecoverySnapshot("doc-1", @"C:\mods\a.rfa", "a.rfa", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "rfa", clipBytes)));
            string snapshotPath = Directory.GetFiles(recoveryDir, "*.json").Single();
            byte[] recoverySeed = File.ReadAllBytes(snapshotPath);

            // library cache
            const string archivePath = @"C:\Games\RF\meshes.vpp";
            var archives = new Dictionary<string, LibraryArchiveCache>(StringComparer.OrdinalIgnoreCase)
            {
                [archivePath] = new(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 123456,
                    [new LibraryProbe("clip.rfa", 4096, clipBytes.Length, RfaProbe.Probe(clipBytes, "clip.rfa"), null, null),
                     new LibraryProbe("mesh.v3c", 8192, meshBytes.Length, null, V3dProbe.Probe(meshBytes, "mesh.v3c"), null),
                     new LibraryProbe("bad.rfa", 16384, 4, null, null, "'bad.rfa' is too short")],
                    VppArchive.FromDirectory(archivePath, 1, ["clip.rfa", "mesh.v3c", "bad.rfa"], [clipBytes.Length, meshBytes.Length, 4])),
            };
            var files = new Dictionary<string, LibraryFileCache>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\mods\a.rfa"] = new(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), clipBytes.Length,
                    new LibraryProbe("a.rfa", 0, clipBytes.Length, RfaProbe.Probe(clipBytes, "a.rfa"), null, null)),
            };
            string cachePath = temp.File("library-cache.json");
            LibraryCacheFile.Save(cachePath, archives, files);
            byte[] cacheSeed = File.ReadAllBytes(cachePath);

            var m = new Mutator(0x5EED_0007);
            for (int i = 0; i < FileJsonMutationsPerSeed * Scale; i++)
            {
                string how = "unmutated";
                byte[] input = i == 0 ? settingsSeed : m.MutateJsonBytes(settingsSeed, cacheSeed, out how);
                h.Input($"settings #{i} [{how}]", input.Length, () =>
                {
                    File.WriteAllBytes(settingsPath, input);
                    // SettingsStore.Load never throws and never hands out null collections.
                    var loaded = SettingsStore.Load(settingsPath);
                    if (loaded.SearchFolders is null || loaded.RecentFiles is null || loaded.Layout is null || loaded.Panels is null
                        || loaded.Window is null || loaded.Values is null)
                    {
                        throw new FuzzContractException("SettingsStore.Load returned a null collection");
                    }
                    h.Accepted++;
                    // AppSettings.Get returns the fallback for anything that is not a T.
                    foreach (string key in loaded.Values.Keys.ToList())
                    {
                        _ = loaded.Get<int>(key);
                        _ = loaded.Get<string>(key);
                        _ = loaded.Get<double[]>(key);
                        _ = loaded.Get<Dictionary<string, double>>(key);
                        _ = loaded.Get<AppTheme>(key);
                    }
                    _ = loaded.Clone();
                });
            }
            for (int i = 0; i < FileJsonMutationsPerSeed * Scale; i++)
            {
                string how = "unmutated";
                byte[] input = i == 0 ? recoverySeed : m.MutateJsonBytes(recoverySeed, settingsSeed, out how);
                h.Input($"recovery #{i} [{how}]", input.Length, () =>
                {
                    File.WriteAllBytes(snapshotPath, input);
                    // RecoveryStore.List never throws; unreadable snapshots are skipped.
                    foreach (var snapshot in new RecoveryStore(recoveryDir).List())
                    {
                        if (string.IsNullOrEmpty(snapshot.Id) || snapshot.Data is null)
                            throw new FuzzContractException("RecoveryStore.List returned a snapshot without an id or data");
                        h.Accepted++;
                    }
                });
            }
            for (int i = 0; i < FileJsonMutationsPerSeed * Scale; i++)
            {
                string how = "unmutated";
                byte[] input = i == 0 ? cacheSeed : m.MutateJsonBytes(cacheSeed, recoverySeed, out how);
                h.Input($"library cache #{i} [{how}]", input.Length, () =>
                {
                    File.WriteAllBytes(cachePath, input);
                    // AssetLibrary treats exactly these as "the cache could not be read; rebuild it".
                    try
                    {
                        _ = LibraryCacheFile.Load(cachePath);
                        h.Accepted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                        or NotSupportedException or ArgumentException or InvalidOperationException)
                    {
                    }
                });
            }
            Assert.True(h.Accepted > FileJsonMutationsPerSeed / 2, $"only {h.Accepted} of {h.Inputs} mutated workspace files loaded");
        });
    }

    // ── Corpus helpers ───────────────────────────────────────────────────────

    private static List<string> CorpusFiles(string extension)
    {
        if (TestPaths.Corpus is null) return [];
        return [.. Directory.EnumerateFiles(TestPaths.Corpus, "*" + extension)
            .Where(p => Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase) && new FileInfo(p).Length <= CorpusMaxBytes)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> Stride(List<string> files, int count)
    {
        if (files.Count == 0) yield break;
        int step = Math.Max(1, files.Count / count);
        for (int i = 0; i < count && i * step < files.Count; i++) yield return files[i * step];
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    /// <summary>A broken round-trip or post-load promise (as opposed to an exception escaping a reader).</summary>
    private sealed class FuzzContractException(string message) : Exception(message);

    /// <summary>
    /// Runs inputs one at a time, measuring each one's time and allocation, and collects every failure
    /// (grouped by exception type and throwing method) so one run reports every crash class at once.
    /// </summary>
    private sealed class Harness
    {
        private readonly Dictionary<string, (int Count, string Example)> _failures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int Count, TimeSpan Time)> _kinds = new(StringComparer.Ordinal);
        private volatile string _current = "(setup)";

        public int Inputs { get; private set; }

        public int Accepted { get; set; }

        public void Input(string label, int size, Action body)
        {
            _current = label;
            Inputs++;
            long budget = Math.Max(AllocFloorBytes, size * AllocPerInputByte);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Fail($"{ex.GetType().Name} in {ThrowingMethod(ex)}", $"{label}: {ex.GetType().Name}: {ex.Message}");
            }
            var elapsed = Stopwatch.GetElapsedTime(start);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            string kind = label.Split(' ')[0];
            _kinds[kind] = _kinds.TryGetValue(kind, out var k) ? (k.Count + 1, k.Time + elapsed) : (1, elapsed);
            if (elapsed > InputTimeBudget) Fail("too slow", $"{label}: took {elapsed.TotalMilliseconds:F0} ms");
            if (allocated > budget) Fail("too much memory", $"{label}: allocated {allocated / (1024.0 * 1024):F1} MB for a {size}-byte input");
        }

        private void Fail(string key, string example)
        {
            _failures[key] = _failures.TryGetValue(key, out var f) ? (f.Count + 1, f.Example) : (1, example);
        }

        private static string ThrowingMethod(Exception ex)
        {
            foreach (var frame in new StackTrace(ex).GetFrames())
            {
                var method = frame.GetMethod();
                if (method?.DeclaringType?.Namespace is { } ns && ns.StartsWith("Cairn.Rfa", StringComparison.Ordinal)
                    && !ns.StartsWith("Cairn.Rfa.Tests", StringComparison.Ordinal))
                {
                    return $"{method.DeclaringType.Name}.{method.Name}";
                }
            }
            return new StackTrace(ex).GetFrame(0)?.GetMethod()?.Name ?? "?";
        }

        public static void Run(ITestOutputHelper output, string area, Action<Harness> body)
        {
            var h = new Harness();
            Exception? setup = null;
            long start = Stopwatch.GetTimestamp();
            var thread = new Thread(() =>
            {
                try { body(h); }
                catch (Exception ex) { setup = ex; }
            }, 16 * 1024 * 1024) { IsBackground = true, Name = "fuzz " + area };
            thread.Start();
            if (!thread.Join(Watchdog))
                Assert.Fail($"{area}: still running after {Watchdog.TotalSeconds:F0} s; stuck on {h._current}");
            output.WriteLine($"{area}: {h.Inputs} inputs, {h.Accepted} accepted, {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms (scale {Scale})");
            foreach (var (kind, (count, time)) in h._kinds.OrderByDescending(k => k.Value.Time))
                output.WriteLine($"  {kind}: {count} inputs, {time.TotalMilliseconds:F0} ms");
            if (h._failures.Count > 0)
            {
                var lines = h._failures.OrderByDescending(f => f.Value.Count)
                    .Select(f => $"  [{f.Value.Count}x] {f.Key}\n      e.g. {f.Value.Example}");
                Assert.Fail($"{area}: {h._failures.Count} failure class(es) over {h.Inputs} inputs:\n{string.Join("\n", lines)}");
            }
            if (setup is not null) throw new InvalidOperationException($"{area}: the fuzz run itself failed: {setup.Message}", setup);
        }
    }

    // ── Mutators ─────────────────────────────────────────────────────────────

    private sealed class Mutator(int seed)
    {
        private static readonly byte[] RunBytes = [0x00, 0xFF, 0x7F, 0x80];
        private static readonly int[] Int32s = [0, 1, -1, 2, 3, 4, 16, 0x50, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000,
            0x40000, 0x7FFFFF, 0x40000000, int.MaxValue, int.MinValue, int.MaxValue - 3, -16];
        private static readonly short[] Int16s = [0, 1, -1, 2, 0x7F, 0x80, 0xFF, 0x100, short.MaxValue, short.MinValue, 0x4000];
        private static readonly string[] TblTokens = ["$", "+", ":", "\"", "#", "//", "\n", "\r", "\r\n", "(", ")", "{", "}", "<", ">", ",",
            "$Name:", "+State:", "+Action:", "+Weapon Specific:", "$V3D Filename:", "$Model:", "+Custom Fpgun:", "\"\"", ".mvf", ".vcm",
            ".v3d", "\0", "\u00FF", " ", "\t", "-17.0", "1e39", "NaN", "\"x.mvf\"", "En:", ":"];

        private readonly Random _rng = new(seed);

        /// <summary>One to three stacked byte-level mutations.</summary>
        public byte[] Mutate(byte[] input, byte[]? other, out string how)
        {
            var ops = new List<string>();
            int count = _rng.Next(4) == 0 ? _rng.Next(2, 4) : 1;
            byte[] b = input;
            for (int n = 0; n < count; n++) b = MutateOnce(b, other, ops);
            how = string.Join("; ", ops);
            return b;
        }

        private int Position(int length) =>
            _rng.Next(2) == 0 ? _rng.Next(length) : _rng.Next(Math.Min(length, 256));

        private byte[] MutateOnce(byte[] input, byte[]? other, List<string> ops)
        {
            if (input.Length == 0)
            {
                ops.Add("empty");
                return input;
            }
            var b = (byte[])input.Clone();
            int len = b.Length;
            switch (_rng.Next(10))
            {
                case 0:
                {
                    int at = _rng.Next(len);
                    ops.Add($"truncate@{at}");
                    return b[..at];
                }
                case 1:
                {
                    int at = Position(len), bit = _rng.Next(8);
                    b[at] ^= (byte)(1 << bit);
                    ops.Add($"flip@{at}.{bit}");
                    return b;
                }
                case 2:
                {
                    int flips = _rng.Next(2, 9);
                    for (int i = 0; i < flips; i++) b[_rng.Next(len)] ^= (byte)(1 << _rng.Next(8));
                    ops.Add($"flip x{flips}");
                    return b;
                }
                case 3:
                {
                    byte v = RunBytes[_rng.Next(RunBytes.Length)];
                    int at = Position(len);
                    if (_rng.Next(2) == 0) at &= ~3;
                    int n = Math.Min(_rng.Next(1, 9), len - at);
                    b.AsSpan(at, n).Fill(v);
                    ops.Add($"run 0x{v:X2}x{n}@{at}");
                    return b;
                }
                case 4:
                case 5:
                {
                    if (len < 4) goto case 1;
                    int at = Position(len - 3) & ~3;
                    int v = _rng.Next(4) == 0 ? (_rng.Next(2) == 0 ? len + _rng.Next(-8, 9) : _rng.Next(-64, 1024)) : Int32s[_rng.Next(Int32s.Length)];
                    BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);
                    ops.Add($"int32 {v}@{at}");
                    return b;
                }
                case 6:
                {
                    if (len < 2) goto case 1;
                    int at = Position(len - 1) & ~1;
                    short v = Int16s[_rng.Next(Int16s.Length)];
                    BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(at), v);
                    ops.Add($"int16 {v}@{at}");
                    return b;
                }
                case 7:
                {
                    if (other is not { Length: > 0 }) goto case 1;
                    int cut = _rng.Next(len), from = _rng.Next(other.Length);
                    ops.Add($"splice {cut}+other@{from}");
                    return [.. b.AsSpan(0, cut), .. other.AsSpan(from)];
                }
                case 8:
                {
                    int at = _rng.Next(len), n = Math.Min(_rng.Next(1, 65), len - at), to = _rng.Next(len + 1);
                    ops.Add($"dup {at}+{n}->{to}");
                    return [.. b.AsSpan(0, to), .. b.AsSpan(at, n), .. b.AsSpan(to)];
                }
                default:
                {
                    int at = _rng.Next(len), n = Math.Min(_rng.Next(1, 65), len - at);
                    ops.Add($"cut {at}+{n}");
                    return [.. b.AsSpan(0, at), .. b.AsSpan(at + n)];
                }
            }
        }

        /// <summary>Table text: either byte mutations of its Latin-1 bytes, or inserted / swapped table syntax.</summary>
        public string MutateText(string text, string other, out string how)
        {
            if (_rng.Next(2) == 0)
            {
                byte[] b = Mutate(Encoding.Latin1.GetBytes(text), Encoding.Latin1.GetBytes(other), out how);
                return Encoding.Latin1.GetString(b);
            }
            var sb = new StringBuilder(text);
            var ops = new List<string>();
            int count = _rng.Next(1, 5);
            for (int n = 0; n < count; n++)
            {
                int at = _rng.Next(sb.Length + 1);
                switch (_rng.Next(3))
                {
                    case 0:
                    {
                        string token = TblTokens[_rng.Next(TblTokens.Length)];
                        sb.Insert(at, token);
                        ops.Add($"insert {Escape(token)}@{at}");
                        break;
                    }
                    case 1:
                    {
                        int length = Math.Min(_rng.Next(1, 16), sb.Length - at);
                        sb.Remove(at, length);
                        ops.Add($"delete {at}+{length}");
                        break;
                    }
                    default:
                    {
                        int from = _rng.Next(other.Length + 1), length = Math.Min(_rng.Next(1, 200), other.Length - from);
                        sb.Insert(at, other.AsSpan(from, length));
                        ops.Add($"paste other@{from}+{length}@{at}");
                        break;
                    }
                }
            }
            how = string.Join("; ", ops);
            return sb.ToString();
        }

        private static string Escape(string s) => s.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\0", "\\0");

        /// <summary>JSON text: half structural mutations, half raw byte mutations of its UTF-8.</summary>
        public string MutateJsonOrText(string json, string other, out string how)
        {
            if (_rng.Next(2) == 0) return MutateJson(json, out how);
            byte[] b = Mutate(Encoding.UTF8.GetBytes(json), Encoding.UTF8.GetBytes(other), out how);
            return Encoding.UTF8.GetString(b);
        }

        /// <summary>A JSON file's bytes: half structural mutations, half raw byte mutations.</summary>
        public byte[] MutateJsonBytes(byte[] json, byte[] other, out string how)
        {
            if (_rng.Next(2) == 0) return Encoding.UTF8.GetBytes(MutateJson(Encoding.UTF8.GetString(json), out how));
            return Mutate(json, other, out how);
        }

        /// <summary>
        /// Replaces, removes or duplicates one to three values anywhere in the tree: nulls, wrong types,
        /// extreme numbers, empty containers and values copied from elsewhere in the same document.
        /// </summary>
        public string MutateJson(string json, out string how)
        {
            var root = JsonNode.Parse(json)!;
            var ops = new List<string>();
            int count = _rng.Next(1, 4);
            for (int n = 0; n < count; n++)
            {
                var all = new List<JsonNode>();
                Collect(root, all);
                if (all.Count == 0) break;
                var target = all[_rng.Next(all.Count)];
                var parent = target.Parent!;
                string where = target.GetPath();
                int op = _rng.Next(10);
                if (op == 0)
                {
                    if (parent is JsonObject o) o.Remove(target.GetPropertyName());
                    else ((JsonArray)parent).RemoveAt(target.GetElementIndex());
                    ops.Add($"remove {where}");
                    continue;
                }
                if (op == 1 && parent is JsonArray dupIn)
                {
                    dupIn.Insert(target.GetElementIndex(), target.DeepClone());
                    ops.Add($"duplicate {where}");
                    continue;
                }
                JsonNode? replacement = Replacement(target, all);
                if (parent is JsonObject obj) obj[target.GetPropertyName()] = replacement;
                else ((JsonArray)parent)[target.GetElementIndex()] = replacement;
                ops.Add($"set {where}={replacement?.ToJsonString() ?? "null"}");
            }
            how = string.Join("; ", ops);
            return root.ToJsonString();
        }

        private JsonNode? Replacement(JsonNode target, List<JsonNode> all)
        {
            if (target is JsonValue v && v.TryGetValue(out double d) && _rng.Next(2) == 0)
            {
                return _rng.Next(4) switch
                {
                    0 => JsonValue.Create(-d),
                    1 => JsonValue.Create(d + 1),
                    2 => JsonValue.Create(d * 65536),
                    _ => JsonValue.Create(Math.Floor(d) + 0.5),
                };
            }
            if (_rng.Next(5) == 0) return null;
            return _rng.Next(16) switch
            {
                0 => JsonValue.Create(-0.0),
                1 => JsonValue.Create(0),
                2 => JsonValue.Create(-1),
                3 => JsonValue.Create(int.MaxValue),
                4 => JsonValue.Create(int.MinValue),
                5 => JsonValue.Create(4294967295L),
                6 => JsonValue.Create(1e30),
                7 => JsonValue.Create(0.5),
                8 => JsonValue.Create(""),
                9 => JsonValue.Create("x\0y"),
                10 => new JsonArray(),
                11 => new JsonObject(),
                12 => JsonValue.Create(true),
                13 => new JsonArray(JsonValue.Create(1), null, JsonValue.Create("a")),
                _ => all[_rng.Next(all.Count)].DeepClone(),
            };
        }

        private static void Collect(JsonNode node, List<JsonNode> all)
        {
            if (node is JsonObject o)
            {
                foreach (var (_, child) in o)
                {
                    if (child is null) continue;
                    all.Add(child);
                    Collect(child, all);
                }
            }
            else if (node is JsonArray a)
            {
                foreach (var child in a)
                {
                    if (child is null) continue;
                    all.Add(child);
                    Collect(child, all);
                }
            }
        }
    }
}
