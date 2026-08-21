<#
    Gen-Bindings.ps1 - erzeugt aus der Flaechendatei den Registrierungscode fuer den Wirt.

    EINE QUELLE, DREI ERZEUGNISSE
        surface\candidates.json  ->  Host\Generated\GeneratedSurface.g.cs   (Bindungen)
                                 ->  surface\s1.lua                         (Editor-Stubs)
                                 ->  docs\API.md                            (Nachschlagewerk)

    Damit ist eine Anhebung nach einem Spiel-Update eine GENERATORLAUF, keine Umschreibung:
    Gen-Surface.ps1 liest die neue Assembly, dieses Skript erzeugt den Code neu, der Compiler
    meldet, was nicht mehr passt.

    WAS ERZEUGT WIRD
    Je Manager eine Lua-Tabelle unter s1, je Member ein Callback:

        s1.time.current_time()            -- Zustand lesen
        s1.money.change_cash_balance(500, true, true)
        s1.level.add_xp(100)

    GRENZREGEL, unveraendert: ueber die Lua-Grenze gehen nur Zahlen, Zeichenketten und
    Wahrheitswerte. Enums werden als ZAHL uebergeben - eine Namensaufloesung braeuchte
    Reflection ueber Il2Cpp-Typen, und die gibt dort zuverlaessig null.

    UEBERLADUNGEN sind der einzige Fall, in dem der Generator etwas WEGLASSEN muss: zwei
    Ueberladungen ergeben denselben Lua-Namen. Was wegfaellt, wird GENANNT - eine stille
    Auswahl liest sich sonst wie Vollstaendigkeit.
#>
[CmdletBinding()]
param(
    # Die NAMENSKARTE. Sie entkoppelt die Lua-Namen von den Spiel-Membernamen: benennt das
    # Spiel um, aendert sich hier EIN Eintrag und kein einziges Nutzerskript bricht.
    # Fehlt die Datei, verhaelt sich der Erzeuger wie vorher (Namen mechanisch abgeleitet).
    [string] $NameMap,
    # Schreibt die Karte aus dem, was dieser Lauf ERZEUGT hat. Einmalig zum Anlegen, danach
    # nur nach bewusster Erweiterung - sonst schreibt sich ein Bruch selbst fest.
    [switch] $UpdateMap,
    # ⚠ KEINE Vorgabewerte mit $PSScriptRoot - siehe den Block direkt unter param().
    [string] $Surface,
    [string] $OutCs,
    [string] $OutLua,
    [string] $OutDoc,
    [switch] $All      # alles aufnehmen, unabhaengig von "include"
)

$ErrorActionPreference = 'Stop'

# ⚠ $PSScriptRoot IST IN EINEM param()-VORGABEWERT LEER, sobald [CmdletBinding()] dabei ist
# UND das Skript per 'powershell -File' gestartet wird. Im RUMPF ist es gesetzt, und aus
# einer Sitzung heraus ('& skript.ps1') stimmt auch der Vorgabewert - der Fehler zeigt sich
# also genau dann nicht, wenn man ihn von Hand sucht. Gemessen am 2026-08-19: der Aufruf
# brach mit "Split-Path: Das Argument ... leere Zeichenfolge" ab, BEVOR eine einzige eigene
# Zeile lief. Deshalb werden die Pfade hier abgeleitet, nicht oben.
$Wurzel = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrEmpty($Wurzel)) { Write-Host "ABORT: cannot determine the script folder." -ForegroundColor Red; exit 1 }
if (-not $Surface) { $Surface = Join-Path $Wurzel 'surface\candidates.json' }
if (-not $OutCs)   { $OutCs   = Join-Path $Wurzel 'Host\Generated\GeneratedSurface.g.cs' }
if (-not $OutLua)  { $OutLua  = Join-Path $Wurzel 'surface\s1.lua' }
if (-not $OutDoc)  { $OutDoc  = Join-Path $Wurzel 'docs\API.md' }
if (-not (Test-Path $Surface)) { Write-Host "MISSING: $Surface" -ForegroundColor Red; exit 1 }

$doc = Get-Content $Surface -Raw -Encoding UTF8 | ConvertFrom-Json

# ---------------------------------------------------------------- Typabbildung

