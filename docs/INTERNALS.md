# ScriptOne internals: how the host works, and what was measured to get there

Status **2026-08-20**.

> ### This document explains the host using one game as an example
>
> ScriptOne is built for Unity games in general, on both backends. To explain **how** the host
> works, this document walks through one concrete game - **Schedule I** - because a real case
> shows the shape better than an abstract one, and because that is the game the host was first
> measured in.
>
> **Schedule I is the example, not a requirement.** Nothing described here is specific to it: in
> any other game the host does the same things and finds different names. Where a passage looks
> like it is about that game, it is about the *shape of the problem* that game happened to
> illustrate.
>
> The exact internals of that game - file positions, line numbers, the wording of its own code -
> are deliberately **not** reproduced. They belong to the game, they change with each of its
> updates, and none of the reasoning here needs them. What stands here is the result of a
> measurement, described in prose.

> **All numbers below are measured, not remembered.** The measuring tools live in the repo
> (`tests\`) and can be repeated without the game.

---

## 1. The question this host answers

A Lua host for a game has to answer one question before anything else: **what can a script
actually call?**

The answer cannot be a fixed list. A list written for one game is worth nothing in the next, and
writing one per game does not scale past the first. So the host derives it instead: it looks at
the assemblies the game has loaded and works out what is worth offering to Lua. That is
`Host\SurfaceScan.cs`, and the result is written down as a readable file the user can edit.

The second question follows from the first: **how thin can the layer between Lua and the game
be?** In the example game the answer turned out to be very thin - the hand-written part of the
binding is **three** game calls, and everything else is found rather than written.

## 2. What the mod is

**The mod is the `.lua` file.** `ScriptOne.dll` is not a mod but the interpreter -
the same relationship as MelonLoader to a C# mod.

| File | Role | Location |
|---|---|---|
| `movespeed.lua` | **the mod** - all of the logic, editable without a rebuild | `LuaScripts\` (next to the exe) |
| `ScriptOne.IL2CPP.dll` | the host (interpreter + game binding) | `Plugins\` |

> ⚠ **Corrected on 2026-08-17.** The first version put the host into `Mods\` and the
> scripts under `UserData\ScriptOne\`, on the grounds that a `MelonPlugin` has no
> scene callbacks. That is only half true: `OnSceneWasInitialized` cannot be overridden
> on a plugin, but `MelonEvents.OnSceneWasInitialized.Subscribe` very much exists.
> Reasoning for the move in Section 9a.

**`UserLibs\` carries exactly one file, and only on the MelonLoader route.** MoonSharp is embedded
into the mod DLL as an `EmbeddedResource` and stays the fallback - but measured, the Mono runtime
never asks the `AssemblyResolve` hook, so the installer additionally drops
`MoonSharp.Interpreter.dll` there.
Keeping that to one file is deliberate: a file that does not exist cannot end up in the wrong
folder, and "the DLL is one level too deep" is a support case nobody enjoys on either side.

---

## 3. The mod, in full

```lua
local SPEED = 7

s1.on("game_ready", function()
    s1.console("setmovespeed " .. SPEED)

    local actual = s1.move_speed()
    if actual == SPEED then
        s1.log("Move speed multiplier is now " .. actual .. " (backend " .. s1.backend .. ")")
    else
        s1.warn("Submitted 'setmovespeed " .. SPEED .. "' but the multiplier reads " .. actual
                .. " - console commands only run for the lobby host.")
    end
end)
```

Change the number, reload the save - done. No compiler, no restart of the game.

---

## 4. The chain, link by link

### 4.1 The way in: a console the game already has

Many Unity games ship a developer console, and in the example game its entry point happens to be
**public and static**. Where that is the case, no UI is needed, no key press and no Harmony patch:
the host can hand a command line straight to the game.

> **This is an example, not a mechanism ScriptOne depends on.** Another game may have no console,
> a private one, or a different way in entirely - and ScriptOne does not care, because what a
> script can reach is whatever the surface scan finds in *that* game. The example is here because
> a concrete case explains the shape better than an abstract one.
>
> The exact locations inside the example game are deliberately **not** listed here. They belong to
> that game, they change with every one of its updates, and the argument does not need them.

### 4.2 Two silent locks, and why the mod measures instead of trusting

The example game's console has two conditions that are easy to miss, and both fail **silently** -
exactly the kind of bug one otherwise hunts for hours:

1. **Host only.** As a multiplayer guest the call does nothing, wordlessly. No throw, no log, no
   return value.
2. **Not before the console has initialised.** Its command table is filled during startup. Before
   that, the command is simply reported as unknown.

Neither is unusual, and neither is specific to that game - a silent no-op behind a permission
check is a normal shape in game code. What matters is the consequence for a script:

**Do not believe a call, measure the effect.** The example mod reads the value back afterwards and
only reports success if it actually changed. That turns the guest case into an understandable
warning instead of an apparent success, and it is the pattern worth copying into any script that
asks a game to do something.

### 4.3 The right moment

Games reset their own state during cleanup - on a scene change, when leaving a save. A one-off
assignment at mod start is therefore discarded on the next load, and the script looks broken when
it is not.

The host handles this rather than leaving it to the script: it resets its event registration on
every scene change, so `game_ready` fires again for each loaded save. A script written against
that event does not have to know when the game cleans up.

Two conditions are enough as a readiness signal, both cheap, and both about *our* side:

```csharp
// Game/GameBridge.cs
if (GamePlayer.Local == null) return false;
var cmds = GameConsole.Commands;
return cmds != null && cmds.Count > 0;
```

### 4.4 A backend difference you cannot see

`Player.Local` and `StaticMoveSpeedMultiplier` are **different kinds of member** on the two
backends:

| | Mono | Il2Cpp |
|---|---|---|
| a static instance accessor | a **field** | a **property** |
| a static multiplier | a **field** | a **property** |

That is the documented Il2CppInterop rule, and it was confirmed in both trees of the example game
rather than taken on faith. Without consequence for us: field and property are
written the same way in C# **source**. The difference would only show up on access via reflection -
and precisely then `Type.GetField()` reliably returns `null` under Il2Cpp. That is why there is not
a single line of reflection in this mod, and the shared ban list (BannedApiAnalyzers, RS0030) turns
that into a build error instead of a good intention.

The backend difference of *this* binding thus consists of **three type aliases** in a single file
(`Game/GameBridge.cs`). For the host as a whole that is no longer the full story: the standalone
branch carries a second, larger difference - the whole Il2CppInterop substructure exists only
there, and the Mono branch drops it rather than shrinking it.

---

## 5. Measured findings on MoonSharp

Measuring tools: `tests\` (game-free) and a throwaway trial run for the pure MoonSharp questions.
Environment: MoonSharp 2.0.0, `lib/netstandard1.6`, net6.0/CoreCLR, Windows, `de-DE`.

### 5.1 Does MoonSharp hold up under Il2Cpp?

**Yes - and the widespread AOT worry misses the case.** A MelonLoader mod on an
Il2Cpp game is a perfectly ordinary managed `net6.0` assembly on CoreCLR; JIT and
Reflection.Emit are available. Il2Cpp is the *game*, not the mod.

Whether that really holds under Il2Cpp was the open question, and it is answered by measurement
rather than by argument: the build runs, the DLL loads, the interpreter works - **and since
2026-08-17 in the running game as well, see Section 8.**

### 5.2 The hard sandbox is too hard

`CoreModules.Preset_HardSandbox` (value 1387) contains, **measured**:

```
assert collectgarbage error print select tonumber tostring type
next ipairs pairs string table math bit32 unpack
```

**Missing** are: `pcall`, `xpcall`, `setmetatable`, `getmetatable`, `rawget`, `rawset`,
`rawequal`, `rawlen`.

Without metatables a script author cannot build their own types (`setmetatable({}, Class)`),
and without `pcall` cannot catch errors. The hard preset alone is therefore too hard to write
real scripts against.

**Remedy, measured:** `Preset_HardSandbox | Metatables | ErrorHandling` (value 1519) brings all
eight back, **without** `io`, `os`, `load`, `loadfile`, `dofile`, `require`, `json`,
`dynamic` or `coroutine` reappearing. That is exactly what ScriptOne uses.

### 5.3 Infinite loops can be aborted - but only via a detour

MoonSharp 2.0.0 has **no** abort action: `DebuggerAction.ActionType` knows `Run`, `StepIn`,
`StepOver`, breakpoints - nothing to terminate with. The only lever is that the interpreter asks
`IDebugger.IsPauseRequested()` before every step. A **throw out of there** ends
execution.

Measured:

| | Value |
|---|---|
| `while true do i = i + 1 end` aborts after | 200,001 steps / **18 ms** |
| Script still usable afterwards | **yes** (`return 7*6` → 42) |
| Surcharge of the permanently attached hook | **1.13×** (47 ms → 53 ms over 200,000 loops) |

Because the surcharge is so small, the hook is attached permanently instead of only on
suspicion. On the game-free test bench a script with `while true do end` returns after **21 ms**
instead of freezing the game.

### 5.4 The most expensive finding: `..` is culture-dependent

On a `de-DE` machine, with no precautions at all:

```
tostring(1234.5)          ->  "1234.5"     culture-neutral
'' .. 1234.5              ->  "1234,5"     CULTURE-DEPENDENT
tonumber('' .. 1234.5)    ->  12345        silent factor of 10
```

`tostring()` is invariant, the **concatenation operator `..` is not**. A script that writes a
decimal number into a console command via `..` therefore sends something different on German
machines than on English ones - with no error message, no warning.

This hits every Lua host, and where a game parses a console argument with the runtime's culture
as well, it hits twice: two culture-dependent places in a row, neither of which reports anything.

**Remedy:** `LuaEngine.RunGuarded` brackets **every** jump into the script in
`CultureInfo.InvariantCulture` and restores the previous culture in the `finally`, so that the
culture of the game stays untouched. The example mod additionally uses an **integer**
on purpose - that has no decimal separator at all.

### 5.5 Embedding instead of placing alongside - works

`EmbeddedResource` + `AssemblyResolve`, verified by the marker `Assembly.Location == ""` (empty =
loaded from `byte[]`, not from disk). Two traps, both commented in the code:

* The method that attaches the resolver must name **no** MoonSharp type - the JIT resolves
  the types of a method before it executes it, and would otherwise look for the assembly before
  the attaching. Hence `LuaBoot` with `[MethodImpl(NoInlining)]` and `object` as parameter type.
* Between `Assembly.Load` and `return` there must be **no logging**; a throwing logger
  silently discards the loaded assembly.

Deliberately embedded **uncompressed**: the release check scans the finished DLL byte by byte
for name leaks, and a deflate attachment would be blind to it. 350 KB are worth that.

### 5.6 File name ≠ assembly name

The libraries first sat in `libs\` as `MoonSharp.Interpreter.netstandard16.dll` and `…net40.dll`
respectively. The mod built cleanly (it embeds, and there the `LogicalName` counts), but the
game-free test bench died at runtime with `FileNotFoundException` - under net6.0 the
`deps.json` decides, and that carries the **assembly** name. Layout now `libs\<tfm>\MoonSharp.Interpreter.dll`.

---

## 6. Test bench without the game

`tests\` compiles the **real** host files from `Host\` and replaces only `Game\GameBridge.cs`
with a stub. That way the real `scripts\movespeed.lua` runs through the real interpreter,
and what is checked is what actually arrives at the game boundary. The stub reproduces both
properties of the original that matter: the host lock and the `float.TryParse`
in the runtime culture.

```
dotnet run -c Release      (from the tests\ folder)
```

> **The bench was broken between `168d3d0` and 2026-08-19** and nobody noticed: `Host/GameGate.cs`
> became the seam to the game, but was never added to `harness.csproj` - 12x CS0103. It hangs off
> neither backend build, so its breakage stays invisible until someone starts it by hand. Fixed
> together with a `GameLayer` stub; whoever puts a new file into the seam enters it there as well.

| Case | Expectation | Result |
|---|---|---|
| 1 - host | exactly `setmovespeed 7`, multiplier 7, no double fire | **passed** |
| 2 - multiplayer guest | warning instead of a false success message | **passed** |
| 3 - infinite loop | abort instead of freeze (21 ms) | **passed** |
| 4 - syntax error next to a good file | broken one skipped, good one runs | **passed** |

This is only possible because the host is separated from the loader via `IScriptLog` and from
the game via `GameBridge`. That separation is what makes the core testable without a game at all,
and it is a well-established shape rather than anything novel.

---

## 7. Release check

| Check | Result |
|---|---|
| `Check-Release.ps1` Il2Cpp | passed, exit code 0 |
| `Check-Release.ps1` Mono | passed, exit code 0 |
| `Check-HarmonyTargets.ps1` | no `[HarmonyPatch]` classes - nothing to check |
| `Check-AnalyzerArmed.ps1` | **effective** (WarningLevel 6) |

Both runs report a foreign PDB path from MoonSharp
(`Z:\git\my\moonsharp\…`). That is correctly classified as an embedded third-party assembly and is
**not a name leak of our own** - the path comes from the MoonSharp author, not from this machine.

---

## 8. The run in the real game - confirmed

**2026-08-17, 20:23-20:24, Schedule I Il2Cpp (Steam default branch), first attempt.**
That settles the point that was still listed as open in Section 5.1.

### What the mod logs (`MelonLoader\Latest.log`)

```
[20:23:36.348] Melon Assembly loaded: '.\Mods\ScriptOne.IL2CPP.dll'
[20:23:36.844] ScriptOne v0.1.0
[20:23:36.844] by Virtunerd
[20:23:42.175] [ScriptOne] Loaded script: movespeed.lua
[20:23:42.175] [ScriptOne] ScriptOne ready (Il2Cpp) - 1 script(s) from …\UserData\ScriptOne
[20:24:33.627] [ScriptOne] Game is ready - dispatching 'game_ready' to 1 script(s).
[20:24:33.632] [ScriptOne] [movespeed.lua] Move speed multiplier is now 7 (backend Il2Cpp)
```

The **51 seconds** between "ready" and "Game is ready" are the load time of the save -
exactly the gap the readiness check from Section 4.3 was built for. A mod that had
set the value once at startup would have run into nothing here.

The last line is the **success branch** of the script, not the warning: the multiplier was
read back and actually stood at 7. So it is not "no error occurred", but
the measured effect.

### What the game itself logs (`Player.log`)

Independently of that, from the game's side and without any action by the mod:

```
Setting player move speed multiplier to 7
UnityEngine.Debug:Log(Object, Object)
ScheduleOne.SetMoveSpeedCommand:Execute(List`1)
ScheduleOne.Console:SubmitCommand(List`1)
```

That is the complete chain from Section 4.1 - `Console.SubmitCommand` →
`SetMoveSpeedCommand.Execute` → the game's own log line. The binding derived from the
decompilation is thereby evidenced at runtime, and from the other side at that.

### ⚠ What the run additionally uncovered: a missing dependency declaration

```
[20:23:36.796] [WARNING] Some Melons are missing dependencies, which you may have to install.
If these are optional dependencies, mark them as optional using the MelonOptionalDependencies attribute.
This warning will turn into an error and Melons with missing dependencies will not be loaded
in future versions of MelonLoader.
- 'ScriptOne' is missing the following dependencies:
    - 'MoonSharp.Interpreter' v2.0.0.0
```

MelonLoader's **static reference scan** reads the assembly references of the DLL before any
`AssemblyResolve` is attached. It therefore never sees an embedded assembly at all. Today
that has no consequence - the mod loads and runs - but the loader announces in the same line that
this will become a **load error**.

**Fix, one line:**

```csharp
[assembly: MelonLoader.MelonOptionalDependencies("MoonSharp.Interpreter")]
```

What is remarkable is less the bug than **where it did not show up**: the game-free test bench from
Section 6 is blind to this class of error, because it bypasses the host - it loads MoonSharp via the
normal .NET route and never asks MelonLoader. That is exactly what the run in the real game is for,
and exactly why it cannot be replaced by yet more test cases.

---

## 9. What is still open

Named honestly, because it limits the result.

* ~~`MelonOptionalDependencies` is missing~~ - **done on 2026-08-18.** The attribute now
  sits in `ScriptOnePlugin.cs`; demonstrated in the finished DLL via Cecil. With that the
  last message MelonLoader had attributed to ScriptOne is gone.
* **The Mono branch is only built, not run.** There is no Mono installation of Schedule I on
  this machine; the build goes against the decompilation.
* ~~**The surface is deliberately tiny**~~ - **superseded on 2026-08-17**, see Section 9a.
  Since then the surface is **generated** from `surface/candidates.json` (extent: see the head of `docs/API.md`)
  instead of wired by hand, which is what makes it survive a game update at all.
* ~~**The repo is not buildable on its own.**~~ - **done on 2026-08-18.** MoonSharp now sits
  under `ThirdParty/` together with its licence file and is versioned; `libs/` stays blocked
  unchanged, because game DLLs live there. Two folders, two meanings. Since 2026-08-19 that also holds for the
  standalone branch: the proxy assemblies are no longer shipped but generated on the user's machine
  from **his** game files (Section 9c).
  Original wording: The `.gitignore` excluded `libs/`, justified
  with "copies of foreign DLLs, legal reasons, machine-local". None of the three
  applies to MoonSharp: BSD-style licensed, contained in the shipped DLL anyway, and not
  machine-local. A fresh clone nonetheless does not find the two `MoonSharp.Interpreter.dll`.
  One of two things has to be decided - a `!` exception for `libs/**/MoonSharp.Interpreter.dll`
  plus a licence file, or a `PackageReference` on `MoonSharp 2.0.0` and the embedding via the
  resolved package path. Until then: fill `libs\netstandard1.6\` and `libs\net40\` by hand from the
  NuGet package `https://www.nuget.org/api/v2/package/MoonSharp/2.0.0`.
* ~~**`s1.console` passes arbitrary lines through.**~~ - **done.** `Host\ConsolePolicy.cs` keeps
  an allow list with three levels; 14 commands are permitted under `safe`, six more under
  `extended`, and `bind` falls below the highest level in every case: it *stores* a command for
  the game to run later and would therefore defeat any list. The classification was measured
  against the command bodies, not their names - two counter-examples out of 63 read commands: one
  sounded like weather and produced an explosion, another switched the HUD off and had no
  counterpart.

---

## 9a. From feasibility to a library (2026-08-17, part two)

After the successful run the experiment became a base library. Three decisions,
all justified by measurement.

### The interpreter is not a mod

`ScriptOne.dll` now sits in **`Plugins\`**, not in `Mods\`, and the Lua files in
**`LuaScripts\`** next to the exe. Reasoning: a mod extends the game, this thing *executes
foreign scripts*. Measured, `Plugins\` is loaded about **2 seconds before** `Mods\`
(20:23:34.7 against 20:23:36.3). A `MelonPlugin` cannot override `OnSceneWasInitialized` -
the way goes via `MelonEvents.OnSceneWasInitialized.Subscribe`, the same
solution as in ModProfiler.

> ⚠ **The following paragraph is refuted in its conclusion - see Section 9b.** The
> physical core stays correct: an Il2Cpp game brings no managed runtime with it, it
> needs a proxy DLL next to the exe that pulls a CLR into the process. What is wrong is only the
> inference "so, MelonLoader": that proxy can be UnityDoorstop, which ScriptOne brings
> along itself. The paragraph stays in its original wording, because it shows what the error hung on.

**What does not work, and for physical reasons:** leaving MelonLoader out. Il2Cpp games
contain no managed runtime; `version.dll` next to the exe is the proxy that Windows loads
and that brings hostfxr and thereby the .NET runtime into the process. MoonSharp is a
C# library - no CLR, no interpreter. The honest formulation is therefore:
**MelonLoader is our runtime, not our framework.** We take three things (CLR in the
process, entry point, log); the surface talks to the game, not to MelonLoader.

### The surface is a file, not code

```
Game assembly ──Gen-Surface.ps1──> surface/candidates.json ──Gen-Bindings.ps1──┬─> GeneratedSurface.g.cs
                                                                                ├─> surface/s1.lua
                                                                                └─> docs/API.md
```

| | |
|---|---|
| flatly bindable members on singletons (measured) | 600 actions + 312 state |
| after the plumbing filter (ISaveable/FishNet/UI colours) | **320 filtered out, reported** |
| candidates in the surface file | block `totals` in `surface/candidates.json` |
| **generated and compiled** | **the full surface** (numbers in the head of `docs/API.md`) |
| omitted because of a Lua name collision | 6 - **named individually**, not silently |

**The Il2Cpp counter-check sits inside the generator.** Generation happens from the *Mono*
set (only there are there real access modifiers - the Il2Cpp proxies are ~95 % public), but it
has to run on *Il2Cpp*. Every candidate is checked against the Il2Cpp set; result across **all**
of them: **not a single difference.** After a game update this check runs along automatically.

That the generated bindings **compile is the proof** that every single one resolves - on both
backends, without a line of reflection.

Two traps along the way, both found by the compiler:
* **Enum parameters need a real cast.** `(int)` alone gives CS1503. The generator therefore
  carries the full enum type name along and creates a `using` alias per enum.
* **Generic managers** (`App\`1`) have no nameable alias name and drop out.

