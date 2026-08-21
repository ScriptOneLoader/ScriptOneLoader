<#
    Check-Paket.ps1 - laesst die FERTIGE Installer-exe gegen nachgebaute Spielordner laufen
    und prueft, was danach im Spielordner steht.

    WARUM ES DAS GIBT
    Check-Installation.ps1 prueft den Entwickler-Weg gegen die QUELLEN. Genau daneben lagen
    die Fehler, die der Autor am 2026-08-19 im echten Test fand - unter anderem ein Installer,
    der die Plugin-DLL "nicht im Paket" meldete, waehrend sie in seiner eigenen exe lag.
    Geprueft wurde bis dahin, was der Code TUT, nicht, was beim Nutzer HINTERBLEIBT.

    ⚠ EINGANG IST DIE AUSGELIEFERTE EXE, kein Stellvertreter. Bis zur Umstellung fuhr dieser
    Pruefer die Konsolenfassung aus der ZIP; seit die ZIP entfallen ist, gaebe es die beim
    Nutzer gar nicht mehr - er haette etwas geprueft, das niemand bekommt. Die exe laeuft mit
    Argumenten STILL (ohne Fenster), genau dafuer.

    WAS ER NICHT KANN
    Er startet kein Spiel und klickt keine Oberflaeche. Dass der Knopf im Fenster tut, was hier
    gemessen wird, sagt er NICHT - er misst dieselbe Kernlogik ueber denselben Einstieg.

    AUFRUF
        .\tools\Check-Paket.ps1 [-Exe <pfad>] [-Selbsttest]
#>
[CmdletBinding()]
param(
    [string] $Exe,
    [string] $Wurzel,
    [switch] $Selbsttest
)
$ErrorActionPreference = 'Stop'

# ⚠ Nicht im param()-Vorgabewert: mit [CmdletBinding()] ist $PSScriptRoot dort leer,
# sobald das Skript per 'powershell -File' startet.
if (-not $Wurzel) { $Wurzel = Split-Path $PSScriptRoot -Parent }

# ⚠ AUSDRUECKLICH AUF 0. Eine nicht gesetzte Variable ist $null, und '$null -ne 0' ist in
#   PowerShell WAHR - eine Sperre darauf feuerte sonst bei jedem Lauf.
$fehler = 0
# ⚠ DIE FALLZAHL WIRD GEZAEHLT, NICHT GETIPPT. Sie stand als "eight" in der Schlusszeile
#   und war beim ersten neuen Fall still falsch - dieselbe Klasse Fehler wie jede andere
#   Zahl, die an keiner Quelle haengt.
$script:faelle = 0
function Sag($t, $f = 'Gray') { Write-Host "  $t" -ForegroundColor $f }
function Fall($t) { $script:faelle++; Write-Host "  case: $t" -ForegroundColor Cyan }
function Schlecht($t) { Sag "FAIL  $t" 'Red'; $script:fehler++ }
function Gut($t)      { Sag "ok    $t" 'DarkGray' }

if (-not $Exe) {
    $kandidaten = @()
    foreach ($o in @((Join-Path $Wurzel 'Standalone\release'), [Environment]::GetFolderPath('Desktop'))) {
        if (Test-Path $o) { $kandidaten += @(Get-ChildItem $o -Filter 'ScriptOne-Installer.exe' -File -ErrorAction SilentlyContinue) }
    }
    if ($kandidaten.Count -gt 0) { $Exe = ($kandidaten | Sort-Object LastWriteTime -Descending)[0].FullName }
}

# ⚠ Kein Pruefgegenstand ist KEIN Gruen. Exitcode 2, und der Marker geht per Write-Output nach
#   stdout - Write-Host schreibt nach Stream 6, den ein Aufrufer mit 2>&1 NICHT auffaengt.
if (-not $Exe -or -not (Test-Path $Exe)) {
    Write-Output "  HINWEIS[UNGEPRUEFT] no installer exe found - build it with Standalone\Make-Package.ps1"
    exit 2
}
Sag ("installer: {0} ({1:N1} MB)" -f (Split-Path $Exe -Leaf), ((Get-Item $Exe).Length / 1MB))

