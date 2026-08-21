<#
    Check-Installation.ps1 - laesst den Installer gegen NACHGEBAUTE Spielordner laufen.

    WARUM ES DAS GIBT
    Nach der Ordner-Umbenennung stand in der Zeile, die doorstop_config.ini SCHREIBT,
    weiter der alte Pfad:

        target_assembly=ScriptOne\<ALTER-NAME>\$tfm\ScriptOne.Preloader.dll
                                    ^ der Ordner hiess inzwischen anders

    Bau, Tests und Harness blieben gruen - es ist eine Zeichenkette. Aufgefallen ist es
    beim Lesen, nicht beim Pruefen. Ein grep-Pruefer (Check-Ordnernamen.ps1) faengt den
    Namen; dieser hier faengt die KLASSE: er fragt nicht, wie die Zeile aussieht, sondern
    ob der erzeugte Pfad auf eine Datei zeigt, die es gibt.

    WAS ER NICHT KANN
    Er startet kein Spiel. Ob der Wirt danach wirklich anlaeuft - besonders im
    Mono-Zweig, fuer den hier keine Installation existiert - sagt er NICHT. Er prueft die
    Installation, nicht den Lauf.

    AUFRUF
        .\tools\Check-Installation.ps1
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

$installer = Join-Path $Wurzel 'Standalone\Install-Standalone.ps1'
$stage     = Join-Path $Wurzel 'Standalone\stage\ScriptOne\core-runtime'
$fehler    = 0

# Positivkontrolle. Die tragende Zusicherung dieses Pruefers ist "der erzeugte Pfad zeigt
# auf eine Datei, DIE ES GIBT" - also muss belegt sein, dass er einen Pfad ins Leere auch
# als solchen erkennt. Ohne das weiss man nur, dass er schweigt.
if ($Selbsttest) {
    $d = Join-Path ([IO.Path]::GetTempPath()) ("scriptone-selbsttest-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    try {
        $da   = Join-Path $d 'gibt-es.dll'
        New-Item -ItemType File -Path $da -Force | Out-Null
        # Kein literaler Pfad mit Backslash: beim Erzeugen dieser Datei wurde daraus
        # schon einmal ein Tabulator. Join-Path setzt die Trenner selbst.
        $fehlt = Join-Path (Join-Path $d 'gibt-es-nicht') 'weg.dll'
        if ((Test-Path $da) -and -not (Test-Path $fehlt)) {
            Write-Host "  Self-test: an existing target passes, a dangling one does not." -ForegroundColor Green
        } else {
            Write-Host "  SELF-TEST FAILED: the existence check does not distinguish the two cases." -ForegroundColor Red
            exit 1
        }
    }
    finally { if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue } }
}

if (-not (Test-Path $stage)) {
    Write-Host "  SKIPPED: stage\ is missing - build it first:" -ForegroundColor Yellow
    Write-Host ("    dotnet publish Standalone/Preloader/ScriptOne.Preloader.csproj -c Release -f net6.0  -o Standalone/stage/ScriptOne/core-runtime/net6")
    Write-Host ("    dotnet publish Standalone/Preloader/ScriptOne.Preloader.csproj -c Release -f net472 -o Standalone/stage/ScriptOne/core-runtime/net472")
    # ⚠ Exitcode 0 waere hier falsch: "konnte nicht pruefen" ist kein "geprueft".
    exit 2
}

# Zwei Attrappen. Das Backend erkennt der Installer an GameAssembly.dll bzw. *_Data\Managed\.
$faelle = @(
    @{ Name = 'Il2Cpp'; Tfm = 'net6';   Bauen = { param($d) New-Item -ItemType File -Path (Join-Path $d 'GameAssembly.dll') -Force | Out-Null } },
    @{ Name = 'Mono';   Tfm = 'net472'; Bauen = { param($d) New-Item -ItemType File -Path (Join-Path $d 'FakeGame_Data\Managed\Assembly-CSharp.dll') -Force | Out-Null } }
)

foreach ($f in $faelle) {
    $d = Join-Path ([IO.Path]::GetTempPath()) ("scriptone-probe-" + $f.Name)
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    & $f.Bauen $d

    Write-Host ""
    Write-Host ("  --- {0} ---" -f $f.Name)
    try {
        $aus = & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Modus Standalone -Spiel $d 2>&1
        $cfg = Join-Path $d 'doorstop_config.ini'
        if (-not (Test-Path $cfg)) {
            Write-Host "    NO doorstop_config.ini was written" -ForegroundColor Red; $fehler++; continue
        }

        $zeilen = [IO.File]::ReadAllLines($cfg)
        $ta  = ($zeilen | Where-Object { $_ -like 'target_assembly=*' }) -replace '^target_assembly=', ''
        $clr = ($zeilen | Where-Object { $_ -like 'coreclr_path=*'    }) -replace '^coreclr_path=', ''

        # 1. Der Zweig muss zum erkannten Backend passen.
        if ($ta -notmatch [regex]::Escape("\$($f.Tfm)\")) {
            Write-Host ("    WRONG BRANCH: {0} (expected \{1}\)" -f $ta, $f.Tfm) -ForegroundColor Red; $fehler++
        }

        # 2. Der Pfad muss auf eine Datei zeigen, DIE ES GIBT. Das ist der Kern.
        $ziel = Join-Path $d $ta
        if (Test-Path $ziel) {
            Write-Host ("    target_assembly -> {0}  ({1:N0} B)" -f $ta, (Get-Item $ziel).Length) -ForegroundColor Green
        } else {
            Write-Host ("    target_assembly POINTS NOWHERE: {0}" -f $ta) -ForegroundColor Red
            Write-Host  "    The host would not start at all - Doorstop cannot find the assembly." -ForegroundColor Red
            $fehler++
        }

        # 3. Nur Il2Cpp braucht CoreCLR.
        if ($f.Tfm -eq 'net6') {
            if ($clr -and (Test-Path $clr)) { Write-Host ("    coreclr_path    -> present") -ForegroundColor Green }
            else { Write-Host ("    coreclr_path POINTS NOWHERE: {0}" -f $clr) -ForegroundColor Red; $fehler++ }
        }

        # 4. Die Lizenztexte sind Pflicht, nicht Komfort.
        $liz = Join-Path $d 'ScriptOne\licenses'
        $n = @(Get-ChildItem $liz -File -ErrorAction SilentlyContinue).Count
        if ($n -gt 0) { Write-Host ("    licenses\       -> {0} file(s)" -f $n) -ForegroundColor Green }
        else { Write-Host "    NO license texts were installed" -ForegroundColor Red; $fehler++ }
    }
    finally {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ""
if ($fehler -eq 0) {
    Write-Host "  Both branches: the generated configuration points at files that exist." -ForegroundColor Green
    Write-Host "  (Whether the host then RUNS is not what this checker says - it starts no game.)" -ForegroundColor Gray
    exit 0
}
Write-Host ("  {0} finding(s)." -f $fehler) -ForegroundColor Red
exit 1