### The reverse direction hardly needs Harmony - correction of one of our own findings

Section 8 said, in effect, that the game has almost no hooks and that the host has to set
them itself via Harmony. **That was measured too narrowly** - only events on
singletons with a flat surface were counted. In fact `ScheduleOne.*` has:

* **74 public events** (`event` keyword)
* **11 public static `Action` fields** - the usual way in the game's code

And they are usable ones: `Player.onArrested`, `onFreed`, `onTased`, `onEnterVehicle`,
`onStruckByLightning`, `onLocalPlayerSpawned`, `NPCRelationData.OnRelationshipChange`,
`DeliveryManager.onDeliveryCompleted`, `Dealer.OnCompleteDeal`, `Supplier.OnDeaddropReady`,
`Customer.onCustomerUnlocked`, `Business.onOperationStarted`.

`Game/EventBridge.cs` subscribes to them - **without a single Harmony patch**. Three rules stand
in the code, all learned expensively:

1. Under Il2Cpp an `Action` field is an `Il2CppSystem.Action`; a method group does not fit
   (CS0019). It needs `DelegateSupport.ConvertDelegate`.
2. The converted delegate must be **cached** - every conversion creates a
   new instance, and `-=` would otherwise find nothing to unsubscribe. The handler would stay
   attached forever and would fire more often after every scene change.
