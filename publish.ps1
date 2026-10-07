#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Cairn to dist/.

.DESCRIPTION
    By default this produces a self-contained, single-file win-x64 build that runs on a machine with
    no .NET installed. The single file is compressed (EnableCompressionInSingleFile) and ships only
    English resources (SatelliteResourceLanguages), which together roughly halve its size. IL
    trimming is deliberately not used: WPF resolves types by name from XAML, so a trimmer removes
    things the app still needs and the failure only shows up at runtime.

    With -FrameworkDependent the build instead targets an installed .NET 9 Desktop Runtime, which is
    a few megabytes rather than a hundred and is the better choice when you control the machines.

.PARAMETER FrameworkDependent
    Build against an installed .NET 9 Desktop Runtime instead of bundling one.

.PARAMETER Output
    Where to publish. Defaults to dist/ (or dist/framework-dependent for the smaller build).

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [switch] $FrameworkDependent,
    [string] $Output
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not $Output) {
    $Output = if ($FrameworkDependent) { 'dist/framework-dependent' } else { 'dist' }
}

$common = @(
    'publish', 'src/Cairn.Shell',
    '-c', 'Release',
    '-r', 'win-x64',
    '-p:PublishSingleFile=true',
    '-p:SatelliteResourceLanguages=en',
    '-p:PublishTrimmed=false',
    '-o', $Output
)

if ($FrameworkDependent) {
    # Compression in a single-file bundle only works for self-contained builds (NETSDK1176), and
    # there is little left to compress here anyway — the runtime is the part that is not shipped.
    $args = $common + @('--self-contained', 'false', '-p:EnableCompressionInSingleFile=false')
    Write-Host "Publishing framework-dependent (needs the .NET 9 Desktop Runtime) to $Output"
} else {
    $args = $common + @(
        '--self-contained', 'true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true')
    Write-Host "Publishing self-contained single file to $Output"
}

& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $Output 'Cairn.exe'
if (Test-Path -LiteralPath $exe) {
    $size = (Get-Item -LiteralPath $exe).Length
    $total = (Get-ChildItem -LiteralPath $Output -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host ("Cairn.exe         {0:N1} MB" -f ($size / 1MB))
    Write-Host ("output folder     {0:N1} MB" -f ($total / 1MB))
}
Write-Host "Published to $(Join-Path $PSScriptRoot $Output)"
