<#
    Install-Standalone.ps1 - installiert ScriptOne und WAEHLT dabei den Einstiegspfad.

    ============================================================================
    WARUM DIESES SKRIPT UEBERHAUPT WAEHLT

    ScriptOne hat zwei Einstiegspfade fuer denselben Wirt:
        PLUGIN      eine DLL in Schedule I\Plugins\, gestartet von MelonLoader
        STANDALONE  UnityDoorstop (winhttp.dll) startet CoreCLR und ruft uns direkt

    Die beiden sind NICHT nebeneinander betreibbar, und das ist keine Konfigurations-
    frage, sondern der Mechanismus: BEIDE ersetzen denselben Importeintrag
    kernel32!GetProcAddress in der Importtabelle von UnityPlayer.dll - MelonLoader per
    plthook, Doorstop per eigenem iat_hook. KEINER von beiden verkettet den Vorgaenger.
    Verschiedene Proxy-DATEINAMEN (version.dll gegen winhttp.dll) helfen nicht: es geht
    um denselben Slot. Nebeneinander ueberlebt genau einer, lautlos.

    Frueher stand im Quelltext, die beiden kaemen sich "nicht in die Quere". Das war
    falsch und ist am 2026-08-18 korrigiert worden.

    WAS HIER NICHT GEMACHT WIRD, UND WARUM
    MelonLoader hat mit Dependencies\CompatibilityLayers\ einen Weg, sich frueher
    einzuklinken als jedes Plugin. Fuer uns ist er der falsche:
      - die DLL muesste MelonLoader.dll referenzieren - das ist "kein Mod", nicht
        "ohne MelonLoader"
      - es gibt je Name genau EINEN Platz; ein zweites Werkzeug verdraengt das erste
      - der Ordner gehoert MelonLoader und wird von dessen Updates ueberschrieben
      - ein Fehler dort reisst den GANZEN Loader mit (kein try/catch um GetTypes)
    Ein kaputtes Plugin schadet nur sich selbst. Das ist der bessere Handel.
    ============================================================================

    AUFRUF
        .\Install-Standalone.ps1                      # Auto: erkennen und waehlen
        .\Install-Standalone.ps1 -Mode Status
        .\Install-Standalone.ps1 -Mode Standalone    # erzwingen (fragt bei Konflikt)
        .\Install-Standalone.ps1 -Mode Plugin
        .\Install-Standalone.ps1 -Mode Entfernen
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto','Standalone','Plugin','Entfernen','MelonLoader','Status')]
    # ⚠ ENGLISCH, und der alte deutsche Name bleibt als Alias: dieser Parameter wird
    #   GETIPPT und steht in der veroeffentlichten Doku, ist also nutzersichtbar.
    [Alias('Modus')]
    [string] $Mode = 'Auto',
    [string] $Spiel = 'C:\Steam\steamapps\common\Schedule I',
    [switch] $Neuordnen,
    # Nur mit dieser Zusage wird ein erkannter Konflikt uebergangen.
    [Alias('Trotzdem')]
    [switch] $Force
)
$ErrorActionPreference = 'Stop'
trap { Write-Host "  ABORT: $($_.Exception.Message)" -ForegroundColor Red; exit 1 }

# Zwei Baue aus EINEM Quellstand. Welcher installiert wird, entscheidet das erkannte
# Backend - fuer den Nutzer bleibt es ein Paket ohne Auswahl (Punkt 3).
$stageRoot = Join-Path $PSScriptRoot 'stage\ScriptOne\core-runtime'
$doorstop = Join-Path $PSScriptRoot 'doorstop\x64'
$modRoot  = Split-Path $PSScriptRoot -Parent