3. A static game event **belongs to everyone**. Setting the field to `null` deletes the
   subscriptions of all other mods along with it. Only `-=` and `+=`, never an assignment.

The game-free test bench has its own case for this: a Lua script subscribes to
`player_arrested` and `player_freed`, the stub raises both, and what is checked is that the
callback actually arrived. **5 of 5 cases green.**

---

### The new layout has run in the game as well

**2026-08-18, 08:27-08:28.** Plugin move, surface generator and event bridge are
not only built but have run:

```
[08:27:37.439] Melon Assembly loaded: '.\Plugins\ScriptOne.IL2CPP.dll'
[08:27:43.131] [ScriptOne] ScriptOne ready (Il2Cpp) - 2 script(s) from ...\LuaScripts
[08:28:25.393] [ScriptOne] [movespeed.lua] Move speed multiplier is now 7 (backend Il2Cpp)
[08:28:25.399] [ScriptOne] [starterkit.lua] Key bindings installed: 12/12
```

And from the game's side, in the `Player.log`, all twelve bindings by name:
`Binding F5 to save`, `Binding F6 to settime 700`, ... `Binding Keypad0 to cleartrash`.

**Zero errors and zero warnings from ScriptOne** in the entire run. The other 13 messages in the
log come from the other components loaded at the time and are unrelated to this measurement.

