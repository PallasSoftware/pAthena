#Requires -Version 7.0
# Self-test of Get-CodeImpact.ps1: which changed paths trigger the full server validation.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Get-CodeImpact.ps1'
$failures = 0
function Assert-Impact([bool]$Expected, [string[]]$Paths) {
    $actual = [bool](& $script -Paths $Paths)
    if ($actual -eq $Expected) { Write-Host "ok: $($Paths -join ', ') -> $actual" } else { $script:failures++; Write-Host "FAIL: $($Paths -join ', ') -> $actual, expected $Expected" }
}
foreach ($p in '.claude/settings.json', '.claude/skills/x/SKILL.md', 'CLAUDE.md', 'AGENTS.md', 'README.md', 'docs/a.png', 'ai/iro-2026-wire.md', 'src/MapServer/README.md', 'LICENSE', '.gitignore', '.vscode/launch.json', 'graphify-out/graph.json') { Assert-Impact $false $p }
Assert-Impact $false '.claude/a.json', 'README.md'
foreach ($p in 'src/MapServer/A.cs', 'tests/World.Tests/A.cs', 'tools/launcher/a.cs', 'data/world/x.json', 'conf/x.conf', 'Directory.Build.props', 'VERSION', 'legacy/rathena', '.gitmodules', 'src/MapServer/Dockerfile', '.dockerignore', 'docker-compose.yml', '.github/workflows/release.yml', '.github/scripts/Publish-ReleaseImages.ps1', 'scripts/x.ps1', 'newthing/x') { Assert-Impact $true $p }
Assert-Impact $true '.claude/a.json', 'src/A.cs'
Assert-Impact $true @()
if ($failures) { throw "$failures assertion(s) failed" }
