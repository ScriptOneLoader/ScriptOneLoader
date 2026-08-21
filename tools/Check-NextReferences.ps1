<#
    Check-NextReferences.ps1 - prueft jede Fundstelle in NEXT.md.

    WARUM ES DAS GIBT
    Ein Doku-Audit fand am 2026-08-18 drei Fundstellen in NEXT.md, die auf eine LEERZEILE
    oder einen reinen Trennkommentar zeigten. Das ist schlimmer als gar keine Angabe: sie
    sieht geprueft aus, und wer ihr folgt, haelt den Punkt fuer falsch statt die Angabe.
    Zeilennummern wandern bei JEDER Aenderung - eine Fundstelle ist deshalb kein Fakt,
    sondern eine Behauptung mit Verfallsdatum. Noch am selben Tag wanderten zwei weitere,
    weil im selben Commit Code darueber eingefuegt wurde.

    GEPRUEFT WIRD
      1. Existiert die Datei?
      2. Hat sie so viele Zeilen?
      3. Steht dort SUBSTANZ - keine Leerzeile, kein Kommentar, kein Trenner?

    Punkt 3 hat ZWEI Anlaeufe gebraucht:
      - Fassung 1 liess "<!-- ===== MONO-Referenzen ===== -->" durch, weil Worte drinstanden.
      - Fassung 2 liess eine Zeile MITTEN IN einem mehrzeiligen <!-- --> durch, weil sie
        weder mit <!-- begann noch mit --> endete. Ein Kommentarzustand ueber die Datei ist
        also noetig; eine Regex je Zeile reicht nicht.

    Exitcode 0 = alle tragen, 1 = mindestens eine nicht (oder die Pruefung selbst scheiterte).
#>
[CmdletBinding()]
param(
    [string] $Next,
    [switch] $Quiet,
    [switch] $Selbsttest
)
trap { Write-Host "  ABORT (the check itself failed): $($_.Exception.Message)" -ForegroundColor Red; exit 1 }
$ErrorActionPreference = 'Stop'

# Pfadermittlung mit Rueckfallebenen.
# ⚠ KORREKTUR (2026-08-18): hier stand, $PSScriptRoot sei bei 'powershell -File <relativer
# Pfad>' LEER - das ist WIDERLEGT. Nachgemessen mit einer Sonde ueber alle drei Aufrufformen
# (-File relativ, -File absolut, -Command): $PSScriptRoot ist ueberall gesetzt und ein
# Join-Path im Parameter-Vorgabewert loest ueberall korrekt auf.
# Ein Lauf ist einmal an Join-Path mit leerer Zeichenkette gescheitert; die URSACHE ist
# ungeklaert und war nicht reproduzierbar. Die Rueckfallebenen bleiben - sie kosten nichts
# und ein Pruefer, der an der eigenen Pfadermittlung stirbt, meldet gar nichts. Aber sie
# stehen hier als VORSORGE, nicht als Abhilfe gegen ein belegtes Verhalten.
$hier = $PSScriptRoot
if ([string]::IsNullOrEmpty($hier) -and $MyInvocation.MyCommand.Path) {
    $hier = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrEmpty($hier)) { $hier = (Get-Location).Path }
if ([string]::IsNullOrEmpty($Next)) { $Next = Join-Path $hier '..' | Join-Path -ChildPath 'NEXT.md' }

# ⚠ IN EINEM FRISCHEN KLON GIBT ES NEXT.md GAR NICHT MEHR. Die Datei ist seit dem 2026-08-21
#   ausdruecklich NICHT SICHTBAR (siehe den Block am Ende der .gitignore) - sie ist der interne
#   Arbeitsvorrat und geht niemanden ausserhalb an. Fuer diesen Pruefer ist ihr Fehlen deshalb
#   kein Fehler, sondern schlicht "nichts zu pruefen". Als Exitcode 1 haette er jeden fremden
#   Klon rot gemacht, und ein Pruefer, den man routinemaessig rot sieht, wird nicht mehr gelesen.
#   Write-Output, nicht Write-Host: Stream 6 faengt ein Aufrufer mit 2>&1 NICHT.
if (-not (Test-Path $Next)) {
    Write-Output "  HINWEIS[UNGEPRUEFT] no $(Split-Path $Next -Leaf) here - it is an internal file and not part of a clone."
    Write-Output "  Nothing to verify; this run checked nothing."
    exit 2
}
$Next = (Resolve-Path $Next).Path
$repo = Split-Path $Next -Parent
$text = Get-Content $Next -Raw -Encoding UTF8

# `pfad/zur/datei.ext:123`
$muster = [regex]'`([A-Za-z0-9_./\\-]+\.(?:cs|csproj|ps1|ini|md|json|lua)):(\d+)`'
$treffer = $muster.Matches($text)

if ($treffer.Count -eq 0) {
    # ⚠ NICHT ROT UND NICHT GRUEN. "Es gibt gerade keine Fundstelle" ist derselbe Zustand
    #   wie in Check-Paket, wenn der Pruefgegenstand fehlt - und dort traegt ihn der stabile
    #   Marker HINWEIS[UNGEPRUEFT] plus Exitcode 2, nicht ein Fehlschlag. Als Exit 1 blockierte
    #   dieser Pruefer den ganzen Satz fuer einen voellig gesunden Zustand (NEXT.md ohne
    #   Zeilenverweise), und wer einen Pruefer routinemaessig rot sieht, liest ihn irgendwann
    #   nicht mehr. Write-Output, nicht Write-Host: Stream 6 faengt ein Aufrufer mit 2>&1 NICHT.
    Write-Output "  HINWEIS[UNGEPRUEFT] no file:line reference in $Next - nothing to verify."
    Write-Output "  Either that is correct, or the pattern no longer matches - this run checked nothing."
    exit 2
}

