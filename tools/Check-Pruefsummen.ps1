<#
    Check-Pruefsummen.ps1 - rechnet die Pruefsummen der mitgelieferten Fremdbinaerdateien nach.

    WARUM ES DAS GIBT
    Zwei Notizen im Repo sichern zu, dass die vier Fremddateien UNVERAENDERT weitergegeben
    werden - `Standalone\THIRDPARTY-NOTICE.md` und `Standalone\doorstop\THIRDPARTY-NOTICE.md`.
    Bei UnityDoorstop ist das keine Hoeflichkeit, sondern die Grundlage der LGPL-Konstruktion:
    sobald die Binaerdatei veraendert waere, gaelten zusaetzliche Pflichten. Nachrechnen konnte
    die Zusicherung bis zum 2026-08-19 niemand.

    ⚠ WAS ER PRUEFT UND WAS NICHT
    Er prueft DRIFT: weichen die Dateien im Arbeitsbaum von den festgehaltenen Werten ab.
    Er prueft NICHT die HERKUNFT - ob die Bytes mit dem offiziellen Upstream-Release
    uebereinstimmen, weiss er nicht und kann es nicht wissen, dafuer muesste er herunterladen.
    Das steht so auch in `Standalone\SHASUMS.md` und gehoert bei jeder Aussage dazu, die sich
    auf diesen Pruefer stuetzt.

    ⚠ EINE WAHRHEIT, NICHT ZWEI
    Die Sollwerte stehen NUR in `Standalone\SHASUMS.md` und werden von dort gelesen. Eine
    zweite Liste hier im Skript waere beim ersten bewussten Austausch veraltet, ohne dass es
    jemandem auffaellt - und ein Pruefer, der gegen veraltete Sollwerte gruen meldet, ist
    schaedlicher als keiner.

    ⚠ NULL ZEILEN SIND EIN AUSFALL, KEIN ERFOLG
    Bricht das Tabellenformat in SHASUMS.md, liest der Parser null Eintraege - und ein Lauf
    ueber null Dateien findet null Abweichungen und meldet Erfolg. Deshalb ist "keine
    Eintraege gelesen" hier ein ABBRUCH, und der Selbsttest prueft den Parser ausdruecklich
    mit.

    AUFRUF
        .\tools\Check-Pruefsummen.ps1
        .\tools\Check-Pruefsummen.ps1 -Selbsttest
#>
[CmdletBinding()]
param(
    [string] $Wurzel,
    [switch] $Selbsttest
)
$ErrorActionPreference = 'Stop'

# ⚠ Nicht im param()-Vorgabewert: mit [CmdletBinding()] ist $PSScriptRoot dort leer,
# sobald das Skript per 'powershell -File' startet (gemessen 2026-08-19).
if (-not $Wurzel) { $Wurzel = Split-Path $PSScriptRoot -Parent }
if ([string]::IsNullOrEmpty($Wurzel)) { Write-Host "ABORT: cannot determine repository root." -ForegroundColor Red; exit 1 }

$sollDatei = Join-Path $Wurzel 'Standalone\SHASUMS.md'

# Eine Tabellenzeile aus SHASUMS.md:
#   | `Pfad\zur\Datei.dll` | 26.112 | `<64 Hexziffern>` |
# Der Backtick ist in einfachen Anfuehrungszeichen literal, nicht das PowerShell-Fluchtzeichen.
# Die 64 Hexziffern in der dritten Spalte sind der Diskriminator: die uebrigen Tabellen im
# Dokument (Herkunft, Lizenz) koennen so gar nicht aussehen.
$zeilenMuster = '^\s*\|\s*`([^`]+)`\s*\|\s*([0-9.,]+)\s*\|\s*`([0-9a-fA-F]{64})`\s*\|'

function Lies-Sollwerte {
    param([string[]] $Zeilen)
    $liste = New-Object 'System.Collections.Generic.List[object]'
    foreach ($z in $Zeilen) {
        if ($z -match $zeilenMuster) {
            $liste.Add([pscustomobject]@{
                # Tausenderpunkte der deutschen Schreibweise entfernen, sonst wird aus 26.112 die Zahl 26112 nie.
                Pfad  = $Matches[1]
                Bytes = [int64]($Matches[2] -replace '[.,]', '')
                Hash  = $Matches[3].ToLowerInvariant()
            })
        }
    }
    # ⚠ HIER NICHT `@($liste)` SCHREIBEN. Das ist sonst das richtige Idiom - aber auf einer
    # System.Collections.Generic.List[object] WIRFT es unter PS 5.1
    # (ArgumentException "Die Argumenttypen stimmen nicht ueberein"), und zwar unabhaengig
    # vom Inhalt, auch bei LEERER Liste. Gemessen 2026-08-19 auf PS 5.1.19041.6456;
    # List[string], List[psobject], ArrayList und native Arrays sind nicht betroffen.
    # ToArray() gibt ein echtes Array; das @() gehoert an die AUFRUFSTELLE, damit ein
    # Ergebnis mit genau einem Element beim Rueckgeben nicht zum Skalar entrollt.
    return $liste.ToArray()
}

