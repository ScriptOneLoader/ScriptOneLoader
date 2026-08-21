# Checksums of the third-party binaries shipped here

Four files in this repository are not ours and are passed on **unchanged**.
`THIRDPARTY-NOTICE.md` promises exactly that - until 2026-08-19 nobody could check it.
This file fixes the values, and `tools\Check-Pruefsummen.ps1` verifies them.

## The values

Measured on **2026-08-19** in the working tree, `Get-FileHash -Algorithm SHA256`.

| Datei | Bytes | SHA-256 |
|---|---:|---|
| `Standalone\doorstop\x64\winhttp.dll` | 26.112 | `8c6cdbc38836dee87e3368f5de1994d7c0ccebf29e4ce7aba3c0981f9375412c` |
| `Standalone\doorstop\x86\winhttp.dll` | 22.016 | `cc643b54484f694a8e0e6641cac79d74141009afc9e24d826f6fd7fd48fd182a` |
| `ThirdParty\net40\MoonSharp.Interpreter.dll` | 366.592 | `1db76110f21698639f55d28e21bddb536c0c497ceb741dee49fedcca9bcd1588` |
| `ThirdParty\netstandard1.6\MoonSharp.Interpreter.dll` | 357.376 | `d1caf8c1a1cd7669b749d4cacdda88e949d5138b86e8ac3f73943abebd6e7e18` |

> The size column keeps its dots as thousand separators, and the first column header keeps its
> original word. `Check-Pruefsummen.ps1` parses these rows; changing their shape makes the parser
> read zero entries, and a run with nothing to check would report success.

## Provenance

### UnityDoorstop 4.5.0 - both `winhttp.dll`

| | |
|---|---|
| Project | https://github.com/NeighTools/UnityDoorstop |
| Rights holder | NeighTools |
| License | LGPL-2.1 (text: `licenses\UnityDoorstop.LICENSE.txt`) |
| Version | **4.5.0** |
| How the version is established | `.doorstop_version` in the same folder (`4.5.0`, both architectures) **and** the version resource of the DLL (`FileVersion 4.5.0.0`, Company `Doorstop`, Product `NeighTools`) - two independent sources, both measured |
| In this repository since | 2026-08-18, commit `f5a9bfb` |

⚠ Only `x64\` is shipped. The `x86\` set is there for a 32-bit game and is **unverified** - the
installer does not reach for it. Its checksum is listed anyway, because otherwise an unnoticed
change to it could later pass as "it was always like that".

### MoonSharp 2.0.0 - both `MoonSharp.Interpreter.dll`

| | |
|---|---|
| Project | https://www.moonsharp.org/ |
| Rights holder | Marco Mastropaolo |
| License | BSD-style (text: `..\ThirdParty\MoonSharp.LICENSE.txt`) |
| Version | **2.0.0** (version resource `2.0.0.0`) |
| Source | NuGet package `https://www.nuget.org/api/v2/package/MoonSharp/2.0.0`, taken from `lib\net40\` and `lib\netstandard1.6\` |
| In this repository since | 2026-08-18, commit `66f4297` |

The two builds are **different files of different sizes**, and that is not an oversight: `net40`
goes into the Mono branch, `netstandard1.6` into the Il2Cpp branch (`ScriptOne.csproj`, property
`MoonSharpDll`). Swapping one for the other produces no build error - it produces a
`FileLoadException` at run time, on the user's machine.

## What this file promises, and what it does not

**Promised and checkable:** the four files in the working tree are byte for byte the ones that
entered the repository on 2026-08-18. Each has **exactly one** commit in its history
(`git log -- <file>`) and has therefore never been changed. From now on any change to them shows
up, because `Check-Pruefsummen.ps1` reports it - including an accidental one, such as a line
ending conversion. `*.dll binary` in `.gitattributes` guards against that as well; the checksum is
the second, independent lock.

**HINWEIS[UNGEPRUEFT] - not promised:** that these bytes match the **official upstream release**.
That would require downloading the Doorstop 4.5.0 release from GitHub and the MoonSharp package
from NuGet and comparing; that has **not been done**. The statement "unmodified binaries from the
official release" in `THIRDPARTY-NOTICE.md` and `doorstop\THIRDPARTY-NOTICE.md` still rests solely
on the fact that whoever put the files in took them from there.

That is a real difference and not a formality: this file makes **drift** visible, not
**provenance**. Anyone who wants the provenance proof downloads both sources once, compares them
against the values above, and records the result and the retrieval date here. Only then may the
promise in the notices count as verified.

## Verifying

```powershell
.\tools\Check-Pruefsummen.ps1
.\tools\Check-Pruefsummen.ps1 -Selbsttest   # positive control: does it react at all?
```

Exit code 0 means all four files are present, with the size and SHA-256 listed above. Exit code 1
means at least one differs, is missing, or is listed in this table but not in the working tree.

⚠ The checker reads the expected values **from this file**, not from a copy of its own. There is
one truth; anyone deliberately replacing a file changes the table above and nothing else.