---

## 9b. Without a loader - ScriptOne no longer needs MelonLoader (2026-08-18)

Up to here ScriptOne was a **MelonLoader plugin**. That is convenient but costs a
third-party dependency for something that is not a mod at all: from the loader the interpreter
needs only two things - an entry point and a log.

**Result: both can be brought along by ourselves.** The host runs without MelonLoader and without
BepInEx.

**⚠ Correction of the evidence claim (2026-08-18).** This used to say "measured with **completely
removed** MelonLoader - neither `version.dll` nor a `MelonLoader\` was in the game folder". The
second part is wrong: the folder `MelonLoader\` was there the whole time (timestamp 2026-08-03,
unchanged). My directory check at the time did not list it; the same command lists it today - the
measurement could not be reproduced and is therefore not carried as evidence.

**What is actually evidenced, and it carries the statement in full:** MelonLoader's proxy
`version.dll` had been renamed and sat as `ScriptOne\disabled-loaders\version.dll.melonloader-off`.
Without that file MelonLoader is never loaded by Windows - it cannot start anything, no matter
what lies in its folder. The run therefore took place with a **non-startable** MelonLoader. That is
the weaker and correct formulation.

### The chain

| Link | With what | Why this way |
|---|---|---|
| way into the game | **UnityDoorstop 4.5.0** (`winhttp.dll`) | a proxy DLL name that Windows loads ahead of the real system library; starts CoreCLR and calls `Doorstop.Entrypoint.Start()` |
| managed view of Il2Cpp | **Il2CppInterop.Runtime 1.5.1** from NuGet | the same library MelonLoader uses - but **from NuGet**, not out of its folder. That is the difference between "uses the same library" and "depends on MelonLoader" |
| native detours | **MonoMod.RuntimeDetour 25.3.6** | purely managed, so nothing native ships with the package |
| start time | detour on `il2cpp_runtime_invoke`, wait for `Internal_ActiveSceneChanged` | solves two problems at once: the runtime is up **and** we are on the Unity main thread. Afterwards the detour immediately unhooks itself again |
| frame tick | our own, injected `MonoBehaviour` | the convenient BCL routes are **stripped away** in this build - see below |

The entire preloader is **11 files, 1,622 lines** (measured 2026-08-19). An earlier version named
6 files and 647 lines and cited two commit ids that no longer exist in this repository - a count
pinned to a commit rots twice over.

**⚠ Correction of an earlier version of this section.** This used to say "not a line was changed
on the host" - that was wrong, and the commit `cfe942f` carries the same error. `Game/EventBridge.cs`
was very much changed (+43/−6): the static field initialisers had to give way to the lazy
`DelegateVorbereiten()`, that is, exactly the rework this section describes further down as
failure 1. This file is **shared host code** - both projects compile it
(`ScriptOne.Preloader.csproj` via `<Compile Include=…\Game\EventBridge.cs />`, `ScriptOne.csproj`
via the `**\*.cs` glob).

What is correct is the weaker, still remarkable statement: only the two
files that name MelonLoader were **replaced** (`LuaBoot.cs`, `ScriptOnePlugin.cs`); the rest of the
host carried on with **one** substantive change, and that one was a bug that would have been a bug
under MelonLoader too. This is possible because the host was cut along its own log interface
(`IScriptLog`) and its own game bridge (`GameBridge`) anyway - a cut that
originally was only meant to enable the **game-free test bench** (Section 6). The same
separation carries the change of loader.

### Four failures that described the way

They stand here because each one left a rule behind.

1. **`TypeInitializationException`, reported at an uninvolved place.** The event bridge
   converted its delegates in **static field initialisers**. Those run in the static
   constructor; if that throws, the class is dead for the rest of the process, and the error
   surfaces where someone touches a field for the first time. Of six subsystems, all failed
   because of one. → lazy, encapsulated initialisation.

2. **`NullReferenceException` in `Detour.Apply` - the detour provider is NOT optional.**
   My assumption was: Il2CppInterop needs detours only for class injection, and ScriptOne
   injects nothing. Wrong - **every** `DelegateSupport.ConvertDelegate` goes through
   `ClassInjector.RegisterTypeInIl2Cpp`. A single callback on a game event is therefore already enough.

3. **No trampoline - two opposing contracts.** Il2CppInterop calls `GenerateTrampoline()`
   **before** `Apply()`; MonoMod, however, creates the `OrigEntrypoint` **only when applying**. Both
   sides correct on their own, together it jams. The obvious way out `ApplyByDefault = true`
   is ruled out: then the subsequent `Apply()` throws. Solution: pull it forward in the provider and
   make it idempotent.

4. **`Method unstripping failed` - the frame tick.** `Application.onBeforeRender` no longer exists
   in this build: if nobody in the game registers such a callback, IL2CPP throws the
   registration out of the build. ⚠ **The stack trace points at the wrong method here** -
   `add_onBeforeRender` itself is present, what is stripped is only what it calls
   (`BeforeRenderHelper.RegisterCallback`).

Instead of guessing the next candidate by starting the game, all of them were measured
**statically** (search the IL body for the string `unstripping failed`). That saved three start
attempts and showed that the obvious URP route is dead as well:

```
STRIPP  BeforeRenderHelper::RegisterCallback
STRIPP  RenderPipelineManager::add_beginFrameRendering
STRIPP  RenderPipelineManager::add_beginContextRendering
STRIPP  RenderPipelineManager::add_endFrameRendering
ok      GameObject::AddComponent(Type)  .ctor(String)  SetActive
ok      Object::DontDestroyOnLoad   MonoBehaviour::.ctor(IntPtr)
ok      PlayerLoop::GetCurrentPlayerLoop / SetPlayerLoop
```

An injected `MonoBehaviour` bypasses the problem entirely - its `Update` hangs on the native
loop and needs no BCL method that could be stripped. That is also the route that
MelonLoader and BepInEx take. It only became passable **after** failures 2 and 3 were fixed:
class injection presupposes the detour provider.

### A gap that showed up along the way

The log reported `frame tick attached` - but that is only a statement about the **registration**,
not about whether Unity ever calls the callback. Had `onBeforeRender` let itself be registered
silently and never fired, the log would have been **error-free** and all timers and
events dead nonetheless. Since then there are two separate messages - `attached` when hooking up,
`alive - first frame observed` at the first real frame - plus a watchdog that explicitly reports the
absence after 8 s.

The same class of error as "analyzer attached" against "analyzer fires": **an assurance about
the configuration is read as an assurance about the run.**

### The run

`Schedule I\ScriptOne\logs\ScriptOne.log`, MelonLoader not installed. **Verbatim excerpts**,
with `…` for what has been omitted.

Quoted is the **11:02** run - it is the last one and thus the only one that can be read back:
`FileLog` recreated the file on every start back then. *(No longer since 2026-08-19: the
running start sits as `ScriptOne\ScriptOne.log` one level up, the five previous ones in `logs\`.)* The first successful run was 10:39, still with the flat folder layout; the lines
shown here are identical in both runs, only the timestamps differ.

```
preloader alive - CoreCLR is up, Unity is not
unity version: 2022.3.62
Il2CppInterop started
il2cpp_runtime_invoke at 0x7FFD5A236D50
il2cpp runtime is up - starting Lua host on the main thread
scripts loaded from ...\LuaScripts (4)
frame tick attached (injected MonoBehaviour on 'ScriptOne')
frame tick alive - first frame observed
event bridge: static hook attached (Player.onLocalPlayerSpawned)
event bridge: 5 player hooks attached (arrested, freed, tased, tased_end, lightning)
Game is ready - dispatching to 4 script(s).
[movespeed.lua] Move speed multiplier is now 7 (backend Il2Cpp)
[selftest.lua] === self-test: 20 ok, 0 failed ===
[starterkit.lua] Key bindings installed: 12/12
[trip.lua] Heartbeat done, cancelling timer 1
```

Both directions, timers and memory - **zero errors, zero warnings.**

### The folder layout

Since `543ad1f` (2026-08-18) everything sits in subfolders, modelled on BepInEx (`core`/`interop`) and
MelonLoader (separate log and dependency folders):

```
Schedule I\
  winhttp.dll                 mechanism
  doorstop_config.ini         mechanism - the FILE NAME is baked into winhttp.dll
  LuaScripts\           4     the mods themselves, deliberately visible next to the exe
  ScriptOne\
    .doorstop_version         which Doorstop version is installed
    core-runtime\net6\  17    preloader + substructure (4.9 MB)
    interopgenerator\  136    Il2Cpp proxy assemblies (59 MB)
    documentation\            what is callable in THIS game - written by the host
    licenses\                 licence texts of the shipped third-party DLLs
    logs\                     the five previous runs (the running one sits one level up)
    state\                    memory of the scripts (s1.get / s1.set)
    disabled-loaders\         disabled version.dll among others
