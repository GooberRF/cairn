using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.Retargeting;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of the retarget dialogs (phase 6), driven through their view-models: the nine golden jobs
/// (park_jeep_driver, park_jeep_gunner, ult2_on_turret from ult2_guard onto nurse1, merc_grunt, tech01)
/// give exactly the clips Core's reference path gives (the default dialog settings equal a direct Core call with
/// the built-in rigs; when <see cref="LocalPaths.GoldenClips"/> is configured, each folder's af_*.rfa is byte for
/// byte the dialog's Seated result with either the default quantisation or the reference rounding, the two
/// generations of the goldens); bone-map edits change statuses and block the run when unsupported; Retarget opens
/// a new unsaved tab previewed on the target; a batch writes to a temp folder, asks about collisions
/// once, and produces table lines and a report. Needs the game's clips and meshes in the library; skips
/// (logged) when they are absent. <c>--retarget-out &lt;folder&gt;</c> also writes the nine outputs there.
/// </summary>
internal static class RetargetSelfTests
{
    private static readonly (string Source, string Clip)[] Jobs =
    [
        ("park_jeep_driver.rfa", "jeep_driver"),
        ("park_jeep_gunner.rfa", "jeep_gunner"),
        ("ult2_on_turret.rfa", "on_turret"),
    ];

    private static readonly (string Rig, string Mesh)[] Targets = [("female", "nurse1.v3c"), ("merc", "merc_grunt.v3c"), ("civilian", "tech01.v3c")];

