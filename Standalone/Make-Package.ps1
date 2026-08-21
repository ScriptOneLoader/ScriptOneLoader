<#
    Make-Package.ps1 - erzeugt die beiden Auslieferungsformen.

    ZWEI FORMEN, EIN INHALT
      ScriptOne-Installer.exe   Einzeldownload mit EINGEBETTETER Nutzlast. Doppelklick,
                                Spielordner waehlen (oder finden lassen), installieren.
      (Die ZIP ist entfallen - siehe Abschnitt 4.)
                                laufen lassen. Fuer alle, die keine exe herunterladen wollen.

    Beide benutzen DIESELBE Logik (Standalone\Installer\Core\) - es gibt keinen Weg, auf dem
    sich die beiden unterschiedlich verhalten koennten.

    ⚠ WAS NICHT MITGEHT, und warum:
      * Die Il2Cpp-Proxy-Assemblies. Sie entstehen aus den Spieldateien des NUTZERS und
        duerfen nicht weitergegeben werden. Ohne sie startet der Wirt auf einem Il2Cpp-Spiel
        nicht - das Setup sagt das ausdruecklich.
      * Das .ps1. Es ist Entwicklerwerkzeug.

    AUFRUF
        .\Standalone\Make-Package.ps1
        .\Standalone\Make-Package.ps1 -Ziel "C:\Users\...\Desktop"
#>
[CmdletBinding()]
param(
    [string] $Wurzel,
    [string] $Ziel,
    # Betreff-Teil des Code-Signing-Zertifikats, z. B. 'Virtunerd'. Leer = nicht signieren.
    [string] $Signieren
)
$ErrorActionPreference = 'Stop'

# ⚠ Nicht im param()-Vorgabewert: mit [CmdletBinding()] ist $PSScriptRoot dort leer,
# sobald das Skript per 'powershell -File' startet (gemessen 2026-08-19).
if (-not $Wurzel) { $Wurzel = Split-Path $PSScriptRoot -Parent }
if ([string]::IsNullOrEmpty($Wurzel)) { Write-Host "ABORT: cannot determine repository root." -ForegroundColor Red; exit 1 }
if (-not $Ziel) { $Ziel = Join-Path $Wurzel 'Standalone\release' }

$stage      = Join-Path $Wurzel 'Standalone\stage'
$paket      = Join-Path $stage  'paket'          # der Beipack-Inhalt
# ⚠ DEN ZWISCHENORDNER LEEREN. Er wurde nie geraeumt - eine Datei, die das Packskript
# nicht mehr kopiert, blieb deshalb FUER IMMER im Paket. Gemessen 2026-08-19: die drei
# Beispielskripte lagen noch in der ZIP, obwohl die Kopierzeile laengst entfernt war.
# Ein Paket darf nur enthalten, was dieser Lauf hineingelegt hat.
if (Test-Path $paket) { Remove-Item $paket -Recurse -Force }
$installerP = Join-Path $Wurzel 'Standalone\Installer\Installer\ScriptOne.Installer.csproj'
$setupP     = Join-Path $Wurzel 'Standalone\Installer\Setup\ScriptOne.Setup.csproj'
$preloadP   = Join-Path $Wurzel 'Standalone\Preloader\ScriptOne.Preloader.csproj'

function Schritt($t) { Write-Host ""; Write-Host "  == $t" -ForegroundColor Cyan }
function Sag($t)     { Write-Host "     $t" }
function Fehler($t)  { Write-Host "  $t" -ForegroundColor Red; exit 1 }
function Ordner($p)  { if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null } }

<#  ⚠ NICHT [IO.Compression.ZipFile]::CreateFromDirectory.
    Unter .NET Framework (also unter PowerShell 5.1) schreibt es die Eintragsnamen mit
    BACKSLASH - spec-widrig. Gemessen an einem so erzeugten Archiv: 33 von 35 Eintraegen.
    Windows-Entpacker verzeihen das, andere legen eine Datei mit Trennzeichen im NAMEN an.
    Also selbst schreiben, mit Vorwaertsschraegstrich. #>
function Neu-Zip($quelle, $zip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $q = (Resolve-Path $quelle).Path.TrimEnd([char]92) + [char]92
    $a = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($f in (Get-ChildItem $quelle -Recurse -File)) {
            $rel = $f.FullName.Substring($q.Length).Replace([char]92, [char]47)
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($a, $f.FullName, $rel, 'Optimal')
        }
    }
    finally { $a.Dispose() }
}

