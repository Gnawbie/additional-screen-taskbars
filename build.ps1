# Builds AST into dist\. Needs Visual Studio or the VS Build Tools with a .NET Framework 4.7.1+ targeting pack.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found.' }

# The app icon is generated so the repo needs no binary art tools.
$ico = Join-Path $root 'src\AST\app.ico'
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $sizes = 16, 32, 48, 256
    $images = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::FromArgb(28, 28, 28))
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(96, 205, 255))
        $g.FillRectangle($brush, [int]($s * 0.68), [int]($s * 0.12), [int]($s * 0.2), [int]($s * 0.76))
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }
    $out = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($ico))
    $out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $out.Write([byte]$dim); $out.Write([byte]$dim); $out.Write([byte]0); $out.Write([byte]0)
        $out.Write([uint16]1); $out.Write([uint16]32)
        $out.Write([uint32]$images[$i].Length); $out.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $out.Write([byte[]]$img) }
    $out.Close()
}

& $msbuild (Join-Path $root 'src\AST\AST.csproj') /nologo /v:m /p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw 'The app failed to build.' }
& $msbuild (Join-Path $root 'samples\plugins\ExampleStyle\ExampleStyle.csproj') /nologo /v:m /p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw 'The sample plugin failed to build.' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $root 'src\AST\bin\Release\AST.exe') (Join-Path $dist 'AST.exe') -Force
Copy-Item (Join-Path $root 'src\AST\bin\Release\AST.exe') (Join-Path $dist 'ASTSetup.exe') -Force
Copy-Item (Join-Path $root 'samples\plugins\ExampleStyle\bin\Release\ExampleStyle.dll') (Join-Path $dist 'ExampleStyle.dll') -Force
Get-ChildItem $dist | Select-Object Name, Length
