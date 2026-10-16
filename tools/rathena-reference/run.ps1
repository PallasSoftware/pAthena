#!/usr/bin/env pwsh
# Builds the pinned rAthena in Docker, runs the scenarios, writes raw reference output.
# Usage: pwsh tools/rathena-reference/run.ps1 [-Output <fixture.json>]  (default: the committed MapServer.Tests fixture)
param([string]$Output = (Join-Path $PSScriptRoot '../../tests/MapServer.Tests/Gameplay/Reference/rathena-reference.json'))
$ErrorActionPreference = 'Stop'
$pin = 'e985006171d2eb320ee512a653f4c83aea3d81b6'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$ra = Join-Path $root 'legacy/rathena'

function Invoke-Native([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if ((git -C $ra rev-parse HEAD) -ne $pin) { throw "legacy/rathena is not at pinned commit $pin" }

$ctx = Join-Path ([IO.Path]::GetTempPath()) "athena-ra-ref-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $ctx | Out-Null
try {
    # git archive: exact pinned tree only, submodule working tree is never touched.
    $tar = Join-Path $ctx 'ra.tar'
    Invoke-Native git @('-C', $ra, 'archive', '--format=tar', '--prefix=legacy-rathena/', '-o', $tar, 'HEAD')
    Invoke-Native tar @('-xf', $tar, '-C', $ctx)
    Remove-Item $tar
    Copy-Item (Join-Path $PSScriptRoot 'Dockerfile'), (Join-Path $PSScriptRoot 'reference_runner.cpp') $ctx
    Invoke-Native docker @('build', '--progress=plain', '-t', 'athena-rathena-reference', $ctx) *>&1 | Out-Host

    $inner = @'
set -e
mkdir -p /run/mysqld /out && chown mysql:mysql /run/mysqld
mysqld_safe >/tmp/mysqld.log 2>&1 &
for i in $(seq 1 60); do mariadb -e 'select 1' >/dev/null 2>&1 && break; sleep 1; done
mariadb -e "create database ragnarok; create user 'ragnarok'@'127.0.0.1' identified by 'ragnarok'; create user 'ragnarok'@'localhost' identified by 'ragnarok'; grant all on ragnarok.* to 'ragnarok'@'127.0.0.1'; grant all on ragnarok.* to 'ragnarok'@'localhost';"
for f in main logs; do mariadb ragnarok < /src/rathena/sql-files/$f.sql; done
cd /src/rathena
RA_OUT=/out/reference.json ./map-server >/tmp/map.log 2>&1 || { tail -60 /tmp/map.log >&2; exit 1; }
cat /out/reference.json
'@ -replace "`r", ''
    $result = & docker run --rm athena-rathena-reference sh -c $inner
    if ($LASTEXITCODE -ne 0) { throw "reference run failed with exit code $LASTEXITCODE" }
    $compiler = (& docker run --rm athena-rathena-reference g++ --version | Select-Object -First 1)
    $raw = ($result -join "`n") | ConvertFrom-Json
    $scope = @{
        'VF-01' = 'Fresh Novice status_calc_pc on prontera: stats, batk, hit/flee/crit, aspd, max HP/SP. No equipment, skills or statuses.'
        'VF-07' = 'Blessing started twice via status_change_start on a fresh Novice: retained val1/val2 and resulting STR/INT/DEX/HIT. Timer/deadline not captured.'
        'VF-14' = 'Values stored by the real mob_db loader for mob 1002 (Poring). Does NOT cover the attack roll or its random bounds.'
    }
    $fixture = [ordered]@{
        schema = 1
        reference = [ordered]@{
            repository = 'rathena/rathena'
            commit = $pin
            renewalCompiled = [bool]$raw.renewal_compiled
            compiler = $compiler
            runner = 'tools/rathena-reference (map-server built from the pinned tree plus one call to reference_runner.cpp after init)'
            configuration = 'unmodified conf/ and db/re defaults, no import overrides'
        }
        scenarios = @($raw.scenarios | ForEach-Object {
            [ordered]@{ id = $_.id; evidence = 'ReferenceExecuted'; scope = $scope[$_.id]; input = $_.input; output = $_.output }
        })
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $Output) | Out-Null
    ($fixture | ConvertTo-Json -Depth 20) + "`n" | Set-Content -Path $Output -NoNewline
}
finally { Remove-Item -Recurse -Force $ctx -ErrorAction SilentlyContinue }