# ---------------------------------------------------------------- Version
$version = (& dotnet msbuild (Join-Path $Wurzel 'ScriptOne.csproj') -p:Configuration=Il2Cpp -getProperty:Version 2>&1 |
            Where-Object { $_ -and $_.ToString().Trim() } | Select-Object -Last 1).ToString().Trim()
if (-not $version) { Fehler "Version not determinable." }
Write-Host ""
Write-Host "  ScriptOne $version - building the delivery packages" -ForegroundColor White

# ---------------------------------------------------------------- 1. Wirt
Schritt "host (both branches)"
if (Test-Path $paket) { Remove-Item $paket -Recurse -Force }
foreach ($t in @(@('net6.0','net6'), @('net472','net472'))) {
    $out = Join-Path $paket "ScriptOne\core-runtime\$($t[1])"
    & dotnet publish $preloadP -c Release -f $t[0] -o $out -v:q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Fehler "publish failed for $($t[0])" }
    $n = @(Get-ChildItem $out -File).Count
    Sag ("{0,-7} {1,3} files  {2:N1} MB" -f $t[1], $n, ((Get-ChildItem $out -File | Measure-Object Length -Sum).Sum / 1MB))
}

# ---------------------------------------------------------------- 2. Beipack
Schritt "payload"
Ordner (Join-Path $paket 'licenses')
Copy-Item (Join-Path $Wurzel 'Standalone\licenses\*.txt') (Join-Path $paket 'licenses') -Force
Copy-Item (Join-Path $Wurzel 'Standalone\THIRDPARTY-NOTICE.md') (Join-Path $paket 'licenses') -Force
Copy-Item (Join-Path $Wurzel 'LICENSE') (Join-Path $paket 'licenses\ScriptOne.LICENSE.txt') -Force
# ⚠ MoonSharp ist im Plugin-Weg das EINZIGE installierte Fremdwerk - und sein Lizenztext
# fehlte in allen drei Wegen. Er liegt nicht unter Standalone\licenses, deshalb eigene Zeile.
Copy-Item (Join-Path $Wurzel 'ThirdParty\MoonSharp.LICENSE.txt') (Join-Path $paket 'licenses') -Force
# Sollliste zusichern, damit nicht wieder acht von neun still fehlen duerfen.
foreach ($n in @('ScriptOne.LICENSE.txt','MoonSharp.LICENSE.txt','UnityDoorstop.LICENSE.txt',
                 'HarmonyX.LICENSE.txt','Iced.LICENSE.txt','Il2CppInterop.LICENSE.txt',
                 'Microsoft.Extensions.Logging.Abstractions.LICENSE.txt','Mono.Cecil.LICENSE.txt',
                 'MonoMod.LICENSE.txt','THIRDPARTY-NOTICE.md')) {
    if (-not (Test-Path (Join-Path $paket ('licenses\' + $n)))) { Fehler "license text missing: $n" }
}
Sag ("licenses          {0} files" -f @(Get-ChildItem (Join-Path $paket 'licenses') -File).Count)

Ordner (Join-Path $paket 'setup-files')
# ⚠ BEIDE ARCHITEKTUREN. Vorher ging nur x64 mit - ein 32-Bit-Unity-Spiel haette eine
# 64-Bit-winhttp.dll bekommen, die Windows gar nicht laedt. Der Installer waehlt beim
# Installieren nach der gemessenen Bitbreite des Spiels.
foreach ($arch in @('x86','x64')) {
    $q = Join-Path $Wurzel ('Standalone\doorstop\' + $arch)
    if (-not (Test-Path (Join-Path $q 'winhttp.dll'))) { Fehler "doorstop missing for $arch" }
    $z = Join-Path $paket ('setup-files\doorstop\' + $arch)
    Ordner $z
    Copy-Item (Join-Path $q 'winhttp.dll') $z -Force
    if (Test-Path (Join-Path $q '.doorstop_version')) { Copy-Item (Join-Path $q '.doorstop_version') $z -Force }
    Sag ("loader            {0}  {1:N0} KB" -f $arch, ((Get-Item (Join-Path $q 'winhttp.dll')).Length / 1KB))
}
Sag "setup-files       winhttp.dll (UnityDoorstop 4.5.0, unmodified)"

# ⚠ DIE PLUGIN-BAUE GEHOEREN INS PAKET. Sie fehlten in der ersten Fassung, und damit war
# der Installer genau im HAEUFIGSTEN Fall nutzlos: ist MelonLoader aktiv, ist das Plugin der
# richtige Weg - und er brach mit "not in this package" ab. Ein Installer, der den Normalfall
# nicht kann, ist keiner.
$DllIl2Cpp = 'ScriptOne.IL2CPP.dll'
$DllMono   = 'ScriptOne.MONO.dll'
foreach ($k in @(@('Il2Cpp','net6.0',$DllIl2Cpp), @('Mono','net472',$DllMono))) {
    & dotnet build (Join-Path $Wurzel 'ScriptOne.csproj') -c $k[0] -v:q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Fehler "plugin build failed for $($k[0])" }
    $q = Join-Path $Wurzel ('bin' + [char]92 + $k[0] + [char]92 + $k[1] + [char]92 + $k[2])
    if (-not (Test-Path $q)) { Fehler "plugin build missing: $q" }
    Copy-Item $q (Join-Path $paket 'setup-files') -Force
    Sag ("plugin            {0}  {1:N0} KB" -f $k[2], ((Get-Item $q).Length / 1KB))
}

# ⚠ DER INTERPRETER GEHT ALS DATEI MIT, nicht nur eingebettet. Gemessen 2026-08-19 unter
# Unity 6 / Mono / MelonLoader: die eingebettete Fassung wird NICHT gefunden - der
# AssemblyResolve-Haken wird nie gefragt (0 Anfragen), und Vorabladen per Assembly.Load
# reicht auch nicht. Der Loader findet aber jede DLL, die an der richtigen Stelle LIEGT.
# Je Zielrahmen eine: net472 nimmt die net40-Fassung, net6 die netstandard1.6.
foreach ($m in @(@('net472','net40'), @('net6','netstandard1.6'))) {
    $q = Join-Path $Wurzel ('ThirdParty' + [char]92 + $m[1] + [char]92 + 'MoonSharp.Interpreter.dll')
    if (-not (Test-Path $q)) { Fehler "MoonSharp fehlt: $q" }
    $z = Join-Path $paket ('setup-files' + [char]92 + 'moonsharp' + [char]92 + $m[0])
    Ordner $z
    Copy-Item $q $z -Force
    Sag ("interpreter       {0}  {1:N0} KB" -f $m[0], ((Get-Item $q).Length / 1KB))
}

# ⚠ DER BEPINEX-ADAPTER GEHOERT INS PAKET. Ohne ihn kann der Installer den dritten Weg
# (BepInEx aktiv -> Plugin dort) gar nicht gehen und muesste abbrechen - und genau diese
# Konstellation hatte der Autor am 2026-08-19 in seinem Spiel.
# ⚠ DREI ADAPTER, NICHT EINER. BepInEx 5 und 6 filtern gegen verschiedene Assemblynamen, und
# 6 hat je einen eigenen Vertrag fuer Mono und Il2Cpp. Der falsche Bau wird nicht abgelehnt,
# sondern gar nicht angefasst - der Nutzer haette eine erfolgreiche Installation und im Spiel
# nichts. Fehlt einer im Paket, bricht der Bau hier ab statt beim Nutzer.
$bepZ = Join-Path $paket ('setup-files' + [char]92 + 'bepinex')
Ordner $bepZ
foreach ($ad in @(
    @{ Proj = 'Adapters\BepInEx\ScriptOne.BepInEx.csproj';        Cfg = 'Mono';   Tfm = 'net472'; Dll = 'ScriptOne.BepInEx5.MONO.dll';   Bin = 'Adapters\BepInEx\bin' },
    @{ Proj = 'Adapters\BepInEx\ScriptOne.BepInEx6Mono.csproj';   Cfg = 'Mono';   Tfm = 'net472'; Dll = 'ScriptOne.BepInEx6.MONO.dll';   Bin = 'Adapters\BepInEx\bin' },
    @{ Proj = 'Adapters\BepInEx6\ScriptOne.BepInEx6.csproj';      Cfg = 'Il2Cpp'; Tfm = 'net6.0'; Dll = 'ScriptOne.BepInEx6.IL2CPP.dll'; Bin = 'Adapters\BepInEx6\bin' })) {
    & dotnet build (Join-Path $Wurzel $ad.Proj) -c $ad.Cfg -v:q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Fehler ("adapter build failed: " + $ad.Dll) }
    $bepQ = Join-Path $Wurzel ($ad.Bin + [char]92 + $ad.Cfg + [char]92 + $ad.Tfm + [char]92 + $ad.Dll)
    if (-not (Test-Path $bepQ)) { Fehler ("adapter missing: " + $bepQ) }
    Copy-Item $bepQ $bepZ -Force
    Sag ("bepinex adapter   {0,-30} {1,6:N0} KB" -f $ad.Dll, ((Get-Item $bepQ).Length / 1KB))
}

# ⚠ DER PROXY-ERZEUGER GEHT MIT - sonst ist der Standalone-Weg auf einem Il2Cpp-Spiel
# tot. Er erzeugt beim Nutzer die verwalteten Sichten auf die Spiel-API aus DESSEN
# global-metadata.dat; ausliefern kann man sie nicht, weil sie an genau diesen Spielstand
# gebunden sind. Bis 2026-08-20 fehlte er im Paket, waehrend der Installer nur einen Hinweis
# druckte und der Wirt dann im Spiel sofort mit "no Il2Cpp interop assemblies found" starb.
# Die fremden Laufzeiten (Linux/macOS/ARM-capstone) schneidet die csproj selbst weg -
# 51,68 MiB -> 4,61 MiB; Begruendung und Messwerte stehen in der csproj selbst.
$genQ = Join-Path $Wurzel ('tools' + [char]92 + 'InteropGen' + [char]92 + 'InteropGen.csproj')
$genZ = Join-Path $paket ('setup-files' + [char]92 + 'generator')
Ordner $genZ
# ⚠ ScriptOneShipping=true laesst den eingestempelten NuGet-Pfad weg - er enthaelt den
#   Benutzernamen des Entwicklers und wird auf einem fremden Rechner ohnehin nie benutzt.
#   Begruendung in tools\InteropGen\InteropGen.csproj.
& dotnet publish $genQ -c Release -f net6.0 -o $genZ -p:ScriptOneShipping=true -v:q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Fehler "publish failed for InteropGen" }
$genExe = Join-Path $genZ 'InteropGen.exe'
if (-not (Test-Path $genExe)) { Fehler "InteropGen.exe missing after publish: $genExe" }
# ⚠ ZUSICHERN, DASS DAS TRIMMEN GEGRIFFEN HAT. Ohne diese Pruefung waechst das Paket beim
#   naechsten Paketwechsel still wieder um 47 MB, und niemand merkt es vor der Auslieferung.
$fremd = @(Get-ChildItem $genZ -Recurse -File | Where-Object { $_.FullName -like '*runtimes*' })
if ($fremd.Count -gt 0) { Fehler ("foreign runtimes are back in the generator: " + $fremd.Count + " files") }
$genSize = (Get-ChildItem $genZ -Recurse -File | Measure-Object Length -Sum).Sum
Sag ("generator         InteropGen.exe  {0,6:N1} MB, {1} files, no foreign runtimes" -f ($genSize/1MB), @(Get-ChildItem $genZ -Recurse -File).Count)

# ⚠ GENAU EIN SKRIPT geht mit: hello.lua. Ansage des Autors - man soll nach der Installation
# sehen, dass es laeuft, ohne selbst etwas zu schreiben. Es benutzt nur Kernfunktionen und
# laeuft deshalb in JEDEM Spiel. Die drei spielspezifischen Beispiele bleiben im Repo.
Ordner (Join-Path $paket 'LuaScripts')
$helloQ = Join-Path $Wurzel ('scripts' + [char]92 + 'hello.lua')
if (-not (Test-Path $helloQ)) { Fehler "hello.lua missing: $helloQ" }
Copy-Item $helloQ (Join-Path $paket 'LuaScripts') -Force
Sag "LuaScripts        hello.lua (the one script that ships)"

# ⚠ Die Zusicherung, die eine Umbenennung faengt: was der Installer sucht, muss da sein.
foreach ($p in @('ScriptOne\core-runtime\net6\ScriptOne.Preloader.dll',
                 'ScriptOne\core-runtime\net472\ScriptOne.Preloader.dll',
                 'setup-files\doorstop\x64\winhttp.dll',
                 'setup-files\doorstop\x86\winhttp.dll',
                 'setup-files\generator\InteropGen.exe',
                 'setup-files\ScriptOne.IL2CPP.dll',
                 'setup-files\ScriptOne.MONO.dll',
                 'licenses\UnityDoorstop.LICENSE.txt')) {
    if (-not (Test-Path (Join-Path $paket $p))) { Fehler "payload incomplete: $p" }
}
Sag "checked: everything the installer looks for is present."

# ---------------------------------------------------------------- 3. Nutzlast einbetten
Schritt "installer (single file, payload embedded)"
$payloadZip = Join-Path $Wurzel 'Standalone\Installer\Installer\payload.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Neu-Zip $paket $payloadZip
Sag ("payload.zip       {0:N1} MB" -f ((Get-Item $payloadZip).Length / 1MB))

& dotnet build $installerP -c Release -v:q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Fehler "installer build failed" }
$exe = Join-Path $Wurzel 'Standalone\Installer\Installer\bin\Release\net472\ScriptOne-Installer.exe'
if (-not (Test-Path $exe)) { Fehler "installer exe not found" }
# Zusicherung gegen einen ALTEN Bau: eine exe, die aelter ist als die Nutzlast, traegt
# eine andere. Genau das sah der Autor am 2026-08-19 - der Fehler stand dann im
# Installer, nicht im Paket, und war von aussen nicht unterscheidbar.
if ((Get-Item $exe).LastWriteTimeUtc -lt (Get-Item $payloadZip).LastWriteTimeUtc) {
    Fehler "installer exe is older than the payload it should carry"
}
$einbau = [System.IO.Compression.ZipFile]::OpenRead($payloadZip)
try {
    foreach ($n in @($DllIl2Cpp, $DllMono)) {
        if (-not ($einbau.Entries | Where-Object { $_.FullName -like "*$n" })) {
            Fehler "payload.zip does not contain $n"
        }
    }
} finally { $einbau.Dispose() }
Sag "checked: the embedded payload carries both plugin builds."
Sag ("ScriptOne-Installer.exe  {0:N1} MB" -f ((Get-Item $exe).Length / 1MB))

# ---------------------------------------------------------------- 4. (keine ZIP mehr)
# ⚠ DIE ZIP IST RAUS. Ansage des Autors am 2026-08-19: "schmeiss die zip raus, wir bleiben bei
# der setup exe". Sie hatte zwei Probleme, die sich nicht wegpflegen liessen: nach dem Entpacken
# lagen zwei Dinge im Spielordner, die der Nutzer sortieren musste, und die Konsolenfassung
# darin war eine ZWEITE Bedienoberflaeche mit eigener Argumentbehandlung.
# Ausgeliefert wird ab jetzt GENAU EINE Datei: ScriptOne-Installer.exe. Sie oeffnet ohne
# Argumente ihr Fenster und laeuft MIT Argumenten still - damit prueft der Paketpruefer
# dasselbe Artefakt, das der Nutzer bekommt, statt eines Stellvertreters.

# Die eine Datei an ihren Platz legen.
Ordner $Ziel
$exeZiel = Join-Path $Ziel 'ScriptOne-Installer.exe'
Copy-Item $exe $exeZiel -Force

# ---------------------------------------------------------------- 5. Abnahme
Schritt "check"

# ⚠ GEPRUEFT WIRD DIE EINGEBETTETE NUTZLAST, nicht mehr eine ZIP daneben. Sie ist das, was beim
#   Nutzer ankommt; ein Stellvertreter waere wieder eine zweite Wahrheit.
$eintraege = [IO.Compression.ZipFile]::OpenRead($payloadZip)
try {
    $namen = @($eintraege.Entries | ForEach-Object { $_.FullName })
    $backslash = @($namen | Where-Object { $_ -like '*\*' }).Count
    Sag ("{0} entries in the payload, {1} with a backslash (must be 0)" -f $namen.Count, $backslash)
    if ($backslash -gt 0) { Fehler "payload entries contain backslashes - unpacking would create broken names" }
    foreach ($muss in @('ScriptOne/core-runtime/net6/ScriptOne.Preloader.dll',
                        'ScriptOne/core-runtime/net472/ScriptOne.Preloader.dll',
                        'setup-files/generator/InteropGen.exe',
                        'setup-files/doorstop/x64/winhttp.dll',
                        'setup-files/doorstop/x86/winhttp.dll',
                        'setup-files/bepinex/ScriptOne.BepInEx5.MONO.dll',
                        'setup-files/bepinex/ScriptOne.BepInEx6.MONO.dll',
                        'setup-files/bepinex/ScriptOne.BepInEx6.IL2CPP.dll',
                        'LuaScripts/hello.lua')) {
        if ($namen -notcontains $muss) { Fehler "missing from the payload: $muss" }
    }
    Sag "checked: everything the installer needs is inside the exe."
}
finally { $eintraege.Dispose() }

# Und die exe muss ihren stillen Modus koennen - sonst kann der Paketpruefer sie nicht fahren.
$hilfe = & $exeZiel --help 2>&1 | Out-String
if ($hilfe -notmatch 'silent|--remove') { Fehler "the installer exe does not answer --help - silent mode is broken" }
Sag "checked: the exe answers --help, so it can be driven without a window."

# ---------------------------------------------------------------------------------------------
# LECK-SUCHE UEBER DIE AUSGELIEFERTE DATEI - und AUCH UEBER IHRE NUTZLAST.
#
# ⚠ DAS FEHLTE BISHER GANZ. Check-Release.ps1 durchsucht die Mod-DLLs auf Benutzernamen, die
#   Installer-exe aber pruefte niemand - und die ist das Einzige, was der Nutzer herunterlaedt.
#
# ⚠ UND EINE BYTESUCHE UEBER DIE EXE ALLEIN WAERE ZU 97 % BLIND. Die Nutzlast liegt als
#   KOMPRIMIERTE ZIP in der Datei (4,7 von 5,0 MB); ein Name darin steht nirgends im Klartext.
#   Genau diese Falle ist dokumentiert. Deshalb wird die ZIP hier IM SPEICHER entpackt und jeder
#   Eintrag einzeln durchsucht - ohne die exe dafuer aufzublaehen.
$leck = New-Object System.Collections.Generic.List[string]
$nutzer = $env:USERNAME
# Ein Backslash fuer die REGEX heisst zwei Zeichen. Aus dem Code gebaut, damit ihn kein
# Transportweg mehr halbieren kann.
$BS2 = [string][char]92 + [string][char]92
function Sichten([byte[]] $b) {
    # Drei Sichten: .NET legt Stringliterale als UTF-16 ab und ein Literal kann auf einem
    # UNGERADEN Offset beginnen; ein PDB-Pfad ist dagegen ASCII. Eine Sicht allein meldet falsch.
    @(
        [Text.Encoding]::GetEncoding(28591).GetString($b),
        [Text.Encoding]::Unicode.GetString($b, 0, $b.Length - ($b.Length % 2)),
        [Text.Encoding]::Unicode.GetString($b, 1, $b.Length - 1 - (($b.Length - 1) % 2))
    )
}
# EIGENES LECK GEGEN FREMDEN PDB-PFAD - das sind ZWEI Befunde, nicht einer.
#   Unser Benutzername in einer Datei ist ein harter Fehler und verhindert die Auslieferung.
#   Ein FREMDER Benutzerpfad im RSDS-Eintrag einer Fremdbibliothek gehoert dem, der sie gebaut
#   hat; wir koennen ihn nicht entfernen, ohne die Binaerdatei zu aendern - und die geben wir
#   bewusst UNVERAENDERT weiter. Er wird deshalb GEMELDET, blockiert aber nicht. Wer beides
#   gleich behandelt, bekommt einen Pruefer, der nie gruen wird, und schaltet ihn ab.
$hinweis = New-Object System.Collections.Generic.List[string]
function SuchIn([byte[]] $b, [string] $wo) {
    foreach ($v in (Sichten $b)) {
        if ($nutzer -and $v.Contains($nutzer))         { [void]$leck.Add("$wo : user name '$nutzer'") ; break }
    }
    foreach ($v in (Sichten $b)) {
        # ⚠ Das Muster aus dem ZEICHENCODE bauen. Als Literal geschrieben ist es zweimal beim
        #   Durchreichen zu einfachen Backslashes zerfallen, und '\U' ist keine gueltige
        #   Regex-Escape-Sequenz - der Packer brach dann mit einer Meldung ab, die nach einem
        #   Fehler im Muster aussah und einer im Transportweg war.
        if ($v -match ('C:' + $BS2 + 'Users' + $BS2 + '[A-Za-z0-9._-]+')) {
            $treffer = $Matches[0]
            if ($nutzer -and $treffer -like "*$nutzer*") { [void]$leck.Add("$wo : $treffer") }
            else { [void]$hinweis.Add("$wo : $treffer (foreign build path, this file is passed on unchanged)") }
            break
        }
    }
}

$exeBytes = [IO.File]::ReadAllBytes($exeZiel)
# ⚠ POSITIVKONTROLLE: ohne sie ist "nichts gefunden" nicht von "die Suche lief nicht" zu
#   unterscheiden. Der Pruefbegriff kommt aus den METADATEN der Datei, nicht aus ihrem Namen -
#   ein Name aus dem Dateinamen bricht, sobald jemand die Datei umbenennt.
$probe = (Get-Item $exeZiel).VersionInfo.ProductName
if ([string]::IsNullOrWhiteSpace($probe)) { $probe = 'ScriptOne' }
$probe = $probe.Split(' ')[0]
$sichten = Sichten $exeBytes
if (-not $sichten[0].Contains($probe)) { Fehler "leak scan self-test failed: '$probe' not found even in the byte view - the scan checks NOTHING" }
if (-not ($sichten[1].Contains($probe) -or $sichten[2].Contains($probe))) { Fehler "leak scan self-test failed: neither UTF-16 view finds '$probe'" }

SuchIn $exeBytes 'installer'

# Und jetzt HINEIN in die Nutzlast.
$geprueft = 0
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$nlZip = [System.IO.Compression.ZipFile]::OpenRead($payloadZip)
try {
    foreach ($e in $nlZip.Entries) {
        if ($e.Length -eq 0) { continue }
        $ms = New-Object IO.MemoryStream
        $st = $e.Open(); try { $st.CopyTo($ms) } finally { $st.Dispose() }
        SuchIn $ms.ToArray() ("payload/" + $e.FullName)
        $ms.Dispose()
        $geprueft++
    }
} finally { $nlZip.Dispose() }

foreach ($h in $hinweis) { Write-Host "     NOTE[FOREIGN]: $h" -ForegroundColor DarkGray }
if ($leck.Count -gt 0) {
    Write-Host ""
    foreach ($f in $leck) { Write-Host "     LEAK: $f" -ForegroundColor Red }
    Fehler "the delivered file carries OUR machine path or user name - it must not be uploaded"
}
Sag ("checked for name leaks: the exe and all {0} payload entries, in three byte views." -f $geprueft)

Schritt "release folder"
# WARUM DAS HIER STEHT UND NICHT VON HAND GEMACHT WIRD
# Ausgeliefert wird genau EINE Datei - aber wer sie hochlaedt, braucht daneben zwei Dinge,
# die niemand aus dem Kopf richtig hinschreibt: eine Pruefsumme und die Angabe, WELCHER
# Stand das ist. Von Hand erzeugt ist beides beim naechsten Bau still veraltet.
$buildNr = (& git -C $Wurzel rev-list --count HEAD 2>$null | Out-String).Trim()
$commit  = (& git -C $Wurzel rev-parse --short HEAD 2>$null | Out-String).Trim()
$dreck   = (& git -C $Wurzel status --porcelain 2>$null | Out-String).Trim()
if ([string]::IsNullOrEmpty($buildNr)) { $buildNr = '?'; $commit = '?' }

# ⚠ Ein Release aus einem SCHMUTZIGEN Arbeitsbaum ist nicht wiederherstellbar - die exe
#   enthaelt dann Aenderungen, die kein Commit fuehrt. Das ist eine Warnung und kein Abbruch,
#   weil auch ein Probepaket seinen Zweck hat; verschwiegen werden darf es aber nicht.
if (-not [string]::IsNullOrEmpty($dreck)) {
    Write-Host "     WARNING: working tree is not clean - this build cannot be reproduced from a commit" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------------------------
# SIGNIEREN - VOR der Pruefsumme, sonst beschreibt BUILD-INFO.txt eine andere Datei.
#
# ⚠ DAS IST DIE FALLE AN DIESER STELLE: eine Signatur AENDERT die Datei. Wer hinterher
#   signiert, hat eine BUILD-INFO, deren sha256 nicht mehr stimmt - und das ist schlimmer als
#   gar keine Pruefsumme, weil sie zum Vergleich einlaedt und dann falschen Alarm ausloest.
#
# ⚠ WAS EINE SELBSTSIGNATUR LEISTET UND WAS NICHT: sie macht die Datei manipulationssicher
#   pruefbar. Sie nimmt die SmartScreen-Warnung NICHT weg (die Kette endet in einer Wurzel, der
#   Windows nicht traut) und verbessert kein Virenscanner-Urteil; ein Scanner kann eine nicht
#   verifizierbare Signatur sogar leicht negativ werten. Deshalb ist sie OPTIONAL und nicht
#   der Vorgabeweg - wer sie will, gibt das Zertifikat ausdruecklich an.
if ($Signieren) {
    $zert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -like "*$Signieren*" -and $_.HasPrivateKey } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $zert) { Fehler "no code-signing certificate matching '$Signieren' in Cert:\CurrentUser\My" }
    $sig = Set-AuthenticodeSignature -FilePath $exeZiel -Certificate $zert `
             -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    $selbst = $sig.SignerCertificate.Subject -eq $sig.SignerCertificate.Issuer
    Sag ("signed            {0}  ({1})" -f $zert.Subject, $sig.Status)
    if (-not $sig.TimeStamperCertificate) {
        Fehler "the signature carries no timestamp - it would expire with the certificate"
    }
    if ($selbst) {
        Write-Host "     NOTE: self-signed. Windows still shows the SmartScreen warning; this only makes" -ForegroundColor DarkGray
        Write-Host "           tampering detectable for anyone who knows the certificate." -ForegroundColor DarkGray
    }
}

$sha = (Get-FileHash $exeZiel -Algorithm SHA256).Hash
$md5 = (Get-FileHash $exeZiel -Algorithm MD5).Hash
$groesse = (Get-Item $exeZiel).Length

$infoPfad = Join-Path (Split-Path $exeZiel) 'BUILD-INFO.txt'
$info = @(
    "ScriptOne $version",
    "",
    "file       ScriptOne-Installer.exe",
    ("size       {0:N0} bytes" -f $groesse),
    "sha256     $sha",
    "md5        $md5",
    "",
    "build      $buildNr",
    "commit     $commit",
    ("clean      {0}" -f $(if ([string]::IsNullOrEmpty($dreck)) { 'yes' } else { 'NO - not reproducible from a commit' })),
    "",
    "This single file is the whole delivery. It carries its payload inside it;",
    "nothing else needs to be downloaded. Run it and point it at a game folder.",
    "Verify the download with:  Get-FileHash ScriptOne-Installer.exe -Algorithm SHA256"
)
[IO.File]::WriteAllLines($infoPfad, $info, (New-Object Text.UTF8Encoding $false))
Sag "BUILD-INFO.txt  build $buildNr / $commit"

# Die Freigabetexte kommen MECHANISCH aus dem CHANGELOG - eine zweite, von Hand gepflegte
# Fassung waere eine zweite Wahrheit, und die beiden laufen garantiert auseinander.
$changelog = Join-Path $Wurzel 'CHANGELOG.md'
if (Test-Path $changelog) {
    $zeilen = Get-Content $changelog -Encoding UTF8
    $von = -1; $bis = $zeilen.Count
    for ($i = 0; $i -lt $zeilen.Count; $i++) {
        if ($zeilen[$i] -match ('^##\s+\[' + [Regex]::Escape($version) + '\]')) { $von = $i; continue }
        if ($von -ge 0 -and $zeilen[$i] -match '^##\s+\[') { $bis = $i; break }
    }
    $notizPfad = Join-Path (Split-Path $exeZiel) 'RELEASE-NOTES.md'
    if ($von -lt 0) {
        # ⚠ KEIN FEHLER, ABER AUCH KEIN SCHWEIGEN. Solange nichts veroeffentlicht ist, hat der
        #   CHANGELOG zu Recht keinen Abschnitt - der Autor entscheidet, was online geht. Ein
        #   Abbruch waere hier falsch, ein stilles Weitergehen aber auch:
        # ⚠ DIE ALTE DATEI MUSS WEG. Bliebe sie liegen, laege im Freigabeordner eine
        #   RELEASE-NOTES.md aus einem FRUEHEREN Stand neben der neuen exe - und genau die
        #   wuerde jemand hochladen. Eine veraltete Datei ist schlimmer als keine.
        if (Test-Path $notizPfad) {
            Remove-Item $notizPfad -Force
            Sag "RELEASE-NOTES.md  removed - it was left over from an earlier build"
        }
        Sag "RELEASE-NOTES.md  none: CHANGELOG.md has no section for version $version yet"
    }
    else {
        $notizen = $zeilen[$von..($bis - 1)]
        [IO.File]::WriteAllLines($notizPfad, $notizen, (New-Object Text.UTF8Encoding $false))
        Sag ("RELEASE-NOTES.md  {0} lines from CHANGELOG.md" -f $notizen.Count)
    }
}
else { Fehler "CHANGELOG.md not found - release notes cannot be produced" }

Write-Host ""
Write-Host "  Done - upload these:" -ForegroundColor Green
Get-ChildItem (Split-Path $exeZiel) -File | Sort-Object Name | ForEach-Object {
    Write-Host ("    {0,-28} {1,10:N0} bytes" -f $_.Name, $_.Length)
}
Write-Host ""
