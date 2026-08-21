# ScriptOne - manual

A Lua framework for Unity games, on Mono and Il2Cpp.

> **On the paths in this document.** Where a folder is shown, it is written as
> `<game>\` - your game folder, whatever it is called. A few passages name **Schedule I**
> because that is where a number was measured; it is an **example**, not a requirement.
> ScriptOne works the same way in any Unity game, and what a script can call comes from the
> game you have.

## Install

One file does it: **`ScriptOne-Installer.exe`**. Double-click it, check that the game folder it
found is the right one, and press the button. Everything ScriptOne installs is already inside
that exe - there is no ZIP to unpack, and on a Mono game the install needs no internet at all.
On an **Il2Cpp** game it downloads the matching Unity base libraries once (about 3 MB) so it can
derive the proxy assemblies from your copy of the game.

You do not choose a build. The installer reads the backend (Il2Cpp or Mono) and the bit width out
of the game's own binaries and installs the one that fits; guessing here would give you a loader
that simply never starts.

**What it does depends on what is already in the game folder:**

| What it finds | What it installs |
|---|---|
| no mod loader | its own loader (UnityDoorstop) plus the host -- ScriptOne runs on its own |
| **MelonLoader** active | a MelonLoader plugin, so your other mods keep working -- plus a disarmed loader of its own that stays silent and takes over by itself if you ever remove MelonLoader |
| **BepInEx** active | ScriptOne takes the entry point and starts BepInEx itself afterwards, so your plugins load exactly as before -- and ScriptOne keeps working if you remove BepInEx later. The loader's original `doorstop_config.ini` is saved first. |

On an **Il2Cpp** game without a loader there is one extra step: the proxy assemblies have to be
generated from *your* game files, because they are specific to your build. The installer does that
itself and downloads the matching Unity base libraries for it (about 3 MB, from
`nuget.bepinex.dev`) -- the only thing it ever fetches. Il2Cpp games also need the **.NET 6 Desktop
Runtime**; the installer says so and stops rather than installing something that cannot start.

Run it a second time and it updates in place -- that is safe and the button says `Update`.

**Updating to a later version** means downloading the new exe and running it once. The installer
carries its own payload and has no version list of its own.

The command line, the exact removal behaviour and what is deliberately left out of a release are
under [Releases](#releases) -- not repeated here, because two places saying the same thing end up
saying two different things.

### Standalone

ScriptOne also runs **standalone** - on its own host rather than as somebody's plugin. That is
the normal case with no loader installed, and it is also what happens next to BepInEx, where
ScriptOne owns the entry point and starts BepInEx itself. Your `.lua` files and the whole
`s1.*` surface behave identically. What differs is where things live:

| | Plugin build | Standalone build |

| starts via | MelonLoader | UnityDoorstop (`winhttp.dll`) |
| interpreter | `<game>\Plugins\ScriptOne.IL2CPP.dll` | `<game>\ScriptOne\core-runtime\net6\` |
| your scripts | `<game>\LuaScripts\` | same |
| log | MelonLoader's `Latest.log` | `<game>\ScriptOne\ScriptOne.log` |
| script state | `UserData\ScriptOne\<script>.lua.state` | `<game>\ScriptOne\state\<script>.lua.state` |
| what you can call | `<game>\ScriptOne\documentation\` | same |

There is a **third** case that the table does not show: **next to BepInEx, ScriptOne uses the
standalone column** - it owns the entry point and starts BepInEx itself afterwards, so it writes
its own `ScriptOne\ScriptOne.log` and lays everything out exactly as a standalone installation
does. Your BepInEx plugins load as before.

Only if the entry point cannot be written does it fall back to being a **BepInEx plugin** in
`BepInEx\plugins\ScriptOne\`, writing into BepInEx's own log - and it says so when it has to.
BepInEx **5 and 6** are both served, Mono and Il2Cpp; the three plugin builds are not
interchangeable and the installer picks the right one, so there is nothing for you to choose.

Layout in the game folder:

```
<game>\
  winhttp.dll               both must sit next to the exe: winhttp.dll because that is the
  doorstop_config.ini       name Windows resolves from the game folder first, and the .ini
                            because its name is compiled into winhttp.dll
  LuaScripts\               your mods
  ScriptOne\
    ScriptOne-Starter.cfg   settings, every option explained inside the file
    ScriptOne.log           the run you are in
    logs\                   the five previous runs, .log.1 is the most recent
    documentation\          what you can call IN THIS GAME - written by ScriptOne itself
    core-runtime\<tfm>\     the host and its dependencies (net6 on Il2Cpp, net472 on Mono)
    interopgenerator\       Il2Cpp proxy assemblies, generated from your own game files
    licenses\               the license texts of everything foreign that ships
    state\                  script memory
    disabled-loaders\       loader files set aside, e.g. MelonLoader's version.dll
    diagnostics\            things that are not ours but turn up here, e.g. Unity's
                            output_log.txt when Doorstop is told to redirect it