function Get-CsType {
    param([string] $Art)
    switch -Regex ($Art) {
        '^enum:'    { return 'int' }          # Enums queren als Zahl
        '^String$'  { return 'string' }
        '^Boolean$' { return 'bool' }
        '^Single$'  { return 'float' }
        '^Double$'  { return 'double' }
        '^Int32$'   { return 'int' }
        '^Int64$'   { return 'long' }
        '^UInt32$'  { return 'uint' }
        '^UInt64$'  { return 'ulong' }
        '^Int16$'   { return 'short' }
        '^UInt16$'  { return 'ushort' }
        '^Byte$'    { return 'byte' }
        '^SByte$'   { return 'sbyte' }
        '^Void$'    { return 'void' }
        default     { return $null }
    }
}

# Lua-Argument -> C#-Ausdruck
function Get-ArgExpr {
    param([string] $Art, [int] $Index)
    # Ein Enum-Parameter braucht den ECHTEN Typ - '(int)' allein gibt CS1503.
    if ($Art -match '^enum:(.+)$') {
        $al = Get-EnumAlias $Matches[1]
        return "($al)(int)Arg.Num(a, $Index)"
    }
    $cs = Get-CsType $Art
    switch ($cs) {
        'string' { return "Arg.Str(a, $Index)" }
        'bool'   { return "Arg.Bool(a, $Index)" }
        default  { return "($cs)Arg.Num(a, $Index)" }
    }
}

# C#-Wert -> DynValue
function Get-RetExpr {
    param([string] $Art, [string] $Call)
    $cs = Get-CsType $Art
    if ($cs -eq 'void')   { return "$Call; return DynValue.Nil;" }
    if ($cs -eq 'string') { return "return DynValue.NewString($Call);" }
    if ($cs -eq 'bool')   { return "return DynValue.NewBoolean($Call);" }
    if ($Art -match '^enum:') { return "return DynValue.NewNumber((int)($Call));" }
    return "return DynValue.NewNumber($Call);"
}

# ---------------------------------------------------------------- Spielwissen
# Kommt aus candidates.json (Gen-Surface hat es ermittelt). Fehlt der Block, gelten die
# alten Werte - eine Karte aus der Zeit vor Punkt 6 soll weiter funktionieren.
$basisErlaubt = @{}
$il2Pre = 'Il2Cpp'
if ($doc.PSObject.Properties.Name -contains 'spiel' -and $doc.spiel) {
    $il2Pre = "$($doc.spiel.il2cpp_prefix)"
    foreach ($b in $doc.spiel.singleton_bases.PSObject.Properties.Name) {
        $basisErlaubt[($b -replace '`.*$','')] = $doc.spiel.singleton_bases.$b
    }
    Write-Host ("  Game knowledge from candidates.json: prefix '{0}', {1} singleton base(s)" -f `
        $il2Pre, $basisErlaubt.Count) -ForegroundColor Gray
} else {
    foreach ($b in @('Singleton','PersistentSingleton','NetworkSingleton','PlayerSingleton')) {
        $basisErlaubt[$b] = 'ScheduleOne.DevUtilities'
    }
    Write-Host "  candidates.json has no 'spiel' block - falling back to old hardwired values." -ForegroundColor Yellow
}

# ---------------------------------------------------------------- reservierte Namen
# ⚠ DIE FLAECHE WIRD NACH DEM KERN INSTALLIERT UND UEBERSCHREIBT IHN.
# Belegter Fall (2026-08-18, unbeaufsichtigter Spiellauf): der Manager 'SaveManager' wurde
# zu 's1.save' - und ueberschrieb damit die Kernfunktion s1.save(), die die README
# dokumentiert. Jedes Skript, das ihr folgte, starb mit 'attempt to call a table value'.
# Der Fehler ist nicht 'ein Name doppelt', sondern 'der Erzeuger darf den Kern nicht kennen
# und ueberschreibt ihn trotzdem'. Darum hier ausdruecklich reserviert.
# Manager, die wegen eines Kernnamens NICHT gebunden wurden - am Ende ausgewiesen,
# nie stillschweigend weggelassen.
$tabKollision = New-Object System.Collections.Generic.List[string]
$reserviert = @('log','warn','console','move_speed','backend','on','after','every',
                'cancel','get','set','save','surface_size')

# ---------------------------------------------------------------- Namenskarte
if (-not $NameMap) { $NameMap = Join-Path $PSScriptRoot '..\surface\names.json' }

$karte      = $null            # tabelle -> @{ clr; member = @{ luaName = clrName } }
$karteUmk   = @{}              # "Manager.clrName" -> luaName  (Rueckrichtung, das ist die genutzte)
$karteTab   = @{}              # Manager (clr)     -> luaTabellenname
$verwendet  = New-Object 'System.Collections.Generic.HashSet[string]'

if (Test-Path $NameMap) {
    $karte = Get-Content $NameMap -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($tabName in $karte.tabellen.PSObject.Properties.Name) {
        $eintrag = $karte.tabellen.$tabName
        $karteTab[$eintrag.clr] = $tabName
        foreach ($luaName in $eintrag.member.PSObject.Properties.Name) {
            $karteUmk["$($eintrag.clr).$($eintrag.member.$luaName)"] = $luaName
        }
    }
    Write-Host ("  Name map: {0} table(s), {1} member(s) (from {2})" -f `
        $karteTab.Count, $karteUmk.Count, (Split-Path $NameMap -Leaf))
} else {
    Write-Host "  Name map: none ($NameMap) - names are derived mechanically." -ForegroundColor Yellow
}

