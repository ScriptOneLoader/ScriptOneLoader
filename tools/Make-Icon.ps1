<#
    Make-Icon.ps1 - erzeugt das Dateisymbol des Installers.

    WARUM EIN SKRIPT UND KEINE GEZEICHNETE DATEI
    Das Zeichen entsteht im Code, damit es nur EINE Quelle gibt. Aendert sich die Optik,
    laeuft dieses Skript noch einmal und beide Orte - Fenster und Explorer - stimmen wieder.

    ⚠ ZWEI FORMATE IN EINEM CONTAINER, und das ist kein Stil:
    Ab Vista darf ein ICO-Eintrag ein komplettes PNG sein, und fuer 256x256 ist das der
    einzige vernuenftige Weg (ein DIB waere 256 KB). ABER: .NETs eigener Icon-Leser kann
    PNG-Eintraege NICHT dekodieren - `new Icon(datei, 48, 48).ToBitmap()` wirft dort
    "Der angeforderte Bereich geht ueber das Arrayende hinaus" (gemessen 2026-08-19, als
    ALLE sieben Groessen PNG waren). Windows Explorer kommt damit klar, andere Leser nicht.
    Deshalb: die kleinen Groessen als klassisches DIB, nur 128 und 256 als PNG.

    ⚠ Die Klammern werden als SCHRIFTZEICHEN gesetzt, nicht aus Boegen konstruiert. Von Hand
    gezeichnete Boegen lasen sich als runde Klammern; das Zeichen soll aber '{ }' sein.

    AUFRUF
        .\tools\Make-Icon.ps1
#>
[CmdletBinding()]
param([string] $Ziel)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $Ziel) {
    $wurzel = Split-Path $PSScriptRoot -Parent
    $Ziel = Join-Path $wurzel 'Standalone\Installer\Installer\ScriptOne.ico'
}

# Dieselben Farben wie das Kopfband im Fenster.
$tinte = [System.Drawing.Color]::FromArgb(0x1B, 0x24, 0x30)
$hell  = [System.Drawing.Color]::FromArgb(0x6E, 0xA8, 0xFF)
$text  = [System.Drawing.Color]::FromArgb(0xE6, 0xED, 0xF5)

function Zeichne([int] $k) {
    $bmp = New-Object System.Drawing.Bitmap($k, $k, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

        # Abgerundeter dunkler Grund - ohne ihn verschwindet das Zeichen auf hellen Themen.
        $r = [single]($k * 0.20)
        $pfad = New-Object System.Drawing.Drawing2D.GraphicsPath
        $pfad.AddArc(0, 0, 2*$r, 2*$r, 180, 90)
        $pfad.AddArc($k-2*$r-1, 0, 2*$r, 2*$r, 270, 90)
        $pfad.AddArc($k-2*$r-1, $k-2*$r-1, 2*$r, 2*$r, 0, 90)
        $pfad.AddArc(0, $k-2*$r-1, 2*$r, 2*$r, 90, 90)
        $pfad.CloseFigure()
        $b = New-Object System.Drawing.SolidBrush($tinte)
        $g.FillPath($b, $pfad)
        $b.Dispose(); $pfad.Dispose()

        $mitte = New-Object System.Drawing.StringFormat
        $mitte.Alignment     = [System.Drawing.StringAlignment]::Center
        $mitte.LineAlignment = [System.Drawing.StringAlignment]::Center

        # Die geschweiften Klammern, gross und aussen.
        $kf = New-Object System.Drawing.Font('Consolas', [single]($k * 0.78),
                                             [System.Drawing.FontStyle]::Regular,
                                             [System.Drawing.GraphicsUnit]::Pixel)
        $kb = New-Object System.Drawing.SolidBrush($hell)
        $g.DrawString('{', $kf, $kb, (New-Object System.Drawing.RectangleF(($k * -0.235), 0, $k, $k)), $mitte)
        $g.DrawString('}', $kf, $kb, (New-Object System.Drawing.RectangleF(($k *  0.235), 0, $k, $k)), $mitte)
        $kf.Dispose(); $kb.Dispose()

        # ⚠ Der Schriftzug erst ab 32 px: darunter wird er zu einem grauen Fleck und macht das
        #   Zeichen unleserlich, statt es zu erklaeren. Und schmal genug, damit er die Klammern
        #   nicht beruehrt - die erste Fassung ueberlappte die linke.
        if ($k -ge 32) {
            $tf = New-Object System.Drawing.Font('Segoe UI', [single]($k * 0.235),
                                                 [System.Drawing.FontStyle]::Bold,
                                                 [System.Drawing.GraphicsUnit]::Pixel)
            $tb = New-Object System.Drawing.SolidBrush($text)
            $g.DrawString('Lua', $tf, $tb, (New-Object System.Drawing.RectangleF(0, ($k * 0.02), $k, $k)), $mitte)
            $tf.Dispose(); $tb.Dispose()
        }
        $mitte.Dispose()
    }
    finally { $g.Dispose() }
    return $bmp
}

