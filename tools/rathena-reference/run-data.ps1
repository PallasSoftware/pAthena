#!/usr/bin/env pwsh
# Feature 16 data-pipeline evidence from the REAL pinned rAthena (same disposable build as run.ps1):
#   - resolves script identifiers through script_get_constant / script_get_parameter  -> -ConstantsOut (name<TAB>value<TAB>c|p)
#   - dumps every item_db entry as the real item loader stored it                     -> -ItemsOut (gzip, one canonical line per item)
# Usage: pwsh tools/rathena-reference/run-data.ps1 -NamesFile <identifiers.txt> -ConstantsOut <file.tsv> -ItemsOut <file.txt.gz> [-GroupsOut <file.txt.gz>]
# Regeneration is deliberate; the importer and its tests only read the committed outputs.
param(
    [Parameter(Mandatory)][string]$NamesFile,
    [Parameter(Mandatory)][string]$ConstantsOut,
    [Parameter(Mandatory)][string]$ItemsOut,
    [string]$GroupsOut
)
$ErrorActionPreference = 'Stop'
$pin = 'e985006171d2eb320ee512a653f4c83aea3d81b6'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$ra = Join-Path $root 'legacy/rathena'

function Invoke-Native([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if ((git -C $ra rev-parse HEAD) -ne $pin) { throw "legacy/rathena is not at pinned commit $pin" }

$ctx = Join-Path ([IO.Path]::GetTempPath()) "athena-ra-data-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $ctx | Out-Null
$io = Join-Path $ctx 'io'
New-Item -ItemType Directory $io | Out-Null
try {
    $tar = Join-Path $ctx 'ra.tar'
    Invoke-Native git @('-C', $ra, 'archive', '--format=tar', '--prefix=legacy-rathena/', '-o', $tar, 'HEAD')
    Invoke-Native tar @('-xf', $tar, '-C', $ctx)
    Remove-Item $tar
    Copy-Item (Join-Path $PSScriptRoot 'Dockerfile'), (Join-Path $PSScriptRoot 'reference_runner.cpp') $ctx
    Invoke-Native docker @('build', '--progress=plain', '-t', 'athena-rathena-reference', $ctx) *>&1 | Out-Host
    Copy-Item $NamesFile (Join-Path $io 'names.txt')

    $inner = @'
set -e
mkdir -p /run/mysqld && chown mysql:mysql /run/mysqld
mysqld_safe >/tmp/mysqld.log 2>&1 &
for i in $(seq 1 60); do mariadb -e 'select 1' >/dev/null 2>&1 && break; sleep 1; done
mariadb -e "create database ragnarok; create user 'ragnarok'@'127.0.0.1' identified by 'ragnarok'; create user 'ragnarok'@'localhost' identified by 'ragnarok'; grant all on ragnarok.* to 'ragnarok'@'127.0.0.1'; grant all on ragnarok.* to 'ragnarok'@'localhost';"
for f in main logs; do mariadb ragnarok < /src/rathena/sql-files/$f.sql; done
cd /src/rathena
RA_NAMES=/io/names.txt RA_NAMES_OUT=/io/constants.tsv RA_ITEMS_OUT=/io/items.txt RA_GROUPS_OUT=/io/groups.txt ./map-server >/tmp/map.log 2>&1 || { tail -60 /tmp/map.log >&2; exit 1; }
'@ -replace "`r", ''
    Invoke-Native docker @('run', '--rm', '-v', "${io}:/io", 'athena-rathena-reference', 'sh', '-c', $inner)

    New-Item -ItemType Directory -Force (Split-Path -Parent $ConstantsOut), (Split-Path -Parent $ItemsOut) | Out-Null
    # Stable order and LF line endings so regeneration is byte-identical.
    (Get-Content (Join-Path $io 'constants.tsv') | Sort-Object { $_.Split("`t")[0] } -CaseSensitive) -join "`n" | Set-Content -Path $ConstantsOut -NoNewline
    Add-Content -Path $ConstantsOut -Value "`n" -NoNewline

    function Write-Gzip([string]$source, [string]$target) {
        $bytes = [IO.File]::ReadAllBytes($source)
        $fs = [IO.File]::Create($target)
        try {
            $gz = [IO.Compression.GZipStream]::new($fs, [IO.Compression.CompressionLevel]::SmallestSize)
            $gz.Write($bytes, 0, $bytes.Length)
            $gz.Dispose()
        } finally { $fs.Dispose() }
    }
    Write-Gzip (Join-Path $io 'items.txt') $ItemsOut
    if ($GroupsOut) { New-Item -ItemType Directory -Force (Split-Path -Parent $GroupsOut) | Out-Null; Write-Gzip (Join-Path $io 'groups.txt') $GroupsOut }
}
finally { Remove-Item -Recurse -Force $ctx -ErrorAction SilentlyContinue }