    [SelfTest("retarget", Order = 600)]
    public static async Task Retarget(SelfTestContext ctx)
    {
        var shell = ctx.Model;
        var library = shell.Assets.Snapshot;
        if (library.FindMesh("ult2_guard.v3c") is null || library.FindClip("park_jeep_driver.rfa") is not { } jeep)
        {
            ctx.Log("selftest retarget: the stock clips and meshes are not in the library; skipped");
            return;
        }
        // Alpine Faction's golden outputs (one folder per generation), read in place when configured.
        var goldenFolders = LocalPaths.GoldenClips;
        if (goldenFolders.Count == 0)
            ctx.Log("selftest retarget: golden clip comparisons skipped (" + LocalPaths.HowToSet(LocalPaths.GoldenClipsVariable, "goldenClips") + ")");
        string? outFolder = ctx.Options.Extra.TryGetValue("retarget-out", out string? o) ? o : null;
        if (outFolder is not null) Directory.CreateDirectory(outFolder);

        var guard = await shell.Assets.LoadMeshAsync(library.FindMesh("ult2_guard.v3c")!);
        var sourceRig = RetargetRig.FromMesh(guard, RigProfiles.RigA);

        foreach (var (sourceName, clipToken) in Jobs)
        {
            if (library.FindClip(sourceName) is not { } libraryClip) { ctx.Log($"selftest retarget: {sourceName} missing; skipped"); continue; }
            var sourceClip = await shell.Assets.LoadClipAsync(libraryClip);
            foreach (var (rig, meshName) in Targets)
            {
                var vm = new RetargetDialogViewModel(shell, null, libraryClip);
                try
                {
                    vm.Setup.Select("ult2_guard.v3c", meshName);
                    await vm.SettleAsync();
                    vm.RecomputeNow();
                    await vm.SettleAsync();
                    string job = $"{sourceName} → {meshName}";
                    ctx.Check(vm.Setup.TargetProfile?.Name == rig && vm.Setup.SourceProfile?.Name == "A",
                        $"retarget {job}: profiles chosen automatically (source {vm.Setup.SourceProfile?.Name}, target {vm.Setup.TargetProfile?.Name})");
                    ctx.Check(vm.OutputName.Equals($"af_{rig}_{clipToken}.rfa", StringComparison.OrdinalIgnoreCase), $"retarget {job}: default name {vm.OutputName}");

                    // Default dialog settings = Core with the built-in rigs (rest mesh and stand clip from the profile).
                    var profile = RigProfiles.Get(rig)!;
                    var targetMesh = await shell.Assets.LoadMeshAsync(library.FindMesh(profile.RestMesh!)!);
                    var stand = await shell.Assets.LoadClipAsync(library.FindClip(profile.ReferenceClip!)!);
                    var targetRig = RetargetRig.FromMesh(targetMesh, profile, stand, profile.ReferenceClip);
                    var core = Retargeter.Retarget(new RetargetRequest(sourceClip, sourceRig, targetRig));
                    bool same = vm.Result?.Clip is { } ours && core.Clip is { } theirs && RfaWriter.Write(ours).AsSpan().SequenceEqual(RfaWriter.Write(theirs));
                    ctx.Check(same, $"retarget {job}: the dialog's result equals Core's default retarget byte for byte");
                    ctx.Check(vm.Report is { StructureOk: true } r && r.HandsModelSpaceMaxCm < 0.5 && r.FeetModelSpaceMaxCm < 0.5,
                        $"retarget {job}: report — {vm.Report?.Summary}");
                    byte[]? defaultBytes = vm.Result?.Clip is { } d ? RfaWriter.Write(d) : null;

                    // Reference rounding = retarget.py's goldens.
                    vm.Setup.Quantization = KeyQuantization.Reference;
                    vm.RecomputeNow();
                    await vm.SettleAsync();
                    var reference = Retargeter.Retarget(new RetargetRequest(sourceClip, sourceRig, targetRig) { Options = RetargetOptions.Reference });
                    bool refSame = vm.Result?.Clip is { } r2 && reference.Clip is { } c2 && RfaWriter.Write(r2).AsSpan().SequenceEqual(RfaWriter.Write(c2));
                    ctx.Check(refSame, $"retarget {job}: with reference rounding the dialog equals Core's reference path");
                    byte[]? referenceBytes = vm.Result?.Clip is { } r3 ? RfaWriter.Write(r3) : null;

                    // Each golden folder holds one generation: RFA Workbench's own (default quantisation) or retarget.py's
                    // (reference rounding). A golden must be byte-identical to the dialog's Seated result in one of them.
                    foreach (string goldenFolder in goldenFolders)
                    {
                        string golden = Path.Combine(goldenFolder, $"af_{rig}_{clipToken}.rfa");
                        if (!File.Exists(golden)) continue;
                        byte[] file = File.ReadAllBytes(golden);
                        string? generation = defaultBytes is not null && file.AsSpan().SequenceEqual(defaultBytes) ? "default quantisation"
                            : referenceBytes is not null && file.AsSpan().SequenceEqual(referenceBytes) ? "reference rounding" : null;
                        ctx.Check(generation is not null,
                            $"retarget {job}: byte-identical to the golden {Path.GetFileName(goldenFolder)}/{Path.GetFileName(golden)} ({generation ?? "matches neither generation"})");
                    }
                    if (outFolder is not null && vm.Result?.Clip is { } r4)
                    {
                        File.WriteAllBytes(Path.Combine(outFolder, $"af_{rig}_{clipToken}.rfa"), RfaWriter.Write(r4));
                        File.WriteAllText(Path.Combine(outFolder, $"af_{rig}_{clipToken}.report.txt"),
                            (vm.Report?.Summary ?? "") + Environment.NewLine + string.Join(Environment.NewLine, vm.ReportLines) + Environment.NewLine
                            + string.Join(Environment.NewLine, vm.Warnings));
                    }
                }
                finally { vm.Dispose(); }
            }
        }

        await BoneMapEdits(ctx, jeep);
        await Presets(ctx, jeep, sourceRig);
        await OpensNewTab(ctx, jeep);
        await Batch(ctx);
    }

