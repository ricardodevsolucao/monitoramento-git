# Gera app.ico (16/32/48/256, PNG embutido): medidor branco sobre quadrado arredondado #0F766E.
param([string]$Saida = (Join-Path $PSScriptRoot '..\app.ico'))
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$tam) {
    $ss = 1024
    $bmp = New-Object System.Drawing.Bitmap $ss, $ss, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = 224; $m = 40; $w = $ss - 2 * $m
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $fundo = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(0, $ss)), ([System.Drawing.ColorTranslator]::FromHtml('#14998D')), ([System.Drawing.ColorTranslator]::FromHtml('#0F766E'))
    $g.FillPath($fundo, $path)

    $cx = 512; $cy = 600; $raio = 290
    $trilho = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), 92
    $trilho.StartCap = 'Round'; $trilho.EndCap = 'Round'
    $arco = [System.Drawing.RectangleF]::new($cx - $raio, $cy - $raio, 2 * $raio, 2 * $raio)
    $g.DrawArc($trilho, $arco, 180, 180)
    $ativo = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 92
    $ativo.StartCap = 'Round'; $ativo.EndCap = 'Round'
    $g.DrawArc($ativo, $arco, 180, 125)

    $ang = (180 + 125) * [Math]::PI / 180
    $px = $cx + [Math]::Cos($ang) * ($raio - 30); $py = $cy + [Math]::Sin($ang) * ($raio - 30)
    $agulha = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 70
    $agulha.StartCap = 'Round'; $agulha.EndCap = 'Round'
    $g.DrawLine($agulha, $cx, $cy, [single]$px, [single]$py)
    $g.FillEllipse([System.Drawing.Brushes]::White, $cx - 74, $cy - 74, 148, 148)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#0F766E'))), $cx - 30, $cy - 30, 60, 60)
    $g.Dispose()

    $out = New-Object System.Drawing.Bitmap $tam, $tam, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = 'HighQualityBicubic'; $g2.PixelOffsetMode = 'HighQuality'; $g2.SmoothingMode = 'HighQuality'
    $g2.DrawImage($bmp, 0, 0, $tam, $tam)
    $g2.Dispose(); $bmp.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $out.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
    return , $ms.ToArray()
}

$tamanhos = 16, 32, 48, 256
$imgs = foreach ($t in $tamanhos) { , (New-Frame $t) }
$fs = [System.IO.File]::Create($Saida)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$tamanhos.Count)
$offset = 6 + 16 * $tamanhos.Count
for ($i = 0; $i -lt $tamanhos.Count; $i++) {
    $t = $tamanhos[$i]; $d = $imgs[$i]
    $bw.Write([byte]($(if ($t -ge 256) { 0 } else { $t }))); $bw.Write([byte]($(if ($t -ge 256) { 0 } else { $t })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $imgs) { $bw.Write($d) }
$bw.Dispose(); $fs.Dispose()
Write-Host "Icone gerado em $Saida"
