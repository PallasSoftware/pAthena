#Requires -Version 7.0
<#
Static rules of the release workflows, so that a refactoring can not silently weaken them:
 - validation runs for pull requests and main with the same steps (no event-dependent skipping);
 - a version tag can only be created after a successful validation, only on main, never for a pull request;
 - images are published only after the tag job, a GitHub Release only after all images, and nothing deploys.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '../..'
$failures = 0
function Assert-That([bool]$Condition, [string]$Message) { if ($Condition) { Write-Host "ok: $Message" } else { $script:failures++; Write-Host "FAIL: $Message" } }
function Get-Job([string]$Text, [string]$Name) {
    $m = [regex]::Match($Text, "(?ms)^  ${Name}:[ \t]*\n(.*?)(?=^  \S|\z)")
    if ($m.Success) { $m.Groups[1].Value } else { '' }
}
function Read-Code([string]$Path) { ((Get-Content -LiteralPath $Path -Raw) -split "`n" | Where-Object { $_.TrimStart() -notmatch '^#' }) -join "`n" }

$workflow = Read-Code (Join-Path $root '.github/workflows/release.yml')
$validate = Get-Job $workflow 'validate'
$retag = Get-Job $workflow 'retag-digest'
$tag = Get-Job $workflow 'version'
$publish = Get-Job $workflow 'publish'
$release = Get-Job $workflow 'github-release'

Assert-That (-not (Test-Path (Join-Path $root '.github/workflows/release-version.yml')) -and -not (Test-Path (Join-Path $root '.github/workflows/release-images.yml'))) 'there is one release workflow (no workflows orchestrating each other)'
Assert-That ($workflow -notmatch 'workflow_dispatch|gh workflow run|workflow_call|workflow_run') 'no workflow is started by another workflow or by hand: a push to main is the only release trigger'
Assert-That ($workflow -match '(?m)^  pull_request:\s*$' -and $workflow -match '(?ms)^  push:\s*\n\s+branches:\s*\[ main \]') 'validation runs for every pull request and every push to main'
Assert-That ($workflow -notmatch '(?ms)^  pull_request:\s*\n\s+(paths|paths-ignore|branches):') 'the pull request trigger has no path filter, so the required check always reports'
Assert-That ($validate -notmatch '(?m)^\s+if:' -and $validate -notmatch 'github\.event_name') 'no step or job of validate depends on the event: pull requests and main run the same steps'
foreach ($project in 'LoginServer', 'CharServer', 'MapServer', 'World') { Assert-That ($validate -match $project) "validate runs the $project tests" }
Assert-That ($validate -match 'Test-NoVulnerablePackages\.ps1') 'validate fails on vulnerable packages'
Assert-That ($workflow -notmatch 'always\(\)|!cancelled\(\)|failure\(\)') 'nothing overrides the default "only after success" (no always()/!cancelled()/failure())'
Assert-That ($tag -match '(?m)^    needs:\s*\[validate, retag-digest\]\s*$') 'the tag job needs validate and the digest check'
Assert-That ($tag -match "github\.event_name == 'push'" -and $tag -match "github\.ref == 'refs/heads/main'") 'a tag is created only for a push to main, never for a pull request'
Assert-That ($publish -match '(?m)^    needs:\s*version\s*$') 'publishing needs the tag job'
Assert-That ($release -match '(?m)^    needs:\s*\[version, publish\]\s*$') 'the GitHub Release needs the publish job (all four images)'
Assert-That ($retag -notmatch 'contents:\s*write|packages:\s*write') 'the digest check has no write permission'
Assert-That ($validate -notmatch 'write') 'validate has no write permission'
Assert-That (([regex]::Matches($workflow, 'New-ReleaseTag\.ps1')).Count -eq 1 -and $tag -match 'New-ReleaseTag\.ps1') 'the tag is created in exactly one place, the tag job'
Assert-That (([regex]::Matches($workflow, 'packages:\s*write')).Count -eq 1 -and $publish -match 'packages:\s*write') 'only the publish job can write packages'
Assert-That ($workflow -notmatch '(?i)kubectl|deploy\s*:|environment:') 'nothing deploys'
if ($failures) { throw "$failures assertion(s) failed" }
