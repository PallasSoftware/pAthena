#Requires -Version 7.0
<#
.SYNOPSIS
Publishes the four official server images for a release tag to GHCR. Unchanged servers are re-tagged (same digest, no rebuild).
.DESCRIPTION
Runs from the release workflow (publish job) with the tag checked out (rAthena submodule initialised) and an authenticated docker.
-PlanOnly prints the decisions without touching the registry.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Registry = 'ghcr.io/pallassoftware',
    [string]$SourceUrl = 'https://github.com/PallasSoftware/pAthena',
    [switch]$PlanOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseImages.psm1') -Force

function Get-RemoteDigest([string]$Reference) { Get-ImageDigest $Reference }

$commit = (git rev-parse "refs/tags/$Tag^{commit}").Trim()
$plan = Get-ImagePlan -Tag $Tag -GetPreviousDigest { param($app, $prev) Get-RemoteDigest "$Registry/pathena/$($app):$prev" }

$rows = foreach ($item in $plan) {
    $image = "$Registry/pathena/$($item.App)"
    # Safe retry: a version tag that already exists in the registry is never overwritten or rebuilt.
    $published = if ($PlanOnly) { $null } else { Get-RemoteDigest "$($image):$Tag" }
    if ($PlanOnly) { $digest = '-' }
    elseif ($published) { $item.Action = 'DONE'; $item.Reason = 'already published by an earlier attempt'; $digest = $published }
    elseif ($item.Action -eq 'RETAG') {
        # Same manifest, new tag: the digest must not change (verified inside), and nothing is rebuilt.
        $digest = Add-ImageTag -Image $image -Digest $item.PreviousDigest -Tag $Tag
    }
    else {
        $meta = Join-Path ([IO.Path]::GetTempPath()) "meta-$($item.App).json"
        docker buildx build --push --metadata-file $meta -f (Get-ImageDockerfile $item.App) `
            --build-arg "SOURCE_URL=$SourceUrl" --build-arg "SOURCE_REVISION=$commit" --build-arg "VERSION=$Tag" `
            --tag "$($image):$Tag" .
        if ($LASTEXITCODE -ne 0) { throw "building $image failed" }
        $digest = (Get-Content $meta -Raw | ConvertFrom-Json).'containerimage.digest'
    }
    "| $($item.App) | $($item.Action) | $($item.Reason) | ``$digest`` |"
}
$summary = @("## Official images $Tag", '', '| Server | Action | Reason | Digest |', '|---|---|---|---|') + $rows
$summary | Write-Host
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -Path $env:GITHUB_STEP_SUMMARY }
