# Samples

Small, fully synthetic files to try Cairn on without the game's data. Every file here,
this README included, is written by `tools/Cairn.Rfa.SampleGen`; nothing comes from the game.
To regenerate them (the output is byte-for-byte the same every time):

```
dotnet run --project tools/Cairn.Rfa.SampleGen -- samples/rfa
```

`SampleTests` in the test project regenerates the set and checks that these files match it, that
the clean files have no errors or warnings, and that the broken clip trips the rules listed below.

| File | What it is |
| --- | --- |
| `sample_figure.v3c` | A blocky 15-bone biped about 1.77 m tall, facing +Z with its feet on the ground: pelvis, spine, head, and upper arm, forearm, hand, thigh, shin and foot on each side. Each box is skinned to one bone. One material, three collision spheres (`head`, `torso`, `legs`) and a prop point (`hand_grip`, in the right hand). |
| `sample_figure.tga` | Its 64x64 24-bit texture: one tile per material (shirt, skin, trousers, boots, belt, hair, gloves), with a face on the head's front and a zip on the shirt's front so you can tell which way the figure faces. |
| `sample_figure_idle.rfa` | A two-second breathing idle that loops: the weight sways from foot to foot and the head looks around. |
| `sample_figure_walk.rfa` | A walk cycle in place (32 frames) that loops: legs, knees, feet, arms and pelvis all keyed, with smooth Bezier control points on the pelvis bob. |
| `sample_figure_wave.rfa` | The right arm rises, waves twice and comes back down, with ease-in/ease-out on every key. Made to play as an action: the arm's bones have weight 10 and the rest 0, with 480/640-tick ramps. |
| `sample_figure_broken.rfa` | A one-second clip that is wrong on purpose. Open it to see the Problems panel and its quick fixes. |

The clips address the figure's 15 bones by index, so preview them on it: add this folder as a search
folder (Settings) and choose `sample_figure.v3c` as a clip's preview mesh.

## What `sample_figure_broken.rfa` gets wrong

| Code | Severity | Problem |
| --- | --- | --- |
| RFA003 | Error | `hand_l` has no position keys, so it collapses onto its parent's joint (the elbow). |
| RFA005 | Error | `shin_r` has two rotation keys at the same time (tick 2560, halfway through). |
| RFA006 | Error | `head` has a rotation key two frames after the clip's end. |
| RFA012 | Error | `hand_r`'s last rotation key is all zeros. |
| RFA004 | Warning | `foot_l` has no rotation keys. |
| RFA020 | Warning | `thigh_l`'s position keys have Bezier control points left at (0, 0, 0). |
| RFA021 | Warning | `spine`'s last rotation key is stored 0.9 long. |
| RFA022 | Warning | The ramps (2400 in + 2880 out ticks) are longer than the one-second clip (4800 ticks). |
| RFA023 | Warning | `forearm_l` is half as long again as in the other clips (needs the library: it compares with the idle). |
| RFA025 | Warning | `pelvis` has weight 12. |
| RFA028 | Warning | `upper_arm_r`'s two keys are stored slightly longer than 1 and 0.1 degrees apart, so the game's slerp snaps instead of turning. |
| RFA030 | Info | `upper_arm_l`'s middle key is stored negated (the same rotation, the other hemisphere). |
| RFA031 | Info | `forearm_r`'s last rotation key has a non-zero pad word. |
