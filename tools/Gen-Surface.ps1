<#
    Gen-Surface.ps1 - erzeugt die KANDIDATENLISTE fuer die Lua-Flaeche aus der Spiel-Assembly.

    WARUM ES DIESES SKRIPT GIBT
    Die Flaeche einer Skript-API von Hand zu pflegen ist die teuerste Variante: bei jedem
    Spiel-Update muss jemand raten, was sich verschoben hat. Deshalb ist die Flaeche hier eine
    DATEI (surface\schedule1.surface.json), und dieses Skript erzeugt ihren Kandidatenteil
    mechanisch aus der Assembly. Ein Skript kann nicht formulieren und damit auch nichts
    erfinden - im Gegensatz zu einem Menschen, der aus dem Gedaechtnis Signaturen abschreibt.

    WAS ES NICHT TUT
    Es schaltet NICHTS scharf. Jeder Eintrag kommt mit "include": false heraus. Das ist Absicht:
    gemessen sind 4557 Methoden flach bindbar, davon 591 auf Singletons - eine automatisch
    scharfgeschaltete Flaeche waere unbrauchbar und unwartbar. Kuratieren ist Handarbeit,
    FINDEN ist es nicht.

    AUSWAHLREGEL
    Aufgenommen wird nur, was OHNE Spieltyp ueber die Lua-Grenze passt: Parameter und Rueckgabe
    ausschliesslich Primitive, string oder enum. Das ist dieselbe Regel, an der der ganze Wirt
    haengt - kein Il2Cpp-Proxy darf ein Skript je erreichen.

    QUELLE IST DER MONO-SATZ, nicht der Il2Cpp-Proxysatz: nur dort stehen die echten
    Zugriffsmodifikatoren (die Proxies sind zu ~95 % public und wuerden Internes mitschleppen).
    Die Namen sind auf beiden Backends gleich, nur der Namensraum-Praefix unterscheidet sich.

    Aufruf:
        .\tools\Gen-Surface.ps1                     # schreibt surface\candidates.json
        .\tools\Gen-Surface.ps1 -Manager MoneyManager,TimeManager
        .\tools\Gen-Surface.ps1 -MinSurface 8       # nur Manager ab 8 erreichbaren Membern
#>
[CmdletBinding()]
param(
    # Kein Vorgabewert mit Benutzernamen - er kommt aus Local.props oder per -Assembly.
    [string]   $Assembly,
    # Gegenprobe-Satz. Erzeugt wird aus MONO (nur dort stehen echte Zugriffsmodifikatoren),
    # LAUFEN muss es auf IL2CPP - also wird jeder Kandidat gegen den Il2Cpp-Satz geprueft.
    # Ohne das erzeugt man Bindungen, die erst im Spiel als CS0117 auffallen.
    [string]   $Il2Cpp     = 'C:\Steam\steamapps\common\Schedule I\ScriptOne\interopgenerator\Assembly-CSharp.dll',
    [string]   $Cecil      = 'C:\Steam\steamapps\common\Schedule I\MelonLoader\net6\Mono.Cecil.dll',
    [string]   $Out,
    [string[]] $Manager,
    [int]      $MinSurface = 1,

    # ---- Punkt 6: SPIELWISSEN, das frueher fest verdrahtet war -----------------
    # Leer lassen heisst ERMITTELN. Die Werte sind nur da, um eine Ermittlung zu
    # ueberstimmen, die danebengreift - nicht, um sie zu ersetzen.
    #
    # Warum ueberhaupt ermitteln: mit diesen drei Angaben haengt der Erzeuger an
    # Schedule I. Werden sie gefunden statt gesetzt, laeuft dasselbe Werkzeug an
    # jedem Unity-Spiel, das seine Manager ueber generische Singleton-Basen fuehrt.
    [string[]] $RootNamespace,     # z. B. 'ScheduleOne'
    [string[]] $SingletonBase,     # z. B. 'Singleton`1','NetworkSingleton`1'
    [string]   $Il2CppPrefix       # z. B. 'Il2Cpp'
)