```

`interopgenerator\` is deliberately **not** named like MelonLoader's `Il2CppAssemblies\`: whoever stands in
a foreign game folder should see from the name whose proxies are in front of him. The old name
stays in the preloader's search path so that an older installation keeps working.

**What can NOT be filed away is measured, not assumed.** A byte search over
`winhttp.dll` (26,112 B) finds `doorstop_config.ini` as UTF-16 @0x3FF0 and `output_log.txt`
@0x482A - both names are compiled in hard, so the files have to lie in the game root.
`.doorstop_version` does **not** occur and could be moved without consequence. For `output_log.txt`
the only thing that helped was therefore to remove the cause: `redirect_output_log=false`, since
then it is not created at all any more and Unity writes to `Player.log` again.

The `doorstop_config.ini` is **generated** by `Install-Standalone.ps1`, not shipped - the
script determines the highest installed .NET 6 runtime itself (on the development machine
three sit side by side) and writes the file BOM-free, because the native parser otherwise hangs on
`[General]`.

### What the standalone branch costs

Honestly accounted for, so that the decision stays comprehensible:

* **The proxy assemblies come into being on the user's machine.** 136 DLLs, 59 MB. *(The original
  wording named 134 DLLs with 70 MB "taken from the decompilation" and listed our own generation
  run as pending - both superseded since 2026-08-18, see Section 9c.)* They may not be passed on:
  they are derived from **someone else's** game files. They are generated with
  `tools\Update-Interop.ps1 -Force`, checked with `-Check`.
* **The substructure weighs 4.9 MB** - `ScriptOne\core-runtime\` contains 17 DLLs, **16 of them
  next to** the preloader (Il2CppInterop 3, MonoMod 6, Mono.Cecil 4, 0Harmony, Iced,
  Logging.Abstractions). The plugin branch, by contrast, is **one** file plus the interpreter:
  `ScriptOne.IL2CPP.dll` at 555,008 B (542 KiB), the Mono version 563,200 B, and
  `MoonSharp.Interpreter.dll` at 357,376 B in `UserLibs\`. *(Measured 2026-08-18 on the installation and on
  `bin\`; an earlier version named "256 KB" here - that was never a measured size and contradicted
  our own statement further up that the embedded MoonSharp alone weighs ~350 KB.)*
* **The `doorstop_config.ini` carries an absolute CoreCLR path.** It is machine-local and
  is therefore **determined** at install time, not wired in - `Install-Standalone.ps1` looks for
  the highest installed .NET 6 runtime and generates the file. *(Stood here as an open cost item
  until 2026-08-18; done and described 25 lines further up in this section.)*
* **The branch brings copyleft obligations with it.** It ships 16 third-party DLLs plus the native
  `winhttp.dll`; two of them are copyleft - UnityDoorstop (LGPL-2.1) and Il2CppInterop
  (**LGPL-3.0-only**, previously not recognised as copyleft at all). Both go along unchanged and as
  separate, replaceable files, which the LGPL permits without reaching through to our own
  source - but the licence texts **must** go along, and since 2026-08-19 they do
  (`ScriptOne\licenses\`). The most durable item on the list, and the only one that is not
  technical: `Standalone/THIRDPARTY-NOTICE.md`.
* **Two loaders side by side exclude each other.** ⚠ *Until 2026-08-18 the opposite stood here:
  "Doorstop hijacks a DLL name, MelonLoader an import entry - they would technically not get in each
  other's way."* That is wrong. **Both** replace the same import entry
  (`kernel32!GetProcAddress` in `UnityPlayer.dll`), and neither chains the predecessor - exactly
  one survives, silently. Different proxy file names do not help, it is the same slot.
  `Install-Standalone.ps1` therefore does not let that state arise in the first place. See Section 9c.

**Which branch for what:** the plugin remains the normal route for users who have MelonLoader
anyway. The standalone branch is for everyone else - and it demonstrates that Lua modding for this
game does not presuppose a loader.

---

## 9c. From "runs" to "holds up" (2026-08-18, part three)

Section 9b ended with a running standalone that hung on three foreign things: on
proxy assemblies from a decompilation, on a naming that the game dictated, and on
the assumption that one has to choose between loader and standalone. All three are
resolved.

### Our own generator - the decompilation is no longer needed

`tools/InteropGen` generates the Il2Cpp proxies itself, from `GameAssembly.dll` and
`global-metadata.dat`. The chain is the same one MelonLoader and BepInEx take, only chained
**in memory** - Cpp2IL hands its assemblies straight to the generator, no
intermediate files:

```
Cpp2IL 2022.1.0-development.1452   →   Il2CppInterop.Generator 1.5.1-ci.845
                                   →   ScriptOne\interopgenerator\  136 Assemblies in 20 s
