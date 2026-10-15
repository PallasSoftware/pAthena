Set-StrictMode -Version Latest

# Release versions: MAJOR.MINOR is controlled by hand in the VERSION file; PATCH is derived from the existing release tags
# (vMAJOR.MINOR.PATCH), so a push to main needs no version commit. A new MAJOR.MINOR series starts at PATCH 0, except 0.0,
# which starts at 0.0.1 (there is no 0.0.0: we are in beta).

function Read-VersionFile {
    param([string]$Path = 'VERSION')
    $lines = @(Get-Content -LiteralPath $Path | Where-Object { $_.Trim() -ne '' })
    if ($lines.Count -ne 1 -or $lines[0].Trim() -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "$Path must contain exactly one line MAJOR.MINOR such as 0.1" }
    $parts = $lines[0].Trim().Split('.')
    [pscustomobject]@{ Major = [int]$parts[0]; Minor = [int]$parts[1] }
}

function ConvertTo-ReleaseVersion([string]$Tag) {
    if ($Tag -cmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" }
}

function Get-NextVersion {
    <# The tag the next release gets, from the VERSION file's MAJOR.MINOR and the existing tags. Pure. #>
    param([Parameter(Mandatory)][int]$Major, [Parameter(Mandatory)][int]$Minor, [string[]]$Tags = @())
    $versions = @($Tags | ForEach-Object { ConvertTo-ReleaseVersion $_ } | Where-Object { $_ })
    if ($versions.Count -gt 0) {
        $highest = $versions | Sort-Object -Descending | Select-Object -First 1
        if (($Major -lt $highest.Major) -or ($Major -eq $highest.Major -and $Minor -lt $highest.Minor)) {
            throw "VERSION $Major.$Minor is lower than the existing release v$highest; MAJOR.MINOR must not go backwards"
        }
    }
    $series = @($versions | Where-Object { $_.Major -eq $Major -and $_.Minor -eq $Minor })
    $patch = if ($series.Count -gt 0) { ($series | Measure-Object -Property Build -Maximum).Maximum + 1 } elseif ($Major -eq 0 -and $Minor -eq 0) { 1 } else { 0 }
    "v$Major.$Minor.$patch"
}

function New-ReleaseTag {
    <#
    Tags $Commit with the next version and pushes the tag. A commit that already carries a release tag keeps it (a re-run
    publishes the same version). Tags are never moved or deleted: if another run took the version first, the push is
    rejected and the next free version is computed again.
    #>
    param([Parameter(Mandatory)][string]$Commit, [string]$VersionFile = 'VERSION', [string]$Remote = 'origin', [int]$Attempts = 5)
    $version = Read-VersionFile -Path $VersionFile
    for ($i = 1; $i -le $Attempts; $i++) {
        git fetch --quiet --tags --force $Remote
        if ($LASTEXITCODE -ne 0) { throw 'git fetch failed' }
        $existing = @(git tag --points-at $Commit | Where-Object { ConvertTo-ReleaseVersion $_ })
        if ($existing.Count -gt 1) { throw "commit $Commit carries several release tags: $($existing -join ', ')" }
        if ($existing.Count -eq 1) { return $existing[0] }
        $tag = Get-NextVersion -Major $version.Major -Minor $version.Minor -Tags @(git tag --list 'v*')
        git tag $tag $Commit
        if ($LASTEXITCODE -ne 0) { throw "git tag $tag failed" }
        git push --quiet $Remote "refs/tags/$tag" 2>$null
        if ($LASTEXITCODE -eq 0) { return $tag }
        git tag -d $tag | Out-Null   # the local tag only; the remote one belongs to the run that won
        Write-Host "tag $tag was taken by a concurrent run; trying the next version"
    }
    throw "could not create a release tag after $Attempts attempts"
}

Export-ModuleMember -Function Read-VersionFile, Get-NextVersion, New-ReleaseTag