$ErrorActionPreference = 'Stop'

# Maschinenpfade stehen in Local.props (gitignored), nicht im Skript.
if (-not $Assembly) {
    $lp = Join-Path (Split-Path $PSScriptRoot -Parent) 'Local.props'
    if (Test-Path $lp) {
        $m = [regex]::Match((Get-Content $lp -Raw), '<MonoManagedPath>([^<]+)</MonoManagedPath>')
        if ($m.Success) { $Assembly = Join-Path $m.Groups[1].Value 'Assembly-CSharp.dll' }
    }
}
if (-not $Assembly) {
    Write-Host "  ABORT: no -Assembly given and no MonoManagedPath in Local.props." -ForegroundColor Red
    Write-Host "  Template: Local.props.example in the repository root."
    exit 1
}
if (-not $Out) { $Out = Join-Path (Split-Path $PSScriptRoot -Parent) 'surface\candidates.json' }

foreach ($p in @($Assembly, $Cecil)) {
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p" -ForegroundColor Red; exit 1 }
}
Add-Type -Path $Cecil

# ---------------------------------------------------------------- flach?

$primitiv = @(
    'System.String','System.Boolean','System.Void',
    'System.Int32','System.Int64','System.UInt32','System.UInt64','System.Byte','System.SByte',
    'System.Int16','System.UInt16','System.Single','System.Double'
)

function Get-FlachArt {
    param($TypeRef)
    if ($null -eq $TypeRef) { return $null }
    if ($primitiv -contains $TypeRef.FullName) { return $TypeRef.Name }
    try {
        $d = $TypeRef.Resolve()
        # Ein Enum geht als ZAHL ueber die Grenze. Der VOLLE Typname muss mit: C# verlangt an
        # einem Enum-Parameter einen echten Cast, ein int allein gibt CS1503. Cecil schreibt
        # geschachtelte Typen mit '/', C# mit '.'.
        if ($d -and $d.IsEnum) { return "enum:$($d.FullName.Replace('/', '.'))" }
    } catch { }
    return $null
}

# ---------------------------------------------------------------- Rauschfilter
# Drei Familien sind SYSTEMATISCH kein Flaechenmaterial. Sie von Hand auszusortieren waere
# Fleissarbeit mit Fehlerquote; als Muster sind sie exakt greifbar. Was hier haengenbleibt,
# wird GEZAEHLT und am Ende gemeldet - eine stille Kappung liest sich sonst wie Vollstaendigkeit.
$rauschen = @(
    # 1. ISaveable-Klempnerei: Persistenz des Spiels, kein Skript hat dort etwas zu suchen
    '^InitializeSaveable$', '^GetSaveString$', '^SaveFolderName$', '^SaveFileName$',
    '^ShouldSaveUnderFolder$', '^HasChanged$', '^LoadOrder$', '^WriteData$', '^SaveError',
    '^LocalExtraFiles$', '^LocalExtraFolders$',
    # 2. FishNet-Netzwerkinnereien: vom Codegenerator erzeugt, teils mit Hash im Namen
    '^OnStart(Server|Client)$', '^OnStop(Server|Client)$', '^NetworkInitialize',
    '^RpcLogic', '^RpcWriter', '^RpcReader', '^SyncAccessor_', '^Awake_',
    '^Dispose$', '^ReadSyncVar', '^Observers[A-Z]', '^Target[A-Z].*Rpc',
    # 3. Text-/Farbhelfer fuer die Spiel-UI: geben markiertes Markup zurueck, nicht Spielzustand
    '^ApplyMoneyTextColor', '^ApplyOnlineBalanceColor', '^Format.*Color$'
)
$gefiltert = 0
function Ist-Rauschen {
    param([string] $Name)
    foreach ($muster in $rauschen) {
        # -cmatch: case-SENSITIV. Die Muster sind auf CLR-Namen gemuenzt, und ein
        # case-insensitiver Treffer wuerde hier still zu viel wegwerfen.
        if ($Name -cmatch $muster) { return $true }
    }
    return $false
}