# Was der Erzeuger STILL verwirft, wird gesammelt und am Ende genannt. Ein Member, der
# lautlos aus der Flaeche faellt, ist fuer ein Nutzerskript dasselbe wie ein Absturz -
# nur ohne Meldung.
$verworfen = New-Object 'System.Collections.Generic.List[string]'
$neuOhneKarte = New-Object 'System.Collections.Generic.List[string]'
$erzeugt = @{}                 # tabelle -> @{ clr; member = @{ lua = clr } }  (fuer -UpdateMap)

# ---------------------------------------------------------------- erzeugen

$cs   = New-Object System.Text.StringBuilder
$lua  = New-Object System.Text.StringBuilder
$md   = New-Object System.Text.StringBuilder
$kollisionen = New-Object System.Collections.Generic.List[string]
$aliasIl2  = New-Object System.Text.StringBuilder
$enumAlias = @{}
function Get-EnumAlias {
    param([string] $VollName)
    if ($enumAlias.ContainsKey($VollName)) { return $enumAlias[$VollName] }
    $a = 'S1E_' + ($VollName -replace '[^A-Za-z0-9]', '_')
    $enumAlias[$VollName] = $a
    return $a
}
$aliasMono = New-Object System.Text.StringBuilder
$nAkt = 0; $nZus = 0; $nMgr = 0

