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
        'VF-02' = 'Novice job-level 1..10 job bonuses (job_stats.yml) plus two high-level mixed-allocation cases; full base_status/battle_status for each. Jobs other than Novice are not captured.'
        'VF-04' = 'Novice max HP/SP (battle_status) for base levels {1,2,9,10,25,50,75,99} x VIT {1,7,30,77,99} with INT = 100-VIT; no equipment, skills or statuses.'
        'VF-11' = 'Allocated VIT/INT raised or lowered on a Novice followed by the real status_calc_pc(SCO_NONE) (what pc_statusup runs after validation): max HP/SP and current HP/SP (battle_status and status) before/after; damaged, empty, full and above-new-max starting HP/SP.'
        'VF-05' = 'Novice (Lv10/J5, mixed stats) wearing nothing / Knife (1201) / Cotton Shirt (2301) / both through the real equipment pass of status_calc_pc: full base_status and battle_status incl. hard DEF, weapon ATK, range, ASPD.'
        'VF-06' = 'Novice with a Knife vs mobs 1002/1007/1013 through real battle_calc_attack, 2000 seeded rolls: normal and critical damage histograms plus miss counts. Covers weapon damage, size fix, element and mob DEF for refine-0 daggers.'
        'VF-15' = 'Mobs 1031/1013 vs a Novice wearing a Cotton Shirt (hard DEF 10) through real battle_calc_attack, 2000 seeded rolls; exercises hard DEF in the defense reduction.'
        'VF-16' = '44 job classes (first/second/transcendent/baby/third/fourth/expanded) at base 1/job 1 and base 70/job 10 with fixed allocated stats, no equipment: full base_status and battle_status (six stats, derived stats, max HP/SP, fist ASPD).'
        'VF-17' = 'Attack motion/delay, weapon type, range and weapon ATK for the VF-16 jobs wearing nothing or one representative real weapon per weapon_type (Knife, Sword, Axe, Javelin, Club, Rod, Bow, Waghnakh, Violin), base 1/job 1, fixed stats.'
        'VF-07' = 'Blessing started twice via status_change_start on a fresh Novice: retained val1/val2, resulting stats, timer deadline movement (50ms apart) and state after status_change_end. Expiry-by-timer callback and client packets not captured.'
        'VF-08' = 'Increase AGI started twice via status_change_start on a fresh Novice: retained val1/val2, stats, speed, ASPD, timer deadline movement and state after status_change_end.'
        'VF-20' = 'Feature 16: base max weight (status_calc_weight: job MaxWeight + STR*300) for 14 representative jobs x STR {1,50,99}, plus the item/weight related battle_config values the pinned binary actually runs with (conf/battle, not code defaults).'
        'VF-21' = 'Feature 16: real pc_additem decisions for 22 cases - empty add, merge, non-stackable, MAX_AMOUNT and Stack.Amount limits, weight, slot capacity, bound/rental/card mismatch, unique id assignment, slot beyond the permitted inventory size - with the resulting rows, weight and free slots.'
        'VF-22' = 'Feature 16: pc_updateweightstatus thresholds and hysteresis (SC_WEIGHT50 / SC_WEIGHT90, regen overweight) over the weight sequence 0,699,700,701,899,900,1000,899,699,0 at max_weight 1000.'
        'VF-14' = 'Loader values for mob 1002, plus mobs 1002/1031/1013 spawned (SZ_SMALL) and attacking the VF-01 Novice through real battle_calc_attack: 2000 seeded samples each (std::mt19937 reseeded 1..2000), histogram of non-zero damage, zero/miss count. Covers roll bounds, batk and def2 for ordinary non-skill attacks; hit/flee outcome is recorded but not compared.'
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