$wurzel   = Join-Path $Spiel 'ScriptOne'
# Die Ordnernamen sagen, was drin liegt. Wer in einem fremden Spielordner steht, soll
# nicht raten muessen - 'core' und 'interop' sagten nichts, 'backup' sogar etwas Falsches
# (dort liegen abgestellte LADER, keine Sicherung von Nutzerdaten).
$core     = Join-Path $wurzel 'core-runtime'      # der Wirt und seine Abhaengigkeiten
$interop  = Join-Path $wurzel 'interopgenerator'  # was der eigene Erzeuger hervorgebracht hat
$logs     = Join-Path $wurzel 'logs'              # die fuenf vorherigen Laeufe
$log      = Join-Path $wurzel 'ScriptOne.log'     # der laufende - oben, wie MelonLoaders Latest.log
$state    = Join-Path $wurzel 'state'
$doku     = Join-Path $wurzel 'documentation'     # erzeugt der WIRT, nicht dieses Skript
$lizenzen = Join-Path $wurzel 'licenses'          # Pflicht: Lizenztexte der Fremd-DLLs
$backup   = Join-Path $wurzel 'disabled-loaders'  # beiseitegelegte Lader
$cfg      = Join-Path $wurzel 'ScriptOne-Starter.cfg'

$mlAn     = Join-Path $Spiel  'version.dll'
$mlAus    = Join-Path $backup 'version.dll.melonloader-off'
$mlAusAlt = Join-Path $Spiel  'version.dll.melonloader-off'
$doorDll  = Join-Path $Spiel  'winhttp.dll'
$doorCfg  = Join-Path $Spiel  'doorstop_config.ini'

function Ordner($p) { if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null } }

# ============================================================================
# ERKENNUNG - erst messen, dann entscheiden.
# ============================================================================
function Erkenne {
    $u = [ordered]@{}

    # --- Backend. Il2Cpp hat GameAssembly.dll, Mono einen Managed-Ordner. Beides zu
    #     pruefen statt eines von beidem anzunehmen: ein Steam-Zweigwechsel taeuscht sonst.
    $u.Il2Cpp = Test-Path (Join-Path $Spiel 'GameAssembly.dll')
    $managed  = @(Get-ChildItem $Spiel -Directory -Filter '*_Data' -ErrorAction SilentlyContinue |
                  ForEach-Object { Join-Path $_.FullName 'Managed\Assembly-CSharp.dll' } |
                  Where-Object { Test-Path $_ })
    $u.Mono   = $managed.Count -gt 0
    $u.Backend = if ($u.Il2Cpp -and -not $u.Mono) { 'Il2Cpp' }
                 elseif ($u.Mono -and -not $u.Il2Cpp) { 'Mono' }
                 elseif ($u.Il2Cpp -and $u.Mono) { 'beides?' }
                 else { 'unbekannt' }

    # --- MelonLoader. version.dll ALLEIN genuegt nicht: ohne seinen Ordner ist es eine
    #     Ruine, die den Start kapert und dann nichts findet. Genau der Zustand entsteht,
    #     wenn jemand nur die Datei zurueckbenennt.
    $mlOrdner = Test-Path (Join-Path $Spiel 'MelonLoader')
    if ((Test-Path $mlAn) -and $mlOrdner)      { $u.Melon = 'aktiv' }
    elseif (Test-Path $mlAn)                   { $u.Melon = 'KAPUTT (version.dll ohne MelonLoader-Ordner)' }
    elseif ((Test-Path $mlAus) -or (Test-Path $mlAusAlt)) { $u.Melon = if ($mlOrdner) { 'abgeschaltet' } else { 'abgeschaltet, Ordner fehlt' } }
    else                                       { $u.Melon = 'nicht installiert' }
    $u.MelonOrdner = $mlOrdner

    # --- BepInEx benutzt DIESELBE winhttp.dll. Ein fremdes Doorstop darf nicht
    #     ueberschrieben werden - das killt eine fremde Installation still.
    $u.BepInEx = Test-Path (Join-Path $Spiel 'BepInEx')
    $u.DoorstopFremd = $false
    if ((Test-Path $doorDll) -and $u.BepInEx -and -not (Test-Path $core)) { $u.DoorstopFremd = $true }

    # Der Preloader liegt seit Punkt 3 in core\<tfm>\ - die flache Altfassung bleibt erkannt.
    $u.Standalone = (Test-Path $doorDll) -and @(Get-ChildItem $core -Recurse -Filter 'ScriptOne.Preloader.dll' -ErrorAction SilentlyContinue).Count -gt 0
    $u.PluginDll  = Test-Path (Join-Path $Spiel "Plugins\ScriptOne.$(if ($u.Backend -eq 'Mono') {'MONO'} else {'IL2CPP'}).dll")
    return $u
}

