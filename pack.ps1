<#
.SYNOPSIS
  Packs RadSharp.Core, RadSharp.Platform.Sdl and RadSharp.Templates into artifacts/packages with ONE version.
.DESCRIPTION
  The version defaults to 0.1.0-dev.<yyyyMMddHHmm> (UTC), stamped once here, so Platform.Sdl's dependency on
  RadSharp.Core always names a package that exists. Projects that reference 0.1.0-dev* get the newest pack.
  Use this (or pack.sh) instead of separate `dotnet pack` runs.
#>
param(
    [string]$Version = "0.1.0-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmm'))",
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
foreach ($project in 'src/RadSharp.Core', 'src/RadSharp.Platform.Sdl', 'templates/RadSharp.Templates.csproj') {
    dotnet pack $project --configuration $Configuration "-p:RadSharpVersion=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack $project failed" }
}
Write-Host "Packed RadSharp $Version into artifacts/packages"
