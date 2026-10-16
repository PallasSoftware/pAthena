#Requires -Version 7.0
<#
Decides whether a change can affect the servers, so documentation-only and Claude-configuration-only pull requests skip the
expensive steps while the required checks ("Validate", "Re-tag keeps the digest") still report. Everything NOT listed as
non-code below counts as code (src, tests, tools, data, conf, build files, submodules, Dockerfiles, workflows, scripts, ...),
so a new kind of file fails safe into full validation. Pushes to main and unknown diffs always validate.
Writes code=true|false to $GITHUB_OUTPUT. -Paths is for the self-test.
#>
param([string]$Base, [string]$Head = 'HEAD', [string[]]$Paths)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$nonCode = '^(\.claude/|docs/|ai/|\.vscode/|graphify-out[^/]*/)', '\.md$', '^(LICENSE|\.gitignore)$'
function Test-CodeImpact([string[]]$Changed) {
    if (-not $Changed) { return $true }   # empty diff: cannot prove it is harmless
    foreach ($p in $Changed) { if (-not ($nonCode | Where-Object { $p -match $_ })) { return $true } }
    return $false
}
if ($PSBoundParameters.ContainsKey('Paths')) { return (Test-CodeImpact $Paths) }
$code = $true
if ($env:GITHUB_EVENT_NAME -eq 'pull_request' -and $Base) {
    $changed = @(git diff --name-only "$Base...$Head")
    if ($LASTEXITCODE -ne 0) { throw 'git diff failed' }
    $code = Test-CodeImpact $changed
    Write-Host "changed files: $($changed.Count); code impact: $code"
}
"code=$($code.ToString().ToLower())" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT
