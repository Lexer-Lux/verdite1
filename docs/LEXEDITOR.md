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
  stencil. Original stencil geometry is retained, so this is not a way to
  shrink the original world's reach.
- **Smooth distance fade** applies the renderer's ordered distance fade to
  geometry at that edge. It works independently at the original reach too.
- **Fog** chooses where the original game's darkening is evaluated: corners,
  pixel depth or radial distance. It is not the geometry fade toggle. Original
  fog becomes black at about 9.17 cells, so geometry beyond that remains dark.
- **Enhancement distance** limits per-pixel lighting and other enhancements.
  It does not select which world cells are drawn.

The inherited maximum of 30 cells is retained. A cell is 2,000 game units;
the retained renderer uses a 16-bit view depth, whose far plane is 65,536.
See `RenderDistance.cs`, `DistanceFade.cs` and `docs/GPU_RENDERER.md`.

With the optional diagnostic channel enabled, `distance` reports both the
settings and the current retained frame's gate and fade band. `distance 24 0`
sets and saves view distance 24 with smooth fading off. Invalid distances are
rejected. This diagnostic server is opt-in, bound to loopback, as before.

## Controls

**Apply Lexer's controls** in F1 → Testing writes ordinary input settings:

- RT/RB attack; LT/LB and Square use the equipped spell or item.
- Cross/E interact; Circle/Left Shift held runs at twice movement speed.
- Triangle/Tab opens the menu; Q uses the equipped item or spell, including
  the map. Cross confirms and Circle backs out of menus. Start/Select remain.

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
`a65dea7487dd47758f102c6fd0e48a93efd43d72` in
`Lexer-Lux/verdite-recompone` (`codex/kf1-hidden-runtime`). The generic changes
are the configurable mod root and opt-in `RECOMPONE_HIDDEN=1` window creation
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
  external library, with 14 registered hooks. No new gameplay mod was made.

These are runtime measurements, not visual gameplay acceptance. Physical
controller feel, the look of distant geometry and menu usability still need
the player's acceptance.