```

Install it with `Standalone\Install-Standalone.ps1 -Mode Standalone`. The script writes
`doorstop_config.ini` itself and locates your .NET 6 runtime - do not hand-edit that file, it is
regenerated. `-Mode Plugin` switches back, `-Mode Remove` removes everything.

> The `.ps1` is the **developer** tool and is not part of a release. What ships is a single
> installer executable - see *[Releases](#releases)* below.

Three caveats, stated plainly:

* It needs the **.NET 6 runtime** installed. The installer finds it; if none is present it stops
  and says so.
* **The Il2Cpp proxy assemblies are NOT part of any package.** They are generated from **your**
  game files and cannot legally be redistributed. **The installer generates them during setup** -
  Cpp2IL and Il2CppInterop.Generator, chained in memory. The generator travels inside the
  installer and stays in `ScriptOne\generator\`, so a game update can be handled later without
  running setup again:

  ```
  ScriptOne\generator\InteropGen.exe --game "<game folder>" --out "ScriptOne\interopgenerator"
  ```

  **One thing is fetched from the internet, and only this one:** the Unity *base* libraries have
  to match the game's Unity version exactly, and an Il2Cpp game carries no managed copy of them.
  The installer downloads the matching `UnityEngine.Modules` package (about 3 MB) once per game.
  It cannot be shipped instead - there are over 1500 Unity versions. Mono games need none of
  this, and neither does a plugin install that never loses its loader. If you are offline the
  installer says so and everything else is still installed.

  In the repository the same job is done by:

  ```
  .\tools\Update-Interop.ps1 -Force     # writes ScriptOne\interopgenerator\
  .\tools\Update-Interop.ps1 -Check     # after a game update: has anything moved?
  ```

  Measured on one game (Schedule I, as an example): 136 assemblies, 59 MB, about 20 seconds. Without them the host does not
  start, and it says so.
* **Two loaders cannot share the entry point.** Both replace the same import entry
  (`kernel32!GetProcAddress` in `UnityPlayer.dll`) and neither chains the previous one, so
  exactly one survives - silently. Different proxy filenames do not help; it is the same slot.
  You do not have to keep track of this: the installer detects what is present and hooks into it
  instead of fighting over the slot: a plugin under MelonLoader; next to BepInEx it takes the
  entry point and **starts BepInEx itself**, because BepInEx *is* Doorstop and standing beside it
  is the one case where a second proxy cannot work; its own loader alone when nothing else is
  there.

Use the plugin build if you already have MelonLoader. The standalone build exists for everyone
else - and to show that Lua modding this game does not require a loader at all.

## Releases

A release is **one file**: `ScriptOne-Installer.exe`, with the whole payload embedded.
Double-click it, pick the game (or press *Find games*), install.

Its exact size and checksums are in `BUILD-INFO.txt` next to it - deliberately not repeated
here, because a number written into prose has nothing holding it to the truth and drifts with
every build. (It already had: corrected on 2026-08-19, wrong again by the next day.)

The window shows what it found **before** any button does anything - Unity backend and bit width,
which mod loader is active and in which version, whether ScriptOne is already there, and above
all *what it is about to install*. There is one main button, and it is labelled after that
decision; the alternative path sits next to it, spelled out, so nobody has to guess which one is
right for their folder.

It picks the entry path by what it finds:

| Found | ScriptOne installs as | Your other mods |
|---|---|---|
| MelonLoader active | MelonLoader plugin, plus a disarmed loader of its own | keep working |
| **BepInEx** active (5 or 6, Mono or Il2Cpp) | its own host - it takes the entry point and starts BepInEx itself | keep working |
| BepInEx active, but its configuration cannot be written | BepInEx plugin (the fallback), and it says that it had to | keep working |
| BepInEx **5** on an Il2Cpp game *and* that fallback | nothing - it stops and says why (that build does not exist) | untouched |
| neither | standalone, with its own loader | - |

⚠ It decides that by reading **who owns the entry point**, not by looking for folders: a
`BepInEx\` folder without its loader does nothing, and `doorstop_config.ini` says whose preloader
actually runs.

⚠ **Next to BepInEx it does change that one file** - that is the whole point: ScriptOne runs
first and then hands over to BepInEx through the very contract Doorstop uses, so nothing of yours
is lost and ScriptOne survives you removing BepInEx later. The original `doorstop_config.ini` is
saved to `ScriptOne\disabled-loaders\` before anything is written, and `--remove` puts it back
byte for byte. No other file of another loader is ever touched.

⚠ **The price, stated plainly:** ScriptOne then sits *in front of* BepInEx. If its preloader
broke, BepInEx would not start either, and none of its plugins. The chain-start therefore runs
as the very first thing, catches everything, and writes a `ScriptOne-CHAIN-PROBLEM.txt` if it
fails - the log does not exist yet at that point.

The same executable also runs **without a window** when given arguments - that is how the package
checker drives the delivered artifact rather than a stand-in:

```
ScriptOne-Installer.exe "<game folder>" --quiet          install, deciding automatically
ScriptOne-Installer.exe "<game folder>" --status         only report what is there, change nothing
ScriptOne-Installer.exe "<game folder>" --standalone     force its own loader
ScriptOne-Installer.exe "<game folder>" --plugin         force the loader plugin
ScriptOne-Installer.exe "<game folder>" --remove         remove it completely
ScriptOne-Installer.exe "<game folder>" --force          proceed even with a foreign loader in the way
ScriptOne-Installer.exe "<game folder>" --melonloader-basedir=<folder>
```

`--melonloader-basedir` matters when a mod manager (r2modman, Thunderstore Mod Manager) is in
use: it keeps `Plugins\` and `UserLibs\` in its **profile**, not in the game folder, and without
this the files land where the loader never looks. An unknown option is refused rather than
ignored - otherwise a typo would silently install instead.

`--remove` means completely: config, logs, documentation, licenses, state, the script folder.
As if it had never been there. Anything it had set aside - your MelonLoader, for instance - is
put back **first**; if that is impossible because the loader's own folder is gone, the file stays
and the log says which file it is and how to get it back.

### What is deliberately **not** in it

* **The Il2Cpp proxy assemblies.** They are generated on your machine from *your* game files, so
  they always match your game version - shipping them would mean shipping a copy of the game's
  types. `ScriptOne\interopgenerator\` therefore starts out empty, and the setup tells you so
  rather than leaving you to discover it.
* **An update check against GitHub releases.** It needs public releases to check against. Until
  those exist the feature is absent rather than present-and-dead - a version check that silently
  never fires is worse than none. Nothing in the installer reaches GitHub at all today; the one
  address it ever contacts is `nuget.bepinex.dev`, for the Unity base libraries an Il2Cpp game
  needs, and only then.
* **`Install-Standalone.ps1` and everything under `tools\`.** Developer tools, built from the
  repository, not part of a release.

## Settings

`ScriptOne\ScriptOne-Starter.cfg` holds them, and **every** way of running ScriptOne reads it -
standalone as well as the MelonLoader and BepInEx plugins. It is created on first start and
**rewritten on every start**, so it always documents the options this build actually has; your
values and any keys it does not know survive that. Defaults are conservative:

| Key | Default | What it does |
|---|---|---|
| `console` | `auto` | Opens a separate console window with the log, like MelonLoader and BepInEx. It appears **before** the game does, so you also see the startup. `auto` means: on when ScriptOne runs on its own, off when it chain-starts BepInEx - that loader brings its own output. `true` and `false` override it and are never changed by a reinstall. The window is always titled `ScriptOne Lua Loader` - deliberately not settable, so it can be found by name. |
| `interop_log` | `warn` | How much of Il2CppInterop's own output reaches the log: `off` / `warn` / `all`. `all` is ~17 KB per clean start and most of it is normal - debugging only. |
| `check_interop` | `true` | Compares the proxy assemblies against the installed game on every start. Leave it on: a mismatch is otherwise completely silent. |
| `console_policy` | `safe` | Which of the game's console commands a script may run through `s1.console`. `safe` = 14 view/camera/movement commands, `extended` = those plus 6 with a side effect, `unrestricted` = all of them. `bind` is refused below `unrestricted` whatever else is allowed - see below. |
| `surface_policy` | `normal` | How far the surface found in your game may reach. `normal` = everything found, `readonly` = only values and argument-less methods that return something, `off` = no generated surface at all. The effective control is the file though: open `ScriptOne\surface.txt` and delete the lines you do not want. |
| `scene_objects` | `auto` | Games hand out their managers in two different ways. Most keep one instance behind a static `Instance`; some just put plain components in the scene. `auto` binds the second kind **only when the first kind found nothing** - measured, because in a game that does use singletons this adds several hundred sample and helper components, and in a game that does not it is the only source there is. `on` always, `off` never. |
| `debug` | `false` | Extra log lines for people working on ScriptOne itself. Not warnings and not errors - nothing is wrong when they appear. |

The first three keys apply wherever ScriptOne runs **its own host** - that is the standalone
installation, *and* an installation next to BepInEx, because there it also runs its own host and
merely starts BepInEx afterwards. Only in a **MelonLoader plugin** installation does that loader
bring its own console and its own log and write ScriptOne's lines into it; `console`,
`interop_log` and `check_interop` do nothing there. The rest apply everywhere.

Note the difference next to BepInEx: `console = auto` deliberately leaves the window **shut**
there, because BepInEx already shows its own. That is a decision, not an absence of one - set
`console = true` if you want ScriptOne's window anyway.

Unknown keys are kept, not discarded. An unreadable value falls back to the default **and says
so** in the log.

The log of a **standalone** installation is not configurable and always written:
`ScriptOne\ScriptOne.log` for the run you
are in, and the five previous runs in `ScriptOne\logs\` as `.log.1` (most recent) to `.log.5` -
the same split MelonLoader uses for `Latest.log` and its `Logs\` folder.

## If the game does not start

**First, the one thing that always works:** delete these two files from the game folder.

```
<game>\winhttp.dll
<game>\doorstop_config.ini
```

The game starts normally again. Nothing else has to be undone - `ScriptOne\` and `LuaScripts\`
are inert without those two, and your scripts and their saved state stay where they are.
**You never have to verify game files or reinstall.**

Then, if you want it working rather than gone, check in this order:

| Symptom | Cause | Fix |
|---|---|---|
| no `ScriptOne\ScriptOne.log` at all | the preloader never ran | see below |
| log ends right after `preloader alive` | .NET 6 runtime missing | install the .NET 6 **runtime**, then re-run the installer - it writes the path itself |
| `interop check: THE GAME HAS CHANGED` | the game updated, proxy assemblies are stale | `.\tools\Update-Interop.ps1 -Force` |
| scripts load, nothing happens in game | no frame tick | look for `frame tick alive`; if it is missing, the log says so in plain words |

**If there is no log at all**, the preloader was never reached. Two usual reasons:

* **Your antivirus quarantined `winhttp.dll`.** A proxy DLL in a game folder is exactly the shape
  heuristics react to. Check the quarantine - the file is UnityDoorstop 4.5.0, unmodified, and
  the standalone build cannot start without it.
* **Another loader is already installed.** MelonLoader and BepInEx use the same hook, and only
  one survives. Run `.\Standalone\Install-Standalone.ps1 -Mode Status`: it reports what it found
  and picks a path for you.

There is also a crash log at `%TEMP%\ScriptOne-preloader-crash.log` for the case where the
preloader starts and dies before it can open its own log.

## Write a mod

Put any `.lua` file into `LuaScripts\`. Subfolders work. Each file gets its own interpreter, so a
broken script cannot take the others down with it.

```lua
s1.on("game_ready", function()
    s1.money.change_cash_balance(500, true, true)
    s1.level.add_xp(100)
    s1.log("Cash is now " .. s1.money.cash_balance() .. ", rank " .. s1.level.rank())
end)

