#Requires -Version 7.0
# Self-test of Get-CodeImpact.ps1: which changed paths trigger the full server validation.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Get-CodeImpact.ps1'
$failures = 0
function Assert-Impact([string]$Expected, [string[]]$Paths) {
    $actual = & $script -Paths $Paths
    if ($actual -eq $Expected) { Write-Host "ok: $($Paths -join ', ') -> $actual" } else { $script:failures++; Write-Host "FAIL: $($Paths -join ', ') -> $actual, expected $Expected" }
}
foreach ($p in '.claude/settings.json', '.claude/skills/x/SKILL.md', 'CLAUDE.md', 'AGENTS.md', 'README.md', 'docs/a.png', 'ai/iro-2026-wire.md', 'src/MapServer/README.md', 'LICENSE', '.gitignore', '.vscode/launch.json', 'graphify-out/graph.json') { Assert-Impact 'none' $p }
Assert-Impact 'none' '.claude/a.json', 'README.md'
foreach ($p in 'src/MapServer/A.cs', 'tests/World.Tests/A.cs', 'tools/launcher/a.cs', 'data/world/x.json', 'conf/x.conf', 'Directory.Build.props', 'VERSION', 'legacy/rathena', '.gitmodules', 'src/MapServer/Dockerfile', '.dockerignore', 'docker-compose.yml', 'scripts/x.ps1', 'newthing/x') { Assert-Impact 'code' $p }
Assert-Impact 'code' '.claude/a.json', 'src/A.cs'
Assert-Impact 'code' @()
foreach ($p in '.github/workflows/release.yml', '.github/scripts/Publish-ReleaseImages.ps1', '.github/workflows/map-server-ci.yml') { Assert-Impact 'ci' $p }
Assert-Impact 'ci' '.github/scripts/a.ps1', '.claude/a.json', 'README.md'
Assert-Impact 'code' '.github/scripts/a.ps1', 'src/A.cs'
if ($failures) { throw "$failures assertion(s) failed" }
