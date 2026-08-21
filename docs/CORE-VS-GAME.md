# Core versus game layer: what ScriptOne is without any particular game

ScriptOne is built for Unity games in general. This document answers the question that follows
from that: **which part of ScriptOne is tied to a specific game, and what is left when that part
falls away?**

> **On the game used here.** The examples below use **Schedule I**, because that is the game the
> host was first measured in and the one whose numbers can be quoted exactly. It is an
> **example**, not a requirement. Everything described as "core" behaves the same in any Unity
> game; everything described as "game layer" is what a *particular* game contributes, and that is
> different in every one of them.

The question came up for a concrete reason: on 2026-08-19 the Mono branch ran in a foreign game
for the first time (No Knock, Unity 6, MelonLoader 0.7.3) and stopped at a type that does not
exist there. The useful question was not "where do we catch that", but the one above.

## How it was measured

Three separate read-throughs over the three surfaces, each with a **positive control** whose
answer was fixed beforehand. Without one, a run that finds nothing is indistinguishable from a run
that failed to look.

| Surface | Positive control | Result |
|---|---|---|
| `Host\Generated\GeneratedSurface.g.cs` | number of lines mentioning the example game's namespace, counted independently first | matched |
| `Game\*.cs` | must be exactly two files | 2 (`EventBridge.cs`, `GameBridge.cs`) |
| `Host\*.cs` without `Generated\` | `s1.log` must be findable | found in `LuaEngine.cs` |

## The result in one sentence

**A surface compiled against one game is game layer, all of it.** Every Lua table in
`GeneratedSurface.g.cs` binds to types from that game's own assembly; not one of them works
without it. In a different game such a surface does not degrade *partly*, it is simply absent.

That is exactly why ScriptOne does not rely on it any more.

| Surface | Scope | works without the game | needs the game |
|---|---|---:|---:|
| `Host\Generated\GeneratedSurface.g.cs` (built in, one game) | all its tables and bindings | **0** | **all** |
| `Game\*.cs` (the seam) | 19 members | 6 | 8, plus 5 undecided |
| `Host\*.cs` (the core) | 20 files | 19 | 1 |

The one core file that names a game is `GameLayer.cs` - the **probe**, and it names it on purpose.
Every other access goes through the gate in `GameGate.cs`, where it sits in its own `NoInlining`
method with a fallback that applies when there is no game. `LuaEngine.cs` calls the gate and names
no game type of its own.

## What replaced the built-in surface

A compiled-in surface can never gain an entry afterwards, so in any other game its yield is zero.
Since 2026-08-20 the surface is therefore **found in the running game** instead:

| File | Job |
|---|---|
| `Host\SurfaceScan.cs` | walks the loaded assemblies by reflection and decides what is worth binding |
| `Host\SurfacePlan.cs` | holds the plan and the line format written to `ScriptOne\surface.txt` |
| `Host\RuntimeSurface.cs` | binds that plan to `s1.*` |
| `Host\SurfacePolicy.cs` | how far the binding may reach (`normal`, `readonly`, `off`) |

This removes the whole question of which folder applies per backend: in-process the types are
already loaded, on Mono and on Il2Cpp alike, as ordinary .NET types.

Measured in a foreign game (No Knock, Unity 6000.0.46f1, Mono): a scan of 3213 public types in
78 ms produced **66 tables with 754 members**, up from zero. The built-in surface for the example
game still exists and is still used when that game is the one running; in every other game it
simply does not apply, and the host says so in one line rather than failing.

## The 13 core functions, one by one

All are registered in `LuaEngine.InstallSurface`.

| `s1.*` | works without a game? | what it rests on |
|---|---|---|
| `s1.log` | **yes** | - |
| `s1.warn` | **yes** | - |
| `s1.after` | **yes** | `Timers` (a stopwatch, deliberately not the game clock) |
| `s1.every` | **yes** | `Timers` |
| `s1.cancel` | **yes** | `Timers` |
| `s1.get` | **yes** | `ScriptStore` |
| `s1.set` | **yes** | `ScriptStore` |
| `s1.save` | **yes** | `ScriptStore` |
| `s1.on` | **partly** | registration works, and `game_ready` fires without a game because it belongs to the host. All other events come from `EventBridge.Attach` and therefore from the game |
| `s1.backend` | no | `GameGate.BackendName()` - reports `core-only` without a game |
| `s1.console` | no | `GameGate` to `GameBridge.SubmitConsoleCommand` |
| `s1.move_speed` | no | `GameGate` to `GameBridge.GetMoveSpeedMultiplier` |
| `s1.surface_size` | no | the size of the bound surface |

**8 of 13 work without any game, 1 partly, 4 do not.** What is left is a complete Lua host:
loading scripts, the sandbox, the execution budget, timers, per-script memory, error isolation and
the log - just without any reach into a game.

## The `core-only` mode, and the one mechanism it depends on

The probe is [`Host\GameLayer.cs`](../Host/GameLayer.cs). It answers the question **once** at
startup and remembers the answer.

The mechanism is the point of it and is not interchangeable: the JIT resolves the types a method
uses **before** it runs the method. A `try` *inside* the method that names the game type is
therefore too late - the `TypeLoadException` happens on **entering** it. So the access sits in its
own method marked `[MethodImpl(MethodImplOptions.NoInlining)]`, and the `try` is around the
**call**. Without the attribute the JIT may inline the probe into its caller and restore exactly
the state the file exists to prevent.

Only `typeof(GamePlayer).FullName` is checked, deliberately - not game state. A probe that touched
a game class that is not initialised yet would raise a false alarm **in the right game** and switch
the game layer off for no reason.

What the hook-up does:

1. **Ask once, early:** `GameLayer.Pruefe(log)` before the surface is installed.
2. **On `false`, skip the game accesses** - in the *gate*, not at the call site. The built-in
   surface and `EventBridge.Attach` are skipped entirely; the four game-bound core functions answer
   from the fallback (`s1.backend` reports `core-only (no game bindings)`). `s1.on` stays
   registered and accepts subscriptions, and `game_ready` does fire, because it belongs to the host
   and is the only entry point a script has in core-only mode.
3. **Say what applies.** `GameLayer` writes a line to the log in every case, including success. A
   switch that flips silently is one nobody can find later in a user's log.
4. **Do not guess what a foreign game offers instead.** `core-only` means the host runs and the
   built-in game layer is off. Finding a surface for the game that *is* running is the job of the
   runtime scan described above, and it is a separate mechanism on purpose.

### Measured in a game, with one case still open

The probe has run in a real game: in No Knock (Unity 6000.0.46f1, Mono, MelonLoader 0.7.3) it
reported `Game layer: NOT available - game type not found (TypeLoadException ...)` and switched the
built-in game layer off without taking the host down with it.

`HINWEIS[UNGEPRUEFT]` still applies to **Il2Cpp**: it has not been measured there whether a
`TypeLoadException` is really what is thrown rather than something else. The catch list therefore
covers several exception kinds and falls back to `Exception`, naming the kind in the reason.

## Side finding, not part of the inventory - fixed

`Escape` and `Unescape` in `Host\ScriptStore.cs` both performed a replacement of a real newline by
a real newline: in C# that is an identity mapping and does nothing. What was meant was a newline to
the two characters backslash-n and back. The backslash half next to it was correct, which is why
the pair looked plausible. Consequence: a stored value containing a newline broke the
one-line-per-key format on the next load. `Check-Steuerzeichen.ps1` cannot catch this and says so
in its own header: an executed escape is indistinguishable from a real newline.
