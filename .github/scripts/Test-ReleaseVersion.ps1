#Requires -Version 7.0
# Offline tests of the release version rules, including concurrent runs against a local bare "remote".
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseVersion.psm1') -Force
$failures = 0
function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { $script:failures++; Write-Host "FAIL: $Message" } else { Write-Host "ok: $Message" } }
function Throws([scriptblock]$Block) { try { & $Block | Out-Null; $false } catch { $true } }

Write-Host '-- version arithmetic'
Assert-That ((Get-NextVersion -Major 0 -Minor 0 -Tags @()) -ceq 'v0.0.1') 'the first release is 0.0.1'
Assert-That ((Get-NextVersion -Major 0 -Minor 0 -Tags 'v0.0.1') -ceq 'v0.0.2') '0.0.1 -> 0.0.2'
Assert-That ((Get-NextVersion -Major 0 -Minor 0 -Tags 'v0.0.1', 'v0.0.2') -ceq 'v0.0.3') '0.0.2 -> 0.0.3'
Assert-That ((Get-NextVersion -Major 0 -Minor 1 -Tags 'v0.0.1', 'v0.0.2', 'v0.0.3') -ceq 'v0.1.0') 'a MINOR bump starts at PATCH 0'
Assert-That ((Get-NextVersion -Major 0 -Minor 1 -Tags 'v0.0.3', 'v0.1.0') -ceq 'v0.1.1') 'the next update after a MINOR bump is 0.1.1'
Assert-That ((Get-NextVersion -Major 1 -Minor 0 -Tags 'v0.1.0', 'v0.1.1') -ceq 'v1.0.0') 'a MAJOR bump starts at PATCH 0'
Assert-That ((Get-NextVersion -Major 1 -Minor 0 -Tags 'v0.1.1', 'v1.0.0') -ceq 'v1.0.1') 'the next update after a MAJOR bump is 1.0.1'
Assert-That ((Get-NextVersion -Major 0 -Minor 0 -Tags 'v0.0.9', 'v0.0.10') -ceq 'v0.0.11') 'PATCH compares numerically (9 < 10)'
Assert-That ((Get-NextVersion -Major 0 -Minor 0 -Tags 'launcher-v0.0.3', 'latest', 'v0.0.1-rc1') -ceq 'v0.0.1') 'other tags are ignored'
Assert-That (Throws { Get-NextVersion -Major 0 -Minor 1 -Tags 'v1.0.0' }) 'a VERSION below an existing release is refused'
Assert-That (Throws { Get-NextVersion -Major 1 -Minor 0 -Tags 'v1.2.0' }) 'a MINOR going backwards is refused'

Write-Host '-- VERSION file'
$work = Join-Path ([IO.Path]::GetTempPath()) "release-version-$([guid]::NewGuid())"
New-Item -ItemType Directory $work | Out-Null
try {
    $file = Join-Path $work 'VERSION'
    Set-Content $file '0.1'
    Assert-That ((Read-VersionFile $file).Minor -eq 1) 'MAJOR.MINOR is read'
    foreach ($bad in '0.1.2', '1', 'v0.1', '01.2', '', "0.1`n0.2") { Set-Content $file $bad; Assert-That (Throws { Read-VersionFile $file }) "invalid VERSION '$($bad -replace "`n", '|')' is refused" }

    Write-Host '-- tagging, re-runs and concurrent runs'
    $remote = Join-Path $work 'remote.git'; git init -q --bare $remote
    function New-Clone([string]$Name) {
        $dir = Join-Path $work $Name; git clone -q $remote $dir 2>$null; git -C $dir config user.email t@t; git -C $dir config user.name t; $dir
    }
    $a = New-Clone 'a'
    Set-Content (Join-Path $a 'VERSION') '0.0'; git -C $a add -A; git -C $a commit -qm one; git -C $a branch -M main; git -C $a push -q origin main
    $one = (git -C $a rev-parse HEAD).Trim()
    Push-Location $a
    try {
        Assert-That ((New-ReleaseTag -Commit $one) -ceq 'v0.0.1') 'the first push to main is tagged v0.0.1'
        Assert-That ((New-ReleaseTag -Commit $one) -ceq 'v0.0.1') 'a re-run of the same commit keeps its tag (no new version)'
        Assert-That ((@(git -C $remote tag --list) -join ',') -ceq 'v0.0.1') 'the tag exists on the remote'
        Set-Content VERSION '0.0'; Add-Content README.md 'x'; git add -A; git commit -qm two; $two = (git rev-parse HEAD).Trim()
        Assert-That ((New-ReleaseTag -Commit $two) -ceq 'v0.0.2') 'the next push is v0.0.2'
        Set-Content VERSION '0.1'; git add -A; git commit -qm minor; $three = (git rev-parse HEAD).Trim()
        Assert-That ((New-ReleaseTag -Commit $three) -ceq 'v0.1.0') 'a manual MINOR bump gives v0.1.0'
        Add-Content README.md y; git add -A; git commit -qm four; $four = (git rev-parse HEAD).Trim()
        Assert-That ((New-ReleaseTag -Commit $four) -ceq 'v0.1.1') 'the next push gives v0.1.1'
        Set-Content VERSION '1.0'; git add -A; git commit -qm major; $five = (git rev-parse HEAD).Trim()
        Assert-That ((New-ReleaseTag -Commit $five) -ceq 'v1.0.0') 'a manual MAJOR bump gives v1.0.0'

        # A concurrent run took the next version between our computation and our push: we must take the following one.
        Add-Content README.md z; git add -A; git commit -qm six; $six = (git rev-parse HEAD).Trim()
        $b = New-Clone 'b'
        git -C $b fetch -q --tags; git -C $b tag v1.0.1 (git -C $b rev-parse origin/main); git -C $b push -q origin refs/tags/v1.0.1
        git tag -d v1.0.1 2>$null | Out-Null
        # The stale clone does not know v1.0.1 locally until it fetches; New-ReleaseTag fetches first, so it picks v1.0.2.
        Assert-That ((New-ReleaseTag -Commit $six) -ceq 'v1.0.2') 'a version taken by a concurrent run is never reused'
        Assert-That ((@(git -C $remote tag --list 'v1.0.*') -join ',') -ceq 'v1.0.0,v1.0.1,v1.0.2') 'each run ends up with its own tag'
        # Race inside the retry window: simulate the rejected push by pre-creating the remote tag after the local computation.
        Add-Content README.md w; git add -A; git commit -qm seven; $seven = (git rev-parse HEAD).Trim()
        git -C $b fetch -q --tags; git -C $b tag v1.0.3 $seven 2>$null; git -C $b push -q origin refs/tags/v1.0.3 2>$null
        Assert-That ((New-ReleaseTag -Commit $seven) -ceq 'v1.0.3') 'a commit that already has a remote release tag keeps it'
        Set-Content VERSION '0.9'; git add -A; git commit -qm back; $back = (git rev-parse HEAD).Trim()
        Assert-That (Throws { New-ReleaseTag -Commit $back }) 'lowering VERSION below an existing release fails the run'
        Assert-That (@(git -C $remote tag --list).Count -eq 8) 'no extra tags were created by the failing run'
    }
    finally { Pop-Location }
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
if ($failures) { throw "$failures assertion(s) failed" }
