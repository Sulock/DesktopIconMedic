param([switch]$Test, [string]$OutputDirectory = 'release')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Path $OutputDirectory,'assets','test-results' -Force | Out-Null

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 256,256
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(38,98,224))
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$pale = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(176,206,255))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(38,98,224)),13
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.FillEllipse($blue,4,4,248,248)
$graphics.FillRectangle($white,65,49,111,155)
$points = [System.Drawing.Point[]]@([System.Drawing.Point]::new(138,49),[System.Drawing.Point]::new(177,49),[System.Drawing.Point]::new(177,89))
$graphics.FillPolygon($blue,$points)
$fold = [System.Drawing.Point[]]@([System.Drawing.Point]::new(138,49),[System.Drawing.Point]::new(138,89),[System.Drawing.Point]::new(177,89))
$graphics.FillPolygon($pale,$fold)
$graphics.DrawLine($pen,86,143,110,167)
$graphics.DrawLine($pen,110,167,158,118)
$bitmap.Save((Join-Path $PSScriptRoot 'assets\app.png'),[System.Drawing.Imaging.ImageFormat]::Png)
$imageStream = New-Object System.IO.MemoryStream
$bitmap.Save($imageStream,[System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $imageStream.ToArray()
$iconStream = [System.IO.File]::Create((Join-Path $PSScriptRoot 'assets\app.ico'))
$writer = New-Object System.IO.BinaryWriter $iconStream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
$writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$pngBytes.Length); $writer.Write([uint32]22)
$writer.Write($pngBytes)
$writer.Dispose(); $imageStream.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $blue.Dispose(); $white.Dispose(); $pale.Dispose(); $pen.Dispose()

$sources = @(Get-ChildItem -LiteralPath 'src' -Filter '*.cs' | ForEach-Object { $_.FullName })
$output = Join-Path (Join-Path $PSScriptRoot $OutputDirectory) 'DesktopIconMedic.exe'
& $compiler '/nologo' '/target:winexe' '/platform:anycpu' '/optimize+' '/warn:4' "/out:$output" '/win32manifest:src\app.manifest' '/win32icon:assets\app.ico' '/r:System.dll' '/r:System.Core.dll' '/r:System.Xml.dll' '/r:Microsoft.CSharp.dll' '/r:System.Drawing.dll' '/r:System.Windows.Forms.dll' '/r:System.Runtime.Serialization.dll' $sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath 'src\app.config' -Destination ($output + '.config') -Force
if (Test-Path -LiteralPath 'README.md') { Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $OutputDirectory 'README.md') -Force }
Copy-Item -LiteralPath 'LICENSE' -Destination (Join-Path $OutputDirectory 'LICENSE') -Force
if ($Test) {
    $testOutput = Join-Path (Join-Path $PSScriptRoot $OutputDirectory) 'Tests.exe'
    & $compiler '/nologo' '/target:exe' '/platform:anycpu' '/optimize+' '/warn:4' "/out:$testOutput" '/r:System.dll' '/r:System.Core.dll' '/r:System.Drawing.dll' '/r:System.Windows.Forms.dll' '/r:System.Runtime.Serialization.dll' "/r:$output" 'tests\Tests.cs'
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $testOutput (Join-Path $PSScriptRoot 'test-results')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
Write-Output "Built: $output"
