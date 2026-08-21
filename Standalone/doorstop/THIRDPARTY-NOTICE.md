# Third-party material in this folder: UnityDoorstop

The two `winhttp.dll` files in `x64/` and `x86/` are **not** ours.

⚠ Only `x64/` is shipped. The `x86/` set sits here in case of a 32-bit game and
is **unverified** - the installer does not reach for it.

| | |
|---|---|
| Project | **UnityDoorstop** - https://github.com/NeighTools/UnityDoorstop |
| Author | NeighTools |
| Version | **4.5.0** (see `.doorstop_version`) |
| License | **LGPL-2.1** (GNU Lesser General Public License, version 2.1) |
| Modified? | **No.** Unmodified binaries from the official release. |

## Why this is written down here

MoonSharp under `ThirdParty/` is BSD-style licensed and therefore unproblematic. **Doorstop
is not** - LGPL is copyleft. Whoever overlooks that will eventually ship a version that is
missing the license text.

## What follows from that - and what does not

* **ScriptOne itself is NOT infected by it.** The preloader is not derived from Doorstop
  and is not linked against it either. Doorstop loads CoreCLR and calls a method
  with a prescribed name (`Doorstop.Entrypoint.Start()`) - that is a call across an
  interface, not a derivation. Our source code is under **MIT** (`LICENSE` in the root directory).
* **The binary may be redistributed**, because it stays unmodified and sits alongside as a
  separate, exchangeable file: a user can replace it with their own Doorstop version at
  any time. That is exactly what the LGPL demands.
* **The license text has to come along.** A shipping package without it is not compliant. It
  lives at `../licenses/UnityDoorstop.LICENSE.txt` and is copied to `ScriptOne\licenses\` when
  installing; the installer's final check aborts if it is missing there.
  ⚠ *Until 2026-08-19 this line pointed at an entry on an internal task list instead. The text
  had long been lying in the repository - only nobody copied it along, and there was no check
  against that. A reference to a task list is not an assurance; the check is.*
* **Never obfuscate, never rebuild, never rename.** As soon as the file were modified,
  additional obligations would apply (source code of the change). There is no reason for it - the
  file name `winhttp.dll` is the mechanism and has to stay exactly that way anyway.

## If Doorstop is to be replaced

There are permissively licensed alternatives for the same purpose (hijacking a proxy DLL name
and starting CoreCLR). They are **not** examined here. As long as Doorstop is used, this
notice applies.
