# Builds AST into dist\. Needs Visual Studio or the VS Build Tools with a .NET Framework 4.7.1+ targeting pack and the
# C++ x64 tools, plus the Windows 10/11 SDK for makeappx.
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

# AST's items in Windows 11's short right-click menu: a small native handler and an unsigned package pointing at
# AST's install folder. Both are embedded in AST.exe, which unpacks and registers them when it's installed.
# makeappx gets /nv because the files the package names live in that folder, not in the package.
$menuSrc = Join-Path $root 'src\ShellMenu'
$menuOut = Join-Path $root 'src\AST\obj\menu'
$stage = Join-Path $menuOut 'package'
Remove-Item $menuOut -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $stage 'Assets') | Out-Null

$vsWithCpp = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if (-not $vsWithCpp) { throw 'The C++ build tools (MSVC x64) were not found.' }
$vcvars = Join-Path $vsWithCpp 'VC\Auxiliary\Build\vcvars64.bat'
$compile = "set `"PATH=$(Split-Path $vswhere);%PATH%`" && call `"$vcvars`" >nul && cl /nologo /O2 /LD /EHsc /MT /DUNICODE /D_UNICODE /Fo`"$menuOut\\`" `"$menuSrc\ASTMenu.cpp`" /link /DEF:`"$menuSrc\ASTMenu.def`" /OUT:`"$menuOut\ASTMenu.dll`" /IMPLIB:`"$menuOut\ASTMenu.lib`""
cmd /c $compile
if ($LASTEXITCODE -ne 0) { throw 'The File Explorer menu handler failed to build.' }

$version = [regex]::Match((Get-Content (Join-Path $root 'src\AST\AssemblyInfo.cs') -Raw), 'AssemblyVersion\("([\d.]+)"\)').Groups[1].Value
$manifest = (Get-Content (Join-Path $menuSrc 'AppxManifest.xml') -Raw).Replace('$VERSION$', $version)
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))
Add-Type -AssemblyName System.Drawing
$logo = New-Object System.Drawing.Bitmap 150, 150
$g = [System.Drawing.Graphics]::FromImage($logo)
$g.Clear([System.Drawing.Color]::FromArgb(28, 28, 28))
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(96, 205, 255))), 102, 18, 30, 114)
$g.Dispose()
$logo.Save((Join-Path $stage 'Assets\logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe was not found. Install the Windows SDK.' }
& $makeappx.FullName pack /o /nv /d $stage /p (Join-Path $menuOut 'ASTMenu.msix') | Out-Null
if ($LASTEXITCODE -ne 0) { & $makeappx.FullName pack /o /nv /d $stage /p (Join-Path $menuOut 'ASTMenu.msix'); throw 'The File Explorer menu package failed to build.' }

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