function Zeige($u) {
    Write-Host ""
    Write-Host "  Game       : $Spiel"
    Write-Host "  Backend    : $($u.Backend)"
    Write-Host "  MelonLoader: $($u.Melon)"
    if ($u.BepInEx) { Write-Host "  BepInEx    : installed" -ForegroundColor Yellow }
    Write-Host "  ScriptOne  : standalone $(if ($u.Standalone) {'yes'} else {'no'}) | plugin $(if ($u.PluginDll) {'yes'} else {'no'})"
    Write-Host ""
    # ⚠ -Recurse ist hier kein Detail: core-runtime\ traegt seinen Inhalt eine Ebene
    # tiefer (net6\ bzw. net472\) und meldete ohne das dauerhaft "0 Datei(en)" -
    # eine vollstaendige Installation sah damit kaputt aus.
    foreach ($e in @(
        @{ N='core-runtime    '; P=$core   },
        @{ N='interopgenerator'; P=$interop },
        @{ N='documentation   '; P=$doku   },
        @{ N='logs            '; P=$logs   },
        @{ N='state           '; P=$state  },
        @{ N='licenses        '; P=$lizenzen },
        @{ N='disabled-loaders'; P=$backup })) {
        if (Test-Path $e.P) {
            Write-Host ("    ScriptOne\{0} {1,5} file(s)" -f $e.N, @(Get-ChildItem $e.P -File -Recurse -ErrorAction SilentlyContinue).Count)
        } else { Write-Host ("    ScriptOne\{0}     -  missing" -f $e.N) }
    }
    $lua = Join-Path $Spiel 'LuaScripts'
    if (Test-Path $lua) { Write-Host ("    LuaScripts     {0,5} script(s)" -f @(Get-ChildItem $lua -Filter *.lua -File).Count) }

    # ⚠ IST DAS INSTALLIERTE AUCH DAS GEBAUTE? Belegter Fall: nach einem Versions-Fix trug
    # stage\ 0.2.0.0 und die Installation weiter 0.0.0.0 - ein Spiellauf belegt dann etwas
    # anderes, als man gerade gebaut hat, und man sucht den Fehler im Code statt im Kopieren.
    $inst = @(Get-ChildItem $core -Recurse -Filter 'ScriptOne.Preloader.dll' -ErrorAction SilentlyContinue)
    if ($inst.Count -gt 0) {
        $ip = $inst[0].FullName
        $iv = [Diagnostics.FileVersionInfo]::GetVersionInfo($ip).FileVersion
        $ih = (Get-FileHash $ip -Algorithm MD5).Hash
        # !! DEN GLEICHEN ZWEIG vergleichen. -Recurse liefert net472 VOR net6 (alphabetisch);
        # wer einfach das erste Ergebnis nimmt, vergleicht den installierten Il2Cpp-Bau gegen
        # den gestageten Mono-Bau und meldet IMMER eine Abweichung. Der Zweig steht im
        # Ordnernamen der Installation - von dort nehmen, nicht raten.
        $instTfm = Split-Path (Split-Path $ip -Parent) -Leaf
        $sp = @(Get-ChildItem (Join-Path $stageRoot $instTfm) -Filter 'ScriptOne.Preloader.dll' -ErrorAction SilentlyContinue)
        if ($sp.Count -eq 0) {
            Write-Host ("`n  installed: {0} (not built - no comparison possible)" -f $iv)
        } else {
            $sh = (Get-FileHash $sp[0].FullName -Algorithm MD5).Hash
            $sv = [Diagnostics.FileVersionInfo]::GetVersionInfo($sp[0].FullName).FileVersion
            if ($ih -eq $sh) {
                Write-Host ("`n  installed = built  ({0})" -f $iv) -ForegroundColor Green
            } else {
                Write-Host ("`n  !! INSTALLED DIFFERS FROM BUILD" ) -ForegroundColor Red
                Write-Host ("    installed {0}  {1}" -f $iv, $ih.Substring(0,12)) -ForegroundColor Red
                Write-Host ("    built     {0}  {1}" -f $sv, $sh.Substring(0,12)) -ForegroundColor Red
                Write-Host  "    -> .\Install-Standalone.ps1  (otherwise the next run checks the old state)" -ForegroundColor Red
            }
        }
    }
    $log = Join-Path $logs 'ScriptOne.log'
    if (Test-Path $log) { Write-Host "`n  Last run: $((Get-Item $log).LastWriteTime)" }
}

