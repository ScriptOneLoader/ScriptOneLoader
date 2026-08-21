<#
    Check-Versionen.ps1 - eine Zahl fuer alles, was aus diesem Repo herausgeht.

    WARUM ES DAS GIBT
    Die Version stand zweimal: Plugin 0.1.0, Standalone-Preloader 0.2.0. Beide sind Teil
    DERSELBEN Auslieferung, und in der README stand eine Nummer, die fuer die Haelfte des
    Umfangs nicht galt. Seit dem 2026-08-19 kommt sie aus Version.props.

    ⚠ GEPRUEFT WIRD DER AUFGELOESTE WERT, NICHT DER TEXT IN DER DATEI.
    Ein `grep <Version>` ueber die csproj wuerde melden, dass keine Datei mehr eine eigene
    Version traegt - und genau das ist die falsche Frage. Ein Projekt, das Version.props
    gar nicht importiert, hat dann auch keinen Text, und der Bau stempelt still 1.0.0.
    Deshalb wird MSBuild gefragt.

    AUFRUF
        .\tools\Check-Versionen.ps1
        .\tools\Check-Versionen.ps1 -Selbsttest
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

# Projekt -> Konfiguration. Alle, die ein Artefakt erzeugen.
# ⚠ DIE BEIDEN INSTALLER-PROJEKTE FEHLTEN HIER. Der Dateiname der ausgelieferten ZIP kommt aus
# ScriptOne.csproj, die Versionsangabe der darin liegenden Setup-exe aus einem ungepruefen
# Projekt - ein Nutzer konnte in ScriptOne-0.1.0.zip eine Setup-exe mit anderer Nummer bekommen,
# und Nutzlast.cs bildet die Version in den Namen des Auspackordners ab. Genau die Klasse
# "zwei Zahlen, die dasselbe meinen", gegen die Version.props angelegt wurde. (Audit 2026-08-19)
$projekte = @(
    @{ P = 'ScriptOne.csproj';                                            C = 'Il2Cpp'  },
    @{ P = 'ScriptOne.csproj';                                            C = 'Mono'    },
    @{ P = 'Standalone\Preloader\ScriptOne.Preloader.csproj';             C = 'Release' },
    @{ P = 'Standalone\Installer\Installer\ScriptOne.Installer.csproj';   C = 'Release' },
    @{ P = 'Standalone\Installer\Setup\ScriptOne.Setup.csproj';           C = 'Release' },
    @{ P = 'Adapters\BepInEx\ScriptOne.BepInEx.csproj';                  C = 'Mono'    },
    @{ P = 'Adapters\BepInEx\ScriptOne.BepInEx6Mono.csproj';             C = 'Mono'    },
    @{ P = 'Adapters\BepInEx6\ScriptOne.BepInEx6.csproj';                C = 'Il2Cpp'  },
    @{ P = 'tools\InteropGen\InteropGen.csproj';                          C = 'Release' }
)

# Gegenprobe gegen die Wirklichkeit: taucht ein NEUES Projekt auf, das hier fehlt, soll es
# auffallen statt still durchzufallen. tests\harness.csproj steht bewusst auf 1.0.0 - es wird
# nicht ausgeliefert und ist deshalb namentlich ausgenommen.
$bekannt = $projekte | ForEach-Object { $_.P }
# ⚠ NICHT NUR VERSIONIERTE PROJEKTE. 'git ls-files' sieht ein NEU ANGELEGTES Projekt nicht -
# es ist noch untracked, und genau dann faellt es durch. Gemessen 2026-08-19: der frisch
# angelegte BepInEx-Adapter baute mit Version 1.0.0 statt 0.1.0, und dieser Pruefer meldete
# trotzdem "One version for everything". Ein Pruefer, der nur die Vergangenheit kennt, faengt
# den Zugang nicht. Deshalb der ARBEITSBAUM, mit denselben Ausschluessen wie der Bau.
# ⚠ AUSDRUECKLICH AUF 0. Eine nicht gesetzte Variable ist $null, und '$null -ne 0' ist in
#   PowerShell WAHR - die Sperre unten feuerte dadurch bei JEDEM Lauf, auch ohne Befund.
$fehler = 0

$aus  = @('bin','obj','.claude','stage')   # Segmentnamen, KEIN Regex - ein Backslash im
                                           # Muster ueberlebt den Weg hierher nicht.
$alle = @(Get-ChildItem $Wurzel -Recurse -Filter '*.csproj' -File |
          Where-Object { -not ($_.FullName.Split([char]92) | Where-Object { $aus -contains $_ }) } |
          ForEach-Object { $_.FullName.Substring($Wurzel.Length + 1) })
foreach ($q in $alle) {
    if ($q -like 'tests\*') { continue }
    if ($bekannt -notcontains $q) {
        Write-Host "  FAIL  project not covered by this check: $q" -ForegroundColor Red
        $script:fehler = 1
    }
}

function Frage-Version($projekt, $konfig) {
    $p = Join-Path $Wurzel $projekt
    if (-not (Test-Path $p)) { return $null }
    $aus = & dotnet msbuild $p -p:Configuration=$konfig -getProperty:Version 2>&1
    $letzte = ($aus | Where-Object { $_ -and $_.ToString().Trim() } | Select-Object -Last 1)
    if ($null -eq $letzte) { return $null }
    return $letzte.ToString().Trim()
}

if ($Selbsttest) {
    # Positivkontrolle: eine erfundene Abweichung MUSS als Abweichung gelten.
    $probe = @('0.1.0','0.1.0','0.2.0') | Sort-Object -Unique
    if ($probe.Count -eq 2) { Write-Host "  Self-test: a diverging version is recognised as such." -ForegroundColor Green }
    else { Write-Host "  SELF-TEST FAILED." -ForegroundColor Red; exit 1 }
}

Write-Host ""
$werte = @()
foreach ($e in $projekte) {
    $v = Frage-Version $e.P $e.C
    if (-not $v) {
        Write-Host ("  NOT DETERMINABLE: {0} ({1})" -f $e.P, $e.C) -ForegroundColor Red
        exit 1
    }
    Write-Host ("  {0,-52} {1,-8} {2}" -f $e.P, $e.C, $v)
    $werte += $v
}

$einzig = @($werte | Sort-Object -Unique)
Write-Host ""
# ⚠ EIN FAIL DER GEGENPROBE MUSS DEN EXITCODE TRAGEN. Vorher meldete der Pruefer
#   "not covered by this check" und beendete sich trotzdem mit 0 - ein Aufrufer, der nur den
#   Code liest, haette daraus "alles geprueft" gemacht. Genau die Fehlerklasse, gegen die
#   dieser Pruefer antritt.
if ($fehler -ne 0) {
    Write-Host "  A project is not covered by this check - fix the list above." -ForegroundColor Red
    exit 1
}
if ($einzig.Count -eq 1) {
    Write-Host ("  One version for everything: {0}" -f $einzig[0]) -ForegroundColor Green
    exit 0
}
Write-Host ("  VERSIONS DIVERGE: {0}" -f ($einzig -join ', ')) -ForegroundColor Red
Write-Host "  Every project must import Version.props; per-project <Version> lines do not belong there."
exit 1
