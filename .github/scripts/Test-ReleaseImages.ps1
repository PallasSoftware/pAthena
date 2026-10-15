#Requires -Version 7.0
# Offline test of the image decisions with a throwaway git repository.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseImages.psm1') -Force
$failures = 0
function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { $script:failures++; Write-Host "FAIL: $Message" } else { Write-Host "ok: $Message" } }

$repo = Join-Path ([IO.Path]::GetTempPath()) "release-images-test-$([guid]::NewGuid())"
New-Item -ItemType Directory $repo | Out-Null
try {
    git -C $repo init -q -b main; git -C $repo config user.email t@t; git -C $repo config user.name t
    foreach ($f in 'Directory.Build.props', 'src/Shared/a.cs', 'src/LoginServer/a.cs', 'src/CharServer/a.cs', 'src/MapServer/a.cs', 'src/WorldServer/a.cs',
        'conf/templates/char_athena.conf', 'conf/templates/inter_athena.conf', 'conf/templates/map_athena.conf', 'conf/world_partitions.json', 'conf/import/x', 'legacy/rathena/db/map_cache.dat', 'tests/t.cs') {
        $p = Join-Path $repo $f; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; Set-Content $p 'v1'
    }
    git -C $repo add -A; git -C $repo commit -qm one; git -C $repo tag v0.1.0
    Set-Content (Join-Path $repo 'src/CharServer/a.cs') 'v2'; Set-Content (Join-Path $repo 'tests/t.cs') 'v2'
    git -C $repo commit -qam two; git -C $repo tag v0.1.1
    Set-Content (Join-Path $repo 'src/Shared/a.cs') 'v3'
    git -C $repo commit -qam three; git -C $repo tag v0.2.0
    $registry = { param($app, $prev) "sha256:$app-$prev" }
    $none = { param($app, $prev) $null }

    $first = @(Get-ImagePlan -Tag v0.1.0 -RepoDir $repo -GetPreviousDigest $registry)
    Assert-That (@($first | Where-Object Action -eq BUILD).Count -eq 4) 'first release builds all four'

    $p = @(Get-ImagePlan -Tag v0.1.1 -RepoDir $repo -GetPreviousDigest $registry)
    Assert-That (($p | Where-Object App -eq charserver).Action -eq 'BUILD') 'a CharServer change builds CharServer'
    Assert-That (@($p | Where-Object Action -eq RETAG).Count -eq 3) 'unchanged servers are re-tagged (tests/ is not an image input)'
    Assert-That (($p | Where-Object App -eq loginserver).PreviousDigest -eq 'sha256:loginserver-v0.1.0') 'a re-tag reuses the previous digest'

    $p = @(Get-ImagePlan -Tag v0.2.0 -RepoDir $repo -GetPreviousDigest $registry)
    Assert-That (@($p | Where-Object App -in 'loginserver', 'charserver', 'mapserver' | Where-Object Action -eq BUILD).Count -eq 3) 'a src/Shared change builds its three consumers'
    Assert-That (($p | Where-Object App -eq worldserver).Action -eq 'RETAG') 'WorldServer does not consume src/Shared and is re-tagged'
    Assert-That (($p | Where-Object App -eq worldserver).PreviousTag -eq 'v0.1.1') 'the previous tag is the highest lower tag'

    $p = @(Get-ImagePlan -Tag v0.1.1 -RepoDir $repo -GetPreviousDigest $none)
    Assert-That (@($p | Where-Object Action -eq BUILD).Count -eq 4) 'a missing previous image falls back to a build'

    Assert-That ((Get-PreviousReleaseTag -Tag v0.10.0 -AllTags 'v0.2.0', 'v0.9.0', 'v0.10.0', 'launcher-v0.0.3') -eq 'v0.9.0') 'tags sort numerically, launcher tags are ignored'
    $threw = $false; try { Get-PreviousReleaseTag -Tag 'main' -AllTags @() } catch { $threw = $true }
    Assert-That $threw 'a non-release tag is rejected'
}
finally { Remove-Item -Recurse -Force $repo -ErrorAction SilentlyContinue }

Write-Host '-- re-tag keeps the digest'
$sha = 'sha256:' + ('a' * 64)
$captured = @{}
$ok = Add-ImageTag -Image 'ghcr.io/x/y' -Digest $sha -Tag 'v0.0.2' -Create { param($i, $d, $t) $captured.Args = "$i@$d -> $t" } -Resolve { param($r) $sha }
Assert-That ($ok -ceq $sha -and $captured.Args -ceq "ghcr.io/x/y@$sha -> v0.0.2") 'a re-tag that resolves to the source digest is accepted'
$threw = $false; try { Add-ImageTag -Image 'ghcr.io/x/y' -Digest $sha -Tag 'v0.0.2' -Create { } -Resolve { 'sha256:' + ('b' * 64) } | Out-Null } catch { $threw = $true; $msg = $_.Exception.Message }
Assert-That ($threw -and $msg -match 'changed the digest') 'a re-tag that changes the digest fails the release'
$threw = $false; try { Add-ImageTag -Image 'ghcr.io/x/y' -Digest $sha -Tag 'v0.0.2' -Create { } -Resolve { $null } | Out-Null } catch { $threw = $true }
Assert-That $threw 'a re-tag that cannot be resolved fails the release'
if ($failures) { throw "$failures assertion(s) failed" }
