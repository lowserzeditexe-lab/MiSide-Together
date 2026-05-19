# MiSide Together v1.5.9

**Focus:** Diagnostic Harmony patches to identify which parameters MiSide really sets on the "Person" Animator. This is the missing piece to fix ghost animations.

---

## Background

Five releases worth of attempts:
- v1.5.4: cullingMode + applyRootMotion + Play(layer=-1)
- v1.5.6: enumerate params from static context — **failed**, GetParameter is stripped 15/15
- v1.5.7: hardcoded MiSide param names (`SpeedForward`, `Walk`, `Run`, ...) — **no visible effect**
- v1.5.8: added `anim-diag` receiver-side log

Hypothesis still standing: **MiSide does NOT drive the `Person` Animator with the params I hardcoded.** Possibly because `Person` is the third-person body which the local player never sees in first-person view — the game might use a stub controller, or drive it via a different mechanism (procedural? IK only?).

---

## What v1.5.9 does

Adds **Harmony Postfix patches** on these `UnityEngine.Animator` methods:
- `SetFloat(string, float)`
- `SetBool(string, bool)`
- `SetTrigger(string)`
- `Play(int, int, float)`

The patches log every call **made by the game code** — but **only when the target animator's GameObject is `Person`, `Player Arms`, `Player2_Guest`, or `Player1_Host_Remote`** (so we filter out the noise from NPCs, UI, etc.).

To avoid log spam, each method logs the first 40 distinct calls only, then goes silent.

---

## What I need from you

1. Update to v1.5.9 (AutoUpdater will detect it).
2. Once in-game, **make BOTH players move around** for ~30 seconds (walk, run, jump if possible).
3. Send me the entire BepInEx log file (`MiSide/BepInEx/LogOutput.log`).

I'll be looking for lines like:
```
[Co-op] anim-patch SetFloat: on='Person' param='Скорость' value=2.31 (call #1)
[Co-op] anim-patch SetBool: on='Person' param='БегВперед' value=True (call #4)
[Co-op] anim-patch Play: on='Person' stateHash=1234567 layer=0 normTime=0.000 (call #2)
```

Those names ARE the truth. Once I have them, the real animation fix is trivial — I'll mirror the host's actual calls onto the ghost's clone in v1.5.10.

---

## Files changed

- `Plugin/PluginInfo.cs` — 1.5.8 → 1.5.9
- `MiSideCoop.csproj` — added new compile entry
- `Plugin/Patches/AnimatorDiagPatch.cs` — **new**, Harmony postfix on Animator.SetFloat / SetBool / SetTrigger / Play
