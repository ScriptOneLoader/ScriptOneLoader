<#
    Check-Ordnernamen.ps1 - findet Reste alter Ordnernamen im Quellbestand.

    WARUM ES DAS GIBT
    Am 2026-08-19 wurden core\ -> core-runtime\, interop\ -> interopgenerator\ und
    backup\ -> disabled-loaders\ umbenannt. Zehn Stellen blieben stehen, VIER davon
    funktional - darunter die Zeile, die doorstop_config.ini schreibt:

        target_assembly=ScriptOne\core\$tfm\ScriptOne.Preloader.dll

    Der Bau blieb gruen, alle Tests blieben gruen, und eine Standalone-Installation
    haette auf einen Pfad gezeigt, den es nicht gibt. Kein Compiler und kein Test sieht
    das: es sind Zeichenketten. Also hier pruefen.

    WAS ERLAUBT IST
    Die Migration MUSS die alten Namen kennen - sie benennt ja von ihnen um. Ebenso
    darf die Projektdoku den alten Zustand historisch zitieren. Beides steht unten in
    der Ausnahmeliste, mit Begruendung. Eine Ausnahme ohne Begruendung ist ein Fehler.

    AUFRUF
        .\tools\Check-Ordnernamen.ps1            # prueft
        .\tools\Check-Ordnernamen.ps1 -Selbsttest # prueft, dass der Pruefer feuern KANN
#>
[CmdletBinding()]
param(
    [string] $Wurzel,
    [switch] $Selbsttest
)
$ErrorActionPreference = 'Stop'

# ⚠ NICHT im param()-Vorgabewert: mit [CmdletBinding()] ist $PSScriptRoot dort leer,
# sobald das Skript per 'powershell -File' startet (gemessen 2026-08-19).
if (-not $Wurzel) { $Wurzel = Split-Path $PSScriptRoot -Parent }
if ([string]::IsNullOrEmpty($Wurzel)) { Write-Host "ABORT: cannot determine repository root." -ForegroundColor Red; exit 1 }

# Der ALTE Name als PFADSEGMENT - genau das, was in eine Datei geschrieben wuerde.
# ⚠ DER ABSCHLIESSENDE TRENNER WAR PFLICHT - und genau daran rutschte ein Fund durch:
# ein Hilfetext endete auf 'ScriptOne\interop' ohne Trenner am Zeilenende. Jetzt ist er
# OPTIONAL, dafuer muss danach ein Wortende stehen, damit 'interopgenerator' nicht selbst
# als Treffer gilt. (Audit 2026-08-19, dritter Grund fuer das Schweigen dieses Pruefers.)
$muster = 'ScriptOne[\\/](core|interop|backup)([\\/]|$|["'' ])'
# ⚠ '-' darf NICHT zaehlen: 'ScriptOne\core-runtime' ist der RICHTIGE Name. Meine erste
#   Erweiterung nahm jedes Nicht-Wortzeichen und meldete damit den korrekten Ordner als Fund.

# Ausnahmen: Datei -> Grund. Jede braucht einen, sonst waechst die Liste bis der
# Pruefer nichts mehr prueft. Der Grund wird AUSGEGEBEN, steht also auf Englisch.
$ausnahmen = @{
    'NEXT.md'                        = 'quotes the state BEFORE the rename, in the "Widerlegt" section'
    'tools/Check-Ordnernamen.ps1'    = 'this checker names the old folder names itself'
}

Push-Location $Wurzel
try {
    $dateien = @(git ls-files | Where-Object { $_ -match '\.(ps1|cs|csproj|props|targets|ini|md|json|lua)$' })
    $treffer = New-Object 'System.Collections.Generic.List[string]'

    foreach ($d in $dateien) {
        if ($ausnahmen.ContainsKey($d)) { continue }
        $i = 0
        foreach ($z in [IO.File]::ReadAllLines((Join-Path $Wurzel $d))) {
            $i++
            if ($z -cmatch $muster) { $treffer.Add(("{0}:{1}  {2}" -f $d, $i, $z.Trim())) }
        }
    }

    if ($Selbsttest) {
        # Positivkontrolle: der Pruefer muss an einer KUENSTLICHEN Zeile anschlagen.
        # Ohne sie weiss man nur, dass er schweigt - nicht, dass er sehen kann.
        $probe = 'target_assembly=ScriptOne\core\net6\ScriptOne.Preloader.dll'
        if ($probe -cmatch $muster) {
            Write-Host "  Self-test: the checker detects an injected line." -ForegroundColor Green
        } else {
            Write-Host "  SELF-TEST FAILED: the pattern does not match the probe." -ForegroundColor Red
            exit 1
        }
    }

    Write-Host ""
    Write-Host ("  {0} files checked, {1} exception(s)" -f $dateien.Count, $ausnahmen.Count)
    foreach ($a in $ausnahmen.GetEnumerator()) { Write-Host ("    excepted: {0}  ({1})" -f $a.Key, $a.Value) -ForegroundColor Gray }

    if ($treffer.Count -eq 0) {
        Write-Host "  No old folder names used as a path." -ForegroundColor Green
        exit 0
    }
    Write-Host ""
    Write-Host ("  {0} LEFTOVER(S) of old folder names:" -f $treffer.Count) -ForegroundColor Red
    $treffer | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "  core\ -> core-runtime\   interop\ -> interopgenerator\   backup\ -> disabled-loaders\"
    exit 1
}
finally { Pop-Location }
