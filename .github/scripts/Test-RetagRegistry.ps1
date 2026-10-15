#Requires -Version 7.0
<#
Proves against a REAL registry (a throw-away registry:2 on localhost, started by the workflow) that re-tagging an unchanged
single-platform image keeps its digest. Not part of the offline tests: it needs docker buildx with an insecure localhost registry.
#>
param([string]$Registry = 'localhost:5000')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseImages.psm1') -Force
$work = Join-Path ([IO.Path]::GetTempPath()) "retag-$([guid]::NewGuid())"
New-Item -ItemType Directory $work | Out-Null
try {
    Set-Content (Join-Path $work 'Dockerfile') "FROM scratch`nCOPY f /f`n"
    Set-Content (Join-Path $work 'f') 'x'
    $image = "$Registry/retag-test/app"
    docker buildx build --provenance=false --sbom=false --push --tag "${image}:v0.0.1" $work
    if ($LASTEXITCODE -ne 0) { throw 'build/push failed' }
    $digest = Get-ImageDigest "${image}:v0.0.1"
    if (-not $digest) { throw 'the pushed image has no digest' }
    $tagged = Add-ImageTag -Image $image -Digest $digest -Tag 'v0.0.2'
    if ($tagged -cne $digest) { throw 'digest changed' }
    Write-Host "v0.0.2 -> $tagged (same as v0.0.1)"
    # The default behaviour of buildx would wrap a single manifest in a new index; this documents why the flag is needed.
    docker buildx imagetools create --tag "${image}:wrapped" "$image@$digest" | Out-Null
    Write-Host "without --prefer-index=false: $(Get-ImageDigest "${image}:wrapped")"
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
