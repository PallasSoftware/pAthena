# .NET Aspire

This project uses a .NET Aspire AppHost instead of Docker Compose.

## Prerequisites
- .NET SDK 10.x installed

## Run
From repo root:
```
dotnet run --project src/AppHost
```

SQL Server 2025 Developer is exposed on a fixed host port (58043) for local tooling. The image is pinned to `mcr.microsoft.com/mssql/server:2025-CU8-ubuntu-24.04`.

## Local two-MapServer topology (item 14)

`dotnet run --project src/AppHost` starts **two** MapServer gateway resources by default:
`map-server` on port 5121 and `map-server-b` on port 5122, both against the same Orleans World
cluster, the same CharServer, and the same SQL data - `map-server-b` passes `--map-port 5122` (a
new CLI override on `map_athena.conf`'s own `map_port` key) so both processes can share one config
file. This exercises the cross-replica player visibility/combat fanout described in
`docs/orleans-world-runtime.md`.

The Ragexe launcher (`tools/launcher/`) gets an opt-in `MapTargetPorts` list in its own config
(`LauncherOptions`), round-robin-assigning each new inbound map connection across the listed backend
ports (`TcpProxy.TunnelAsync`). The default single-`MapTargetPort` behavior is unchanged when this
list is not configured.

### Manual acceptance procedure

This is the only real-client verification of the cross-replica work; the automated tests
(`CrossReplicaPlayerVisibilityIntegrationTests` and friends) do not replace it. Record the result
(pass/fail per step, MapServer log excerpts) before calling item 14 accepted.

Prerequisites: two stock iRO accounts with a character each, the Ragexe launcher
(`tools/launcher/`) on the client machine, and the Aspire stack running on a machine the client can
reach. In `launcher.settings.json` set `AthenaHost` to that machine (the shipped value is the WAN
address) and add `"MapTargetPorts": [5121, 5122]` next to `MapTargetPort`.

1. Start Aspire (`dotnet run --project src/AppHost`). In the dashboard confirm `athena-world`, `char-server`,
   `login-server`, `map-server` and `map-server-b` are all running, and that the console logs show
   `Map server listening on ...:5121` and `...:5122` respectively.
2. Start the launcher and log client A in. The launcher assigns each NEW inbound map connection to
   the next port in `MapTargetPorts` (round-robin, global across connections): the launcher log line
   `proxy.connection.target` carries `targetPort`, and the receiving MapServer logs
   `Client connected`. Confirm A reached 5121 (`map-server`).
3. Log client B in (second client, same launcher) and confirm it reached 5122 (`map-server-b`).
   If both landed on the same replica (e.g. an earlier extra map connection advanced the cursor),
   log B out and in again until the two clients are on different replicas; always verify from the logs,
   never assume.
4. Put both characters on the same map within AOI (14 cells) of each other. Verify each client shows
   the other character, and neither shows it twice.
5. Move A; verify B sees the walk (and the reverse). Rotate A's head/body; verify B sees the turn.
6. Walk A out of B's range and back; verify exactly one disappearance and one re-appearance on B.
7. Open the status/info window on the other character (click it); verify the name resolves.
8. Attack a monster with A (non-lethal first); verify B sees A's attack animation and the damage once.
9. Kill the monster with A; verify B sees the killing hit before the monster disappears.
10. Let a monster hit A; verify B sees that attack once.
11. Make A change equipment (hat/weapon) and then have B walk out of and back into range; verify B sees
    the new look on re-entry. (Live re-broadcast to a viewer who never left range is a known gap, see
    `ai/map-server.md`; do not report it as a regression.)
12. Disconnect client A (close the client, not log out cleanly); verify B sees A disappear within a few
    seconds and `map-server`/`map-server-b` logs show no exception.
13. Log A back in and verify it is discovered again by B exactly once.
14. Throughout, confirm no duplicated actor spawn or combat action on either client and no
    `[WORLD]` error loops in either MapServer log.

## Secrets
The AppHost reads the SQL Server SA password from `solutionfiles/secrets/secret.json`
(`SqlServer.SaPassword`). You can still override it via environment variable:
```
export Parameters__sql-server-password="<your password>"
```

`Parameters__sql-edge-password` remains a temporary compatibility alias for existing local environments. New configuration should use the engine-neutral name.

The LoginServer consumes the connection string from Aspire via
`ConnectionStrings__LoginDb`. The CharServer uses `ConnectionStrings__CharDb`.

Aspire uses a new `athena-sql-server-2025` data volume. It never mounts the retired Azure SQL Edge volume; see [SQL Server development database](sql-server-development.md) for data and Apple Silicon guidance.

## Inter-server service tokens
Interserver authentication uses two independent HMAC-SHA256 challenge/response
handshakes, each over its own shared `ServiceToken` - there is no database row
to seed for either, and no username/password interserver credential exists
anywhere in this stack:

- CharServer -> LoginServer: `ServiceAuthentication.CharServer.Token`
  (see `ai/login-server.md`, "Inter-server service authentication").
- MapServer -> CharServer: `ServiceAuthentication.MapServer.Token`
  (see `ai/char-server.md`, "Inter-server service authentication (MapServer)").

Both tokens must be Base64-encoded and decode to at least 32 bytes (256 bits):

```sh
openssl rand -base64 32
```

Generate a **separate** random value for each token - they protect two
different trust boundaries and must never be equal in production. Configure
them in `solutionfiles/secrets/secret.json`:

```json
{
  "ServiceAuthentication": {
    "CharServer": { "Token": "..." },
    "MapServer": { "Token": "..." }
  }
}
```

or via the `ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN` /
`ATHENA_NET_MAP_SERVER_SERVICE_TOKEN` environment variables, which take
priority over the secrets file when set.
