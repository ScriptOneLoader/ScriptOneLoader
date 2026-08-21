<#
    Measure-Visibility.ps1 - gewinnt die Sichtbarkeit aus Il2Cpp-Proxies zurueck und MISST,
    ob sie stimmt.

    WARUM ES DAS GIBT
    Die Auswahl der s1-Flaeche steht und faellt mit "war das Member im Original public?".
    Im Mono-Satz steht das in den Metadaten. Im Il2Cpp-Proxysatz ist es GEMESSEN verloren:
    ueber dieselben 175 Singleton-Typen sind unter Mono 1842 von 3409 Methoden public (54 %),
    im Proxysatz 8185 von 8360 (98 %) - Il2CppInterop macht praktisch alles public, und kein
    Attribut traegt die urspruengliche Angabe.

    ⚠ SIE IST ABER NICHT WEG. Il2CppInterop legt je Methode ein Feld an, dessen NAME sie
    mitfuehrt:

        NativeMethodInfoPtr_SetWeather_Public_Abstract_Virtual_New_Void_String_0
                            ^Name      ^Zugriff ^Modifikatoren    ^Rueckgabe ^Param ^Index

    Der Zugriff ist das erste Token aus einer geschlossenen Wortliste. Genau daraus wird hier
    die Wahrheit zurueckgewonnen - und gegen den Mono-Satz GEMESSEN statt geglaubt.

    ⚠ EIN NAMENSSCHEMA IST KEIN VERTRAG. Es ist ein Implementierungsdetail von Il2CppInterop
    und kann sich mit dessen Fassung aendern. Deshalb ist dieses Skript kein einmaliger Beweis,
    sondern eine WIEDERHOLBARE Messung: wer die Proxy-Erzeugung wechselt, laesst sie neu laufen.
    Fehlt der Mono-Satz, kann nichts gemessen werden - das wird ausdruecklich gemeldet, nicht
    als Erfolg verbucht.

    AUFRUF
        .\tools\Measure-Visibility.ps1 -Mono <Assembly-CSharp.dll> -Proxy <Il2CppAssemblies\>
#>
[CmdletBinding()]
param(
    [string] $Mono,
    [string] $Proxy,
    [string] $Cecil,
    [int]    $Beispiele = 5,
    # ⚠ Die Zahl ueber ALLE Methoden beantwortet die falsche Frage. Fuer die Flaeche zaehlen
    #   nur die Typen, die auch Tabellen werden. Mit -NurTabellen <abnahme.txt> wird genau
    #   darauf eingeschraenkt - eine Abweichung ausserhalb kostet uns nichts.
    [string] $NurTabellen
)
$ErrorActionPreference = 'Stop'

function Sag($t, $f = 'Gray') { Write-Host "  $t" -ForegroundColor $f }

if (-not $Cecil) {
    foreach ($k in @('C:\Steam\steamapps\common\Schedule I\MelonLoader\net35\Mono.Cecil.dll',
                     'C:\Steam\steamapps\common\Schedule I\MelonLoader\net6\Mono.Cecil.dll')) {
        if (Test-Path $k) { $Cecil = $k; break }
    }
}
if (-not $Cecil -or -not (Test-Path $Cecil)) {
    Write-Output "  HINWEIS[UNGEPRUEFT] Mono.Cecil not found - cannot measure."
    exit 2
}
Add-Type -Path $Cecil

if (-not (Test-Path $Mono) -or -not (Test-Path $Proxy)) {
    Write-Output "  HINWEIS[UNGEPRUEFT] need BOTH a Mono assembly and a proxy folder to compare."
    exit 2
}

# ---- Die geschlossene Wortliste. Alles andere im Namen ist Name, Modifikator, Typ oder Index.
$zugriffe = @('Public', 'Private', 'Protected', 'Internal', 'ProtectedInternal', 'PrivateProtected',
              'FamORAssem', 'FamANDAssem', 'Assembly', 'Family')

# Beide Seiten auf dieselbe Schreibweise bringen: der Proxysatz stellt jedem Namensraum
# 'Il2Cpp' voran, und geschachtelte Typen trennt Cecil mit '/'.
function Normal([string] $voll) {
    $v = $voll -replace '/', '+'
    if ($v.StartsWith('Il2Cpp')) { $v = $v.Substring(6) }
    if ($v.StartsWith('.')) { $v = $v.Substring(1) }
    return $v
}

function Lies-Zugriff([string] $feldname) {
    # NativeMethodInfoPtr_<Name...>_<Zugriff>_<Rest...>
    if (-not $feldname.StartsWith('NativeMethodInfoPtr_')) { return $null }
    $rest = $feldname.Substring('NativeMethodInfoPtr_'.Length)
    $teile = $rest.Split('_')
    for ($i = 0; $i -lt $teile.Length; $i++) {
        if ($zugriffe -contains $teile[$i]) {
            return @{ Name = ($teile[0..($i-1)] -join '_'); Zugriff = $teile[$i] }
        }
    }
    return $null
}

