#Requires -Version 7.0
<#
Fails when any project of the solution has a package (direct or transitive) with a known vulnerability. Nothing is suppressed:
fix it by upgrading, or pin a patched version as a direct reference (see the comments in the .csproj files).
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '../../Athena.NET.sln'
$output = dotnet list $solution package --vulnerable --include-transitive 2>&1 | Out-String
Write-Host $output
if ($LASTEXITCODE -ne 0) { throw 'dotnet list package failed' }
if ($output -match 'has the following vulnerable packages' -or $output -match 'NU190[1-4]') {
    throw 'vulnerable packages found (see above)'
}
Write-Host 'no vulnerable packages'