if ($Selbsttest) {
    $fehler = 0

    # Positivkontrolle 1 - der Parser MUSS eine echte Tabellenzeile zerlegen.
    $probeZeile = '| `ThirdParty\net40\X.dll` | 1.234 | `' + ('a' * 64) + '` |'
    # @() an der AUFRUFSTELLE - siehe Hinweis in Lies-Sollwerte, warum nicht drinnen.
    $geparst = @(Lies-Sollwerte @($probeZeile, '| Datei | Bytes | SHA-256 |', '|---|---:|---|', '| Projekt | https://example.invalid |'))
    if ($geparst.Count -eq 1 -and $geparst[0].Bytes -eq 1234 -and $geparst[0].Pfad -eq 'ThirdParty\net40\X.dll') {
        Write-Host "  Self-test 1: table parser reads a row and ignores header and other tables." -ForegroundColor Green
    } else {
        Write-Host ("  SELF-TEST 1 FAILED: {0} row(s) parsed, expected exactly 1." -f $geparst.Count) -ForegroundColor Red
        $fehler++
    }

    # Positivkontrolle 2 - eine verfaelschte Pruefsumme MUSS als Abweichung gelten.
    $echt = 'a' * 64
    $verfaelscht = ('b' + ('a' * 63))
    if ($echt -ne $verfaelscht) {
        Write-Host "  Self-test 2: a single flipped hex digit counts as a mismatch." -ForegroundColor Green
    } else {
        Write-Host "  SELF-TEST 2 FAILED: comparison does not see the difference." -ForegroundColor Red
        $fehler++
    }

    # Positivkontrolle 3 - Get-FileHash MUSS fuer einen bekannten Inhalt den bekannten Wert geben.
    # "abc" -> ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad (SHA-256, FIPS 180-4).
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("scriptone-shatest-{0}.bin" -f $PID)
    try {
        [IO.File]::WriteAllBytes($tmp, [byte[]](0x61, 0x62, 0x63))
        $ist = (Get-FileHash -Path $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($ist -eq 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad') {
            Write-Host "  Self-test 3: SHA-256 of a known input matches the published value." -ForegroundColor Green
        } else {
            Write-Host ("  SELF-TEST 3 FAILED: got {0}" -f $ist) -ForegroundColor Red
            $fehler++
        }
    } finally { if (Test-Path $tmp) { Remove-Item $tmp -Force } }

    if ($fehler -gt 0) { Write-Host ""; Write-Host "  Self-test failed - the results below would not be trustworthy." -ForegroundColor Red; exit 1 }
}

if (-not (Test-Path $sollDatei)) {
    Write-Host ""
    Write-Host ("  ABORT: expected values not found: {0}" -f $sollDatei) -ForegroundColor Red
    Write-Host "  This file holds the only copy of the expected values. Without it nothing can be checked."
    exit 1
}

$soll = @(Lies-Sollwerte ([IO.File]::ReadAllLines($sollDatei, [Text.Encoding]::UTF8)))

# ⚠ Null Eintraege heisst NICHT "nichts zu beanstanden" - es heisst, die Tabelle wurde nicht
# gelesen. Ohne diesen Abbruch meldete das Skript danach fehlerfrei ueber null Dateien.
if ($soll.Count -eq 0) {
    Write-Host ""
    Write-Host ("  ABORT: no entries parsed from {0}." -f (Resolve-Path $sollDatei).Path) -ForegroundColor Red
    Write-Host "  The table format changed, or the file was rewritten. Expected rows of the form:"
    Write-Host '    | `path\to\file.dll` | 26.112 | `<64 hex digits>` |'
    exit 1
}

Write-Host ""
Write-Host ("  {0} expected entries read from Standalone\SHASUMS.md" -f $soll.Count)
Write-Host ""

$befunde = New-Object 'System.Collections.Generic.List[string]'

foreach ($e in $soll) {
    $p = Join-Path $Wurzel $e.Pfad

    if (-not (Test-Path $p)) {
        Write-Host ("  MISSING   {0}" -f $e.Pfad) -ForegroundColor Red
        $befunde.Add(("{0}: listed in SHASUMS.md but not present in the working tree" -f $e.Pfad))
        continue
    }

    $istBytes = (Get-Item $p).Length
    $istHash  = (Get-FileHash -Path $p -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($istBytes -ne $e.Bytes) {
        Write-Host ("  SIZE      {0}" -f $e.Pfad) -ForegroundColor Red
        Write-Host ("              expected {0} bytes, found {1}" -f $e.Bytes, $istBytes) -ForegroundColor Red
        $befunde.Add(("{0}: size {1} instead of {2}" -f $e.Pfad, $istBytes, $e.Bytes))
        continue
    }

    if ($istHash -ne $e.Hash) {
        Write-Host ("  CHANGED   {0}" -f $e.Pfad) -ForegroundColor Red
        Write-Host ("              expected {0}" -f $e.Hash) -ForegroundColor Red
        Write-Host ("              found    {0}" -f $istHash) -ForegroundColor Red
        $befunde.Add(("{0}: SHA-256 differs" -f $e.Pfad))
        continue
    }

    Write-Host ("  OK        {0,-52} {1,9} bytes" -f $e.Pfad, $istBytes) -ForegroundColor Green
}

Write-Host ""
if ($befunde.Count -eq 0) {
    Write-Host ("  All {0} third-party binaries are unchanged." -f $soll.Count) -ForegroundColor Green
    Write-Host "  NOTE[UNVERIFIED]: this proves they did not change since they were recorded." -ForegroundColor Gray
    Write-Host "  It does NOT prove they match the official upstream release - that needs a" -ForegroundColor Gray
    Write-Host "  download and is documented as open in Standalone\SHASUMS.md." -ForegroundColor Gray
    exit 0
}

Write-Host ("  {0} FINDING(S):" -f $befunde.Count) -ForegroundColor Red
$befunde | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
Write-Host ""
Write-Host "  A third-party binary must never be rebuilt, renamed or obfuscated - for UnityDoorstop"
Write-Host "  that is what the LGPL exception rests on. If the change was deliberate, update the"
Write-Host "  table in Standalone\SHASUMS.md; that file is the single source of the expected values."
exit 1