function ConvertTo-LuaName {
    param([string] $Name)
    # PascalCase -> snake_case; Ziffernfolgen bleiben zusammen
    $s = [Text.RegularExpressions.Regex]::Replace($Name, '(?<=[a-z0-9])(?=[A-Z])', '_')
    $s = [Text.RegularExpressions.Regex]::Replace($s,   '(?<=[A-Z])(?=[A-Z][a-z])', '_')
    return $s.ToLowerInvariant()
}

# ---------------------------------------------------------------- lesen

Write-Host "Reading $Assembly" -ForegroundColor Gray
$asm   = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Assembly)
$oeffentlich = @($asm.MainModule.GetTypes() | Where-Object { $_.IsPublic -or $_.IsNestedPublic })

# ---------------------------------------------------------------- 1. Auswahl der Typen
# WARNUNG: HIER STAND EINE NAMENSRAUM-ZAEHLUNG: "der Spielcode ist der Namensraum mit den
#   meisten oeffentlichen Typen". Sie faellt ERSATZLOS - nicht aus Sparsamkeit, sondern weil
#   sie an zwei Stellen falsch war und beide Male NICHT als Fehler auffiel:
#     * Sehr viele Unity-Spiele legen ihren Code ohne Namensraum ab. Die Zaehlung uebersprang
#       leere Namensraeume und brach danach mit "No namespace found" ab - an einem Spiel,
#       dessen Code vollstaendig vorlag. Ein leerer Wurzelname ist kein fehlender Code.
#     * Liegt der Spielcode in ZWEI Wurzeln, waehlte sie eine und verwarf die andere still.
#       Und ist ein Asset-Store-Paket groesser als der Spielcode, waehlte sie das Paket.
#   Die Assembly IST bereits die Auswahl - es ist die Spielassembly. Ein zweiter Filter
#   darueber gewinnt nichts und kann nur danebengreifen.
#   -RootNamespace bleibt als freiwillige EINSCHRAENKUNG (wer eine riesige Assembly bewusst
#   beschneiden will), ist aber kein Ermittlungsschritt mehr.
$alle = $oeffentlich
if ($RootNamespace) {
    $nsMuster = @($RootNamespace | ForEach-Object { "$_*" })
    $alle = @($oeffentlich | Where-Object { $ns = $_.Namespace; @($nsMuster | Where-Object { $ns -like $_ }).Count -gt 0 })
    Write-Host ("-RootNamespace given: narrowed to {0} of {1} public types" -f $alle.Count, $oeffentlich.Count) -ForegroundColor Gray
} else {
    Write-Host ("{0} public types (no namespace filter)" -f $alle.Count) -ForegroundColor Gray
}

# ---------------------------------------------------------------- 2. Einstiegspunkte
# Ein Typ ist Einstiegspunkt, wenn ueber seine EIGENE BASISKETTE ein statisches 'Instance'
# erreichbar ist. Mehr ist das Merkmal nicht.
#
# WARNUNG: FRUEHER war zusaetzlich verlangt, dass dieses 'Instance' aus einer GENERISCHEN
#   Basis kommt (Singleton`1). Das ist die Konvention DIESES Spiels, nicht das Merkmal. Der
#   mit Abstand haeufigste Unity-Singleton deklariert sein 'public static Foo Instance' im
#   Typ SELBST und hat gar keine Basis - der fiel restlos durch, und das Skript brach mit
#   "No generic base class with a static 'Instance' found" ab und riet, -SingletonBase von
#   Hand zu setzen. Ein Spiel ohne generische Singleton-Basis war damit gar nicht bedienbar.
#   Die generische Basis ist ab jetzt EIN Fall von mehreren, kein Eintrittskriterium.
$nachName = @{}
foreach ($t in $asm.MainModule.GetTypes()) { $nachName[$t.FullName] = $t }

