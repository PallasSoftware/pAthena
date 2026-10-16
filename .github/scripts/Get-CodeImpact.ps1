#Requires -Version 7.0
<#
Decides whether a change can affect the servers, so documentation-only and Claude-configuration-only pull requests skip the
expensive steps while the required checks ("Validate", "Re-tag keeps the digest") still report. Everything NOT listed as
non-code below counts as code (src, tests, tools, data, conf, build files, submodules, Dockerfiles, workflows, scripts, ...),
so a new kind of file fails safe into full validation. Pushes to main and unknown diffs always validate.
Writes two outputs: code=true when application code, tests, builds, data or runtime config may be affected (full server validation),
and ci=true when that or release infrastructure (.github/) changed (release scripts and workflow rules are tested, retag digest runs). -Paths is for the self-test.
#>
param([string]$Base, [string]$Head = 'HEAD', [string[]]$Paths)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$nonCode = '^(\.claude/|docs/|ai/|\.vscode/|graphify-out[^/]*/)', '\.md$', '^(LICENSE|\.gitignore)$'
function Get-Impact([string[]]$Changed) {
    if (-not $Changed) { return 'code' }   # empty diff: cannot prove it is harmless
    $impact = 'none'
    foreach ($p in $Changed) {
        if ($nonCode | Where-Object { $p -match $_ }) { continue }
        if ($p -match '^\.github/') { $impact = 'ci' } else { return 'code' }
    }
    $impact
}
if ($PSBoundParameters.ContainsKey('Paths')) { return (Get-Impact $Paths) }
$impact = 'code'
if ($env:GITHUB_EVENT_NAME -eq 'pull_request' -and $Base) {
    $changed = @(git diff --name-only "$Base...$Head")
    if ($LASTEXITCODE -ne 0) { throw 'git diff failed' }
    $impact = Get-Impact $changed
    Write-Host "changed files: $($changed.Count); impact: $impact"
}
"code=$(($impact -eq 'code').ToString().ToLower())", "ci=$(($impact -ne 'none').ToString().ToLower())" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT
