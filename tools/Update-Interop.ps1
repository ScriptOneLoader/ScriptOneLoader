<#
    Update-Interop.ps1 - erzeugt die Il2Cpp-Proxy-Assemblies neu.

    Das ist der Weg aus der teuersten Falle des Standalone-Zweigs: nach einem Spiel-Update
    passen die Proxies nicht mehr, und das meldet von selbst NICHTS. Der Bau bleibt gruen
    (er uebersetzt gegen denselben Ordner, der auch laeuft) und zur Laufzeit fliegt keine
    Ausnahme - Methoden loesen ueber einen einbetonierten Metadaten-Token auf, und ein toter
    Token gibt eine Attrappe zurueck statt zu werfen.

    Das Skript ist nur die Huelle; die Arbeit macht tools\InteropGen (Cpp2IL +
    Il2CppInterop.Generator, beide als NuGet-Paket, nichts wird heruntergeladen ausser
    ueber den normalen Paketwiederherstellungsschritt).

    BEISPIELE
        .\tools\Update-Interop.ps1 -Check      # nur nachsehen
        .\tools\Update-Interop.ps1             # erzeugen, falls noetig
        .\tools\Update-Interop.ps1 -Force      # in jedem Fall erzeugen
        .\tools\Update-Interop.ps1 -Stamp      # vorhandenen Satz als aktuell vermerken
#>
[CmdletBinding()]
param(
    [string] $Spiel = 'C:\Steam\steamapps\common\Schedule I',
    [string] $Ziel,
    [switch] $Check,
    [switch] $Stamp,
    [switch] $Force
)
trap { Write-Host "  ABORT: $($_.Exception.Message)" -ForegroundColor Red; exit 1 }
$ErrorActionPreference = 'Stop'

if (-not $Ziel) { $Ziel = Join-Path $Spiel 'ScriptOne\interopgenerator' }
$werkzeug = Join-Path $PSScriptRoot 'InteropGen'
# WARNUNG: Das Zielframework wird AUS DER CSPROJ GELESEN, nicht verdrahtet. Hier stand fest
#   'net8.0', waehrend das Projekt seit 0d7bbe8 auf net6.0 steht - und weil der ALTE
#   net8.0-Bau im gitignorierten bin\ liegen blieb, schlug 'Test-Path' an: der Wrapper baute
#   nie neu und fuhr klaglos ein Binaer von VOR der Umstellung. Ein stehengebliebenes bin\
#   verdeckt so einen Fehler unbegrenzt - er meldet sich nicht, er liefert nur veraltete
#   Ergebnisse. Gemessen 2026-08-19: net8.0-Bau vom Vortag, net6.0-Bau vom selben Tag.
$csproj = Join-Path $werkzeug 'InteropGen.csproj'
$tfm = ([regex]::Match((Get-Content $csproj -Raw), '<TargetFramework>([^<]+)</TargetFramework>')).Groups[1].Value
if (-not $tfm) {
    Write-Host "  ABORT: no <TargetFramework> in $csproj" -ForegroundColor Red
    exit 1
}
$exe      = Join-Path $werkzeug ('bin\Release\' + $tfm + '\InteropGen.exe')

if (-not (Test-Path $exe)) {
    Write-Host "  InteropGen has not been built yet - building it now:" -ForegroundColor Yellow
    & dotnet build $werkzeug -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "  Build failed." -ForegroundColor Red; exit 1 }
}

# ⚠ Das Spiel darf nicht laufen: die Proxies liegen im selben Ordner, aus dem der Wirt
# sie geladen hat, und Windows sperrt geladene DLLs.
$laeuft = @(Get-Process -Name 'Schedule I' -ErrorAction SilentlyContinue)
if ($laeuft.Count -gt 0 -and -not $Check) {
    Write-Host "  ABORT: the game is running ($($laeuft.Count) process). Close it first." -ForegroundColor Red
    exit 1
}

$argumente = @('--game', $Spiel, '--out', $Ziel)
if ($Check) { $argumente += '--check' }
if ($Stamp) { $argumente += '--stamp' }
if ($Force) { $argumente += '--force' }

& $exe @argumente
$code = $LASTEXITCODE

if ($code -eq 0 -and -not $Check -and -not $Stamp) {
    # Zusicherung statt Zuversicht: der Erfolg des Werkzeugs allein genuegt nicht, der
    # Ordner muss die Nutzlast NAMENTLICH tragen.
    $pflicht = @('Assembly-CSharp.dll', 'Il2Cppmscorlib.dll', 'Il2CppScheduleOne.Core.dll', 'UnityEngine.CoreModule.dll')
    $fehlt = @($pflicht | Where-Object { -not (Test-Path (Join-Path $Ziel $_)) })
    if ($fehlt.Count) {
        Write-Host "  FAILED - these assemblies are missing from the result:" -ForegroundColor Red
        $fehlt | ForEach-Object { Write-Host "    $_" }
        exit 1
    }
    $n = @(Get-ChildItem $Ziel -Filter *.dll -File).Count
    Write-Host "  $n assemblies, all required files present." -ForegroundColor Green
    Write-Host "  Now rebuild and deploy:"
    Write-Host "    .\Standalone\Install-Standalone.ps1 -Modus Standalone"
}
exit $code
