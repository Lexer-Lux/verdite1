# Lexer's King's Field I port

This fork targets the original Japanese 1994 game (`SLPS-00017`), with the
existing English fan translation supported. It starts from Verdite1's
`archive/recomp` commit `529a6b4999ab1cd246800cbfe89f8dea14e8ef3f`, the port
already used by this installation. Upstream's current main branch uses a
different C++ port; those sources and build instructions do not apply here.

No disc image, extracted executable or generated recompilation source is
published. Follow `docs/DEVELOPMENT.md` with your own disc to rebuild locally.
The ordinary compiled port DLL is installed locally; original disc bytes and
the installation's executable are not patched.

## Distance settings

F1 → Testing has a numeric **View distance** and an independent **Smooth
distance fade** checkbox. Both persist in `interface.ini`, using the existing
`kf1.renderdistance` and `kf1.renderdistance.fade` keys. Turning fading off
sets its band to zero without changing view distance. Turning it on uses the
existing three-cell band. The separate fade-band control remains available.

- **View distance** extends the retained map and models past the original
  stencil. The supported extended range is 8–30 cells; 0 selects the original
  stencil and original fog. Extended values stretch the retained fog curve to
  the selected edge. Original stencil geometry is retained, so this is not a way to
  shrink the original world's reach.
- **Smooth distance fade** applies the renderer's ordered distance fade to
  geometry at that edge. It works independently at the original reach too.
- **Fog** chooses where the original game's darkening is evaluated: corners,
  pixel depth or radial distance. It is not the geometry fade toggle. Original
  fog becomes black at about 9.17 cells; extended view distance now stretches
  that curve so additional geometry is actually visible.
- **Enhancement distance** limits per-pixel lighting and other enhancements.
  It does not select which world cells are drawn.

The inherited maximum of 30 cells is retained. A cell is 2,000 game units;
the retained renderer uses a 16-bit view depth, whose far plane is 65,536.
See `RenderDistance.cs`, `DistanceFade.cs` and `docs/GPU_RENDERER.md`.

With the optional diagnostic channel enabled, `distance` reports both the
settings and the current retained frame's gate and fade band. `distance 24 0`
sets and saves view distance 24 with smooth fading off. Invalid distances are
rejected. This diagnostic server is opt-in, bound to loopback, as before.

### Visible range correction (2026-11-03)

The previous build accepted 30 and submitted more cells but retained original
black fog. At matched camera (55000,-25000,35000), pitch512/yaw0, 15 and 30
therefore produced identical visible output despite gates300/851. The corrected
retained map and world-model cue multiplies DQA by max(1,cells*2000/18333.333),
derived from native DQA-17600/DQB20971520/H200 and doubled IR0. Black is now at
30000/60000 for 15/30; native dynamic dimming, fog mode and independent geometry
fade remain intact. The map-cache key includes this scale. Zero restores native fog.

Model placement also truncated full relative positions to signed16 before
RotTrans. A scoped post-hook on 8004DADC corrects only the three frame-local
render translation words, using existing ModelSmoothing table identities. It
covers interactions, actors, flipbook sprites, effects and people before any
vertex projection, including translucent paths. Guest records and GTE registers
are unchanged. Exact caller gates exclude the arm, menu and gameplay calls.

The corrected candidate visibly adds rooms/structures at30; gates remain300/851
and both fade states were captured. Actual native InputFloat edited15->24->30,
saved30 with fadeoff. Twelve sampled far sprite/person placements matched their
full-coordinate fixed-point reference (the old build wrapped them); 10045
boundary/random matrix checks passed. The whole 100x100 current-area map is
already loaded and baked in full32world coordinates; no new sector or collision
loading is needed. The maximum30 stays inside the65536 view-depth range.