    /// <summary>
    /// Phase 7a: the preset follows the clip (seated for the jeep, standing for a walk) until the user picks one;
    /// an option edit shows Custom; the standing result equals Core's locomotion preset and holds only the feet;
    /// the report keeps pinned contacts apart from joint offsets; a profile with the retired root mode loads.
    /// </summary>
    private static async Task Presets(SelfTestContext ctx, Cairn.Rfa.Assets.LibraryClip jeep, RetargetRig guardRig)
    {
        var shell = ctx.Model;
        var library = shell.Assets.Snapshot;
        if (library.FindClip("ult2_walk.rfa") is not { } walk || library.FindClip("ult2_stand.rfa") is not { } stand) { ctx.Log("selftest presets: ult2_walk/ult2_stand missing; skipped"); return; }
        var vm = new RetargetDialogViewModel(shell, null, jeep);
        try
        {
            vm.Setup.Select("ult2_guard.v3c", "nurse1.v3c");
            await vm.SettleAsync();
            vm.RecomputeNow();
            await vm.SettleAsync();
            ctx.Check(vm.Setup.CurrentPreset == RetargetPreset.Seated && vm.Setup.SelectedPreset?.Preset == RetargetPreset.Seated
                    && vm.Setup.SuggestionText?.Contains("jeep_drive", StringComparison.Ordinal) == true,
                $"presets: park_jeep_driver gets Seated ({vm.Setup.SuggestionText})");
            ctx.Check(vm.Setup.Presets.Count == 4 && !vm.Setup.Presets.Any(p => p.IsAutomatic) && !vm.Setup.Presets.Single(p => p.IsCustom).IsVisible,
                "presets: the dialog offers Seated, Standing, Rotation only (Custom hidden, no Automatic)");

            // A walk: standing / locomotion, feet held on the ground, arms by rotation.
            vm.SourceClips.Selected = vm.SourceClips.All.First(o => o.Library?.Name == walk.Name);
            await Task.Delay(50);
            await vm.SettleAsync();
            vm.RecomputeNow();
            await vm.SettleAsync();
            ctx.Check(vm.Setup.CurrentPreset == RetargetPreset.Locomotion && vm.Setup.RootMode == RootMode.HipHeight && vm.Setup.Ik && !vm.Setup.IkArms && vm.Setup.IkLegs,
                $"presets: ult2_walk gets Standing / locomotion ({vm.Setup.SuggestionText})");
            ctx.Check(vm.Setup.OffHandFollowsMainHand, "presets: Standing / locomotion turns the two-handed grip on by default");
            var contacts = vm.Result?.Contacts ?? [];
            ctx.Check(contacts.Count(c => c.IsLeg) == 2 && contacts.All(c => !c.Stretched && c.MaxErrorCm < (c.IsLeg ? 0.5 : 1.0))
                    && contacts.All(c => c.IsLeg || c.HeldTo.Contains("grip", StringComparison.Ordinal)) && vm.PinnedContacts.Count == contacts.Length,
                $"presets: the walk holds the feet (and the off hand only by the grip), none stretched ({string.Join(", ", contacts.Select(c => $"{c.EndBone} {c.MaxErrorCm:0.00} cm"))})");
            ctx.Check(vm.JointOffsets.Any(j => j.Name == "head" && j.Note == "proportions") && vm.JointOffsets.Count(j => j.Note.StartsWith("pinned", StringComparison.Ordinal)) >= 2,
                "presets: the report lists the head as a proportion offset and the feet as pinned");

            // Same as Core's locomotion preset with the stock rigs (the source's ground from ult2_stand).
            var profile = RigProfiles.Female;
            var nurse = await shell.Assets.LoadMeshAsync(library.FindMesh(profile.RestMesh!)!);
            var femaleStand = await shell.Assets.LoadClipAsync(library.FindClip(profile.ReferenceClip!)!);
            var target = RetargetRig.FromMesh(nurse, profile, femaleStand, profile.ReferenceClip);
            var source = guardRig with { ReferenceClip = await shell.Assets.LoadClipAsync(stand), ReferenceClipName = stand.Name };
            var walkClip = await shell.Assets.LoadClipAsync(walk);
            var core = Retargeter.Retarget(new RetargetRequest(walkClip, source, target) { Options = RetargetPresets.Options(RetargetPreset.Locomotion) });
            bool same = vm.Result?.Clip is { } ours && core.Clip is { } theirs && RfaWriter.Write(ours).AsSpan().SequenceEqual(RfaWriter.Write(theirs));
            ctx.Check(same && core.Ground?.SourceFrom == stand.Name, $"presets: the dialog's standing result equals Core's locomotion preset byte for byte (ground from {vm.Result?.Ground?.SourceFrom})");

            // The two-handed grip goes with any preset; an option edit makes Custom; picking a preset again restores it.
            vm.Setup.OffHandFollowsMainHand = false;
            ctx.Check(vm.Setup.CurrentPreset == RetargetPreset.Locomotion, "presets: clearing the off-hand option keeps the preset");
            vm.Setup.RootMode = RootMode.AnchorPelvis;
            var custom = vm.Setup.Presets.Single(p => p.IsCustom);
            ctx.Check(vm.Setup.SelectedPreset == custom && custom.IsVisible && custom.IsSelected, "presets: editing an option shows Custom");
            var seated = vm.Setup.Presets.Single(p => p.Preset == RetargetPreset.Seated);
            seated.IsSelected = true;
            ctx.Check(vm.Setup.CurrentPreset == RetargetPreset.Seated && vm.Setup.RootMode == RootMode.AnchorPelvis && !custom.IsVisible && vm.Setup.IkArms,
                "presets: choosing Seated applies its options and hides Custom");
            vm.Setup.Presets.Single(p => p.Preset == RetargetPreset.Locomotion && !p.IsAutomatic).IsSelected = true;
            ctx.Check(!vm.Setup.OffHandFollowsMainHand, "presets: once the user cleared the grip, picking Standing again keeps it off");
            seated.IsSelected = true;

            // A chosen preset sticks when the clip changes.
            vm.SourceClips.Selected = vm.SourceClips.All.First(o => o.Library?.Name == stand.Name);
            await Task.Delay(50);
            await vm.SettleAsync();
            ctx.Check(vm.Setup.CurrentPreset == RetargetPreset.Seated, "presets: a preset the user chose is kept when the clip changes");

            // A profile saved with the retired root mode loads as hip height.
            string json = vm.Setup.ProfileJson().Replace("\"RootMode\": \"AnchorPelvis\"", "\"RootMode\": \"ScaleByLegLength\"", StringComparison.Ordinal);
            string message = vm.Setup.ApplyProfileJson(json, "old.json");
            await vm.SettleAsync();
            ctx.Check(vm.Setup.RootMode == RootMode.HipHeight && message.Contains("retired", StringComparison.Ordinal),
                $"presets: a profile with the retired 'scale by leg length' loads as hip height — {message}");
        }
        finally { vm.Dispose(); }
    }