function Get-BasisDefinition($bt) {
    if ($null -eq $bt) { return $null }
    if ($bt -is [Mono.Cecil.GenericInstanceType]) { return $bt.ElementType.FullName }
    return $bt.FullName
}

# Gibt den vollen Namen des Typs zurueck, der das statische 'Instance' DEKLARIERT - oder
# $null. Der Rueckgabewert ist wichtiger als ein blosses Ja/Nein: er entscheidet spaeter,
# WIE die Instanz erreicht wird.
# WARNUNG: Die BASISKETTE mitgehen. Erste Fassung pruefte nur den Typ selbst - damit fiel
#   'PersistentSingleton`1' durch, weil es sein 'Instance' von 'Singleton`1' ERBT und
#   selbst keines deklariert. Ergebnis waren 46 statt 54 Manager: acht Einstiegspunkte
#   lautlos verloren. Aufgefallen nur, weil gegen den alten Stand verglichen wurde - der
#   Lauf selbst meldete Erfolg.
# Rueckgabe: @{ Typ = <voller Name des Deklarierenden>; Member = <EXAKTE Schreibweise> }
# WARNUNG: ZWEI Dinge, die hier gemessen schiefgingen.
#   1. Die Schreibweise MUSS mitgegeben werden. PowerShells '-eq' ist CASE-INSENSITIV,
#      C# ist es nicht: vier Typen dieses Spiels fuehren ihr Singleton als 'instance'
#      klein. Der Erzeuger fand sie, schrieb '.Instance' - und der Bau brach mit CS0117
#      "enthaelt keine Definition fuer Instance" an einem Typ ab, der sie sehr wohl hat,
#      nur anders geschrieben. Der Vergleich bleibt bewusst unscharf (wir wollen beide
#      Schreibweisen finden), aber ERZEUGT wird ausschliesslich der gelesene Name.
#   2. Nur OEFFENTLICHE Member zaehlen. Ein privates statisches 'Instance' erfuellt das
#      Merkmal formal und ist von der Flaeche aus nicht erreichbar - das gibt denselben
#      CS0117 eine Ebene spaeter.
function Get-InstanzHerkunft($def) {
    $tiefe = 0
    while ($null -ne $def -and $tiefe -lt 16) {
        foreach ($p in $def.Properties) {
            if ($p.Name -eq 'Instance' -and $p.GetMethod -and $p.GetMethod.IsStatic -and $p.GetMethod.IsPublic) {
                return @{ Typ = $def.FullName; Member = $p.Name }
            }
        }
        foreach ($f in $def.Fields) {
            if ($f.Name -eq 'Instance' -and $f.IsStatic -and $f.IsPublic) {
                return @{ Typ = $def.FullName; Member = $f.Name }
            }
        }
        $bn = Get-BasisDefinition $def.BaseType
        if (-not $bn -or -not $nachName.ContainsKey($bn)) { return $null }
        $def = $nachName[$bn]
        $tiefe++
    }
    return $null
}

$herkunftVon = @{}      # Managertyp (FullName) -> Typ, der 'Instance' deklariert
$instMember  = @{}      # Managertyp (FullName) -> EXAKTE Schreibweise des Members
$proHerkunft = @{}      # dieser Typ -> wie viele Manager haengen daran
$treffer     = New-Object System.Collections.Generic.List[object]
foreach ($t in $alle) {
    # Ein GENERISCHER Typ hat keine nennbare Instanz und keinen Alias-faehigen Namen -
    # der Traeger des Merkmals (Singleton`1 selbst) ist kein Einstiegspunkt.
    if ($t.HasGenericParameters) { continue }
    $h = Get-InstanzHerkunft $t
    if (-not $h) { continue }
    $herkunftVon[$t.FullName]  = $h.Typ
    $instMember[$t.FullName]   = $h.Member
    if (-not $proHerkunft.ContainsKey($h.Typ)) { $proHerkunft[$h.Typ] = 0 }
    $proHerkunft[$h.Typ]++
    $treffer.Add($t)
}
# WARNUNG: NICHT $singletons = @($treffer). Auf einer List[object] WIRFT der
#   Array-Unterausdruck (ArgumentException, 'Die Argumenttypen stimmen nicht ueberein') -
#   anders als auf jeder anderen Aufzaehlung, wo er noetig waere. .ToArray() geht immer.
$singletons = $treffer.ToArray()