The far audit also found two retained-only paths: translucent models interpreted
unsigned SZ as signed16 in blend ordering, and billboards kept a flat cue from
the original IR0. Blend ordering now reads unsigned depth; extended world sprites
re-evaluate their cue using stretched DQA and the last RTPS's exact read-only
H/SZ3 quotient, including the native conditional3/2 multiplier. Original-distance
and nonworld cues remain bit-identical. 62,785 boundary/math invariants passed;
extended fog intentionally has no native-packet colour-parity claim.

KF1 has one native control setup popup. There is no registered shared onboarding
Controls page; first-run and rerun open the same Keep/Classic/Modern page. Both
were rendered, repeated opens left one popup, and bindings/sensitivity/settings
bytes stayed unchanged. No duplicate-page code path exists to remove here.

## Controls

First launch and **Run control setup again** in F1 → Testing offer:

- **Keep current bindings**, selected initially; opening setup changes nothing.
- **Classic Controls**, the exact baseline upstream port bindings.
- **Modern Controls**: RT/RB attack, LT/LB cast, Cross/E interact, and
  Circle/Left Shift held run. Original Square attack, Triangle/Q item or spell,
  Select/Tab menu, and menu confirm/back actions stay available.

Both frontends read `content/config/controls.json`; there is no second preset
table in Lexeditor. Applying a preset replaces custom bindings only after an
explicit confirmation. Other native settings and memory-card paths remain.

Run uses the otherwise unused R3 action. In gameplay, a held R3 suppresses
Circle's overlapping menu-back action and scales the distance passed to the
game's collision move routine. It does not change turn speed or collision
logic. Rebind R3 in Input to change the run binding. The Hold to run setting
can disable this behavior. This is part of the port, not an enabled mod.

This archived port has **Debug Tools**, not a Remaster Editor. F2 opens that
optional existing mod's panel only when it is enabled; setup does not enable
it or create any other gameplay mod.

## Native library and runtime pin

The runtime reads its mod directory from the saved `mods.root` setting,
defaulting to `mods` as before. Lexeditor points it directly at the shared
King's Field I library and writes the existing `mods.<id>.enabled` keys.
There is no copied deployment or second mod format. Native source mods still
use a root `mod.json` and C# source, native hooks and declared dependencies.
Compatibility of arbitrary hook replacements must be tested per mod.

The RecompOne subtree equals commit
`926e00e36ac231fe059a8eb889db237bf6bf6fe2` in
`Lexer-Lux/verdite-recompone` (`codex/kf1-mod-hud-runtime`). The generic changes
are a full-display HUD render event, configurable mod root and opt-in
`RECOMPONE_HIDDEN=1` window creation
for isolated renderer checks. Other ports' pinned runtimes are unchanged.

## Measured verification

In an isolated hidden OpenGL session with private memory cards:

- English New Game reached area 1 with HP 30/30.
- At 8 and 30 cells with fading off, retained frame gates contained 127 and
  732 cells. At each distance, toggling fade changed its frame band between
  0 and 6,000 without changing fog or enhancement distance.
- View distance 24 and fade off survived a separate process restart.
- A short forward movement measured 1,440 units walking and 2,766 running.
- The existing Debug Tools source mod compiled and loaded directly from an
  external library, with 14 registered hooks.

The separately requested Forget Me Not, Better Sprint, XP Bar and Better Poison
live in the user's ordinary mod library. The port exposes removable interaction,
save-extension, glint, native notification and sprint policy APIs, and contains
no enabled gameplay mod. Their policies and settings stay with those mods.

Hidden rendered checks observed the native first-run/rerun choices, absolute
bottom XP bar and reversible removal, English and Japanese poison/cure text,
green poison in both renderers and red restored on disable. Native interactions
reported locked chest, emptied chest and successful paired door opening.
The bounded sidecar restored the saved locked state on native load, including
a load in a new process. At a configured drain of 100% full gauge per second,
sprint consumed weapon power while MP stayed at 20; disabling the mod restored
zero drain. Default drain is zero because retail has no sprint stamina pool.

These are isolated engine checks, not physical-controller acceptance. Physical
controller feel, the look of distant geometry and menu usability still need
the player's acceptance.