<#  Zeilen, die in einem MEHRZEILIGEN Kommentar liegen - je Datei einmal ermittelt.
    Erfasst <!-- --> (XML/Markdown) und /* */ (C#). Zeilenweise Regex reicht nicht:
    die mittlere Zeile eines Blocks sieht wie normaler Text aus. #>
function Get-KommentarZeilen([string[]] $zeilen) {
    $drin = New-Object 'System.Collections.Generic.HashSet[int]'
    $imBlock = $false
    for ($i = 0; $i -lt $zeilen.Count; $i++) {
        $z = $zeilen[$i]
        if ($imBlock) {
            $drin.Add($i + 1) | Out-Null
            if ($z -match '-->|\*/') { $imBlock = $false }
            continue
        }
        if ($z -match '(<!--|/\*)' -and $z -notmatch '(<!--.*-->|/\*.*\*/)') {
            $imBlock = $true
            $drin.Add($i + 1) | Out-Null
        }
    }
    # ⚠ KOMMA IST PFLICHT. PowerShell LOEST eine zurueckgegebene Sammlung auf: aus dem
    # HashSet wuerde eine Liste einzelner Zahlen (oder $null, wenn leer), und das
    # anschliessende .Contains() liefe auf NULL - der Pruefer stirbt an sich selbst.
    return ,$drin
}

# Positivkontrolle. Ein Pruefer ohne sie belegt nur, dass er schweigt - nicht, dass er
# sehen kann. Geprueft werden die beiden Teile, an denen dieser hier haengt: das Muster,
# das Fundstellen erkennt, und die Kommentarerkennung, die entscheidet, ob eine Zielzeile
# Substanz hat. Beide werden gegen einen Fall gehalten, dessen Antwort feststeht.
if ($Selbsttest) {
    $f = 0

    # 1. Das Muster muss eine echte Fundstelle finden - und eine Zeile ohne Zeilennummer NICHT.
    if ($muster.Matches('siehe `Host/LuaEngine.cs:134` dort').Count -ne 1) {
        Write-Host "  SELF-TEST: the pattern does not find a real reference." -ForegroundColor Red; $f++
    }
    if ($muster.Matches('siehe `Host/LuaEngine.cs` dort').Count -ne 0) {
        Write-Host "  SELF-TEST: the pattern matches something that is not a reference." -ForegroundColor Red; $f++
    }

    # 2. Die Kommentarerkennung muss die MITTLERE Zeile eines Blocks als Kommentar sehen -
    #    genau das, was eine zeilenweise Regex nicht kann.
    $probe = @('code', '<!-- start', 'mitten drin', 'ende -->', 'code')
    $k = Get-KommentarZeilen $probe
    if (-not ($k.Contains(3) -and $k.Contains(4) -and -not $k.Contains(1) -and -not $k.Contains(5))) {
        Write-Host "  SELF-TEST: multi-line comment detection is wrong." -ForegroundColor Red; $f++
    }

    if ($f -gt 0) { exit 1 }
    Write-Host "  Self-test: pattern and multi-line comment detection behave as expected." -ForegroundColor Green
}

$schlecht = 0
$kommentarCache = @{}

foreach ($m in $treffer) {
    $rel = $m.Groups[1].Value
    $nr  = [int] $m.Groups[2].Value
    $pfad = Join-Path $repo ($rel -replace '/', '\')

    if (-not (Test-Path $pfad)) {
        Write-Host "  MISSING   ${rel}:$nr  -> file does not exist" -ForegroundColor Red
        $schlecht++; continue
    }
    $zeilen = @(Get-Content $pfad -Encoding UTF8)
    if ($nr -gt $zeilen.Count) {
        Write-Host "  TOO SHORT ${rel}:$nr  -> file has only $($zeilen.Count) lines" -ForegroundColor Red
        $schlecht++; continue
    }
    if (-not $kommentarCache.ContainsKey($pfad)) { $kommentarCache[$pfad] = Get-KommentarZeilen $zeilen }
    $imBlock = $kommentarCache[$pfad]
    if ($null -eq $imBlock) { $imBlock = New-Object 'System.Collections.Generic.HashSet[int]' }

    $z = $zeilen[$nr - 1].Trim()
    $kurz = if ($z.Length -gt 58) { $z.Substring(0, 58) } else { $z }

    if ([string]::IsNullOrWhiteSpace($z)) {
        Write-Host "  BLANK     ${rel}:$nr" -ForegroundColor Red; $schlecht++
    }
    elseif ($imBlock.Contains($nr)) {
        Write-Host "  IN BLOCK  ${rel}:$nr  -> sits inside a multi-line comment: '$kurz'" -ForegroundColor Red; $schlecht++
    }
    elseif ($z -match '^\s*(<!--.*-->|//.*|#.*|/\*.*\*/)\s*$') {
        Write-Host "  COMMENT   ${rel}:$nr  -> '$kurz'" -ForegroundColor Red; $schlecht++
    }
    elseif ($z -match '^[<>!\-=/*\s]+$') {
        Write-Host "  SEPARATOR ${rel}:$nr" -ForegroundColor Red; $schlecht++
    }
    elseif (-not $Quiet) {
        Write-Host "  ok        ${rel}:$nr  -> $kurz"
    }
}

Write-Host ""
if ($schlecht -gt 0) {
    Write-Host "  $($treffer.Count) references, $schlecht UNUSABLE" -ForegroundColor Red
    exit 1
}
Write-Host "  $($treffer.Count) references, all of them hold." -ForegroundColor Green
exit 0