    private static async Task BoneMapEdits(SelfTestContext ctx, Cairn.Rfa.Assets.LibraryClip clip)
    {
        var vm = new RetargetDialogViewModel(ctx.Model, null, clip);
        try
        {
            vm.Setup.Select("ult2_guard.v3c", "merc_grunt.v3c");
            await vm.SettleAsync();
            vm.RecomputeNow();
            await vm.SettleAsync();
            var map = vm.Setup.Map;
            ctx.Check(map.Rows.Count == 27 && vm.CanRetarget, $"bone map: 27 target rows for merc_grunt and a result ({map.Summary})");
            var spine = map.Rows.FirstOrDefault(r => r.TargetName.EndsWith("spine03", StringComparison.OrdinalIgnoreCase));
            ctx.Check(spine?.StatusText == "reparented", $"bone map: merc spine03 is reparented ({spine?.StatusText})");
            var pad = map.Rows.FirstOrDefault(r => r.TargetName.Contains("shoulderpad", StringComparison.OrdinalIgnoreCase));
            ctx.Check(pad?.StatusText == "extra", $"bone map: merc shoulder pad is an extra bone ({pad?.StatusText})");
            ctx.Check(map.Rows[0].Depth == 0 && map.Rows.Skip(1).All(r => r.Depth > 0), "bone map: rows in hierarchy order (root first, everything below it)");

            // Unmap the head: it holds a still pose ("unmapped", since the automatic map would pair it).
            var head = map.Rows.First(r => r.TargetName.EndsWith("head", StringComparison.OrdinalIgnoreCase));
            int headSource = head.Selected.Index;
            head.Selected = head.Options[0];
            ctx.Check(head.StatusText == "unmapped" && map.Map!.SourceOf(head.TargetIndex) == -1, $"bone map: setting the head to none makes it unmapped ({head.StatusText})");
            await vm.SettleAsync();
            ctx.Check(vm.CanRetarget, "bone map: an unmapped head still retargets");

            // Unmap the pelvis: its mapped children become unsupported, and the run is blocked.
            var pelvis = map.Rows.First(r => r.TargetName.EndsWith("pelvis", StringComparison.OrdinalIgnoreCase));
            pelvis.Selected = pelvis.Options[0];
            await vm.SettleAsync();
            ctx.Check(map.HasErrors && !vm.CanRetarget && vm.Setup.Inputs is null && vm.Error is not null,
                $"bone map: unmapping the pelvis blocks the run with a message ({vm.Error})");

            // Choosing the source again by hand.
            head.Selected = head.Options.First(o => o.Index == headSource);
            ctx.Check(head.MatchText == "by hand", "bone map: a hand-picked row says so");
            map.AutoMapCommand.Execute(null);
            await vm.SettleAsync();
            ctx.Check(!map.HasErrors && vm.CanRetarget && map.Rows.First(r => r.TargetName.EndsWith("pelvis", StringComparison.OrdinalIgnoreCase)).StatusText == "mapped",
                "bone map: Auto-map restores the map and the result");
            map.ClearCommand.Execute(null);
            await vm.SettleAsync();
            ctx.Check(!vm.CanRetarget && vm.Error is not null, $"bone map: Clear leaves nothing to transfer ({vm.Error})");
            map.AutoMapCommand.Execute(null);

            // Profile JSON round trip.
            await vm.SettleAsync();
            vm.Setup.RootMode = RootMode.KeepInPlace;
            await vm.SettleAsync();
            string json = vm.Setup.ProfileJson();
            vm.Setup.RootMode = RootMode.AnchorPelvis;
            string message = vm.Setup.ApplyProfileJson(json, "selftest.json");
            await vm.SettleAsync();
            ctx.Check(vm.Setup.RootMode == RootMode.KeepInPlace && message.Contains("bone map", StringComparison.Ordinal),
                $"profile: saved JSON loads back (options and map) — {message}");
        }
        finally { vm.Dispose(); }
    }