if ($singletons.Count -eq 0) {
    Write-Host "No type with a static 'Instance' reachable through its own base chain." -ForegroundColor Red
    Write-Host "This game arranges its entry points differently - there is nothing to bind here." -ForegroundColor Red
    exit 1
}

# -SingletonBase schraenkt weiterhin ein, wirkt jetzt aber auf die ERMITTELTE Herkunft.
$singletonNs = @()
if ($SingletonBase) {
    $erlaubt = @($SingletonBase | ForEach-Object { ($_ -split '\.')[-1] })
    $singletons = @($singletons | Where-Object {
        $h = $herkunftVon[$_.FullName]
        $erlaubt -contains (($h -split '\.')[-1])
    })
    Write-Host ("-SingletonBase given: narrowed to {0} entry points" -f $singletons.Count) -ForegroundColor Gray
}
if ($Manager) { $singletons = @($singletons | Where-Object { $Manager -contains $_.Name }) }

# Bericht: woher kommt das 'Instance'? Das ist die Stelle, an der man einem FREMDEN Spiel
# ansieht, ob die Ermittlung getroffen hat - eine blosse Trefferzahl sagt das nicht.
# WARNUNG: singleton_bases darf NICHT nur die DEKLARIERENDEN Typen enthalten. Gen-Bindings
#   baut den Alias aus der DIREKTEN Basis des Managers ('PersistentSingleton<GameInput>'),
#   und PersistentSingleton`1 deklariert sein 'Instance' gar nicht - es ERBT es von
#   Singleton`1. Eine Liste nur aus Deklarierenden liess damit jeden Manager unter
#   PersistentSingleton durchfallen: 106 gepinnte Namen verloren ihr Ziel, und zwar per
#   'continue' ohne eine einzige Meldung. Aufgefallen nur am Abgleich gegen die Karte.
#   Aufgenommen wird deshalb JEDE generische Basis, die auf dem Weg zum 'Instance' liegt -
#   die deklarierende eingeschlossen.
$basisNs = @{}
function Add-Basis($voll) {
    if (-not $voll) { return }
    $kurz = ($voll -split '\.')[-1]
    $raum = if ($voll.Contains('.')) { $voll.Substring(0, $voll.LastIndexOf('.')) } else { '' }
    $basisNs[$kurz] = $raum
}
foreach ($t in $singletons) {
    $ziel = $herkunftVon[$t.FullName]
    $d = $t
    $tiefe = 0
    while ($null -ne $d -and $tiefe -lt 16) {
        $bn = Get-BasisDefinition $d.BaseType
        if (-not $bn -or -not $nachName.ContainsKey($bn)) { break }
        Add-Basis $bn
        if ($bn -eq $ziel) { break }
        $d = $nachName[$bn]
        $tiefe++
    }
    # Deklariert der Typ sein 'Instance' selbst, gibt es gar keine Basis - dann steht er
    # selbst in der Liste, damit Gen-Bindings ihn wiederfindet.
    if ($ziel -eq $t.FullName) { Add-Basis $ziel }
}
$singletonNs = @($basisNs.Values | Sort-Object -Unique)
$oben = @($proHerkunft.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 5 |
          ForEach-Object { "{0}={1}" -f (($_.Key -split '\.')[-1]), $_.Value })
