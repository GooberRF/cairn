#requires -Version 5.1
<#
.SYNOPSIS
    Rebuilds win-x64\cairn-openmpt.dll, the module player Cairn's Sounds module uses for FastTracker 2 (.xm) music.

.DESCRIPTION
    Downloads the official source release of libopenmpt 0.8.9 from lib.openmpt.org, checks it against the SHA-256
    recorded below, compiles it unmodified with MSVC (x64, Release, static C runtime, so the DLL needs only Windows
    system DLLs) and links one DLL that exports the C API functions listed in cairn-openmpt.def. It is built with
    no optional dependencies: no zlib, mpg123, Ogg or Vorbis, and none of the bundled fallbacks (miniz, minimp3,
    stb_vorbis). FastTracker 2 modules need none of them; only compressed samples of other formats do. Compiling
    and linking use /Brepro, and source paths are trimmed from the objects, so the same sources and tools give the
    same bytes. libopenmpt is BSD-3-Clause; see THIRD-PARTY-NOTICES.md.

    lib.openmpt.org publishes no checksums. The SHA-256 below was taken from the file downloaded over HTTPS from
    the official site on 2026-11-10 (the release is dated 2026-08-19); a mismatch means the file changed.

    Needs Visual Studio 2022 (or Build Tools) with the C++ x64 tools (C++20).

.PARAMETER VsPath
    The Visual Studio installation folder. Found with vswhere when omitted.

.PARAMETER WorkDir
    Where the sources are downloaded and built. Defaults to a folder under %TEMP%.

.EXAMPLE
    .\native\openmpt\build.ps1
#>
[CmdletBinding()]
param(
    [string] $VsPath,
    [string] $WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'cairn-openmpt-build')
)

$ErrorActionPreference = 'Stop'

$source = @{ Name = 'libopenmpt-0.8.9+release';
             Url = 'https://lib.openmpt.org/files/libopenmpt/src/libopenmpt-0.8.9+release.makefile.tar.gz';
             Sha256 = '9273b88b67973cc69e54d748ab1b749399d6d07695f1c37d0c59f88b4106074f' }

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

$archive = Join-Path $WorkDir 'libopenmpt.tar.gz'
Write-Host "Downloading $($source.Url)"
Invoke-WebRequest -Uri $source.Url -OutFile $archive -UseBasicParsing
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $source.Sha256) { throw "$($source.Name): SHA-256 $hash does not match the recorded $($source.Sha256)." }
& tar.exe -xzf $archive -C $WorkDir
if ($LASTEXITCODE -ne 0) { throw "Extracting $archive failed." }

$src = Join-Path $WorkDir $source.Name
$obj = Join-Path $WorkDir 'obj'
$out = Join-Path $WorkDir 'out'
$def = Join-Path $PSScriptRoot 'cairn-openmpt.def'

# The library's own sources, as its Makefile lists them (LIBOPENMPT_CXX_SOURCES without the optional codecs).
$folders = @('common', 'soundlib', 'soundlib\plugins', 'soundlib\plugins\dmo', 'sounddsp', 'libopenmpt')
# The defines of the official Visual Studio project's Release build (a Unicode build), without its MPT_WITH_*
# dependencies.
$defines = '/DNDEBUG /DMPT_BUILD_MSVC /DWIN32 /DNOMINMAX /D_CRT_NONSTDC_NO_WARNINGS /D_CRT_SECURE_NO_WARNINGS ' +
           '/D_CRT_SECURE_CPP_OVERLOAD_STANDARD_NAMES=1 /D_CRT_SECURE_CPP_OVERLOAD_STANDARD_NAMES_COUNT=1 ' +
           '/D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000008 /DUNICODE /D_UNICODE /DLIBOPENMPT_BUILD'
$flags = '/nologo /c /MP /W1 /O2 /Oi /Gy /Gw /GF /GR /EHsc /MT /std:c++20 /permissive- /Zc:preprocessor /Brepro ' +
         "/d1trimfile:`"$src\\`" /I`"$src`" /I`"$src\src`" /I`"$src\common`""

$lines = @('@echo off', "call `"$vcvars`" >nul 2>&1 || exit /b 1", "mkdir `"$out`" 2>nul")
$objects = @()
foreach ($folder in $folders) {
    $dir = Join-Path $obj $folder
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $files = Get-ChildItem -LiteralPath (Join-Path $src $folder) -Filter '*.cpp' -File | Sort-Object Name
    if (-not $files) { throw "No sources found in $folder." }
    # a response file per folder: the command line would pass cmd.exe's length limit
    $clRsp = Join-Path $WorkDir ('cl-' + ($folder -replace '\\', '-') + '.rsp')
    Set-Content -LiteralPath $clRsp -Value (@("$flags $defines /Fo`"$dir\\`"") + ($files | ForEach-Object { "`"$($_.FullName)`"" })) -Encoding ASCII
    $lines += "cl @`"$clRsp`" || exit /b 1"
    $objects += $files | ForEach-Object { Join-Path $dir ($_.BaseName + '.obj') }
}
# Objects in a fixed order, so the link is reproducible.
$rsp = Join-Path $WorkDir 'objects.rsp'
Set-Content -LiteralPath $rsp -Value ($objects | ForEach-Object { "`"$_`"" }) -Encoding ASCII
$lines += "link /NOLOGO /DLL /MACHINE:X64 /Brepro /OPT:REF /OPT:ICF /INCREMENTAL:NO /DEF:`"$def`" /OUT:`"$out\cairn-openmpt.dll`" @`"$rsp`" || exit /b 1"

$cmd = Join-Path $WorkDir 'build.cmd'
Set-Content -LiteralPath $cmd -Value $lines -Encoding ASCII
# compiler output on stderr is not a failure (the exit code says): keep going when the output is redirected
$ErrorActionPreference = 'Continue'
& cmd.exe /c $cmd
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -ne 0) { throw "The native build failed (exit code $LASTEXITCODE)." }

$target = Join-Path $PSScriptRoot 'win-x64'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $out 'cairn-openmpt.dll') -Destination $target -Force
$dll = Join-Path $target 'cairn-openmpt.dll'
Write-Host ("Wrote {0} ({1:N0} bytes, SHA-256 {2})" -f $dll, (Get-Item -LiteralPath $dll).Length,
    (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant())