function Finde-CoreClr {
    $basis = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
    if (-not (Test-Path $basis)) { return $null }
    $k = @(Get-ChildItem $basis -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '6.*' } |
           Sort-Object { try { [version]$_.Name } catch { [version]'0.0.0' } } -Descending)
    if ($k.Count -eq 0) { return $null }
    return $k[0].FullName
}

function Schreibe-Config($ziel, $clrOrdner, $tfm) {
    $t = @"
# Erzeugt von Install-Standalone.ps1 - nicht von Hand pflegen.
# target_assembly zeigt auf den Zweig, der zum Backend dieses Spiels passt:
#   net6   = Il2Cpp (Doorstop startet CoreCLR)
#   net472 = Mono   (Doorstop laeuft in der Mono-Domaene, KEIN CoreCLR)
[General]
enabled=true
target_assembly=ScriptOne\core-runtime\$tfm\ScriptOne.Preloader.dll
redirect_output_log=false
boot_config_override=
ignore_disable_switch=false

[UnityMono]
dll_search_path_override=
debug_enabled=false
debug_address=127.0.0.1:10000
debug_suspend=false

[Il2Cpp]
coreclr_path=$clrOrdner\coreclr.dll
corlib_dir=$clrOrdner
"@
    [IO.File]::WriteAllText($ziel, $t, (New-Object System.Text.UTF8Encoding($false)))
}