Write-Host ("{0} entry points; instance declared by: {1}" -f $singletons.Count, ($oben -join ', ')) -ForegroundColor Gray
$selbst = @($singletons | Where-Object { $herkunftVon[$_.FullName] -eq $_.FullName }).Count
if ($selbst -gt 0) {
    Write-Host ("  {0} of them declare 'Instance' themselves (no singleton base at all)" -f $selbst) -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- Il2Cpp-Gegenprobe
# Ein Nachschlagewerk "Typ -> Menge der Membernamen" aus dem Il2Cpp-Satz. Alles, was der
# Mono-Satz kennt und dieser hier nicht, wuerde als CS0117 erst beim Bauen auffallen - oder
# schlimmer: gar nicht, weil es nur unter Mono existiert.
$il2Index = @{}
$il2Kurz  = @{}
$il2Fehlt = New-Object System.Collections.Generic.List[string]
if (Test-Path $Il2Cpp) {
    $il2asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Il2Cpp)
    # WARNUNG: Das Praefix ERMITTELN statt annehmen: Il2CppInterop setzt es je nach
    #   Betriebsart (OptOut/OptIn) - erzeugt also 'Il2CppFoo.*' ODER 'Foo.*'. Frueher wurde
    #   dafuer der Wurzelnamensraum gebraucht; ohne ihn wird ueber ALLE Namensraeume
    #   gezaehlt, was dieselbe Frage beantwortet und ohne Spielwissen auskommt.
    if (-not $Il2CppPrefix) {
        $mitPraefix = @($il2asm.MainModule.GetTypes() | Where-Object { $_.Namespace -like 'Il2Cpp*' }).Count
        $ohne       = @($il2asm.MainModule.GetTypes() | Where-Object { $_.Namespace -notlike 'Il2Cpp*' }).Count
        $Il2CppPrefix = if ($mitPraefix -gt $ohne) { 'Il2Cpp' } else { '' }
        Write-Host ("Il2Cpp prefix determined: '{0}' ({1} types with it, {2} without)" -f $Il2CppPrefix, $mitPraefix, $ohne) -ForegroundColor Gray
    }
    # WARNUNG: NACH VOLLEM NAMEN indizieren, nicht nach dem einfachen. Bei einer Messung am
    #   selben Satz kostete der einfache Name 85 Fehlbefunde: gleichnamige Typen aus
    #   verschiedenen Namensraeumen ueberschrieben einander in der Tabelle. Der einfache
    #   Name bleibt nur als Rueckfallebene - und dann nur, wenn er EINDEUTIG ist.
    foreach ($t in $il2asm.MainModule.GetTypes()) {
        $set = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($m in $t.Methods)    { [void]$set.Add($m.Name) }
        foreach ($pp in $t.Properties){ [void]$set.Add($pp.Name) }
        foreach ($f in $t.Fields)     { [void]$set.Add($f.Name) }
        foreach ($e in $t.Events)     { [void]$set.Add($e.Name) }
        $voll = $t.FullName -replace '/', '+'
        if ($Il2CppPrefix -and $voll.StartsWith($Il2CppPrefix)) { $voll = $voll.Substring($Il2CppPrefix.Length) }
        $il2Index[$voll] = $set
        if ($il2Kurz.ContainsKey($t.Name)) { $il2Kurz[$t.Name] = $null }   # $null = mehrdeutig
        else { $il2Kurz[$t.Name] = $set }
    }
    $il2asm.Dispose()
    Write-Host "Il2Cpp cross-check active: $($il2Index.Count) types in the comparison set" -ForegroundColor Gray
} else {
    Write-Host "NOTE[UNVERIFIED]: Il2Cpp set not found ($Il2Cpp) - no cross-check!" -ForegroundColor Yellow
}

