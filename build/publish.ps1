<#
  Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
  34 Middletown Ave, Atlantic Highlands, NJ 07716

  THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
  Aegis AO Soft LLC and Alexander Orlov.

  This code may be used, reproduced, modified, or distributed ONLY with the
  prior written permission of Aegis AO Soft LLC / Alexander Orlov.

  Author: Alexander Orlov
  Aegis AO Soft LLC
#>

<#
.SYNOPSIS
  Builds the shippable artifacts: a NuGet tool package and self-contained binaries.

.DESCRIPTION
  Two distribution channels, because they serve different users:
    - the .nupkg is for developers who already have the .NET SDK ("dotnet tool install -g")
    - the self-contained binaries are for everyone else: download one file, run it, no runtime

  Trimming is deliberately OFF. The tool resolves cultures through ICU and parses C# with Roslyn,
  both of which lean on reflection; a trimmed build fails at run time rather than at build time.

.PARAMETER Runtimes
  Which platforms to build. Defaults to all four supported ones.

.PARAMETER Version
  Overrides the package version.
#>
[CmdletBinding()]
param(
    [string[]]$Runtimes = @('win-x64', 'osx-arm64', 'osx-x64', 'linux-x64'),
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root 'src/Aegis.Localizer.Cli/Aegis.Localizer.Cli.csproj'
$artifacts = Join-Path $root 'artifacts'

$versionArg = if ($Version) { @("-p:Version=$Version") } else { @() }

if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

Write-Host 'Running tests...' -ForegroundColor Cyan
& dotnet test (Join-Path $root 'Aegis.Localizer.sln') -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; refusing to publish.' }

Write-Host 'Packing the dotnet tool...' -ForegroundColor Cyan
& dotnet pack $cli -c Release -o $artifacts -v q --nologo @versionArg
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }

foreach ($rid in $Runtimes) {
    Write-Host "Publishing $rid..." -ForegroundColor Cyan
    $out = Join-Path $artifacts $rid

    & dotnet publish $cli `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $out -v q --nologo @versionArg

    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    # appsettings.json ships empty; a key committed into an artifact would be a leak.
    Remove-Item (Join-Path $out 'appsettings.local.json') -ErrorAction SilentlyContinue

    $zip = Join-Path $artifacts "aegis-localizer-$rid.zip"
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
    Remove-Item -Recurse -Force $out

    Write-Host "  -> $zip" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Artifacts:' -ForegroundColor Cyan
Get-ChildItem $artifacts | ForEach-Object {
    '{0,-42} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