    private static async Task OpensNewTab(SelfTestContext ctx, Cairn.Rfa.Assets.LibraryClip clip)
    {
        var shell = ctx.Model;
        var before = shell.ActiveDocument;
        var vm = new RetargetDialogViewModel(shell, null, clip);
        try
        {
            vm.Setup.Select("ult2_guard.v3c", "nurse1.v3c");
            await vm.SettleAsync();
            vm.RecomputeNow();
            await vm.SettleAsync();
            var doc = vm.Retarget();
            ctx.Check(doc is not null && doc.FilePath is null && doc.IsDirty && doc.DisplayName == vm.OutputName,
                $"retarget: Retarget opens {doc?.DisplayName} as a new unsaved tab");
            if (doc is not null)
            {
                await ctx.SettleAsync();
                ctx.Check(doc.Scene.MeshName == "nurse1.v3c" && doc.FittingSkeleton is not null && doc.ErrorCount == 0,
                    $"retarget: the new tab previews on the target ({doc.Scene.MeshName}, {doc.ErrorCount} errors)");
                ctx.Check(!shell.SaveCommand.CanExecute(null) || doc.IsDirty, "retarget: the new tab can be saved");
                shell.CloseDiscarding(doc);
            }
        }
        finally
        {
            vm.Dispose();
            if (before is not null && shell.Documents.Contains(before)) shell.ActiveDocument = before;
        }
    }

