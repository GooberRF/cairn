#requires -Version 5.1
<#
.SYNOPSIS
    Rebuilds win-x64\cairn-vorbis.dll, the Ogg Vorbis encoder Cairn's Sounds module uses.

.DESCRIPTION
    Downloads the official Xiph.Org source releases of libogg 1.3.5 and libvorbis 1.3.7, checks them against
    the SHA-256 sums Xiph.Org publishes, builds them unmodified as static libraries with MSVC (x64, Release,
    static C runtime, so the DLL needs nothing but KERNEL32), and links one DLL that exports the encoder
    functions listed in cairn-vorbis.def. The link uses /Brepro, so the same sources and tools give the same
    bytes. Both libraries are BSD-3-Clause; see THIRD-PARTY-NOTICES.md.

    Needs CMake 3.15 or later and Visual Studio 2019/2022 (or Build Tools) with the C++ x64 tools.

.PARAMETER VsPath
    The Visual Studio installation folder. Found with vswhere when omitted.

.PARAMETER CMake
    The cmake executable. Defaults to the one on PATH.

.PARAMETER WorkDir
    Where the sources are downloaded and built. Defaults to a folder under %TEMP%.

.EXAMPLE
    .\native\vorbis\build.ps1
#>
[CmdletBinding()]
param(
    [string] $VsPath,
    [string] $CMake = 'cmake',
    [string] $WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'cairn-vorbis-build')
)

$ErrorActionPreference = 'Stop'

$sources = @(
    @{ Name = 'libogg-1.3.5'; Url = 'https://downloads.xiph.org/releases/ogg/libogg-1.3.5.tar.gz';
       Sha256 = '0eb4b4b9420a0f51db142ba3f9c64b333f826532dc0f48c6410ae51f4799b664' },
    @{ Name = 'libvorbis-1.3.7'; Url = 'https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.gz';
       Sha256 = '0e982409a9c3fc82ee06e08205b1355e5c6aa4c36bca58146ef399621b0ce5ab' }
)

if (-not $VsPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found: pass -VsPath.' }
    $VsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $VsPath) { throw 'No Visual Studio with the C++ x64 tools was found: pass -VsPath.' }
}
$vcvars = Join-Path $VsPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars)) { throw "vcvars64.bat not found under $VsPath." }

if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Path $WorkDir | Out-Null

foreach ($s in $sources) {
    $archive = Join-Path $WorkDir ($s.Name + '.tar.gz')
    Write-Host "Downloading $($s.Url)"
    Invoke-WebRequest -Uri $s.Url -OutFile $archive -UseBasicParsing
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $s.Sha256) { throw "$($s.Name): SHA-256 $hash does not match the published $($s.Sha256)." }
    & tar.exe -xzf $archive -C $WorkDir
    if ($LASTEXITCODE -ne 0) { throw "Extracting $archive failed." }
}

$def = Join-Path $PSScriptRoot 'cairn-vorbis.def'
$out = Join-Path $WorkDir 'out'
$install = Join-Path $WorkDir 'install'
$common = '-G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTING=OFF ' +
          '-DINSTALL_DOCS=OFF -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded ' +
          '-DCMAKE_POLICY_VERSION_MINIMUM=3.5 ' + "-DCMAKE_INSTALL_PREFIX=`"$install`" -DCMAKE_PREFIX_PATH=`"$install`""
$script = @"
@echo off
call "$vcvars" >nul || exit /b 1
"$CMake" -S "$WorkDir\libogg-1.3.5" -B "$WorkDir\b-ogg" $common || exit /b 1
"$CMake" --build "$WorkDir\b-ogg" || exit /b 1
"$CMake" --install "$WorkDir\b-ogg" || exit /b 1
"$CMake" -S "$WorkDir\libvorbis-1.3.7" -B "$WorkDir\b-vorbis" $common || exit /b 1
"$CMake" --build "$WorkDir\b-vorbis" || exit /b 1
"$CMake" --install "$WorkDir\b-vorbis" || exit /b 1
mkdir "$out" 2>nul
link /NOLOGO /DLL /MACHINE:X64 /Brepro /OPT:REF /OPT:ICF /INCREMENTAL:NO /DEF:"$def" /OUT:"$out\cairn-vorbis.dll" "$install\lib\vorbisenc.lib" "$install\lib\vorbis.lib" "$install\lib\ogg.lib" || exit /b 1
"@
$cmd = Join-Path $WorkDir 'build.cmd'
Set-Content -LiteralPath $cmd -Value $script -Encoding ASCII
& cmd.exe /c $cmd
if ($LASTEXITCODE -ne 0) { throw "The native build failed (exit code $LASTEXITCODE)." }

$target = Join-Path $PSScriptRoot 'win-x64'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $out 'cairn-vorbis.dll') -Destination $target -Force
$dll = Join-Path $target 'cairn-vorbis.dll'
Write-Host ("Wrote {0} ({1:N0} bytes, SHA-256 {2})" -f $dll, (Get-Item -LiteralPath $dll).Length,
    (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant())