```

Three things had to be right for that, and each one on its own would have cost everything:

* **Both packages reference the same AsmResolver version** (6.0.0.0). Were there two,
  `BuildAssemblies()` could not be put into `GeneratorOptions.Source` and the whole chain would be
  dead. That is the check that belongs before the first line of code.
* **`UnityEngine.Modules` is available version-exact** as 2022.3.62 on the BepInEx feed - the
  Unity base libraries therefore need no procurement of their own. The path is stamped into the
  DLL at build time, and the tool aborts if the game's Unity version does not match it.
* **⚠ `Il2CppPrefixMode` is set to `OptIn` by default.** The first run generated 136 files,
  reported success - and was called `ScheduleOne.Core.dll` instead of `Il2CppScheduleOne.Core.dll`.
  Result 354× CS0246. Both loaders generate with `OptOut`.

The proof is not the file count but that **both branches** compile against our own set with 0
errors - and that the game run accepts them.

That also refutes a finding noted earlier: the `Il2CppAssemblyGenerator` counted as "the
actual blocker" for a loader-independent host. It is not. MelonLoader's 1235 lines
are predominantly procurement, caching and remote configuration; the generation itself is
two library calls.

### After a game update: the order, and why every switch matters

Three steps, in this sequence. None of them is optional, and two of the switches look
dispensable:

```
tools/Update-Interop.ps1 -Force      # proxy assemblies from the NEW game build
tools/Gen-Surface.ps1                # candidates from the game assembly
tools/Gen-Bindings.ps1 -All          # from those, bindings, stubs and API.md
```

* **`-Force` first.** Whoever renews the surface before the proxies measures against the old
  game build and gets a surface that compiles and still points into the void.
  `-Check` says beforehand whether it is necessary, and costs under a second.
* **The default (`-MinSurface 1`) is the value that reproduces the pinned surface** - measured
  2026-08-19. `-MinSurface 4` generates a DIFFERENT one: 56 instead of 132 managers. Both compile,
  both run; only the smaller one no longer matches what the user scripts know, and it drops whole
  tables that are worth having (`AudioManager` among them). Until 2026-08-19 both manuals demanded
  `4` here - the switch was carried over from a time when the surface was meant to stay small. The
  surface file cannot say with which value it came about. That is why it stands here.
* **`-All` is not a convenience.** The versioned `candidates.json` carries its
  entries with `include: false`; without the switch the generator binds **nothing**. Until
  2026-08-19 it wrote the empty products anyway and only complained afterwards -
  since then it aborts beforehand, without writing.
* **⚠ `-UpdateMap` bypasses the pin watchdog.** It writes the name map from whatever
  the run has just generated - a break thereby writes itself in permanently. Use only after a
  deliberate extension, never "to make the message go away".
* **The Mono set has to be pulled along.** Generation happens from the Mono decompilation (only
  there are there real access modifiers), but it has to run on Il2Cpp. If `MonoManagedPath`
  stays on an old state, the counter-check compares two different game builds
  against each other and gives a false all-clear.

After that: build both branches (the compiler reports what no longer resolves) and let the harness
run.

### The host notices by itself when the game has changed

A stamp next to the proxies records which game build they were created against (SHA256 of the
`GameAssembly.dll`). At startup it is compared and **only reported** - generating takes minutes and
does not belong in a game start that someone has just clicked.

That closes the branch's most expensive silent trap: after a game update the build stays green
(it compiles against the same folder that also runs) and the runtime does not throw, because a dead
metadata token returns a **stub** instead of an exception. Without the stamp nobody notices;
with it, it is in the log in the first second.

### The Lua names are a contract, not a by-product

Before, the Lua name was derived mechanically from the game member - a rename in the game
would have killed every user script, wordlessly. `surface/names.json` now pins every name of the
surface (extent in the head of [`API.md`](API.md)), the key is the CLR name, because that is the
breaking point.

| Case | without the map | with the map |
|---|---|---|
| member unchanged | binds | binds |
| member **renamed** | new Lua name, scripts dead | Lua name stays, target is redirected |
| member **gone** | drops silently out of the surface | **abort**, naming the name that would break |

The nicest side effect came for free: a rename appears **from both sides** - "this
pin has no target any more" *and* "this member has no pin". Old name on the left, new one on the
right; the repair stands in the error message.

The cost of that service is a JSON file in this repository and no dependency on anything
outside it.

### The generator no longer knows Schedule I

Namespace, singleton base classes and the `Il2Cpp` prefix were wired in at three places, the
base class names even twice in two scripts. Two of the three are gone entirely: since 2026-08-19
there is **no** root-namespace rule any more - the game assembly already *is* the selection, and a
count over namespaces broke on games that keep their code in the global namespace. What remains is
determined: the entry points **by the marker** - a public static `Instance` reachable through the
type's own base chain, generic base or not - and the prefix by counting in the proxy set.

One mistake along the way justifies the comparison test: the first version checked only the type
itself, and `PersistentSingleton` **inherits** its `Instance`. Result 46 instead of 54 managers -
eight entry points silently lost, and the run reported success. With the base chain the
marker search instead finds **two families more** than the hand-written list ever knew.

### One standalone for both backends

The same `winhttp.dll` carries both runtime paths - `mono_jit_init_version` @0x4530 **and**
`coreclr_initialize` @0x48B8, entry name for both `Doorstop.Entrypoint:Start`. Which one runs is
decided by the game. What cannot do both is a single assembly, so two builds from one
source state (`net6.0` / `net472`) and one `target_assembly` that the installer sets.

The Mono branch **does not shrink, it falls away**: 22 against 6 assembly references. The whole
interop substructure exists only to produce a managed view of a *non*-managed runtime.
⚠ It is compiled, **not run** - there is no Mono installation here.

### The installer chooses instead of hoping

MelonLoader and Doorstop replace the **same** import entry (`kernel32!GetProcAddress` in
`UnityPlayer.dll`), and neither chains. Different proxy file names do not help. The earlier
comment "they would not get in each other's way" was wrong.

⚠ **That holds for MelonLoader. Against BepInEx the conclusion was wrong, and correcting it on
2026-08-20 changed this whole section.** BepInEx *is* Doorstop: there is exactly **one**
`doorstop_config.ini` and both proxies read it, so a free file name buys nothing. The way through
is not to stand beside it but to **take the entry point and start BepInEx afterwards** - through
the very contract Doorstop itself invokes, `Doorstop.Entrypoint.Start()` on
`BepInEx.Preloader.dll`. Chain-starting requires one non-obvious step: BepInEx derives its own root
from `EnvVars.DOORSTOP_INVOKE_DLL_PATH`, which now points at *us*, so it must be set to BepInEx's
path for the call and reset afterwards - without that BepInEx does nothing at all, silently.

So a choice is made: MelonLoader active → **MelonLoader plugin**; BepInEx active (5 or 6, either
backend) → **ScriptOne takes the entry point** and runs as its own host, chain-starting BepInEx, and
falls back to a **BepInEx plugin** only when the configuration cannot be written; neither of the two
→ **standalone**. It aborts only when no build fits the version/backend pair at all - BepInEx 5 on
an Il2Cpp game, and then only on the fallback path. The backend only picks the target framework
(`net472` on Mono, `net6` on Il2Cpp), not the route.

Two consequences worth stating, because both reverse an earlier rule:

* **Stand-by plugin copies are laid down, not moved aside.** The standalone route writes a plugin
  for MelonLoader and for *every* BepInEx build that could load on this backend, so a loader
  installed later finds ScriptOne by itself. Two hosts in one process are prevented at runtime by
  the `HostGuard` - the first claims it, the second stands down - not by hiding a file.
* **A foreign loader's proxy DLL is never touched, but its `doorstop_config.ini` is.** That is the
  takeover. The original is saved to `ScriptOne\disabled-loaders\doorstop_config.ini.original`
  before anything is written, and `--remove` puts it back byte for byte. Whether the proxy is ours
  is decided by a **record** (`.installed-proxy`), never by content: BepInEx ships the byte-identical
  Doorstop binary, so a content check deleted its loader.

MelonLoader's `CompatibilityLayers\` would have been the earlier way in and was deliberately *not*
taken: the DLL would have to reference `MelonLoader.dll`, there is **one** slot per name, the
folder is overwritten by loader updates, and an error there takes the **whole** loader down with it.
A broken plugin only harms itself.

### Console and configuration

`ScriptOne\ScriptOne-Starter.cfg` comes into being on the first start, with comments. In it:

```
console = auto           auto | true | false - own console window; auto = on when alone
interop_log = warn       off | warn | all
check_interop = true     stamp comparison at startup
console_policy = safe    safe | extended | unrestricted
surface_policy = normal  normal | readonly | off
scene_objects = auto     auto | on | off
debug = false            extra lines for whoever works on ScriptOne itself
```

**The class lives in `Host\`, not in the preloader** - and that is not tidiness. As long as it
sat in `Standalone\Preloader\`, the only code that ever CREATED the file was the standalone
host. A MelonLoader or BepInEx installation therefore had no `ScriptOne-Starter.cfg` at all
(measured 2026-08-20 in Cocaine Dealer: under `ScriptOne\` stood only `documentation`,
`licenses` and `surface.txt`). The switches worked - the user just had no way of learning they
exist, which is the same thing as not having them. Rule 1 in that file says it outright: a
configuration you have to write yourself before you can see what is in it does not get used.

Two readers share the file, and the split is by ownership: `HostConfig` owns the **file** -
create it, keep unknown keys, rewrite it with the current descriptions. `HostSchalter` owns the
**meaning** - check the value, warn when it is nonsense, fall back to the default.

The console is pure Win32 (`AllocConsole`) and runs in the preloader, **before** Unity exists -
which is why the startup is visible too. `AllocConsole` alone is not enough, though: by that
point .NET has already resolved its standard output to "nowhere".

`interop_log` is a **level**, not a switch, and the default is `warn`. On `all` it is
17 KB per error-free start, and lines like `Unable to find method GameObject::GetComponent` are
**normal** there - that is how Il2CppInterop resolves generic methods. Whoever lets everything
through buries exactly the messages the logger is attached for.

### What the unattended run found

The self-test now has two phases, because a start without input only gets as far as the main menu -
everything that hangs on `game_ready` never ran there, and the run looked successful nonetheless.
Phase 1 covers what is reachable without a save: sandbox inventory, invariant numbers, **state
across restarts** and a timer after 5 s - the only proof that the frame loop is really running.

The first run built that way immediately found a real bug:

```
[ERR] selftest.lua:(64,7-9): attempt to call a table value
```

The generated surface is installed **after** the core and overwrites it. The manager
`SaveManager` landed as `s1.save` - exactly on the core function `s1.save()` that the README
documents. No build shows this; it is a Lua name that only collides at runtime. Fixed
with **two** locks: the generator reserves the core names (`save` → `save_manager`), and the
host checks the finished result - the second does not hang on the generator and also catches a
hand-edited map.

### Status

Four controlled game runs, three of them with a loaded save:

```
interop check: proxy assemblies match this game build
boot check: 10 ok, 0 failed        state survives restart = 4
timer fired after 5s               self-test: 20 ok, 0 failed
Key bindings installed: 12/12      0 error lines
installed = built (0.2.0.0)
```

---

## 10. What binding straight to a game costs

Binding a script host directly to a game, rather than through a library that sits in between, is a
trade with a real price on both sides. This section states the price rather than arguing for the
choice - other hosts make the other trade for good reasons, and both are defensible.

**What it buys.** The host has no dependency it does not ship itself, so there is nothing that has
to be installed first and nothing that can be missing at load time. The layer between Lua and the
game is short, which means there is less in between that a game update can break.

**What it costs.** There is nobody in between to absorb a game update. When the game changes, the
binding has to follow, and the amount of following depends on how big the surface is: for a
three-call example it is trivial, for hundreds of bindings it is only feasible mechanically.

That cost is the whole reason the surface is **generated** and measured against a frozen baseline
(`surface/abnahme.txt`) instead of maintained by hand - and, since 2026-08-20, why it is found in
the running game rather than compiled in at all. A design decision is worth only as much as the
mechanism that pays for its downside.