function Test-Il2Cpp {
    # WARNUNG: Der Aufrufer gibt den VOLLEN Namen. Frueher wurde nach dem einfachen Namen
    #   nachgeschlagen - dabei treffen gleichnamige Typen aus verschiedenen Namensraeumen
    #   aufeinander, und die Gegenprobe prueft dann einen anderen Typ als den gemeinten.
    #   Der einfache Name bleibt Rueckfallebene, aber nur wenn er EINDEUTIG ist ($null in
    #   $il2Kurz heisst mehrdeutig); sonst wird 'ungeprueft' gemeldet statt geraten.
    param([string] $Voll, [string] $Kurz, [string] $Member)
    if ($il2Index.Count -eq 0) { return $null }   # null = ungeprueft, NICHT "in Ordnung"
    $set = $null
    $schluessel = $Voll -replace '/', '+'
    if ($il2Index.ContainsKey($schluessel)) { $set = $il2Index[$schluessel] }
    elseif ($il2Kurz.ContainsKey($Kurz)) {
        if ($null -eq $il2Kurz[$Kurz]) { return $null }   # mehrdeutig - nicht raten
        $set = $il2Kurz[$Kurz]
    }
    if ($null -eq $set) { return $false }
    return ($set.Contains($Member) -or $set.Contains("get_$Member") -or $set.Contains("set_$Member"))
}

$ergebnis = New-Object System.Collections.Generic.List[object]

foreach ($t in ($singletons | Sort-Object Name)) {

    $aktionen = New-Object System.Collections.Generic.List[object]
    $zustand  = New-Object System.Collections.Generic.List[object]
    $events   = New-Object System.Collections.Generic.List[object]

    foreach ($m in $t.Methods) {
        if (-not $m.IsPublic)  { continue }
        if ($m.IsConstructor -or $m.IsGetter -or $m.IsSetter -or $m.IsAddOn -or $m.IsRemoveOn) { continue }
        if ($m.HasGenericParameters) { continue }          # generische Aufrufe gehen nicht nach Lua
        if ($m.Name -cmatch '^(Awake|Start|Update|LateUpdate|OnDestroy|OnEnable|OnDisable)$') { continue }
        if (Ist-Rauschen $m.Name) { $script:gefiltert++; continue }

        $ret = Get-FlachArt $m.ReturnType
        if (-not $ret) { continue }

        $ps = @()
        $ok = $true
        foreach ($p in $m.Parameters) {
            $art = Get-FlachArt $p.ParameterType
            # out/ref waeren an der Lua-Grenze eine eigene Baustelle - hier bewusst ausgeschlossen.
            if (-not $art -or $p.IsOut -or $p.ParameterType.IsByReference) { $ok = $false; break }
            $ps += [ordered]@{ name = $p.Name; type = $art }
        }
        if (-not $ok) { continue }

        $imIl2 = Test-Il2Cpp $t.FullName $t.Name $m.Name
        if ($imIl2 -eq $false) { $il2Fehlt.Add("$($t.Name).$($m.Name)()"); continue }
        $aktionen.Add([ordered]@{
            include  = $false
            both     = [bool] $imIl2
            lua      = ConvertTo-LuaName $m.Name
            clr      = $m.Name
            static   = [bool] $m.IsStatic
            returns  = $ret
            args     = $ps
        })
    }

    foreach ($p in $t.Properties) {
        if (-not ($p.GetMethod -and $p.GetMethod.IsPublic)) { continue }
        $art = Get-FlachArt $p.PropertyType
        if (-not $art) { continue }
        if (Ist-Rauschen $p.Name) { $script:gefiltert++; continue }
        $imIl2p = Test-Il2Cpp $t.FullName $t.Name $p.Name
        if ($imIl2p -eq $false) { $il2Fehlt.Add("$($t.Name).$($p.Name)"); continue }
        $zustand.Add([ordered]@{
            include  = $false
            both     = [bool] $imIl2p
            lua      = ConvertTo-LuaName $p.Name
            clr      = $p.Name
            static   = [bool] $p.GetMethod.IsStatic
            type     = $art
            writable = [bool] ($p.SetMethod -and $p.SetMethod.IsPublic)
        })
    }

    # Events sind der knappe Rohstoff: was hier steht, braucht KEINEN Harmony-Patch im Wirt.
    foreach ($e in $t.Events) {
        if (-not ($e.AddMethod -and $e.AddMethod.IsPublic)) { continue }
        $events.Add([ordered]@{
            include = $false
            lua     = ConvertTo-LuaName $e.Name
            clr     = $e.Name
            static  = [bool] $e.AddMethod.IsStatic
            handler = $e.EventType.Name
        })
    }

    $summe = $aktionen.Count + $zustand.Count
    if ($summe -lt $MinSurface) { continue }

    $ergebnis.Add([ordered]@{
        manager   = $t.Name
        clr       = $t.FullName
        base      = $(if ($t.BaseType) { $t.BaseType.Name } else { '' })
        # WIE die Instanz erreicht wird - ohne das kann Gen-Bindings nur den
        # Singleton`1-Fall bedienen und verwirft alles andere stillschweigend.
        instance_owner   = $herkunftVon[$t.FullName]
        instance_member  = $instMember[$t.FullName]
        instance_generic = [bool]($nachName.ContainsKey($herkunftVon[$t.FullName]) -and $nachName[$herkunftVon[$t.FullName]].HasGenericParameters)
        lua       = ConvertTo-LuaName $t.Name
        counts    = [ordered]@{ actions = $aktionen.Count; state = $zustand.Count; events = $events.Count }
        actions   = $aktionen
        state     = $zustand
        events    = $events
    })
}