s1.on("player_arrested", function()
    s1.warn("Busted at " .. s1.time.current_time())
end)
```

## API

Everything lives under the global `s1`. Only numbers, strings and booleans cross the boundary -
no game object is ever handed to Lua, on either backend.

**Core**

| Call | Does |
|---|---|
| `s1.log(text)` / `s1.warn(text)` | write to the host log (MelonLoader's `Latest.log`, or `ScriptOne\ScriptOne.log` when standalone) |
| `s1.console(line)` | submit a line to the game's developer console. Returns `false` if the console policy refused it - **check the return value**, it is not an error, it just did not happen. |
| `s1.on(event, fn)` | subscribe to a game event |
| `s1.after(sec, fn)` | run `fn` once after `sec` seconds; returns a timer id |
| `s1.every(sec, fn)` | run `fn` repeatedly; returns a timer id |
| `s1.cancel(id)` | stop a timer you started |
| `s1.get(key, default)` | read a stored value |
| `s1.set(key, value)` | store a number, string or boolean |
| `s1.save()` | write this script's state to disk. **Not** the game's save manager - that is `s1.save_manager`, renamed so it cannot shadow this one |
| `s1.backend` | `"Il2Cpp"` or `"Mono"` |
| `s1.surface_size` | number of generated manager tables available |

Timers run on a **stopwatch, not on game time** - they keep correct time while the game is
paused (the pause menu sets `timeScale` to 0). Limits per script: 128 timers, minimum interval
0.05 s.

State lives in one file per script, holding flat values only - under
`UserData\ScriptOne\` under MelonLoader, and `ScriptOne\state\` both standalone and under BepInEx. Numbers are written and read culture-invariantly, so a file written on a German machine
reads back correctly on an English one.

**Generated surface** - the exact count is in the header of [`docs/API.md`](API.md), e.g.

```
s1.time.current_time()     s1.time.elapsed_days()     s1.time.is_night()
s1.money.cash_balance()    s1.money.change_cash_balance(amount, visualize, sound)
s1.level.add_xp(n)         s1.level.rank()            s1.level.tier()
s1.player_movement...      s1.inventory...            s1.quest...
```

The full list is in [`docs/API.md`](API.md); editor stubs for autocomplete are in
`surface/s1.lua`.

**In the game folder, ScriptOne writes the same thing for the game you actually have.** On every
start it walks the API it just installed and writes `ScriptOne\documentation\`:

```
ScriptOne-API.md    every table and member, with a count
s1.lua              the same as editor stubs
README.txt          where everything lives, and a first script to copy
```

Because it is written from the *installed* surface rather than by hand, it cannot fall behind -
and in a game nobody has scripted before, it is the answer to "what can I even reach from here".

**Events**

`game_ready`, `player_spawned`, `player_arrested`, `player_freed`, `player_tased`,
`player_tased_end`, `player_struck_by_lightning`.

Handlers receive `(text, number)` - both flat, both optional.

## Two things that will bite you

**Console commands only run for the lobby host.** As a multiplayer guest the game swallows them
silently: no error, no return value. Read the value back instead of trusting the call.

**`s1.console` is filtered by default.** The game's console can set money, rank, quest state,
time and health, and it can save - so ScriptOne ships an allow-list rather than a block-list:
after a game update, new commands are forbidden by default instead of allowed by default. Change
it with `console_policy` in `ScriptOne-Starter.cfg`.

The one command that never passes below `unrestricted` is **`bind`**. It hands an arbitrary
command to the game to run on a key press - the game polls bound keys itself, entirely outside
ScriptOne - so allowing it would defeat the whole list. That is why the shipped
`starterkit.lua`, which is built around key bindings, says in its header that it needs
`console_policy = unrestricted`.

Rejections are logged with the reason. On every load ScriptOne also holds its list against the
game's real command table and reports names that no longer exist, so a renamed command shows up
as a message instead of as "it stopped working".

**Numbers with decimals are risky in console commands.** The game parses them with the runtime's
culture. Use whole numbers where you can - the generated bindings (`s1.money.change_cash_balance`)
have no such problem, they pass real floats.

## Sandbox

Scripts run in MoonSharp's hard sandbox plus metatables and error handling: `pairs`, `ipairs`,
`pcall`, `xpcall`, `setmetatable`, the `raw*` functions, `string`, `table`, `math`, `bit32` are
available. `io`, `os`, `load`, `loadfile`, `dofile`, `require`, `coroutine` and `json` are not -
no script can touch your file system.

Each call into a script has a budget of 2,000,000 steps. A script that loops forever is stopped
and disabled instead of freezing the game - it is aborted in milliseconds, not a hang.

This is **not** a security sandbox for hostile files. Only run scripts you trust.

## Build

Two files the build needs are **not** in the repository, and a clone will not build without
them:

* `Local.props` - the machine-local paths. Copy `Local.props.example` and fill it in. It is
  gitignored on purpose: it used to hold an absolute path with a user name in it.
* `Directory.Build.props` - expected **one level above** this repository, and it generates the
  `[MelonInfo]` attributes. It is not part of the clone. Without it a build still goes **green**
  and produces a DLL that MelonLoader will not load, which is the unpleasant part: nothing tells
  you. Until that is resolved, building the MelonLoader plugin needs that file in place; the
  standalone host and the installer build without it.

```
dotnet build -c Il2Cpp
dotnet build -c Mono
```

The Mono build needs no Mono installation; it compiles against the decompiled assemblies, whose
path comes from `Local.props`. Leave it empty and the build fails loudly with MSB3245 + CS0246
rather than silently against a path that does not exist here.

Regenerate the surface after a game update - the generator reads the game assembly and
cross-checks every candidate against the Il2Cpp set:

```
.\tools\Gen-Surface.ps1
.\tools\Gen-Bindings.ps1 -All
```

`-All` is load-bearing: without it the generator binds nothing at all - it now refuses and writes
nothing, rather than emptying the three generated files first. `-MinSurface` stays at its **default
`1`**; that is the value the pinned surface came about with. This line used to say `-MinSurface 4`,
which is what the surface was generated with until 2026-08-19 - it keeps only 56 of the 132
candidate tables and silently drops the other 76, `AudioManager` and `ArrestScreen` among them. After a game update run `.\tools\Update-Interop.ps1 -Force` before either of them.

Run the game-free harness (real host, real `.lua` files, faked game):

```
cd tests
dotnet run -c Release
```

## Licenses

The plugin build ships nothing foreign except MoonSharp (BSD-style) - embedded in the DLL and,
because the Mono runtime does not reliably find it there, additionally installed as a file:
`UserLibs\MoonSharp.Interpreter.dll` under MelonLoader, next to the plugin under BepInEx.

The **standalone** build ships 18 third-party files: 17 DLLs in `ScriptOne\core-runtime\<tfm>\` -
the 16 from the package plus `MoonSharp.Interpreter.dll`, which the installer puts next to the
host - plus `winhttp.dll` in the game root. Two of them are copyleft - UnityDoorstop (LGPL-2.1) and
Il2CppInterop (LGPL-3.0) - and both are redistributed **unmodified** as separate, replaceable
files, which is what the LGPL allows. ScriptOne itself is not affected by that.

The texts ship with the install, in `ScriptOne\licenses\`. In this repository they sit in three
places: seven in `Standalone/licenses/`, MoonSharp's in `ThirdParty/MoonSharp.LICENSE.txt` and our
own as the root `LICENSE` - the package script collects all three. The reasoning is in
`Standalone/THIRDPARTY-NOTICE.md`.

**ScriptOne itself is MIT** - see [`LICENSE`](LICENSE). That is compatible with everything it
ships: the two LGPL components are separate, unmodified, replaceable files that ScriptOne links
against dynamically, which is exactly the case the LGPL permits without reaching into your own
code.

If you fork this: never merge those DLLs into your own assembly, never obfuscate them, never
rename them. MoonSharp is embedded - that is allowed **because** it is BSD-style, and only then.

## Credits

Lua interpreter: [MoonSharp](https://www.moonsharp.org/) 2.0.0 by Marco Mastropaolo, embedded.
BSD-style license in `ThirdParty/MoonSharp.LICENSE.txt` and inside the DLL.

The **standalone** build additionally ships third-party binaries that are *not* ours:

| Component | Author | License |
|---|---|---|
| [UnityDoorstop](https://github.com/NeighTools/UnityDoorstop) 4.5.0 (`winhttp.dll`) | NeighTools | **LGPL-2.1** |
| [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) 1.5.1 | BepInEx | **LGPL-3.0** |
| [HarmonyX](https://github.com/BepInEx/HarmonyX) 2.10.0 (`0Harmony.dll`) | BepInEx | MIT |
| [Mono.Cecil](https://github.com/jbevain/cecil) | Jb Evain | MIT |
| [Iced](https://github.com/icedland/iced) (also as `MonoMod.Iced.dll`) | iced project | MIT |
| [MonoMod](https://github.com/MonoMod/MonoMod) 25.3.6 | MonoMod | MIT |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) 6.0.3 | Microsoft | MIT |

Doorstop is redistributed **unmodified**; it starts the runtime and calls a method by a fixed
name, which does not make ScriptOne a derived work. Details and obligations:
`Standalone/doorstop/THIRDPARTY-NOTICE.md`. Its license text ships with the install, together
with the eight others and `THIRDPARTY-NOTICE.md`, in `ScriptOne\licenses\`.
