<#
    Check-Steuerzeichen.ps1 - findet ausgefuehrte Backslash-Escapes im Textbestand.

    WARUM ES DAS GIBT
    Dreimal in diesem Repo wurde beim Schreiben aus `\b`, `\v` oder `\n` das echte
    STEUERZEICHEN - mitten in einem Pfad, der danach nicht mehr existiert:

        Ordner<0x08>ackup\        statt  Ordner\backup\
        Pfad\MelonLoader<0x0a>et472\  statt  ...\net472\

    Beim vierten Mal traf es ausgerechnet den Eintrag, der die ersten drei BESCHREIBT.
    Im gerenderten Markdown bricht der Codespan auf, in einem Skript wird die Anweisung
    unbrauchbar - und keine Pruefung im Bestand sah das, weil es Text ist.

    Ein Zeilenumbruch aus `\n` ist per Konstruktion NICHT erkennbar (er ist von einem
    echten Umbruch nicht zu unterscheiden). Erkennbar sind die uebrigen: 0x08, 0x0b, 0x0c
    und 0x1b. Genau die deckt dieser Pruefer ab - und sagt das hier, damit niemand ihn
    fuer vollstaendig haelt.

    AUFRUF
        .\tools\Check-Steuerzeichen.ps1
        .\tools\Check-Steuerzeichen.ps1 -Selbsttest
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

$verboten = @{ 8 = '\b (Backspace)'; 11 = '\v (Vertical Tab)'; 12 = '\f (Form Feed)'; 27 = '\e (Escape)' }

Push-Location $Wurzel
try {
    if ($Selbsttest) {
        # Positivkontrolle: an einer kuenstlichen Zeichenkette MUSS er anschlagen.
        $probe = "ScriptOne{0}ackup" -f [char]8
        $n = @($probe.ToCharArray() | Where-Object { $verboten.ContainsKey([int]$_) }).Count
        if ($n -eq 1) { Write-Host "  Self-test: an injected 0x08 is detected." -ForegroundColor Green }
        else { Write-Host "  SELF-TEST FAILED ($n instead of 1)." -ForegroundColor Red; exit 1 }
    }

    $dateien = @(git ls-files | Where-Object { $_ -notmatch '\.(dll|exe|png|jpg|jpeg|zip|ico)$' })
    $treffer = New-Object 'System.Collections.Generic.List[string]'

    foreach ($d in $dateien) {
        $p = Join-Path $Wurzel $d
        if (-not (Test-Path $p)) { continue }
        # Byteweise lesen: eine Kodierungsannahme wuerde genau die Zeichen verschlucken,
        # um die es geht.
        $bytes = [IO.File]::ReadAllBytes($p)
        foreach ($code in $verboten.Keys) {
            $i = [Array]::IndexOf($bytes, [byte]$code)
            if ($i -lt 0) { continue }
            # Zeile bestimmen: Zeilenumbrueche bis zur Fundstelle zaehlen.
            $zeile = 1
            for ($k = 0; $k -lt $i; $k++) { if ($bytes[$k] -eq 10) { $zeile++ } }
            $treffer.Add(("{0}:{1}  {2} an Byte {3}" -f $d, $zeile, $verboten[$code], $i))
        }
    }

    Write-Host ""
    Write-Host ("  {0} files checked for 0x08 / 0x0b / 0x0c / 0x1b" -f $dateien.Count)
    Write-Host "  (an executed \n is by construction NOT detectable - see the header)" -ForegroundColor Gray

    if ($treffer.Count -eq 0) {
        Write-Host "  No executed escapes." -ForegroundColor Green
        exit 0
    }
    Write-Host ""
    Write-Host ("  {0} CONTROL CHARACTER(S) in text:" -f $treffer.Count) -ForegroundColor Red
    $treffer | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "  The cause is almost always a generator script assembling the text in a language"
    Write-Host "  with backslash escapes. Remedy: build the backslash from a character code"
    Write-Host "  (PowerShell [char]92, Python chr(92)) instead of doubling it."
    exit 1
}
finally { Pop-Location }
