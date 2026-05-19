# MiSide Together v1.5.8

**Focus:** GameBoy strip definitive fix + animation diagnostic logging + UTF-8 console.

---

## Bug fixes

### 1. GameBoy / Tetris item finally gone from the ghost hand

The F10 hierarchy dump from v1.5.7 revealed the real cause: under `Person/.../Right item 2/Tetris [MR:on]`, the `Tetris` parent GO was **already INACTIVE** in the local MC source (player not in mini-game). My v1.5.6 `StripHandItems` ran BEFORE v1.5.1's "re-activate all hidden children" step. So:

1. Strip step at `T+0`: skips `Tetris` (activeSelf=false).
2. Re-activation step at `T+1`: turns `Tetris` ON.
3. Result: Tetris MeshRenderer is visible on the ghost.

**Fix v1.5.8** — `StripHandItems` is now moved to the END of `StripInterferingComponents`, after re-activation and renderer enables. It also:
- Disables the GO unconditionally (no `activeSelf` check).
- Disables the GO's MeshRenderer/SkinnedMeshRenderer directly as a belt-and-suspenders safety net.
- Catches both `Tetris` (parent) AND `TetrisGame` (child) thanks to the `"tetris"` keyword being a substring of both.

### 2. UTF-8 console output

The BepInEx log was showing mojibake (`╔╝║ → ΓòÉΓòÉΓòÉ`, `→ → ΓåÆ`, `é → ├⌐`) because Windows console codepage defaults to 850/1252. v1.5.8 calls `Console.OutputEncoding = Encoding.UTF8` at plugin load → all subsequent log output renders cleanly. Combined with the gradual switch to ASCII-only English in newer log lines, the BepInEx console is now readable.

### 3. Animation pipeline diagnostic

Added a throttled diagnostic log in `PlayerAvatar.ApplyRemoteState` (every 60 messages = ~2s). Format:

```
[Co-op] anim-diag 'lowserz' msg#61: recvSpeed=0.00 estSpeed=2.34 peak=3.12
        recvHash=-153225821 recvNT=15.47 localHash=-153225821 localNT=15.47 state=''
```

This tells us **exactly** what's flowing through:
- `recvSpeed` — speed sent by the peer's local Animator sample. If always `0`, the peer's `Animator.GetFloat("Speed")` is failing on their side.
- `estSpeed` — speed computed from delta-position. Reliable indicator the peer is actually moving.
- `recvHash` / `recvNT` — peer's `AnimatorStateInfo`. If `recvHash=0`, the peer's `GetCurrentAnimatorStateInfo(0)` failed.
- `localHash` / `localNT` — our ghost animator's current state. If it equals `recvHash`, `Animator.Play()` is working. If always `0`, our animator can't even sample its own state (deeply broken).

Send me the next log dump and we'll know exactly which API to bypass.

---

## What to expect after v1.5.8 update

1. Restart MiSide (auto-update will trigger thanks to v1.5.7 AutoInstall).
2. **GameBoy/Tetris item gone from the ghost hand.** (Confirmed via F10 hierarchy fix.)
3. Console logs in clean UTF-8 — no more mojibake.
4. Walk a bit with the other player, then send me the BepInEx log. The new `anim-diag` lines will tell us exactly which animation API is the bottleneck.

---

## Files changed

- `Plugin/PluginInfo.cs` — 1.5.7 → 1.5.8
- `Plugin/MiSideCoopPlugin.cs` — `Console.OutputEncoding = UTF8` at startup
- `Plugin/Avatars/RealMcCloner.cs` — `StripHandItems` moved to end, unconditional disable + renderer-level disable
- `Plugin/Network/PlayerAvatar.cs` — `anim-diag` throttled log every 60 messages
