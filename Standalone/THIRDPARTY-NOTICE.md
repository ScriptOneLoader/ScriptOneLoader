# Third-party components in the standalone package

This list is **measured, not remembered**: the payload per branch from a real
`dotnet publish`, the versions from the version resource of the shipped file, the
license information from the `.nuspec` or from the license file in the NuGet cache (2026-08-19).

## How much third-party code each branch really ships

| Branch | Payload in `ScriptOne\core-runtime\<tfm>\` | third-party | plus in the game root |
|---|---|---|---|
| **Il2Cpp** (`net6`) | 17 DLLs, 5,0 MB | **16** | `winhttp.dll` |
| **Mono** (`net472`) | 1 DLL, 516 KB | **0** | `winhttp.dll` |

⚠ This is not a detail: **on Mono we ship nothing third-party apart from Doorstop.** The whole
interop substructure exists only to establish a managed view of a *non*-managed runtime - under
Mono it already exists. Anyone who knows only the Il2Cpp branch mistakes the list below
for the situation of both branches.

## What the Il2Cpp branch ships

| Component | Rights holder | License | Shipped as |
|---|---|---|---|
| **UnityDoorstop 4.5.0** | NeighTools | **LGPL-2.1** | `winhttp.dll` in the game root |
| **Il2CppInterop.Runtime / .Common / .HarmonySupport 1.5.1** | BepInEx, knah et al. | **LGPL-3.0-only** | 3 DLLs |
| MonoMod.RuntimeDetour 25.3.6, .Utils 25.0.14, .Core 1.3.6, .Backports 1.1.2, .ILHelpers | 0x0ade, DaNike | MIT | 5 DLLs |
| Iced 1.17.0 **and** Iced 1.21.0 | iced project | MIT | `Iced.dll` and `MonoMod.Iced.dll` |
| Mono.Cecil 0.11.6 (+ .Mdb, .Pdb, .Rocks) | Jb Evain, Novell | MIT | 4 DLLs |
| **HarmonyX 2.10.0** | BepInEx | MIT | `0Harmony.dll` |
| Microsoft.Extensions.Logging.Abstractions 6.0 | Microsoft | MIT | 1 DLL |
| **MoonSharp 2.0.0** | Marco Mastropaolo | BSD-style | **embedded** in `ScriptOne.Preloader.dll` |

⚠ Two attributions that are obvious and wrong - both measured from the file name, not from the
package name:

* **`MonoMod.Iced.dll` does not belong to MonoMod.** It is Iced code repackaged by MonoMod, version
  resource `1.21.0.0`, company "iced project and contributors". That the license text fits anyway is
  a coincidence: the `LICENSE.txt` in the package `MonoMod.Core` **is** Iced's license, not MonoMod's.
* **`0Harmony.dll` is HarmonyX (BepInEx), not Lib.Harmony (Andreas Pardeike).** Until
  2026-08-19 the text of Lib.Harmony 2.4.2 was enclosed here - right license type, **wrong
  rights holder**, and therefore the MIT obligation for the file actually shipped was not met.
  It was noticed only because license type **and** rights holder were asserted per file; "it is
  MIT" alone would have let the error through.

---

## What follows from this

### The MIT components are unproblematic
Redistribution is permitted, the obligation is the copyright notice together with the license text.
They sit beside it as separate, interchangeable files - none of it is mixed into our DLL.

### The two copyleft components are the actual point
**UnityDoorstop (LGPL-2.1)** and **Il2CppInterop (LGPL-3.0-only)**. Both are passed on **unchanged**,
both are present as **separate, replaceable** files. That is exactly the case that
the LGPL permits for a "work that uses the library".

**ScriptOne itself is not infected by it** - but for two *different* reasons, and one should
not confuse them:

* **Doorstop** merely calls a method with a prescribed name
  (`Doorstop.Entrypoint.Start()`). We do not link against it at all; that is not a derivation.
* **Il2CppInterop** we do reference. That is dynamic linking - expressly permitted by the
  LGPL, **as long as the user can replace the library**. He can: it is a separate DLL next
  to ours.

ScriptOne's own source code is under **MIT** (`LICENSE` in the root directory). That is compatible
with both, and that is precisely why it was freely choosable.

### Three things that therefore must NOT be done

1. **Do not mix any of these DLLs into `ScriptOne.Preloader.dll`** (ILMerge, ILRepack,
   `EmbeddedResource` + resolver). That would be static linking and would void the exception.
   MoonSharp is embedded - that is permissible because it is BSD-style, and **only for that reason**.
2. **Do not obfuscate any of these DLLs.** Obfuscar may run exclusively on our own assembly.
3. **Do not rename or rebuild any of them.** Passing them on unchanged is the basis of the whole
   construction.

---

## Where the texts live

In the repo under `Standalone/licenses/` (seven files), plus `ThirdParty/MoonSharp.LICENSE.txt`.
**On the user's machine they end up in `ScriptOne\licenses\`** - the installer copies them along,
and a missing text is a **blocking** finding of its final check.

⚠ *Until 2026-08-19 this said the texts were still missing. They were no longer missing - but the
installer never copied them along (`grep -ci licen` over the script: 0 hits). So the package on the
user's machine contained not a single license text, while this very document asserted they
were enclosed. Both are fixed; the final check is the safeguard against it coming back.*

The plugin branch is unaffected by this: apart from the embedded MoonSharp version it ships nothing
third-party (measured - its only third-party references are `MelonLoader` and
`MoonSharp.Interpreter`, and we do not ship MelonLoader).

*Origin of the information: version resource of the shipped files; `.nuspec` of the respective
packages in the NuGet cache; Doorstop from `github.com/NeighTools/UnityDoorstop/blob/master/LICENSE`.
Payload per branch from `dotnet publish` into an empty folder. All measured on 2026-08-19.*
