#Requires -Version 7.0
<#
.SYNOPSIS
Creates (or finds) the release tag of a commit on main and exports it as the step output `tag`.
.DESCRIPTION
Runs only for a push or manual run on main (never for a pull request) after the validation passed. See ReleaseVersion.psm1.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Commit)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseVersion.psm1') -Force
$tag = New-ReleaseTag -Commit $Commit
Write-Host "release $tag"
if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "tag=$tag" }