# ---- Einen Eintrag als klassisches DIB schreiben (BITMAPINFOHEADER + BGRA + AND-Maske).
function AlsDib([System.Drawing.Bitmap] $bmp) {
    $k = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w  = New-Object System.IO.BinaryWriter($ms)
    $w.Write([uint32]40)          # biSize
    $w.Write([int32]$k)           # biWidth
    $w.Write([int32]($k * 2))     # biHeight: XOR- UND AND-Maske
    $w.Write([uint16]1)           # biPlanes
    $w.Write([uint16]32)          # biBitCount
    $w.Write([uint32]0)           # BI_RGB
    $w.Write([uint32]($k * $k * 4))
    0..3 | ForEach-Object { $w.Write([uint32]0) }   # Aufloesung, Farben

    # Pixel von UNTEN nach oben, BGRA.
    for ($y = $k - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $k; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    # AND-Maske: 1 Bit je Pixel, auf 4 Byte je Zeile aufgerundet. Alles 0 = alles sichtbar,
    # die Transparenz steckt im Alphakanal.
    $zeile = [math]::Ceiling($k / 32) * 4
    for ($y = 0; $y -lt $k; $y++) { $w.Write((New-Object byte[] $zeile)) }
    $w.Flush()
    $bytes = $ms.ToArray()
    $w.Dispose(); $ms.Dispose()
    # ⚠ KOMMA-OPERATOR. Ohne ihn ENTROLLT PowerShell das Byte-Array beim Zurueckgeben in
    #   einzelne Bytes; der Aufrufer bekommt ein Object[], BinaryWriter.Write findet dafuer
    #   keine Ueberladung und schreibt Unsinn. Der Container sah danach richtig aus - die
    #   Verzeichniseintraege stimmten -, nur die Daten waren Muell (gemessen: erster Eintrag
    #   begann mit 01 01 01 ..., alle weiteren leer).
    return , $bytes
}

function AlsPng([System.Drawing.Bitmap] $bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

$groessen = @(16, 24, 32, 48, 64, 128, 256)
$bilder = @()
foreach ($k in $groessen) {
    $bmp = Zeichne $k
    # Klein = DIB (damit auch .NETs Icon-Leser es kann), gross = PNG (Groesse).
    $bytes = if ($k -ge 128) { AlsPng $bmp } else { AlsDib $bmp }
    $bilder += , @{ K = $k; Bytes = $bytes }
    $bmp.Dispose()
}

$aus = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($aus)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$bilder.Count)

$versatz = 6 + 16 * $bilder.Count
foreach ($b in $bilder) {
    # 256 wird als 0 kodiert - ein Byte kann 256 nicht darstellen.
    $kb = if ($b.K -ge 256) { 0 } else { $b.K }
    $w.Write([byte]$kb); $w.Write([byte]$kb)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$b.Bytes.Length)
    $w.Write([uint32]$versatz)
    $versatz += $b.Bytes.Length
}
foreach ($b in $bilder) { $w.Write([byte[]]$b.Bytes) }   # ausdruecklich als byte[]
$w.Flush()

$ordner = Split-Path $Ziel -Parent
if (-not (Test-Path $ordner)) { New-Item -ItemType Directory -Path $ordner -Force | Out-Null }
[System.IO.File]::WriteAllBytes($Ziel, $aus.ToArray())
$w.Dispose(); $aus.Dispose()

# ⚠ Zusicherung, nicht Vertrauen: der .NET-Leser MUSS die kleinen Groessen oeffnen koennen.
#   Genau das ging mit reinen PNG-Eintraegen nicht, und es faellt sonst erst beim Nutzer auf.
foreach ($k in @(16, 32, 48)) {
    $i = New-Object System.Drawing.Icon -ArgumentList $Ziel, $k, $k
    $b = $i.ToBitmap()
    if ($b.Width -ne $k) { throw "icon: .NET reader returned $($b.Width) px for $k px" }
    $b.Dispose(); $i.Dispose()
}
Write-Host ("  icon written: {0} ({1} sizes, {2:N0} bytes, small sizes readable by .NET)" -f `
            $Ziel, $bilder.Count, (Get-Item $Ziel).Length) -ForegroundColor Green