# ---------------------------------------------------------------- schreiben

$doc = [ordered]@{
    generated_from = [IO.Path]::GetFileName($Assembly)
    note           = 'KANDIDATEN, nichts davon ist scharf. include:true setzen, was in die Flaeche soll.'
    # Punkt 6: was frueher in BEIDEN Skripten verdrahtet war, wird hier ERMITTELT und
    # weitergereicht. Gen-Bindings.ps1 liest es, statt es noch einmal zu wissen - sonst
    # haette man das Spielwissen an zwei Stellen und merkt die Drift nie.
    spiel          = [ordered]@{
        # Leer heisst: es wurde NICHT gefiltert. Das ist der Normalfall und kein Mangel -
        # frueher stand hier ein geratener Wurzelnamensraum.
        root_namespace   = @($RootNamespace)
        il2cpp_prefix    = "$Il2CppPrefix"
        singleton_bases  = [ordered]@{}
    }
    totals         = [ordered]@{
        managers = $ergebnis.Count
        actions  = (@($ergebnis | ForEach-Object { $_.counts.actions }) | Measure-Object -Sum).Sum
        state    = (@($ergebnis | ForEach-Object { $_.counts.state })   | Measure-Object -Sum).Sum
        events   = (@($ergebnis | ForEach-Object { $_.counts.events })  | Measure-Object -Sum).Sum
    }
    managers       = $ergebnis
}

$dir = Split-Path $Out -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
# -Depth ist Pflicht: die Vorgabe 2 wuerde die Argumentlisten zu "System.Object[]" eindampfen.
foreach ($b in ($basisNs.Keys | Sort-Object)) { $doc.spiel.singleton_bases[$b] = $basisNs[$b] }
$json = $doc | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding $false))

$asm.Dispose()

Write-Host ""
Write-Host "written: $Out" -ForegroundColor Green
Write-Host ("  {0} managers, {1} actions, {2} state values, {3} events - all include:false" -f `
    $doc.totals.managers, $doc.totals.actions, $doc.totals.state, $doc.totals.events) -ForegroundColor Green
Write-Host ("  {0} member(s) filtered out as plumbing (ISaveable / FishNet / UI text colours)" -f $gefiltert) -ForegroundColor DarkGray
if ($il2Fehlt.Count -gt 0) {
    Write-Host ("  {0} member(s) dropped because they are MISSING from the Il2Cpp set (Mono only):" -f $il2Fehlt.Count) -ForegroundColor Yellow
    $il2Fehlt | Select-Object -First 15 | ForEach-Object { Write-Host "     $_" -ForegroundColor DarkYellow }
    if ($il2Fehlt.Count -gt 15) { Write-Host ("     ... and {0} more" -f ($il2Fehlt.Count - 15)) -ForegroundColor DarkYellow }
} elseif ($il2Index.Count -gt 0) {
    Write-Host "  Il2Cpp cross-check: not a single difference." -ForegroundColor Green
}
exit 0