# ---- Die Typen, auf die es ankommt (optional).
$nurDiese = $null
if ($NurTabellen -and (Test-Path $NurTabellen)) {
    $nurDiese = @{}
    foreach ($z in (Get-Content $NurTabellen)) {
        if ($z -notmatch '^[^#|]+\|[^|]+\|(S1[IT]_)?([A-Za-z0-9_]+)\.') { continue }
        $nurDiese[$Matches[2]] = $true
    }
    Write-Host ("  restricted to {0} surface types" -f $nurDiese.Count) -ForegroundColor Cyan
}

# ---- Mono: die Wahrheit.
# ⚠ SCHLUESSEL AUS DEM VOLLEN NAMEN, und NUR eindeutige Namen vergleichen. Die erste
#   Fassung nahm den EINFACHEN Typnamen und den Methodennamen - damit trafen zwei
#   verschiedene Typen mit gleichem Kurznamen aufeinander (UIPopupScreen_ConfirmationMenu
#   gegen einen gleichnamigen anderswo) und UEBERLADUNGEN ueberschrieben einander in der
#   Tabelle. Ergebnis waren 85 "Abweichungen", die keine waren: gemessen wurde Verschiedenes.
#   Wo ein Name auf einer der beiden Seiten mehrdeutig ist, wird NICHT geraten, sondern als
#   nicht vergleichbar gezaehlt und ausgewiesen.
$mAsm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Mono)
$wahrheit = @{}
$mehrdeutig = @{}
foreach ($t in $mAsm.MainModule.GetTypes()) {
    foreach ($m in $t.Methods) {
        $k = (Normal $t.FullName) + '::' + $m.Name
        if ($wahrheit.ContainsKey($k)) { $mehrdeutig[$k] = $true }
        else { $wahrheit[$k] = [bool]$m.IsPublic }
    }
}
Sag ("mono truth : {0} methods over {1} types" -f $wahrheit.Count, @($mAsm.MainModule.GetTypes()).Count)

# ---- Proxy: die Rueckgewinnung.
$gleich = 0; $abweichung = 0; $ohneMarke = 0; $nichtInMono = 0; $unklar = 0
# ⚠ NICHT $beispiele nennen: PowerShell-Variablennamen sind CASE-INSENSITIV, der
#   Parameter [int]$Beispiele waere dieselbe Variable - die Zuweisung scheitert dann
#   an seiner Typbindung, und zwar mit einer Meldung ueber List`1 statt ueber den Namen.
$proben = New-Object System.Collections.Generic.List[string]

foreach ($datei in (Get-ChildItem $Proxy -Filter '*.dll' -File)) {
    $pAsm = $null
    try { $pAsm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($datei.FullName) } catch { continue }
    foreach ($t in $pAsm.MainModule.GetTypes()) {
        foreach ($f in $t.Fields) {
            $g = Lies-Zugriff $f.Name
            if (-not $g) { continue }
            if ($nurDiese -and -not $nurDiese.ContainsKey($t.Name)) { continue }
            $schluessel = (Normal $t.FullName) + '::' + $g.Name
            if ($mehrdeutig.ContainsKey($schluessel)) { $unklar++; continue }
            if (-not $wahrheit.ContainsKey($schluessel)) { $nichtInMono++; continue }
            $sollPublic = $wahrheit[$schluessel]
            $istPublic  = ($g.Zugriff -eq 'Public')
            if ($sollPublic -eq $istPublic) { $gleich++ }
            else {
                $abweichung++
                if ($proben.Count -lt $Beispiele) {
                    $proben.Add(("{0}  mono={1}  proxy={2}" -f $schluessel, $sollPublic, $g.Zugriff))
                }
            }
        }
        foreach ($f in $t.Fields) {
            if ($f.Name.StartsWith('NativeFieldInfoPtr_')) { $ohneMarke++ }
        }
    }
    $pAsm.Dispose()
}
$mAsm.Dispose()

Sag ""
Sag ("recovered  : {0} methods matched, {1} deviating" -f $gleich, $abweichung) `
    $(if ($abweichung -eq 0) { 'Green' } else { 'Red' })
Sag ("not in mono: {0} (proxy-only types - generics, compiler helpers)" -f $nichtInMono)
Sag ("ambiguous  : {0} (overloads - name alone does not identify the method)" -f $unklar)
Sag ("⚠ field pointers carry NO access marker: {0} of them" -f $ohneMarke) 'Yellow'
foreach ($b in $proben) { Sag ("    " + $b) 'Red' }

Sag ""
if ($abweichung -eq 0 -and $gleich -gt 0) {
    Write-Host ("  visibility can be recovered from proxy field names: {0} of {0} correct." -f $gleich) -ForegroundColor Green
    exit 0
}
if ($gleich -eq 0) {
    Write-Host "  NOTHING MEASURED - no method pointer matched a mono method. Check the inputs." -ForegroundColor Red
    exit 1
}
Write-Host ("  {0} deviations - the name scheme does not carry the truth here." -f $abweichung) -ForegroundColor Red
exit 1