# Ein Spielordner von Hand. KURZER Pfad: ueber 260 Zeichen wirft File.Copy, und dann misst
# man Windows statt des Installers (gemessen).
function Neu-Spiel([string] $art, [bool] $melon, [bool] $arch32 = $false) {
    $d = Join-Path ([IO.Path]::GetTempPath()) ("s1chk-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Path (Join-Path $d 'Fake_Data\Managed') -Force | Out-Null
    # ⚠ ECHTE PE-DATEIEN. Vorher stand in UnityPlayer.dll ein einzelnes 'x' - damit war die
    # Bitbreite nicht lesbar, und der Installer verweigerte zu Recht. Eine Attrappe, die keine
    # Datei ihrer Art ist, prueft an der Wirklichkeit vorbei.
    $pe = Join-Path $Wurzel ('Standalone\doorstop\' + $(if ($arch32) { 'x86' } else { 'x64' }) + '\winhttp.dll')
    Copy-Item $pe (Join-Path $d 'UnityPlayer.dll') -Force
    Copy-Item $pe (Join-Path $d 'Fake.exe') -Force
    if ($art -eq 'mono') { Set-Content (Join-Path $d 'Fake_Data\Managed\Assembly-CSharp.dll') 'x' }
    else {
        Set-Content (Join-Path $d 'GameAssembly.dll') 'x'
        New-Item -ItemType Directory -Path (Join-Path $d 'Fake_Data\il2cpp_data\Metadata') -Force | Out-Null
        Set-Content (Join-Path $d 'Fake_Data\il2cpp_data\Metadata\global-metadata.dat') 'x'
    }
    if ($melon) {
        New-Item -ItemType Directory -Path (Join-Path $d 'MelonLoader\net35') -Force | Out-Null
        Set-Content (Join-Path $d 'MelonLoader\MelonLoader.dll') 'x'
        Set-Content (Join-Path $d 'version.dll') 'x'
        New-Item -ItemType Directory -Path (Join-Path $d 'Mods') -Force | Out-Null
        Set-Content (Join-Path $d 'Mods\FremderMod.dll') 'fremd'
    }
    return $d
}

function Lauf([string] $d, [string[]] $weitere = @()) {
    $argumente = @($d, '--quiet') + $weitere
    # ⚠ 2>&1 AUF EINE NATIVE EXE ist unter $ErrorActionPreference='Stop' eine Falle: PowerShell
    #   verpackt jede stderr-Zeile in einen ErrorRecord (NativeCommandError), und der beendet
    #   den Lauf. Gemessen 2026-08-19: der Pruefer brach im BepInEx-6-Fall mitten im Durchgang
    #   ab - die zwei letzten Faelle liefen nie - und meldete trotzdem Exitcode 0. Ein
    #   abgebrochener Pruefer, der gruen aussieht, ist schlimmer als gar keiner.
    $alt = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { return (& $Exe $argumente 2>&1 | Out-String) }
    finally { $ErrorActionPreference = $alt }
}

function Pruefe([string] $titel, [string] $art, [bool] $melon, [string[]] $muss, [string[]] $darfNicht, [bool] $arch32 = $false) {
    Sag ""
    Fall "$titel"
    $d = Neu-Spiel $art $melon $arch32
    try {
        $null = Lauf $d
        foreach ($p in $muss) {
            if (Test-Path (Join-Path $d $p)) { Gut $p } else { Schlecht "missing: $p" }
        }
        foreach ($p in $darfNicht) {
            if (Test-Path (Join-Path $d $p)) { Schlecht "should not be there: $p" } else { Gut "absent: $p" }
        }
        if ($melon) {
            $inhalt = Get-Content (Join-Path $d 'Mods\FremderMod.dll') -Raw
            if ($inhalt.Trim() -eq 'fremd') { Gut "a foreign mod was left alone" }
            else { Schlecht "a foreign mod was modified" }
        }
    }
    finally { if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue } }
}

# Positivkontrolle: der Pruefer muss einen FEHLENDEN Bestandteil auch melden. Ohne das weiss
# man nur, dass er schweigt - und Schweigen ist hier die haeufigste Fehlerart.
if ($Selbsttest) {
    $vorher = $fehler
    Pruefe 'self-test (expects one deliberate miss)' 'mono' $false @('gibt-es-nicht.dll') @()
    if ($fehler -gt $vorher) {
        Sag "Self-test: a missing file is reported. (the FAIL above is the control)" 'Green'
        $script:fehler = $vorher
    } else {
        Sag "SELF-TEST FAILED: a missing file was not reported." 'Red'
        exit 1
    }
}

# ⚠ Die Lizenztexte als MENGE, nicht als Stichprobe: vorher war genau EINER zugesichert, und
# der wichtigste (MoonSharp) fehlte in allen drei Wegen, ohne dass etwas meldete.
$lizenzen = @('ScriptOne.LICENSE.txt','MoonSharp.LICENSE.txt','UnityDoorstop.LICENSE.txt',
              'HarmonyX.LICENSE.txt','Iced.LICENSE.txt','Il2CppInterop.LICENSE.txt',
              'Microsoft.Extensions.Logging.Abstractions.LICENSE.txt','Mono.Cecil.LICENSE.txt',
              'MonoMod.LICENSE.txt','THIRDPARTY-NOTICE.md') |
            ForEach-Object { 'ScriptOne\licenses\' + $_ }

# ⚠ winhttp.dll SOLL hier jetzt liegen - das ist die Zusage, nicht ein Versehen. MelonLoader
#   benutzt version.dll und KEIN Doorstop; unser Lader kann deshalb scharf danebenliegen, bleibt
#   still (MelonLoader gewinnt den Einstieg, in Schedule I gemessen) und uebernimmt von selbst,
#   sobald jemand MelonLoader loescht. Bis 2026-08-20 stand hier "absent: winhttp.dll" - genau
#   diese Erwartung war der Grund, warum ScriptOne nach dem Loeschen des Laders tot war.
Pruefe 'Mono + MelonLoader -> plugin (+ armed safety net)' 'mono' $true `
    (@('Plugins\ScriptOne.MONO.dll', 'UserLibs\MoonSharp.Interpreter.dll', 'version.dll',
       'winhttp.dll', 'doorstop_config.ini',
       'ScriptOne\core-runtime\net472\ScriptOne.Preloader.dll') + $lizenzen) `
    @('Plugins\ScriptOne.IL2CPP.dll')

Pruefe 'Mono, no loader -> standalone' 'mono' $false `
    (@('winhttp.dll', 'doorstop_config.ini', 'ScriptOne\core-runtime\net472\ScriptOne.Preloader.dll') + $lizenzen) `
    @('ScriptOne\core-runtime\net6')

Pruefe 'Il2Cpp, no loader -> standalone' 'il2cpp' $false `
    (@('winhttp.dll', 'doorstop_config.ini', 'ScriptOne\core-runtime\net6\ScriptOne.Preloader.dll') + $lizenzen) `
    @('ScriptOne\core-runtime\net472')

# ⚠ Das Paket lieferte lange nur den 64-Bit-Lader; ein 32-Bit-Spiel haette eine DLL bekommen,
# die Windows gar nicht laedt - der Lader waere nie angesprungen.
Sag ""
Fall "32-bit Mono game gets the 32-bit loader"
$d32 = Neu-Spiel 'mono' $false $true
try {
    $aus = Lauf $d32
    if ($aus -match '32-bit') { Gut "installer reports 32-bit" } else { Schlecht "installer did not report 32-bit" }
    $w = Join-Path $d32 'winhttp.dll'
    if (Test-Path $w) {
        $soll = (Get-Item (Join-Path $Wurzel 'Standalone\doorstop\x86\winhttp.dll')).Length
        if ((Get-Item $w).Length -eq $soll) { Gut "the 32-bit loader was installed ($soll bytes)" }
        else { Schlecht ("wrong loader: " + (Get-Item $w).Length + " bytes, expected " + $soll) }
    } else { Schlecht "no winhttp.dll installed at all" }
}
finally { if (Test-Path $d32) { Remove-Item $d32 -Recurse -Force -ErrorAction SilentlyContinue } }

# ⚠ GENAU EIN SKRIPT: hello.lua. Nicht leer (dann sieht der Nutzer nie, dass es laeuft) und
# nicht mehr (die spielspezifischen Beispiele gehoeren nicht in ein fremdes Spiel).
Sag ""
Fall "LuaScripts holds exactly hello.lua, and a user script survives"
$d4 = Neu-Spiel 'mono' $false
try {
    $null = Lauf $d4
    $ls = Join-Path $d4 'LuaScripts'
    $drin = @(Get-ChildItem $ls -Force -ErrorAction SilentlyContinue)
    if ($drin.Count -eq 1 -and $drin[0].Name -eq 'hello.lua') { Gut "exactly hello.lua" }
    else { Schlecht ("expected only hello.lua, found: " + (($drin | ForEach-Object { $_.Name }) -join ', ')) }
    Set-Content (Join-Path $ls 'hello.lua') '-- meins'
    $null = Lauf $d4
    if ((Get-Content (Join-Path $ls 'hello.lua') -Raw).Trim() -eq '-- meins') { Gut "a user script was left alone" }
    else { Schlecht "the installer overwrote a user script" }
}
finally { if (Test-Path $d4) { Remove-Item $d4 -Recurse -Force -ErrorAction SilentlyContinue } }

# ⚠ '--remove' liess frueher die Plugin-DLL liegen und meldete trotzdem Erfolg. Und entfernen
# heisst RESTLOS - Ansage des Autors: "als haette es dort nie existiert".
Sag ""
Fall "install as plugin, then --remove"
$d3 = Neu-Spiel 'mono' $true
try {
    $null = Lauf $d3
    if (Test-Path (Join-Path $d3 'Plugins\ScriptOne.MONO.dll')) { Gut "installed first" } else { Schlecht "install failed" }
    $null = Lauf $d3 @('--remove')
    foreach ($weg in @('Plugins\ScriptOne.MONO.dll', 'UserLibs\MoonSharp.Interpreter.dll',
                       'winhttp.dll', 'doorstop_config.ini', 'ScriptOne', 'LuaScripts')) {
        if (Test-Path (Join-Path $d3 $weg)) { Schlecht "still there after --remove: $weg" } else { Gut "removed: $weg" }
    }
    if (Test-Path (Join-Path $d3 'version.dll')) { Gut "MelonLoader untouched" } else { Schlecht "--remove killed MelonLoader" }
    if ((Get-Content (Join-Path $d3 'Mods\FremderMod.dll') -Raw).Trim() -eq 'fremd') { Gut "a foreign mod was left alone" }
    else { Schlecht "--remove touched a foreign mod" }
}
finally { if (Test-Path $d3) { Remove-Item $d3 -Recurse -Force -ErrorAction SilentlyContinue } }

# ⚠ DER GEFAEHRLICHE FALL: standalone legt den MelonLoader DES NUTZERS beiseite. Wer danach
# entfernt und ScriptOne\ loescht, BEVOR er ihn zurueckgelegt hat, zerstoert eine fremde
# Installation. Genau dafuer ist die Reihenfolge da - also wird sie geprueft, nicht angenommen.
Sag ""
Fall "standalone (MelonLoader set aside), then --remove"
$d5 = Neu-Spiel 'mono' $true
try {
    # ⚠ '--force' IST HIER INHALT, kein Beiwerk. Seit dem Mod-Manager-Fund haelt der stille
    #   Modus vor jedem Eingriff in eine fremde Lader-Installation an - vorher uebergab er
    #   bedingungslos 'trotzdem: true' und uebersprang damit JEDE Schutzabfrage. Wer diesen
    #   Fall ohne --force faehrt, misst ab jetzt die SPERRE und nicht das Beiseitelegen.
    $null = Lauf $d5 @('--standalone', '--force')
    if (Test-Path (Join-Path $d5 'ScriptOne\disabled-loaders\version.dll.melonloader-off')) { Gut "MelonLoader was set aside" }
    else { Schlecht "standalone did not set MelonLoader aside - case is meaningless" }
    $null = Lauf $d5 @('--remove')
    if (Test-Path (Join-Path $d5 'version.dll')) { Gut "MelonLoader came back" }
    else { Schlecht "DATENVERLUST: MelonLoader was not restored" }
    foreach ($weg in @('ScriptOne', 'LuaScripts', 'winhttp.dll', 'doorstop_config.ini')) {
        if (Test-Path (Join-Path $d5 $weg)) { Schlecht "still there after --remove: $weg" } else { Gut "removed: $weg" }
    }
}
finally { if (Test-Path $d5) { Remove-Item $d5 -Recurse -Force -ErrorAction SilentlyContinue } }

# ⚠ Eine Fassung, die wir nicht bedienen, muss ABGELEHNT werden - nicht stillschweigend
# bedient. Unter BepInEx 6 wird ein 5er-Plugin nicht abgelehnt, sondern GAR NICHT angefasst.
Sag ""
Fall "a mod manager must NOT be mistaken for a broken MelonLoader"
# ⚠ DIESER FALL IST DER TEUERSTE, DEN ES BISHER GAB. r2modman und der Thunderstore Mod Manager
#   halten MelonLoader in ihrem PROFIL und starten das Spiel mit '--melonloader.basedir'. Im
#   Spielordner bleibt genau EINE Datei: version.dll. Das sah fuer GameDetect wie 'Kaputt' aus,
#   WaehleModus schickte den Lauf nach Standalone, und der schob version.dll beiseite - womit
#   dem Nutzer ungefragt SEINE GESAMTE Mod-Installation abgeschaltet war.
$dmm = Neu-Spiel 'mono' $false
try {
    Set-Content (Join-Path $dmm 'version.dll') 'x'      # die einzige Datei, die ein Manager laesst
    $aus = Lauf $dmm @('--standalone')
    if ($aus -match 'mod manager') { Gut "refused, and the reason names the mod manager" }
    else { Schlecht "did not refuse: $($aus -split "`n" | Select-Object -First 1)" }
    if (Test-Path (Join-Path $dmm 'version.dll')) { Gut "version.dll untouched" }
    else { Schlecht "DATENVERLUST: version.dll was moved aside anyway" }
    if (-not (Test-Path (Join-Path $dmm 'winhttp.dll'))) { Gut "nothing was installed" }
    else { Schlecht "installed anyway" }
    # Gegenprobe: mit --force MUSS es durchgehen, sonst waere die Sperre eine Sackgasse.
    $null = Lauf $dmm @('--standalone', '--force')
    if (Test-Path (Join-Path $dmm 'winhttp.dll')) { Gut "--force still gets through" }
    else { Schlecht "--force did not work - the guard has no way out" }
}
finally { if (Test-Path $dmm) { Remove-Item $dmm -Recurse -Force -ErrorAction SilentlyContinue } }
Sag ""

Fall "BepInEx present -> ScriptOne takes the ENTRY POINT, and needs no plugin"
# ⚠ HIER STAND "jede BepInEx-Fassung bekommt IHREN Adapter". Das war richtig, solange
#   ScriptOne neben BepInEx nur als Plugin laufen konnte. Seit der Verkettung (2026-08-20)
#   uebernimmt es den Einstiegspunkt und startet BepInEx selbst - ein Plugin waere dann nicht
#   bloss ueberfluessig, sondern SCHAEDLICH (es beansprucht den Prozess zuerst, der eigene
#   Wirt legt sich schlafen, und ausgerechnet der Plugin-Weg hatte den toten Frame-Takt).
#   Der Adapter-Weg wird weiter geprueft - im Fall darunter, wo er wirklich noch greift.
foreach ($lage in @(
    @{ Name = 'BepInEx 5 + Mono';   Art = 'mono';   Kern = 'BepInEx.dll';       Tfm = 'net472' },
    @{ Name = 'BepInEx 6 + Mono';   Art = 'mono';   Kern = 'BepInEx.Core.dll';  Tfm = 'net472' },
    @{ Name = 'BepInEx 6 + Il2Cpp'; Art = 'il2cpp'; Kern = 'BepInEx.Core.dll';  Tfm = 'net6' })) {
    $d6 = Neu-Spiel $lage.Art $false
    try {
        $kern = Join-Path $d6 ('BepInEx' + [char]92 + 'core')
        New-Item -ItemType Directory -Path $kern -Force | Out-Null
        $pe = Join-Path $Wurzel ('Standalone' + [char]92 + 'doorstop' + [char]92 + 'x64' + [char]92 + 'winhttp.dll')
        Copy-Item $pe (Join-Path $kern $lage.Kern) -Force
        Copy-Item $pe (Join-Path $d6 'winhttp.dll') -Force
        $cfg = Join-Path $d6 'doorstop_config.ini'
        Set-Content $cfg ('target_assembly=BepInEx' + [char]92 + 'core' + [char]92 + 'BepInEx.Preloader.dll')
        $fremdVorher = (Get-FileHash $cfg -Algorithm MD5).Hash

        $null = Lauf $d6

        # 1. Der Einstiegspunkt zeigt jetzt auf uns.
        if ((Get-Content $cfg -Raw) -match 'ScriptOne') { Gut ($lage.Name + ': entry point taken over') }
        else { Schlecht ($lage.Name + ': doorstop_config.ini still points at the foreign loader') }

        # 2. ⚠ UND DER RUECKWEG EXISTIERT. Ohne diese Zusicherung ist die Uebernahme ein
        #    unwiderrufliches Ueberschreiben der fremden Konfiguration - genau das, was der
        #    Pruefer vorher (zu Recht) als Datenverlust gemeldet hat.
        $sicherung = Join-Path $d6 ('ScriptOne' + [char]92 + 'disabled-loaders' + [char]92 + 'doorstop_config.ini.original')
        if ((Test-Path $sicherung) -and (Get-FileHash $sicherung -Algorithm MD5).Hash -eq $fremdVorher) {
            Gut ($lage.Name + ': the original config is saved byte-identical')
        } else { Schlecht ($lage.Name + ': the foreign config was overwritten WITHOUT a backup') }

        # 3. Der Wirt liegt da - sonst haette die Uebernahme nichts zu starten.
        $kernDa = Join-Path $d6 ('ScriptOne' + [char]92 + 'core-runtime' + [char]92 + '' + $lage.Tfm + '' + [char]92 + 'ScriptOne.Preloader.dll')
        if (Test-Path $kernDa) { Gut ($lage.Name + ': host installed (' + $lage.Tfm + ')') }
        else { Schlecht ($lage.Name + ': no host - the taken-over entry point would start nothing') }

        # 4. Und KEIN Plugin.
        $plug = @(Get-ChildItem (Join-Path $d6 ('BepInEx' + [char]92 + 'plugins' + [char]92 + 'ScriptOne')) -Filter 'ScriptOne.BepInEx*.dll' -File -ErrorAction SilentlyContinue)
        if ($plug.Count -eq 0) { Gut ($lage.Name + ': no plugin was installed alongside') }
        else { Schlecht ($lage.Name + ': a plugin was installed too (' + (($plug | ForEach-Object { $_.Name }) -join ', ') + ') - two hosts') }
    }
    finally { if (Test-Path $d6) { Remove-Item $d6 -Recurse -Force -ErrorAction SilentlyContinue } }
}
Sag ""

Fall "if the takeover cannot happen, the RIGHT adapter is chosen - not just any"
# WARUM DIESER FALL WEITER EXISTIERT
# Seit der Verkettung uebernimmt ScriptOne den Einstiegspunkt, und der Adapter-Weg ist der
# RUECKFALL - erreichbar, wenn sich die doorstop_config.ini nicht schreiben laesst
# (schreibgeschuetzt, fremde Rechte, Laufwerk voll). Genau so wird er hier erzwungen.
# ⚠ MEIN ERSTER VERSUCH BAUTE STATTDESSEN EINEN SPIELORDNER OHNE winhttp.dll - "BepInEx ueber
#   einen Mod-Manager". Den erkennt der Installer gar nicht als BepInEx (BepInExAktiv verlangt
#   den Lader UND eine Konfiguration, die auf ihn zeigt), er installierte standalone, und der
#   Pruefer mass den Vorsorge-Satz statt der Adapterwahl. Eine Attrappe, die der Wirklichkeit
#   nicht entspricht, prueft an ihr vorbei - egal wie gruen sie wird.
foreach ($lage in @(
    @{ Name = 'BepInEx 5 + Mono';   Art = 'mono';   Kern = 'BepInEx.dll';      Soll = 'ScriptOne.BepInEx5.MONO.dll' },
    @{ Name = 'BepInEx 6 + Mono';   Art = 'mono';   Kern = 'BepInEx.Core.dll'; Soll = 'ScriptOne.BepInEx6.MONO.dll' },
    @{ Name = 'BepInEx 6 + Il2Cpp'; Art = 'il2cpp'; Kern = 'BepInEx.Core.dll'; Soll = 'ScriptOne.BepInEx6.IL2CPP.dll' })) {
    $d6 = Neu-Spiel $lage.Art $false
    try {
        $kern = Join-Path $d6 ('BepInEx' + [char]92 + 'core')
        New-Item -ItemType Directory -Path $kern -Force | Out-Null
        $pe = Join-Path $Wurzel ('Standalone' + [char]92 + 'doorstop' + [char]92 + 'x64' + [char]92 + 'winhttp.dll')
        Copy-Item $pe (Join-Path $kern $lage.Kern) -Force
        Copy-Item $pe (Join-Path $d6 'winhttp.dll') -Force
        $cfg = Join-Path $d6 'doorstop_config.ini'
        Set-Content $cfg ('target_assembly=BepInEx' + [char]92 + 'core' + [char]92 + 'BepInEx.Preloader.dll')
        Set-ItemProperty $cfg -Name IsReadOnly -Value $true

        $aus = Lauf $d6

        $ziel = Join-Path $d6 ('BepInEx' + [char]92 + 'plugins' + [char]92 + 'ScriptOne')
        $da = @(Get-ChildItem $ziel -Filter 'ScriptOne.BepInEx*.dll' -File -ErrorAction SilentlyContinue)
        if ($da.Count -eq 1 -and $da[0].Name -eq $lage.Soll) { Gut ($lage.Name + ' -> ' + $lage.Soll) }
        elseif ($da.Count -eq 0) { Schlecht ($lage.Name + ': nothing was installed - ' + (($aus -split "`n" | Select-Object -Last 3) -join ' / ')) }
        else { Schlecht ($lage.Name + ': got ' + (($da | ForEach-Object { $_.Name }) -join ', ') + ', expected ' + $lage.Soll) }

        # ⚠ UND DER FEHLSCHLAG MUSS DASTEHEN. Ein stiller Rueckfall auf den Plugin-Weg waere
        #   die schlechteste Variante: der Nutzer glaubt, ScriptOne ueberlebe das Entfernen
        #   von BepInEx, und es tut es nicht.
        if ($aus -match 'could not take over the entry point') { Gut ($lage.Name + ': the failed takeover is reported') }
        else { Schlecht ($lage.Name + ': fell back to the plugin WITHOUT saying so') }
    }
    finally { if (Test-Path $d6) { Remove-Item $d6 -Recurse -Force -ErrorAction SilentlyContinue } }
}
Sag ""

Fall "BepInEx 5 on an Il2Cpp game has no build - it must say so"
# Die Gegenrichtung zum Fall darueber: BepInEx 5 gibt es nur fuer Mono. Ohne diesen Fall waere
# die Auswahl auch dann gruen, wenn sie im Zweifel IRGENDETWAS installiert. Ebenfalls mit
# schreibgeschuetzter Konfiguration - sonst uebernimmt ScriptOne und braucht gar keinen Adapter.
$d7 = Neu-Spiel 'il2cpp' $false
try {
    $kern = Join-Path $d7 ('BepInEx' + [char]92 + 'core')
    New-Item -ItemType Directory -Path $kern -Force | Out-Null
    $pe = Join-Path $Wurzel ('Standalone' + [char]92 + 'doorstop' + [char]92 + 'x64' + [char]92 + 'winhttp.dll')
    Copy-Item $pe (Join-Path $kern 'BepInEx.dll') -Force
    Copy-Item $pe (Join-Path $d7 'winhttp.dll') -Force
    $cfg = Join-Path $d7 'doorstop_config.ini'
    Set-Content $cfg ('target_assembly=BepInEx' + [char]92 + 'core' + [char]92 + 'BepInEx.Preloader.dll')
    Set-ItemProperty $cfg -Name IsReadOnly -Value $true

    $aus = Lauf $d7
    if ($aus -match 'No ScriptOne build fits') { Gut "refused with a reason" } else { Schlecht "did not refuse: $aus" }
    if (-not (Test-Path (Join-Path $d7 ('BepInEx' + [char]92 + 'plugins' + [char]92 + 'ScriptOne')))) { Gut "nothing was installed" }
    else { Schlecht "installed something anyway" }
}
finally { if (Test-Path $d7) { Remove-Item $d7 -Recurse -Force -ErrorAction SilentlyContinue } }

Sag ""
Fall "a stand-by plugin for EVERY BepInEx build this backend could get"
# WARUM DIESER FALL EXISTIERT
# Hier lag nur der BepInEx-5-Mono-Bau - unabhaengig davon, was der Nutzer spaeter installiert.
# Ein Plugin fuer die falsche Fassung wird aber nicht abgelehnt, sondern GAR NICHT ANGEFASST:
# wer auf ein Mono-Spiel spaeter BepInEx 6 setzte, verlor ScriptOne lautlos, und auf einem
# Il2Cpp-Spiel lag ueberhaupt nichts bereit. Gefunden 2026-08-20 beim Umbau der Faelle darueber.
foreach ($lage in @(
    @{ Art = 'mono';   Soll = @('ScriptOne.BepInEx5.MONO.dll', 'ScriptOne.BepInEx6.MONO.dll') },
    @{ Art = 'il2cpp'; Soll = @('ScriptOne.BepInEx6.IL2CPP.dll') })) {
    $dsb = Neu-Spiel $lage.Art $false
    try {
        $null = Lauf $dsb
        $ziel = Join-Path $dsb ('BepInEx' + [char]92 + 'plugins' + [char]92 + 'ScriptOne')
        $da = @(Get-ChildItem $ziel -Filter 'ScriptOne.BepInEx*.dll' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        $fehlt = @($lage.Soll | Where-Object { $da -notcontains $_ })
        if ($fehlt.Count -eq 0) { Gut ($lage.Art + ': stand-by for ' + ($lage.Soll -join ' + ')) }
        else { Schlecht ($lage.Art + ': no stand-by for ' + ($fehlt -join ', ') + ' - a later BepInEx of that version would not find ScriptOne') }
        # Gegenrichtung: kein Bau, der auf diesem Backend nie laden koennte.
        $zuviel = @($da | Where-Object { $lage.Soll -notcontains $_ })
        if ($zuviel.Count -eq 0) { Gut ($lage.Art + ': and nothing that could never load here') }
        else { Schlecht ($lage.Art + ': staged a build for the other backend: ' + ($zuviel -join ', ')) }
    }
    finally { if (Test-Path $dsb) { Remove-Item $dsb -Recurse -Force -ErrorAction SilentlyContinue } }
}
Sag ""

# ⚠ ZWEITER LAUF. Der Setup scheiterte frueher reproduzierbar beim zweiten Doppelklick, weil
# seine Quelle in den ZIELordner zeigte und er eine Datei auf sich selbst kopierte.
Sag ""
Fall "the installer must RECOGNISE what it just installed"
# WARUM DIESER FALL EXISTIERT
# Alle Faelle oben pruefen, ob die richtige DATEI am richtigen Ort landet - und alle waren
# gruen, waehrend der Installer dieselbe Installation als "not installed" meldete. Er hat
# naemlich nur MelonLoaders Plugins-Ordner abgesucht. Installieren und NACHSEHEN sind zwei
# Wege, und ein Pruefer, der nur den ersten geht, laesst den zweiten beliebig weit
# auseinanderlaufen. Gemeldet 2026-08-20 mit Bildschirmfoto (Landlord Simulator, BepInEx 5).
# ⚠ DIE ERWARTUNG FUER BepInEx IST NICHT MEHR "plugin bepinex". Seit der Verkettung laeuft
#   ScriptOne dort als STANDALONE-Wirt, der BepInEx nachstartet - "standalone yes | plugin no"
#   ist also die RICHTIGE Antwort und kein Regress. Ein Pruefer, der nach einer Entwurfsaenderung
#   weiter das Alte erwartet, ist rot ohne Fehler.
foreach ($lage in @(
    @{ Name = 'MelonLoader + Mono'; Art = 'mono';   Melon = $true;  Bep = $null;              Soll = 'plugin\s+melonloader'; Text = 'plugin melonloader' },
    @{ Name = 'BepInEx 5 + Mono';   Art = 'mono';   Melon = $false; Bep = 'BepInEx.dll';      Soll = 'standalone\s+yes';     Text = 'standalone (entry point taken over)' },
    @{ Name = 'BepInEx 6 + Il2Cpp'; Art = 'il2cpp'; Melon = $false; Bep = 'BepInEx.Core.dll'; Soll = 'standalone\s+yes';     Text = 'standalone (entry point taken over)' })) {
    $ds = Neu-Spiel $lage.Art $lage.Melon
    try {
        if ($lage.Bep) {
            $kern = Join-Path $ds ('BepInEx' + [char]92 + 'core')
            New-Item -ItemType Directory -Path $kern -Force | Out-Null
            Copy-Item (Join-Path $Wurzel ('Standalone' + [char]92 + 'doorstop' + [char]92 + 'x64' + [char]92 + 'winhttp.dll')) (Join-Path $kern $lage.Bep) -Force
            Copy-Item (Join-Path $Wurzel ('Standalone' + [char]92 + 'doorstop' + [char]92 + 'x64' + [char]92 + 'winhttp.dll')) (Join-Path $ds 'winhttp.dll') -Force
            Set-Content (Join-Path $ds 'doorstop_config.ini') ('target_assembly=BepInEx' + [char]92 + 'core' + [char]92 + 'BepInEx.Preloader.dll')
        }
        # VORHER: es darf noch nichts gemeldet werden - sonst prueft der Fall nichts.
        $vorher = Lauf $ds @('--status')
        $null = Lauf $ds
        $nachher = Lauf $ds @('--status')

        $zeileV = ($vorher  -split "`n" | Where-Object { $_ -match 'ScriptOne\s*:' }) -join ' '
        $zeileN = ($nachher -split "`n" | Where-Object { $_ -match 'ScriptOne\s*:' }) -join ' '

        if ($zeileV -notmatch 'plugin\s+no' -or $zeileV -match 'standalone\s+yes') {
            Schlecht ($lage.Name + ': schon VOR der Installation als vorhanden gemeldet -> "' + $zeileV.Trim() + '"')
        }
        elseif ($zeileN -match $lage.Soll) {
            Gut ($lage.Name + ' -> erkannt als ' + $lage.Text)
        }
        else {
            Schlecht ($lage.Name + ': nach der Installation gemeldet als "' + $zeileN.Trim() + '", erwartet ' + $lage.Text)
        }
    }
    finally { if (Test-Path $ds) { Remove-Item $ds -Recurse -Force -ErrorAction SilentlyContinue } }
}

Fall "next to BepInEx: the full set, and it survives the loader WITHOUT the installer"
# WARUM DIESER FALL EXISTIERT
# Ansage des Autors, 2026-08-20: wird MelonLoader oder BepInEx spaeter geloescht, soll
# ScriptOne sich SELBST organisieren - ohne dass der Nutzer noch einmal etwas anklickt.
# ("meine scripte starten nicht weil ich melonloader geloescht habe?! ... da schreibe ich eine
# negative bewertung"). Der teure, spielabgeleitete Teil muss also schon jetzt daliegen -
# nachtraeglich gibt es niemanden mehr, der ihn erzeugen koennte.
# ⚠ UND DER PRUEFER DARF DEN INSTALLER DANACH NICHT NOCH EINMAL STARTEN. Genau das hat er
#   vorher getan und damit die Anforderung gar nicht gemessen: "der naechste LAUF uebernimmt"
#   ist eine Aussage ueber den Installer, verlangt war eine ueber den SPIELSTART.
$dv = Neu-Spiel 'mono' $false
try {
    $kern = Join-Path $dv ('BepInEx' + [char]92 + 'core')
    New-Item -ItemType Directory -Path $kern -Force | Out-Null
    $pe = Join-Path $Wurzel ('Standalone' + [char]92 + 'doorstop' + [char]92 + 'x64' + [char]92 + 'winhttp.dll')
    Copy-Item $pe (Join-Path $kern 'BepInEx.dll') -Force
    Copy-Item $pe (Join-Path $dv 'winhttp.dll') -Force
    $cfg = Join-Path $dv 'doorstop_config.ini'
    # ⚠ HASH, NICHT LAENGE. Eine Konfiguration gleicher Laenge mit anderem Ziel waere
    #   der schlimmste Fall - der Fremdlader startet dann still den falschen Preloader.
    Set-Content $cfg ('target_assembly=BepInEx' + [char]92 + 'core' + [char]92 + 'BepInEx.Preloader.dll')
    $fremdVorher = (Get-FileHash $cfg -Algorithm MD5).Hash

    $null = Lauf $dv

    $kernDa = Join-Path $dv ('ScriptOne' + [char]92 + 'core-runtime' + [char]92 + 'net472' + [char]92 + 'ScriptOne.Preloader.dll')
    if (Test-Path $kernDa) { Gut "host laid down next to BepInEx" }
    else { Schlecht "no host - a later loader removal would leave nothing" }

    $sicherung = Join-Path $dv ('ScriptOne' + [char]92 + 'disabled-loaders' + [char]92 + 'doorstop_config.ini.original')
    if ((Test-Path $sicherung) -and (Get-FileHash $sicherung -Algorithm MD5).Hash -eq $fremdVorher) {
        Gut "the loader's original doorstop_config.ini is saved byte-identical"
    } else { Schlecht "the foreign config was overwritten WITHOUT a byte-identical backup" }

    # ⚠ UND DIE VERKETTUNG STEHT WIRKLICH DRIN. Ohne diese Zeile belegt der Fall nur, dass
    #   irgendetwas geschrieben wurde - nicht, dass BepInEx danach noch startet.
    $inhalt = Get-Content $cfg -Raw
    if ($inhalt -match 'ScriptOne') { Gut "the entry point points at ScriptOne" }
    else { Schlecht "doorstop_config.ini does not point at ScriptOne" }

    # Jetzt entfernt der Nutzer BepInEx - und startet NUR das Spiel. Kein Installer.
    Remove-Item (Join-Path $dv 'BepInEx') -Recurse -Force
    if ((Test-Path (Join-Path $dv 'winhttp.dll')) -and (Test-Path $cfg) -and
        ((Get-Content $cfg -Raw) -match 'ScriptOne') -and (Test-Path $kernDa)) {
        Gut "after BepInEx was deleted, the entry point is still armed and still ours - no installer run needed"
    } else { Schlecht "deleting BepInEx left ScriptOne without an entry point" }
}
finally { if (Test-Path $dv) { Remove-Item $dv -Recurse -Force -ErrorAction SilentlyContinue } }

Fall "a taken-over BepInEx survives a SECOND run and a --remove"
# WARUM DIESER FALL EXISTIERT
# Nach der Uebernahme zeigt die doorstop_config.ini auf ScriptOne - BepInExAktiv ist damit
# false. Ein zweiter Lauf faellt ohne Gegenmassnahme in den STANDALONE-Weg: der ueberschreibt
# BepInEx' winhttp.dll mit unserer und vermerkt sie als UNSERE; das folgende --remove loescht
# sie dann und laesst BepInEx ohne Lader zurueck. Der Nutzer haette eine ScriptOne-Deinstallation
# angestossen und danach eine tote BepInEx-Installation - die schlimmste Klasse Fehler hier,
# weil sie ein FREMDES Produkt zerstoert. Gefunden 2026-08-20 am echten Landlord Simulator.
$d2 = Neu-Spiel 'mono' $false
try {
    $kern = Join-Path $d2 ('BepInEx' + [char]92 + 'core')
    New-Item -ItemType Directory -Path $kern -Force | Out-Null
    $pe = Join-Path $Wurzel ('Standalone' + [char]92 + 'doorstop' + [char]92 + 'x64' + [char]92 + 'winhttp.dll')
    Copy-Item $pe (Join-Path $kern 'BepInEx.dll') -Force
    Copy-Item $pe (Join-Path $d2 'winhttp.dll') -Force
    $cfg = Join-Path $d2 'doorstop_config.ini'
    Set-Content $cfg ('target_assembly=BepInEx' + [char]92 + 'core' + [char]92 + 'BepInEx.Preloader.dll')
    $fremdVorher = (Get-FileHash $cfg -Algorithm MD5).Hash

    $null = Lauf $d2          # 1. Lauf: Uebernahme
    $zwei = Lauf $d2          # 2. Lauf: darf NICHT in den Standalone-Weg fallen

    if ($zwei -match 'entry point taken over') { Gut "the second run stays on the BepInEx path" }
    else { Schlecht ("the second run left the BepInEx path: " + (($zwei -split "`n" | Select-Object -First 2) -join ' / ')) }

    # ⚠ DIE STATUSZEILE MUSS ES AUCH SAGEN. "BepInEx folder, but its loader is not installed"
    #   ueber eine laufende Installation ist eine Ferndiagnose, die der Nutzer glaubt.
    $st = Lauf $d2 @('--status')
    if ($st -match 'started by ScriptOne') { Gut "--status names the chained BepInEx" }
    else { Schlecht ("--status misreports the chained BepInEx: " + (($st -split "`n" | Where-Object { $_ -match 'Mod loader' }) -join ' ')) }

    $null = Lauf $d2 @('--remove')
    if (Test-Path (Join-Path $d2 'winhttp.dll')) { Gut "--remove left BepInEx's own loader in place" }
    else { Schlecht "--remove DELETED BepInEx's winhttp.dll - that installation is dead now" }
    if ((Test-Path $cfg) -and (Get-FileHash $cfg -Algorithm MD5).Hash -eq $fremdVorher) { Gut "--remove restored BepInEx's config byte-identical" }
    else { Schlecht "--remove did not restore BepInEx's doorstop_config.ini" }
}
finally { if (Test-Path $d2) { Remove-Item $d2 -Recurse -Force -ErrorAction SilentlyContinue } }

Sag ""
Fall "the installer must not contradict itself about its own safety net"
# WARUM DIESER FALL EXISTIERT
# Der Plugin-Weg legt neben MelonLoader ABSICHTLICH eine winhttp.dll scharf ("safety net
# armed") - und meldete eine Zeile spaeter "NOTE: winhttp.dll is still here - a second loader
# next to MelonLoader" ueber genau diese Datei. Die Warnung stammte aus der Zeit, in der eine
# solche Datei zwangslaeufig FREMD war. Zwei Aussagen ueber denselben Zustand sind zwei
# Wahrheiten, und der Nutzer glaubt danach keiner mehr - dieselbe Klasse wie "Will install as:
# standalone" ueber einem Protokoll, das "installing as a BepInEx plugin" sagte.
# Gefunden 2026-08-20 am echten Schedule I.
$dw = Neu-Spiel 'mono' $true
try {
    $aus = Lauf $dw
    if ($aus -match 'safety net armed') { Gut "the safety net is announced" }
    else { Schlecht "no safety net was armed next to MelonLoader" }
    if ($aus -notmatch 'a second loader next to MelonLoader') { Gut "and it is not warned about as a foreign loader" }
    else { Schlecht "the installer warns about the very loader it just armed itself" }
}
finally { if (Test-Path $dw) { Remove-Item $dw -Recurse -Force -ErrorAction SilentlyContinue } }

Sag ""
Fall "a fresh install writes console = auto, and keeps an explicit choice"
# WARUM DIESER FALL EXISTIERT
# Ansage des Autors, 2026-08-20: laeuft ScriptOne ALLEIN, muss die Konsole an sein - sonst gibt
# es im ganzen Spiel kein Fenster, das irgendetwas anzeigt, und der Nutzer sieht nichts, bis er
# von sich aus in eine Logdatei sieht, von der er nichts weiss. Ein BOOL kann das nicht tragen:
# "hat der Nutzer bewusst false gesetzt?" waere nicht von "stand halt so in der Vorlage" zu
# unterscheiden, weil der Installer die Zeile immer schreibt. Deshalb der dritte Wert - und
# deshalb prueft dieser Fall BEIDE Richtungen: die Vorgabe UND dass eine eigene Wahl bleibt.
$dc = Neu-Spiel 'mono' $false
try {
    $null = Lauf $dc
    $cfg = Join-Path $dc ('ScriptOne' + [char]92 + 'ScriptOne-Starter.cfg')
    $zeile = (Select-String -Path $cfg -Pattern '^console\s*=').Line
    if ($zeile -match 'console\s*=\s*auto') { Gut "fresh config: $($zeile.Trim())" }
    else { Schlecht ("fresh config says '" + $zeile + "' - expected 'console = auto'") }

    # ⚠ DIE GEGENRICHTUNG. Ein zweiter Lauf, der die eigene Wahl des Nutzers ueberschreibt,
    #   waere schlimmer als eine falsche Vorgabe - er nimmt ihm den Schalter aus der Hand.
    (Get-Content $cfg -Raw) -replace '(?m)^console\s*=.*$', 'console = false' | Set-Content $cfg -Encoding UTF8 -NoNewline
    $null = Lauf $dc
    $zwei = (Select-String -Path $cfg -Pattern '^console\s*=').Line
    if ($zwei -match 'console\s*=\s*false') { Gut "a second run keeps the user's own choice" }
    else { Schlecht ("the second run overwrote the user's choice: '" + $zwei + "'") }
}
finally { if (Test-Path $dc) { Remove-Item $dc -Recurse -Force -ErrorAction SilentlyContinue } }

Sag ""
Fall "the installer must not report the BepInEx folder it created itself"
# WARUM DIESER FALL EXISTIERT
# Die Vorsorge-Ablage legt BepInEx' + [char]92 + 'plugins' + [char]92 + 'ScriptOne' + [char]92 + ' auf JEDEM Spiel an - auch auf einem, das
# nie ein BepInEx gesehen hat. Die Erkennung fragte aber nur, ob BepInEx' + [char]92 + ' EXISTIERT. Nach der
# eigenen Standalone-Installation meldete die Statuszeile daraufhin "BepInEx folder, but its
# loader is not installed", in Warnfarbe - ueber einen Ordner, den derselbe Lauf eine Sekunde
# vorher selbst erzeugt hatte. Der Kommentar ueber der Erkennungszeile warnte woertlich vor
# genau diesem Fehler ("Wer nur den Ordner prueft ... verunsichert ohne Grund"), und die Zeile
# darunter tat es. Gefunden 2026-08-21 bei einer Messung fuer etwas anderes.
$dbf = Neu-Spiel 'mono' $false
try {
    $vorher = Lauf $dbf @('--status')
    if ($vorher -match 'Mod loader\s*:\s*none') { Gut "before: no mod loader" }
    else { Schlecht ("before: expected 'none', got: " + (($vorher -split "`n" | Where-Object { $_ -match 'Mod loader' }) -join ' ')) }

    $null = Lauf $dbf
    # Die Vorsorge MUSS liegen - sonst prueft der Fall nichts.
    if (Test-Path (Join-Path $dbf ('BepInEx' + [char]92 + 'plugins' + [char]92 + 'ScriptOne'))) { Gut "the stand-by plugin folder was created (that is the point)" }
    else { Schlecht "no stand-by plugin folder - this case cannot measure anything" }

    $nachher = Lauf $dbf @('--status')
    $zeile = (($nachher -split "`n" | Where-Object { $_ -match 'Mod loader' }) -join ' ').Trim()
    if ($zeile -match 'Mod loader\s*:\s*none') { Gut "after: still 'none' - our own folder is not reported as BepInEx" }
    else { Schlecht ("after: the installer reports its own folder as a loader -> '" + $zeile + "'") }
}
finally { if (Test-Path $dbf) { Remove-Item $dbf -Recurse -Force -ErrorAction SilentlyContinue } }

Sag ""
Fall "starting the exe must not write anything - only installing may"
# WARUM DIESER FALL EXISTIERT
# Der Beipack wurde bei JEDEM Start ausgepackt. Zwei Folgen: '--status' verspricht woertlich
# "only report what is there, change nothing" und schrieb dabei rund zwanzig DLLs nach %TEMP%,
# und eine Sandbox, die die exe nur STARTET, sah genau das - ein Programm, das sofort DLLs in
# einen Temp-Ordner legt. Von einem Dropper ist das verhaltensmaessig nicht zu unterscheiden.
# Gemessen 2026-08-21 auf VirusTotal: 2 von rund 70 Motoren, beide generische ML-Urteile, und
# der Verhaltensbericht fuehrte genau diese Schreibvorgaenge auf.
# ⚠ Der Fall misst BEIDE Richtungen. Nur "schreibt nichts" waere auch gruen, wenn das Auspacken
#   voellig kaputt ist - dann installiert der Installer gar nicht mehr.
$dvt = Neu-Spiel 'mono' $false
$muster = 'ScriptOne-*-payload-*'
try {
    Get-ChildItem $env:TEMP -Directory -Filter $muster -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    function Auspackordner { @(Get-ChildItem $env:TEMP -Directory -Filter $muster -ErrorAction SilentlyContinue).Count }

    $null = & $Exe --help 2>&1
    if ((Auspackordner) -eq 0) { Gut "--help writes nothing (this is what a sandbox sees)" }
    else { Schlecht "--help already unpacked the payload into %TEMP%" }

    $null = Lauf $dvt @('--status')
    if ((Auspackordner) -eq 0) { Gut "--status writes nothing - the promise holds" }
    else { Schlecht "--status unpacked the payload, while promising to change nothing" }

    $null = Lauf $dvt
    if ((Auspackordner) -ge 1) { Gut "installing does unpack it - the lazy path still works" }
    else { Schlecht "installing did NOT unpack the payload - nothing would be installed" }

    # Und die Installation muss auch wirklich stattgefunden haben.
    if (Test-Path (Join-Path $dvt ('ScriptOne' + [char]92 + 'core-runtime' + [char]92 + 'net472' + [char]92 + 'ScriptOne.Preloader.dll'))) {
        Gut "and the host really arrived in the game folder"
    } else { Schlecht "the host is missing after the install" }
}
finally {
    if (Test-Path $dvt) { Remove-Item $dvt -Recurse -Force -ErrorAction SilentlyContinue }
    Get-ChildItem $env:TEMP -Directory -Filter $muster -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
}

Sag ""
Fall "run a SECOND time (must not fail)"
$d2 = Neu-Spiel 'mono' $false
try {
    $null = Lauf $d2
    $aus = Lauf $d2
    if ($LASTEXITCODE -ne 0) { Schlecht "second run exits with $LASTEXITCODE" } else { Gut "second run exits 0" }
    if ($aus -match 'Ausnahme|Exception') { Schlecht "second run shows a .NET exception" }
    else { Gut "second run without an exception" }
    if (Test-Path (Join-Path $d2 'ScriptOne\core-runtime\net472\ScriptOne.Preloader.dll')) { Gut "installation still intact" }
    else { Schlecht "the second run damaged the installation" }
}
finally { if (Test-Path $d2) { Remove-Item $d2 -Recurse -Force -ErrorAction SilentlyContinue } }

Sag ""
if ($fehler -gt 0) { Write-Host "  $fehler problem(s) in the delivered installer." -ForegroundColor Red; exit 1 }
Write-Host "  the delivered installer installs, removes and re-runs correctly in all $($script:faelle) cases." -ForegroundColor Green
exit 0
