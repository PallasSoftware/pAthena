#Requires -Version 7.0
<#
.SYNOPSIS
Creates the GitHub Release of a version tag, but only when all four official image tags exist in the registry.
.DESCRIPTION
The last step of the release workflow. It resolves the digest of every image tag first and refuses to create the release if one is
missing, so a release never advertises images that are not there. It is idempotent: an existing release of the tag is left alone
(a re-run of a failed release can finish here). The release notes list the immutable image references by digest.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Registry = 'ghcr.io/pallassoftware'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseImages.psm1') -Force

$lines = foreach ($app in Get-ImageApps) {
    $image = "$Registry/pathena/$app"
    $digest = Get-ImageDigest "${image}:$Tag"
    if (-not $digest) { throw "${image}:$Tag is not published; no GitHub Release is created" }
    "- ``${image}:$Tag`` -> ``$image@$digest``"
}
$notes = @("Official images of $Tag (use the digest for deployments):", '') + $lines
$notes | Write-Host

gh release view $Tag *> $null
if ($LASTEXITCODE -eq 0) { Write-Host "the GitHub Release $Tag already exists"; return }
$file = Join-Path ([IO.Path]::GetTempPath()) "release-notes-$([guid]::NewGuid()).md"
try {
    Set-Content -LiteralPath $file -Value ($notes -join "`n")
    gh release create $Tag --verify-tag --title $Tag --notes-file $file --generate-notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create $Tag failed" }
}
finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