[void]$cs.AppendLine(@'
// <auto-generated>
//   ERZEUGT von tools\Gen-Bindings.ps1 aus surface\candidates.json - NICHT von Hand aendern.
//   Aenderungen gehoeren in die Flaechendatei; danach den Generator laufen lassen.
//
//   Grenzregel: ueber diese Datei geht NIE ein Spieltyp nach Lua. Nur Zahlen, Zeichenketten
//   und Wahrheitswerte. Enums queren als Zahl.
// </auto-generated>
using MoonSharp.Interpreter;
__ALIASE__
namespace ScriptOne.Host.Generated
{
    /// <summary>Argumentzugriff mit Vorgaben - ein fehlendes Argument darf nie werfen.</summary>
    internal static class Arg
    {
        internal static double Num(CallbackArguments a, int i)
        {
            if (a.Count <= i) return 0;
            var v = a[i];
            return v.Type == DataType.Number ? v.Number : 0;
        }
        internal static string Str(CallbackArguments a, int i)
        {
            if (a.Count <= i) return string.Empty;
            var v = a[i];
            return v.IsNil() ? string.Empty : v.CastToString();
        }
        internal static bool Bool(CallbackArguments a, int i)
        {
            if (a.Count <= i) return false;
            return a[i].CastToBool();
        }
    }

    internal static partial class GeneratedSurface
    {
        /// <summary>Haengt alle erzeugten Manager-Tabellen unter das Global "s1".</summary>
        internal static int Install(Script script, Table s1)
        {
            var n = 0;
'@)

$body = New-Object System.Text.StringBuilder

[void]$lua.AppendLine('---@meta')
[void]$lua.AppendLine('-- GENERATED by tools/Gen-Bindings.ps1 - editor stubs, never executed.')
[void]$lua.AppendLine('---@class s1')
[void]$lua.AppendLine('s1 = {}')
[void]$lua.AppendLine()

[void]$md.AppendLine('# ScriptOne - generated Lua surface')
[void]$md.AppendLine()
[void]$md.AppendLine('Generated from `surface/candidates.json`. **Do not edit by hand.**')
[void]$md.AppendLine()
[void]$md.AppendLine('Enums cross as numbers. A missing argument is 0 / "" / false, never an error.')
[void]$md.AppendLine()

foreach ($m in $doc.managers) {

    $akt = @($m.actions | Where-Object { $All -or $_.include })
    $zus = @($m.state   | Where-Object { $All -or $_.include })
    if ($akt.Count -eq 0 -and $zus.Count -eq 0) { continue }

    # GENERISCHE Manager (App`1) haben keinen nennbaren Namen in einem using-Alias und
    # keine eindeutige Instanz - sie fallen raus. Ohne diese Zeile erzeugt der Generator
    # 'using S1T_App`1 = ...', was der Compiler als Syntaxfehler ablehnt.
    if ($m.manager -match '`') { continue }

    # Basisklasse bestimmt den Zugriffsweg auf die Instanz.
    # ⚠ Punkt 6: die zulaessigen Basen und ihr Namensraum stehen NICHT mehr hier, sondern
    # kommen aus candidates.json - Gen-Surface hat sie am Merkmal ermittelt (generische
    # Basis mit statischem 'Instance', Basiskette mitgegangen). Zwei Skripte, die dasselbe
    # Spielwissen verdrahten, driften unbemerkt auseinander.
    # WIE die Instanz erreicht wird, steht seit dem spielunabhaengigen Regelsatz in der
    # Karte (instance_owner / instance_generic). Es gibt genau zwei Faelle, und der zweite
    # fehlte hier vorher restlos:
    #   generisch : Singleton`1<Foo>.Instance   - braucht den geschlossenen generischen Alias
    #   sonst     : Foo.Instance                - statische Member sind in C# vererbt, der
    #                                             Typ selbst genuegt, auch wenn eine nicht-
    #                                             generische Basis das 'Instance' deklariert
    # WARNUNG: Ohne den zweiten Fall wurde jeder Manager, der sein 'Instance' selbst
    #   deklariert (der haeufigste Unity-Singleton ueberhaupt), per 'continue' STILL
    #   verworfen - die Flaeche war dann kleiner und niemand sah warum.
    $generisch = $true
    if ($m.PSObject.Properties.Name -contains 'instance_generic') { $generisch = [bool]$m.instance_generic }
    # Die EXAKTE Schreibweise des Members - siehe Gen-Surface: vier Typen schreiben es klein.
    $instMember = 'Instance'
    if ($m.PSObject.Properties.Name -contains 'instance_member' -and $m.instance_member) { $instMember = $m.instance_member }
    $aliasTyp  = "S1T_$($m.manager)"
    $aliasInst = "S1I_$($m.manager)"
    # Backend-neutral ueber using-Aliase: der Namensraum ist der EINZIGE Unterschied
    # zwischen den Backends (Il2CppScheduleOne.* statt ScheduleOne.*). Ein Alias auf einen
    # geschlossenen generischen Typ ist in C# erlaubt - damit bleibt der Rumpf identisch.
    # WARNUNG: Il2CppInterop stellt das Praefix dem NAMENSRAUM voran, nicht dem TYPNAMEN.
    #   Bei einem Typ OHNE Namensraum ist das Ergebnis der Namensraum 'Il2Cpp' - also
    #   'Il2Cpp.SteamManager', nicht 'Il2CppSteamManager'. Gemessen an SteamManager und
    #   ECartelStatus, die im Mono-Satz beide im globalen Namensraum liegen; ohne den
    #   Punkt gab es CS0246 auf einen Typ, den es unter genau diesem Namen nie gab.
    $clrIl2 = if ($m.clr -like '*.*') { "$il2Pre$($m.clr)" } else { "$il2Pre.$($m.clr)" }
    [void]$aliasIl2.AppendLine("using ${aliasTyp} = $clrIl2;")
    [void]$aliasMono.AppendLine("using ${aliasTyp} = $($m.clr);")
    if ($generisch) {
        $basis = $m.base -replace '`.*$', ''
        if (-not $basisErlaubt.ContainsKey($basis)) { continue }
        $basisRaum = $basisErlaubt[$basis]
        [void]$aliasIl2.AppendLine("using ${aliasInst} = $il2Pre$basisRaum.$basis<$clrIl2>;")
        [void]$aliasMono.AppendLine("using ${aliasInst} = $basisRaum.$basis<$($m.clr)>;")
        $inst = "$aliasInst.$instMember"
    } else {
        $inst = "$aliasTyp.$instMember"
    }

    $nMgr++
    # PIN: steht der Manager in der Karte, gilt IHR Tabellenname - nicht der abgeleitete.
    $ausKarte = $karteTab.ContainsKey($m.manager)
    if ($ausKarte) { $tab = $karteTab[$m.manager] }
    else {
        $tab = $m.lua -replace '_manager$', '' -replace '_controller$', ''
        # Reservierten Kernnamen NICHT ueberschreiben - lieber den unverkuerzten nehmen.
        if ($reserviert -contains $tab -and $m.lua -ne $tab) {
            Write-Host ("  reserved: table '{0}' would shadow the core function s1.{0}() - using '{1}' instead" -f $tab, $m.lua) -ForegroundColor Yellow
            $tab = $m.lua
        }
        if ($null -ne $karte) { $neuOhneKarte.Add("Tabelle $tab ($($m.manager))") }
    }
    if ($reserviert -contains $tab) {
        # WARNUNG: HIER STAND EIN PAUSCHALES 'ABORT: the map pins ... onto a reserved core
        #   name'. Die Meldung war in genau dem Fall FALSCH, in dem sie zuerst auftrat:
        #   ScheduleOne.Console ergibt die Lua-Tabelle 'console', die Karte kannte den
        #   Manager gar nicht - der Ausweichpfad "nimm den unverkuerzten Namen" ist ein
        #   NO-OP, sobald der Name selbst schon unverkuerzt der reservierte ist. Der Lauf
        #   brach also ab und beschuldigte eine Datei, in der nichts dergleichen stand.
        #   Jetzt getrennt: ein GEPINNTER Kernname ist ein Fehler des Menschen und bricht
        #   ab; ein ABGELEITETER wird UEBERSPRUNGEN, namentlich gemeldet und am Ende noch
        #   einmal aufgefuehrt - mit dem Hinweis, wo man ihm einen Namen gibt. Stilles
        #   Weglassen waere die schlechteste der drei Moeglichkeiten.
        if ($ausKarte) {
            Write-Host ("  ABORT: the map pins table '{0}' onto a reserved core name." -f $tab) -ForegroundColor Red
            exit 1
        }
        # Der Zaehler lief schon hoch, die Aliase sind schon geschrieben - beides
        # zuruecknehmen, sonst meldet der Lauf 132 Manager und erzeugt 131 Tabellen.
        # Eine Zahl, die um eins danebenliegt, prueft niemand nach.
        $nMgr--
        $tabKollision.Add(("{0} -> s1.{1}" -f $m.manager, $tab))
        Write-Host ("  NOT BOUND[NAME COLLISION]: {0} would become s1.{1}, which is a core function" -f $m.manager, $tab) -ForegroundColor Yellow
        continue
    }
    $erzeugt[$tab] = @{ clr = $m.manager; member = @{} }
    [void]$body.AppendLine("            // ---------- $($m.manager) ----------")
    [void]$body.AppendLine("            {")
    [void]$body.AppendLine("                var t = new Table(script);")

    [void]$lua.AppendLine("---@class s1_$tab")
    [void]$lua.AppendLine("s1.$tab = {}")
    [void]$md.AppendLine("## ``s1.$tab`` - $($m.manager)")
    [void]$md.AppendLine()

    $gesehen = New-Object 'System.Collections.Generic.HashSet[string]'

    foreach ($a in $akt) {
        # PIN je Member. Schluessel ist der CLR-Name, denn DER ist die Bruchstelle.
        $luaName = $a.lua
        $pin = $karteUmk["$($m.manager).$($a.clr)"]
        if ($pin) { $luaName = $pin; [void]$verwendet.Add("$($m.manager).$($a.clr)") }
        $istNeu = (-not $pin) -and ($null -ne $karte)   # Meldung erst NACH der Kollisionspruefung

        if (-not $gesehen.Add($luaName)) { $kollisionen.Add("$tab.$luaName (overload)"); continue }
        $argExprs = @()
        $i = 0
        $sig = @()
        foreach ($p in $a.args) {
            $e = Get-ArgExpr $p.type $i
            if (-not $e) { $argExprs = $null; break }
            $argExprs += $e
            $sig += "$($p.name)"
            $i++
        }
        if ($null -eq $argExprs) {
            # NICHT still: ein Argument liess sich nicht auf Lua abbilden.
            $verworfen.Add("$tab.$luaName - Argument nicht abbildbar ($($m.manager).$($a.clr))")
            continue
        }
        $call = "$inst.$($a.clr)(" + ($argExprs -join ', ') + ")"
        if ($a.static) { $call = "$aliasTyp.$($a.clr)(" + ($argExprs -join ', ') + ")" }
        $ret = Get-RetExpr $a.returns $call
        [void]$body.AppendLine("                t.Set(""$luaName"", DynValue.NewCallback((c, a) => { $ret }));")
        [void]$lua.AppendLine("function s1.$tab.$luaName($($sig -join ', ')) end")
        [void]$md.AppendLine("- ``$luaName($($sig -join ', '))`` -> ``$($a.returns)``")
        $erzeugt[$tab].member[$luaName] = $a.clr
        if ($istNeu) { $neuOhneKarte.Add("$tab.$luaName ($($m.manager).$($a.clr))") }
        $nAkt++
    }

    foreach ($s in $zus) {
        $luaName = $s.lua
        $pin = $karteUmk["$($m.manager).$($s.clr)"]
        if ($pin) { $luaName = $pin; [void]$verwendet.Add("$($m.manager).$($s.clr)") }
        $istNeu = (-not $pin) -and ($null -ne $karte)   # Meldung erst NACH der Kollisionspruefung

        if (-not $gesehen.Add($luaName)) { $kollisionen.Add("$tab.$luaName (duplicate name)"); continue }
        $get = if ($s.static) { "$aliasTyp.$($s.clr)" } else { "$inst.$($s.clr)" }
        $ret = Get-RetExpr $s.type $get
        [void]$body.AppendLine("                t.Set(""$luaName"", DynValue.NewCallback((c, a) => { $ret }));")
        [void]$lua.AppendLine("function s1.$tab.$luaName() end")
        # ⚠ ENGLISCH: dieser Text landet in der VEROEFFENTLICHTEN docs/API.md, ist also
        #   nutzersichtbar - nicht Quelltextkommentar. Die Hausregel gilt hier nicht.
        $w = if ($s.writable) { ' *(writable in-game, bound read-only here)*' } else { '' }
        [void]$md.AppendLine("- ``$luaName()`` -> ``$($s.type)``$w")
        $erzeugt[$tab].member[$luaName] = $s.clr
        if ($istNeu) { $neuOhneKarte.Add("$tab.$luaName ($($m.manager).$($s.clr))") }
        $nZus++
    }

    [void]$body.AppendLine("                s1.Set(""$tab"", DynValue.NewTable(t)); n++;")
    [void]$body.AppendLine("            }")
    [void]$lua.AppendLine()
    [void]$md.AppendLine()
}

$enumIl2 = New-Object System.Text.StringBuilder
$enumMono = New-Object System.Text.StringBuilder
foreach ($k in ($enumAlias.Keys | Sort-Object)) {
    # Dieselbe Namensraum-Regel wie beim Manager-Alias: OHNE Namensraum lautet der Proxy
    # 'Il2Cpp.ECartelStatus', nicht 'Il2CppECartelStatus'. Und das Praefix kommt aus der
    # Karte - hier stand es fest verdrahtet, was jedem Spiel mit OptIn-Erzeugung die
    # Enum-Aliase zerschossen haette.
    $kIl2 = if ($k -like '*.*') { "$il2Pre$k" } else { "$il2Pre.$k" }
    [void]$enumIl2.AppendLine("using $($enumAlias[$k]) = $kIl2;")
    [void]$enumMono.AppendLine("using $($enumAlias[$k]) = $k;")
}
$aliasBlock = "`n#if IL2CPP`n" + $aliasIl2.ToString() + $enumIl2.ToString() + "#endif`n#if MONO`n" + $aliasMono.ToString() + $enumMono.ToString() + "#endif`n"
# WICHTIG: .Replace() der .NET-Zeichenkette, NICHT der PowerShell-Operator -replace.
# -replace ist regulaerausdruckbasiert; der Aliasblock enthaelt '<', '>' und '.', und der
# Ersatztext wuerde ausserdem '$' als Rueckverweis deuten. Hier ist alles woertlich gemeint.
$cs = New-Object System.Text.StringBuilder ($cs.ToString().Replace('__ALIASE__', $aliasBlock))
[void]$cs.Append($body.ToString())
[void]$cs.AppendLine(@'
            return n;
        }
    }
}
'@)

# Muss VOR dem Waechter stehen: PowerShell loest Funktionen zur LAUFZEIT auf, eine
# weiter unten definierte Funktion existiert oben schlicht nicht.
function Liste($titel, $eintraege, $farbe, $max = 12) {
    if ($eintraege.Count -eq 0) { return }
    Write-Host ("  {0} {1}" -f $eintraege.Count, $titel) -ForegroundColor $farbe
    $eintraege | Select-Object -First $max | ForEach-Object { Write-Host "     $_" -ForegroundColor DarkGray }
    if ($eintraege.Count -gt $max) { Write-Host ("     ... and {0} more" -f ($eintraege.Count - $max)) -ForegroundColor DarkGray }
}

# ================================================================== VOR dem Schreiben
# ⚠ REIHENFOLGE IST HIER DER GANZE SCHUTZ. Diese Pruefung stand frueher NACH den drei
# WriteAllText-Aufrufen. Ein Lauf ohne -All bindet nichts (die Flaechendatei traegt 0
# Eintraege mit include:true), schrieb aber trotzdem: GeneratedSurface.g.cs schrumpfte
# von 1033 auf 48 Zeilen, s1.lua von 630 auf 5, API.md von 635 auf 10 - und ERST DANACH
# kam der rote Text und exit 1. Der Schaden war da, der Waechter meldete ihn nur.
# Jetzt wird abgebrochen, BEVOR etwas geschrieben wird.

if ($nMgr -eq 0) {
    Write-Host ""
    Write-Host "  ABORT: 0 managers bound - NOTHING was written." -ForegroundColor Red
    Write-Host "  Most likely cause: the -All switch is missing. The surface file lists its" -ForegroundColor Red
    Write-Host "  candidates with include:false; without -All the generator binds none of them." -ForegroundColor Red
    Write-Host "  The full invocation is:" -ForegroundColor Red
    Write-Host ('      .' + [char]92 + 'tools' + [char]92 + 'Gen-Surface.ps1 -MinSurface 4')
    Write-Host ('      .' + [char]92 + 'tools' + [char]92 + 'Gen-Bindings.ps1 -All')
    exit 1
}

# ---------------------------------------------------------------- Karte durchsetzen
# ⚠ HIER ENTSCHEIDET SICH DER GANZE NUTZEN. Ein Pin, dessen Ziel im Spiel nicht mehr
# existiert, darf NICHT still zu einer kleineren Flaeche fuehren - fuer ein Nutzerskript ist
# das ein 'attempt to call a nil value' ohne jede Erklaerung. Also: benennen und abbrechen.
if ($null -ne $karte) {
    $gebrochen = New-Object 'System.Collections.Generic.List[string]'
    foreach ($tabName in $karte.tabellen.PSObject.Properties.Name) {
        $eintrag = $karte.tabellen.$tabName
        foreach ($luaName in $eintrag.member.PSObject.Properties.Name) {
            $ziel = "$($eintrag.clr).$($eintrag.member.$luaName)"
            if (-not $verwendet.Contains($ziel)) { $gebrochen.Add("s1.$tabName.$luaName  ->  $ziel") }
        }
    }

    Liste "NEW, not in the map yet (add them with -UpdateMap):" $neuOhneKarte 'Cyan'

    if ($gebrochen.Count -gt 0) {
        Write-Host ""
        Write-Host ("  {0} PINNED NAME(S) NO LONGER HAVE A TARGET:" -f $gebrochen.Count) -ForegroundColor Red
        $gebrochen | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
        Write-Host ""
        Write-Host "  Each one is a user script that dies after the next game update." -ForegroundColor Red
        Write-Host "  Either point the target in surface\names.json at the new member name," -ForegroundColor Red
        Write-Host "  or remove the entry deliberately and name the change in the CHANGELOG." -ForegroundColor Red
        exit 1
    }
    Write-Host "  Name map: every pin has a target." -ForegroundColor Green
}

foreach ($p in @($OutCs, $OutLua, $OutDoc)) {
    $d = Split-Path $p -Parent
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText($OutCs,  $cs.ToString(),  $utf8)
[IO.File]::WriteAllText($OutLua, $lua.ToString(), $utf8)
# Die ZAHLEN gehoeren in die erzeugte Datei, nicht in handgepflegte Prosa. Sie standen
# an SIEBEN Stellen in fuenf Dateien und waren ueberall veraltet (449/53 gegen 460/55) -
# weil keine davon am Erzeuger hing. Wer sie braucht, verweist hierher.
# ⚠ DER KOPF MUSS SAGEN, DASS DIES EIN BEISPIEL IST. Die Datei listet die Fläche EINES
#   Spiels und sieht damit aus wie "die API von ScriptOne" - ist sie nicht. In jedem
#   anderen Spiel stehen dort andere Namen, und die findet der Wirt beim Start selbst.
#   Ohne diesen Absatz liest ein fremder Modder eine Liste, die er in seinem Spiel nie
#   wiederfindet, und haelt ScriptOne fuer ein Werkzeug fuer genau dieses eine Spiel.
$kopf = "# ScriptOne - a generated Lua surface, as an example`r`n`r`n" +
        ("**$nAkt actions + $nZus state values = " + ($nAkt + $nZus) + " bindings across $nMgr tables.**`r`n") +
        "*Generated by tools/Gen-Bindings.ps1 - do not maintain by hand. These numbers are the source;`r`n" +
        "other files should point here instead of repeating them.*`r`n`r`n" +
        "> **This is one game's surface, not the ScriptOne API.** The names below come from the game`r`n" +
        "> this file happened to be generated against, and they are here so you can see what a`r`n" +
        "> surface looks like and how it is written down. **Your game will list different names.**`r`n" +
        "> ScriptOne finds them in the game you actually have, when it starts, and writes the`r`n" +
        "> result to ``ScriptOne\surface.txt`` plus a readable reference in ``ScriptOne\documentation\``.`r`n" +
        "> The 13 ``s1.*`` core functions are the part that is the same everywhere - they are listed`r`n" +
        "> in ``docs/CORE-VS-GAME.md``.`r`n"
$mdText = $md.ToString()
if ($mdText.StartsWith("# ")) { $mdText = $mdText.Substring($mdText.IndexOf("`n") + 1) }
[IO.File]::WriteAllText($OutDoc, $kopf + $mdText, $utf8)

Write-Host ""
Write-Host "generated:" -ForegroundColor Green
Write-Host ("  {0}  ({1} managers, {2} actions, {3} state values)" -f (Split-Path $OutCs -Leaf), $nMgr, $nAkt, $nZus) -ForegroundColor Green
Write-Host ("  {0}   editor stubs" -f (Split-Path $OutLua -Leaf)) -ForegroundColor Green
Write-Host ("  {0}      reference" -f (Split-Path $OutDoc -Leaf)) -ForegroundColor Green

Liste "MANAGER not bound (would shadow a core function - give it a name in names.json):" $tabKollision 'Yellow'
Liste "member(s) NOT bound (name collision in Lua):"     $kollisionen 'Yellow'
Liste "member(s) dropped SILENTLY (invisible until now):" $verworfen  'Yellow'

# ---------------------------------------------------------------- Karte schreiben
if ($UpdateMap) {
    $aus = [ordered]@{
        _hinweis  = 'Die Lua-Namen sind ein VERTRAG mit den Skripten. Diese Karte haelt sie fest, ' +
                    'auch wenn das Spiel seine Member umbenennt: dann aendert sich hier der Zielname ' +
                    'und kein Nutzerskript bricht. Erzeugt mit Gen-Bindings.ps1 -UpdateMap.'
        erzeugt   = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        tabellen  = [ordered]@{}
    }
    foreach ($tabName in ($erzeugt.Keys | Sort-Object)) {
        $mitglieder = [ordered]@{}
        foreach ($luaName in ($erzeugt[$tabName].member.Keys | Sort-Object)) {
            $mitglieder[$luaName] = $erzeugt[$tabName].member[$luaName]
        }
        $aus.tabellen[$tabName] = [ordered]@{ clr = $erzeugt[$tabName].clr; member = $mitglieder }
    }
    $json = $aus | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($NameMap, $json, (New-Object Text.UTF8Encoding $false))
    Write-Host ""
    Write-Host ("  Name map written: {0} ({1} tables)" -f $NameMap, $erzeugt.Count) -ForegroundColor Green
    exit 0
}

exit 0