    private static async Task Batch(SelfTestContext ctx)
    {
        var shell = ctx.Model;
        string folder = Path.Combine(Path.GetTempPath(), "Cairn-rfa-selftest-batch-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        var vm = new BatchRetargetViewModel(shell);
        try
        {
            vm.Setup.Select("ult2_guard.v3c", "nurse1.v3c");
            await vm.Setup.SettleAsync();
            await ctx.SettleAsync();
            vm.OutputFolder = folder;
            vm.NamePattern = "st_{rig}_{clip}.rfa";
            foreach (string name in new[] { "park_jeep_driver.rfa", "ult2_walk.rfa", "ult2_stand.rfa" })
            {
                if (shell.Assets.Snapshot.FindClip(name) is { } c) vm.Enqueue(c, "self-test");
            }
            vm.RefreshNames();
            ctx.Check(vm.Clips.Count == 3 && vm.Clips[0].OutputName == "st_female_jeep_driver.rfa" && vm.CanRun,
                $"batch: three clips queued with previewed names ({string.Join(", ", vm.Clips.Select(c => c.OutputName))})");
            int asked = 0;
            vm.AskCollision = _ => { asked++; return CollisionDecision.Skip; };
            var results = await vm.RunAsync();
            ctx.Check(results.Count == 3 && results.All(r => r.Success) && results.All(r => File.Exists(r.OutputPath)),
                $"batch: three clips written ({vm.Summary})");
            ctx.Check(vm.Results.Count == 3 && vm.Results.All(r => !double.IsNaN(r.PinnedCm) && r.PinnedCm < 0.5),
                $"batch: the results table has every clip with its pinned contacts ({string.Join(", ", vm.Results.Select(r => r.PinnedText))})");
            // Automatic preset: the jeep clip is seated (the tables play it as jeep_drive), the walk and stand standing.
            ctx.Check(vm.Setup.IsAutomatic && vm.Results.Select(r => r.PresetText).SequenceEqual(["seated", "standing", "standing"]),
                $"batch: automatic presets per clip ({string.Join(", ", vm.Results.Select(r => $"{r.Clip} {r.PresetText}"))})");
            ctx.Check(vm.Results[1].Result.Result?.Contacts is { Length: 2 } legs && legs.All(c => c.IsLeg && !c.Stretched),
                "batch: the standing clips hold only the feet, and no leg is fully stretched");
            vm.RefreshNames();
            ctx.Check(vm.Clips.All(c => c.Problem is null && c.HasNote && c.Note!.Contains("ask", StringComparison.Ordinal)),
                $"batch: names already in the folder get a quiet note, not a problem ({vm.Clips[0].Problem ?? vm.Clips[0].Note})");
            results = await vm.RunAsync();
            ctx.Check(asked == 1 && results.All(r => r.Status == BatchItemStatus.Skipped), $"batch: the collision prompt is asked once per batch ({asked}) and Keep existing skips");
            vm.AskCollision = _ => CollisionDecision.Overwrite;
            results = await vm.RunAsync();
            ctx.Check(results.All(r => r.Success), "batch: Replace overwrites");
            string lines = vm.TableLines();
            ctx.Check(lines.Contains("st_female_jeep_driver", StringComparison.OrdinalIgnoreCase)
                    && (lines.Contains("+State", StringComparison.Ordinal) || lines.Contains("+Action", StringComparison.Ordinal)),
                $"batch: table lines name the outputs ({lines.Split('\n').Length} lines)");
            string report = vm.ReportMarkdown();
            ctx.Check(report.Contains("| park_jeep_driver.rfa | done |", StringComparison.Ordinal), "batch: the report lists each clip");

            // Two clips with one output name: never overwritten within the batch, the later one is renamed.
            if (shell.Assets.Snapshot.FindClip("esgd_stand.rfa") is { } esgd)
            {
                vm.ClearCommand.Execute(null);
                vm.Enqueue(shell.Assets.Snapshot.FindClip("ult2_stand.rfa")!, "self-test");
                vm.Enqueue(esgd, "self-test");
                vm.NamePattern = "dup_{rig}_{clip}.rfa";
                ctx.Check(vm.Clips.Any(c => c.Problem?.Contains("_2.rfa", StringComparison.Ordinal) == true), "batch: two clips with one output name are flagged");
                asked = 0;
                vm.AskCollision = _ => { asked++; return CollisionDecision.Overwrite; };
                results = await vm.RunAsync();
                ctx.Check(asked == 0 && results.Count == 2 && results.All(r => r.Success)
                    && results.Select(r => r.OutputName).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(["dup_female_stand.rfa", "dup_female_stand_2.rfa"]),
                    $"batch: the second clip with the same name is written as _2 without asking ({string.Join(", ", results.Select(r => r.OutputName))})");
            }

            // Long names and stock collisions are flagged before running.
            vm.NamePattern = "{source}.rfa";
            ctx.Check(vm.Clips.Any(c => c.Problem?.Contains("exists", StringComparison.Ordinal) == true), "batch: a name that is a stock clip's is flagged");
            vm.NamePattern = new string('x', 60) + "{clip}";
            ctx.Check(vm.Clips.All(c => c.Problem?.Contains("60-byte", StringComparison.Ordinal) == true), "batch: names over 59 characters are flagged");
        }
        finally
        {
            vm.Dispose();
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
