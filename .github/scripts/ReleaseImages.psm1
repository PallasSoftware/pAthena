Set-StrictMode -Version Latest

# Source paths that decide whether a server's image can change. The server's own Dockerfile lives under its src folder.
$script:ImagePaths = [ordered]@{
    loginserver = @('Directory.Build.props', 'src/Shared', 'src/LoginServer')
    charserver  = @('Directory.Build.props', 'src/Shared', 'src/CharServer', 'conf/templates/char_athena.conf', 'conf/templates/inter_athena.conf', 'conf/import')
    mapserver   = @('Directory.Build.props', 'src/Shared', 'src/WorldServer', 'src/MapServer', 'conf/templates/map_athena.conf', 'conf/world_partitions.json')
    worldserver = @('Directory.Build.props', 'src/WorldServer', 'src/MapServer', 'conf/world_partitions.json', 'legacy/rathena')
}
$script:Dockerfiles = @{
    loginserver = 'src/LoginServer/Dockerfile'; charserver = 'src/CharServer/Dockerfile'
    mapserver = 'src/MapServer/Dockerfile'; worldserver = 'src/WorldServer/Dockerfile'
}

function Get-ImageApps { @($script:ImagePaths.Keys) }
function Get-ImageDockerfile([string]$App) { $script:Dockerfiles[$App] }

function Get-PreviousReleaseTag {
    <# The highest v<major>.<minor>.<patch> tag below $Tag, or $null for the first release. #>
    param([Parameter(Mandatory)][string]$Tag, [string]$RepoDir = '.', [string[]]$AllTags)
    if ($Tag -cnotmatch '^v(\d+)\.(\d+)\.(\d+)$') { throw "release tag '$Tag' is not vMAJOR.MINOR.PATCH" }
    if (-not $AllTags) { $AllTags = @(git -C $RepoDir tag --list 'v*') }
    $key = { param($t) if ($t -cmatch '^v(\d+)\.(\d+)\.(\d+)$') { [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" } }
    $current = & $key $Tag
    $AllTags | Where-Object { $_ -cmatch '^v\d+\.\d+\.\d+$' } | Where-Object { (& $key $_) -lt $current } |
        Sort-Object { & $key $_ } -Descending | Select-Object -First 1
}

function Get-ImagePlan {
    <#
    BUILD when the server's sources changed since the previous release tag (or there is no usable previous image);
    RETAG when they did not: the previous image's manifest is tagged with the new version, same digest, no rebuild.
    $TestImage is injected (returns the previous image's digest or $null) so the decision is testable without a registry.
    #>
    param(
        [Parameter(Mandatory)][string]$Tag, [string]$RepoDir = '.', [string[]]$AllTags,
        [Parameter(Mandatory)][scriptblock]$GetPreviousDigest
    )
    $previous = Get-PreviousReleaseTag -Tag $Tag -RepoDir $RepoDir -AllTags $AllTags
    foreach ($app in Get-ImageApps) {
        $decision = [ordered]@{ App = $app; Action = 'BUILD'; Reason = 'first release'; PreviousTag = $previous; PreviousDigest = $null }
        if ($previous) {
            git -C $RepoDir diff --quiet "refs/tags/$previous" "refs/tags/$Tag" -- @($script:ImagePaths[$app])
            $changed = $LASTEXITCODE -ne 0
            if ($changed) { $decision.Reason = "sources changed since $previous" }
            else {
                $digest = & $GetPreviousDigest $app $previous
                if ($digest) { $decision.Action = 'RETAG'; $decision.Reason = "unchanged since $previous"; $decision.PreviousDigest = $digest }
                else { $decision.Reason = "unchanged since $previous but its image is not in the registry" }
            }
        }
        [pscustomobject]$decision
    }
}

function Get-ImageDigest {
    <# The digest the registry serves for a reference ($null when it does not exist). #>
    param([Parameter(Mandatory)][string]$Reference)
    $out = docker buildx imagetools inspect $Reference --format '{{json .Manifest}}' 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    ($out | ConvertFrom-Json).digest
}

function Add-ImageTag {
    <#
    Gives an existing image (by digest) another tag WITHOUT rebuilding and WITHOUT changing the digest, then proves it:
    the new tag must resolve to exactly the source digest, otherwise this throws. `--prefer-index=false` makes buildx copy a
    single-platform manifest as it is instead of wrapping it in a new index (which would be a different digest).
    $Create and $Resolve are injectable for tests.
    #>
    param(
        [Parameter(Mandatory)][string]$Image, [Parameter(Mandatory)][string]$Digest, [Parameter(Mandatory)][string]$Tag,
        [scriptblock]$Create = { param($Image, $Digest, $Tag) docker buildx imagetools create --prefer-index=false --tag "${Image}:$Tag" "$Image@$Digest"; if ($LASTEXITCODE -ne 0) { throw "re-tagging $Image failed" } },
        [scriptblock]$Resolve = { param($Reference) Get-ImageDigest $Reference }
    )
    & $Create $Image $Digest $Tag
    $actual = & $Resolve "${Image}:$Tag"
    if ($actual -cne $Digest) { throw "re-tagging ${Image}:$Tag changed the digest: expected $Digest, registry serves $actual" }
    $actual
}

Export-ModuleMember -Function Get-ImageDigest, Add-ImageTag, Get-ImageApps, Get-ImageDockerfile, Get-PreviousReleaseTag, Get-ImagePlan