function Neuordnen-Alt {
    if (-not (Test-Path $wurzel)) { return }

    # ⚠ ZUERST die Ordner UMBENENNEN, dann einsortieren. Andersherum legt 'Ordner $core'
    # den neuen Ordner leer an, der Move findet ihn vor und bricht ab - und die alte
    # Installation liegt danach in zwei Haelften.
    foreach ($u in @(
        @{ Alt = 'core';             Neu = $core    },
        @{ Alt = 'interop';          Neu = $interop },
        @{ Alt = 'Il2CppAssemblies'; Neu = $interop },
        @{ Alt = 'backup';           Neu = $backup  })) {
        $q = Join-Path $wurzel $u.Alt
        if ((Test-Path $q) -and -not (Test-Path $u.Neu)) {
            Move-Item $q $u.Neu -Force
            Write-Host ("  renamed: {0}\ -> {1}\" -f $u.Alt, (Split-Path $u.Neu -Leaf)) -ForegroundColor Gray
        }
    }

    Ordner $core; Ordner $logs; Ordner $backup

    # Die Konfiguration traegt jetzt 'Starter' im Namen. Umbenennen statt neu anlegen -
    # sonst verliert der Nutzer seine Einstellungen an eine Datei, die er nie sah.
    $cfgAlt = Join-Path $wurzel 'ScriptOne.cfg'
    if ((Test-Path $cfgAlt) -and -not (Test-Path $cfg)) { Move-Item $cfgAlt $cfg -Force }

    # Das Protokoll des laufenden Starts gehoert nach oben, die aelteren in logs\.
    $logAlt = Join-Path $logs 'ScriptOne.log'
    if ((Test-Path $logAlt) -and -not (Test-Path $log)) { Move-Item $logAlt $log -Force }

    foreach ($f in @(Get-ChildItem $wurzel -File -ErrorAction SilentlyContinue)) {
        $ziel = $null
        if ($f.Extension -in '.dll','.json')          { $ziel = $core }
        elseif ($f.Name -eq 'ScriptOne.log')          { continue }   # gehoert hierher
        elseif ($f.Extension -eq '.log')              { $ziel = $logs }
        elseif ($f.Name -eq '.doorstop_version')      { continue }
        elseif ($f.Name -eq 'ScriptOne-Starter.cfg')  { continue }
        if ($ziel) { Move-Item $f.FullName (Join-Path $ziel $f.Name) -Force }
    }

    if (Test-Path $mlAusAlt) { Move-Item $mlAusAlt $mlAus -Force }

    # Fremdlinge, die NICHT von uns stammen, aber im Spielordner anfallen. Sie lagen frueher
    # in 'backup' und liessen den Namen luegen: output_log.txt ist UNITYS Protokoll (Doorstop
    # schreibt es bei redirect_output_log=true), die exports-Datei ein Diagnoseabzug.
    Ordner (Join-Path $wurzel 'diagnostics')
    foreach ($n in @('version.dll.exports.txt','output_log.txt')) {
        foreach ($q in @((Join-Path $Spiel $n), (Join-Path $backup $n))) {
            if (Test-Path $q) { Move-Item $q (Join-Path $wurzel "diagnostics\$n") -Force }
        }
    }

    # Toter Rest: eine abgestellte Plugin-DLL, waehrend die aktive schon in Plugins\ liegt.
    $pOff = Join-Path $backup 'ScriptOne.IL2CPP.dll.plugin-off'
    if ((Test-Path $pOff) -and (Test-Path (Join-Path $Spiel 'Plugins\ScriptOne.IL2CPP.dll'))) {
        Remove-Item $pOff -Force
        Write-Host "  removed: set-aside plugin copy (the active one lives in Plugins\)" -ForegroundColor Gray
    }
}

# ============================================================================
function Install-Standalone($u) {
    # Punkt 3: Mono ist KEIN Abbruchgrund mehr. Doorstop traegt beide Laufzeitpfade in
    # derselben winhttp.dll; wir liefern beide Baue mit und zeigen auf den passenden.
    $tfm = if ($u.Backend -eq 'Mono') { 'net472' } else { 'net6' }
    if ($u.Backend -eq 'unbekannt') {
        Write-Host "  ABORT: backend not recognised (neither GameAssembly.dll nor *_Data\Managed\)." -ForegroundColor Red
        return 1
    }
    $stage = Join-Path $stageRoot $tfm
    $coreZiel = Join-Path $core $tfm
    if ($u.DoorstopFremd -and -not $Force) {
        Write-Host "  ABORT: a FOREIGN Doorstop (BepInEx) is already installed here." -ForegroundColor Red
        Write-Host "  Overwriting would silently kill that installation. -Force overrides this."
        return 1
    }
    if ($u.Melon -eq 'aktiv' -and -not $Force) {
        Write-Host "  ABORT: MelonLoader is active." -ForegroundColor Red
        Write-Host "  Both replace the same import entry in UnityPlayer.dll and do not chain -"
        Write-Host "  side by side exactly one survives, silently. Two ways out:"
        Write-Host "     -Mode Plugin      ScriptOne runs UNDER MelonLoader (recommended)" -ForegroundColor Green
        Write-Host "     -Mode Standalone -Force   MelonLoader gets switched off in the process"
        return 1
    }

    $clr = Finde-CoreClr
    if (-not $clr -and $tfm -eq 'net6') {
        Write-Host "  ABORT: no .NET 6 runtime found (Il2Cpp needs CoreCLR)." -ForegroundColor Red
        return 1
    }
    if ($clr) { Write-Host "  .NET 6 runtime: $clr" }
    elseif ($tfm -eq 'net472') { $clr = '(unused on Mono)'; Write-Host "  Mono branch: CoreCLR is not needed" -ForegroundColor Gray }

    Ordner $wurzel; Ordner $core; Ordner $logs; Ordner $state; Ordner $backup
    # Immer, nicht nur auf Wunsch: die Umbenennung von 2026-08-19 muss auch dann
    # greifen, wenn jemand nur den Zweig wechselt. Die Funktion ist idempotent.
    Neuordnen-Alt
    if (-not (Test-Path $stage)) {
        Write-Host "  ABORT: $stage is missing - build it first:" -ForegroundColor Red
        Write-Host ('    dotnet publish Preloader -c Release -f net6.0  -o stage\ScriptOne\core-runtime\net6')
        Write-Host ('    dotnet publish Preloader -c Release -f net472 -o stage\ScriptOne\core-runtime\net472')
        return 1
    }

    # Der ANDERE Zweig muss weg: zwei Preloader nebeneinander sind zwei Wirte, sobald
    # jemand die Konfiguration von Hand umbiegt.
    foreach ($alt in @('net6','net472')) {
        $ap = Join-Path $core $alt
        if ($alt -ne $tfm -and (Test-Path $ap)) { Remove-Item $ap -Recurse -Force }
    }
    # Und die FLACHE Altfassung (vor Punkt 3) ebenso.
    foreach ($f in @(Get-ChildItem $core -File -ErrorAction SilentlyContinue)) { Remove-Item $f.FullName -Force }

    Ordner $coreZiel
    Copy-Item (Join-Path $stage '*') $coreZiel -Force
    Write-Host ("  Branch: {0} (backend {1})" -f $tfm, $u.Backend) -ForegroundColor Gray
    Copy-Item (Join-Path $doorstop 'winhttp.dll')       $Spiel  -Force
    Copy-Item (Join-Path $doorstop '.doorstop_version') $wurzel -Force

    # ⚠ RECHTLICHE PFLICHT, nicht Komfort. Der Standalone-Bau liefert 16 fremde DLLs
    # plus die native winhttp.dll aus; MIT verlangt den Copyright-Hinweis SAMT Lizenztext
    # "in all copies", LGPL ebenso. Bis 2026-08-19 kopierte dieses Skript sie NIE - das
    # Paket beim Nutzer enthielt also keinen einzigen Lizenztext, waehrend die eigene
    # THIRDPARTY-NOTICE.md zusicherte, dass sie beiliegen.
    Ordner $lizenzen
    Copy-Item (Join-Path $PSScriptRoot 'licenses\*.txt') $lizenzen -Force
    $notice = Join-Path $PSScriptRoot 'THIRDPARTY-NOTICE.md'
    if (Test-Path $notice) { Copy-Item $notice $lizenzen -Force }
    Schreibe-Config $doorCfg $clr $tfm

    if (Test-Path $mlAn) {
        Move-Item $mlAn $mlAus -Force
        Write-Host "  MelonLoader switched off (version.dll -> ScriptOne\disabled-loaders\)" -ForegroundColor Yellow
    }
    # Zwei Einstiege waeren zwei Wirte im selben Prozess.
    $pl = Join-Path $Spiel 'Plugins\ScriptOne.IL2CPP.dll'
    if (Test-Path $pl) {
        Move-Item $pl (Join-Path $backup 'ScriptOne.IL2CPP.dll.plugin-off') -Force
        Write-Host "  Plugin build set aside (otherwise two hosts)" -ForegroundColor Yellow
    }

    # Der Lizenzordner gehoert in DIESE Liste: ein vergessener Kopierschritt faellt sonst
    # wieder still durch - genau so entstand der Zustand, den er behebt.
    $fehlt = @(@((Join-Path $coreZiel 'ScriptOne.Preloader.dll'), $doorDll, $doorCfg,
                 (Join-Path $lizenzen 'UnityDoorstop.LICENSE.txt')) | Where-Object { -not (Test-Path $_) })
    if ($fehlt.Count) { Write-Host "  FAILED - missing:" -ForegroundColor Red; $fehlt | ForEach-Object { Write-Host "    $_" }; return 1 }
    # Nur auf Il2Cpp. Auf Mono gibt es NICHTS zu erzeugen - die Spiel-DLLs sind die
    # Referenz, und ein Hinweis auf ein leeres interop\ waere dort eine falsche Faehrte.
    if ($tfm -eq 'net6' -and -not (Test-Path (Join-Path $interop 'Assembly-CSharp.dll'))) {
        Write-Host "  NOTE: ScriptOne\interopgenerator\ is empty - without proxy assemblies the host will not start." -ForegroundColor Yellow
        Write-Host "        Generate them: .\tools\Update-Interop.ps1 -Force"
    }
    Write-Host "  ScriptOne STANDALONE installed." -ForegroundColor Green
    return 0
}

function Install-Plugin($u) {
    Neuordnen-Alt   # auch hier: alte Ordnernamen liegen unabhaengig vom Zweig herum
    if ($u.Melon -notin 'aktiv','abgeschaltet') {
        Write-Host "  ABORT: MelonLoader is not installed here - a plugin would have nobody to" -ForegroundColor Red
        Write-Host "  load it. Either install MelonLoader or use -Mode Standalone."
        return 1
    }
    if ($u.Melon -eq 'abgeschaltet') {
        $q = if (Test-Path $mlAus) { $mlAus } else { $mlAusAlt }
        if ($q -and (Test-Path $q)) {
            if (-not $u.MelonOrdner) {
                Write-Host "  ABORT: version.dll is present but switched off, and the MelonLoader folder is gone." -ForegroundColor Red
                Write-Host "  Renaming the file back would leave a ruin that hijacks startup and then"
                Write-Host "  finds nothing. Please reinstall MelonLoader."
                return 1
            }
            Move-Item $q $mlAn -Force
            Write-Host "  MelonLoader switched back on." -ForegroundColor Green
        }
    }
    $suffix = if ($u.Backend -eq 'Mono') { 'MONO' } else { 'IL2CPP' }
    $tfm    = if ($u.Backend -eq 'Mono') { 'net472' } else { 'net6.0' }
    $cfg    = if ($u.Backend -eq 'Mono') { 'Mono' }   else { 'Il2Cpp' }
    $quelle = Join-Path $modRoot "bin\$cfg\$tfm\ScriptOne.$suffix.dll"
    if (-not (Test-Path $quelle)) {
        Write-Host "  ABORT: $quelle is missing - build it first: dotnet build -c $cfg" -ForegroundColor Red
        return 1
    }
    Ordner (Join-Path $Spiel 'Plugins')
    Copy-Item $quelle (Join-Path $Spiel 'Plugins') -Force

    # Der Standalone-Einstieg MUSS weg, sonst zwei Wirte und ein IAT-Duell.
    foreach ($d in @($doorDll, $doorCfg)) { if (Test-Path $d) { Remove-Item $d -Force; Write-Host "  removed: $(Split-Path $d -Leaf)" -ForegroundColor Yellow } }

    Ordner (Join-Path $Spiel 'LuaScripts')
    Write-Host "  ScriptOne PLUGIN installed ($suffix, under MelonLoader)." -ForegroundColor Green
    return 0
}

function Entfernen($u) {
    foreach ($d in @($doorDll, $doorCfg)) { if (Test-Path $d) { Remove-Item $d -Force } }
    foreach ($s in @('IL2CPP','MONO')) {
        $p = Join-Path $Spiel "Plugins\ScriptOne.$s.dll"
        if (Test-Path $p) { Remove-Item $p -Force }
    }
    $q = if (Test-Path $mlAus) { $mlAus } elseif (Test-Path $mlAusAlt) { $mlAusAlt } else { $null }
    if ($q -and -not (Test-Path $mlAn)) {
        if ($u.MelonOrdner) { Move-Item $q $mlAn -Force; Write-Host "  MelonLoader back ON" -ForegroundColor Green }
        else { Write-Host "  NOTE: version.dll stays switched off - the MelonLoader folder is missing." -ForegroundColor Yellow }
    }
    Write-Host "  Entry points removed. ScriptOne\ and LuaScripts\ are left in place."
    return 0
}

# ============================================================================
$u = Erkenne

switch ($Mode) {
    'Status'      { Zeige $u; exit 0 }
    'Standalone'  { $c = Install-Standalone $u; Zeige (Erkenne); exit $c }
    'Plugin'      { $c = Install-Plugin $u;     Zeige (Erkenne); exit $c }
    'MelonLoader' { $c = Install-Plugin $u;     Zeige (Erkenne); exit $c }   # alter Name
    'Remove'      { $c = Entfernen $u;          Zeige (Erkenne); exit $c }
    'Entfernen'   { $c = Entfernen $u;          Zeige (Erkenne); exit $c }   # alter Name
    default {
        # ---- AUTO: erkennen, begruenden, waehlen. Nie beides. ----
        Write-Host "  Detected:" -ForegroundColor Cyan
        Write-Host "    Backend     $($u.Backend)"
        Write-Host "    MelonLoader $($u.Melon)"
        if ($u.BepInEx) { Write-Host "    BepInEx     installed" -ForegroundColor Yellow }
        Write-Host ""
        if ($u.Melon -eq 'aktiv') {
            Write-Host "  -> PLUGIN. MelonLoader runs here; two native loaders do not get along." -ForegroundColor Green
            $c = Install-Plugin $u
        } elseif ($u.DoorstopFremd) {
            Write-Host "  -> ABORT. A foreign Doorstop (BepInEx) is present - overwriting would be silently fatal." -ForegroundColor Red
            $c = 1
        } else {
            Write-Host "  -> STANDALONE. No foreign loader in the way." -ForegroundColor Green
            $c = Install-Standalone $u
        }
        Zeige (Erkenne)
        exit $c
    }
}
