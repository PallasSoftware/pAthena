# Official server images

Every push to `main` is a release: `release-version.yml` tests it and tags it `vMAJOR.MINOR.PATCH`, and the new tag starts `release-images.yml`, which publishes four production images to GHCR with that tag (for example `ghcr.io/pallassoftware/pathena/loginserver:v0.0.1`):

| Image | Contents |
|---|---|
| `ghcr.io/pallassoftware/pathena/loginserver` | LoginServer |
| `ghcr.io/pallassoftware/pathena/charserver` | CharServer, `char_athena.conf`/`inter_athena.conf` templates, `conf/import` |
| `ghcr.io/pallassoftware/pathena/mapserver` | MapServer, Athena Map Pack (`MapData/AthenaMaps.bin`), `map_athena.conf` template, `world_partitions.json` |
| `ghcr.io/pallassoftware/pathena/worldserver` | WorldServer (Orleans silo), `world_partitions.json`, rAthena `map_cache.dat` |

The images contain no environment-specific configuration and no secrets. They run as UID 1654 on `dotnet/runtime`, with base
images pinned by digest. Deployments mount their own configuration (for example a ConfigMap at `/app/conf/k8s`) and supply
secrets at runtime. `docker-compose.yml` builds the same Dockerfiles.

## Versions

- `MAJOR.MINOR` is the single line of the `VERSION` file (`0.0` while in beta) and is changed by hand in a pull request.
- `PATCH` is derived from the existing `v*` release tags: the next free number of the `MAJOR.MINOR` series. Changing
  `VERSION` starts the new series at `PATCH` 0 (the very first release is `0.0.1`, there is no `0.0.0`).
  `0.0.1 → 0.0.2 → 0.0.3`, bump to `0.1` → `0.1.0 → 0.1.1`, bump to `1.0` → `1.0.0 → 1.0.1`.
- No version commit is made, so nothing can loop: the tag is created by the workflow with `GITHUB_TOKEN`, which does not
  start another run. Tags are never moved or deleted. A pull request never tags or publishes.
- Order: tests pass on the commit → the tag is created → the images are published. A re-run of the same commit keeps its tag;
  two concurrent runs never get the same version (a rejected tag push is retried with the next free number); lowering
  `VERSION` below an existing release fails the run.
- A version tag starts `release-images.yml` (explicitly via `workflow_dispatch`, because a tag pushed with `GITHUB_TOKEN` starts nothing; a `v*` tag pushed by hand starts it directly). If it fails half way, re-run it: an image tag that already exists is never overwritten or rebuilt, only the missing ones are added.
- Nothing here deploys: `pathena-server` selects a release through its submodule and deploys on its own.
- Tests: `pwsh .github/scripts/Test-ReleaseVersion.ps1`.

## Same digest for unchanged servers

A release does not rebuild a server whose inputs did not change since the previous release tag (its project, `src/Shared`,
the projects it compiles, its configuration templates and `Directory.Build.props`; see `.github/scripts/ReleaseImages.psm1`).
The previous image is re-tagged with the new version, so it keeps its digest and deployments pinned by digest see no change.
Tests, docs and other servers' code are not inputs. The first release builds all four.

Maintainers: when a server starts to compile a new directory, add it to `ReleaseImages.psm1`. Run the decision tests with
`pwsh .github/scripts/Test-ReleaseImages.ps1`.

## Advertised public address

LoginServer (`ATHENA_NET_LOGIN_IRO_CHAR_IP`) and CharServer (`ATHENA_NET_CHAR_IRO_MAP_IP`) take an IPv4 address **or a hostname**.
The Ragnarok client only understands IPv4, so a hostname is resolved to an A record (`src/Shared/Net/AdvertisedIpv4.cs`):
at startup (failure to resolve is fatal) and again whenever the cached answer is older than 60 seconds, each time a client
handoff packet is built. A changed WAN address therefore reaches new connections without a restart; existing connections may
drop. If a later lookup fails the last good address is kept and an error is logged.

## Real client IP behind a TCP load balancer (LoginServer)

Ragnarok is raw TCP, so a load balancer such as Traefik reports the client address with the **PROXY protocol v2** (binary header
before the first byte), not with HTTP headers. It is opt-in and generic (`src/Shared/Net/ProxyProtocolV2.cs`):

- `ATHENA_NET_LOGIN_PROXY_PROTOCOL_TRUSTED` = comma separated IPs/CIDRs of the proxies allowed to send the header, for example
  `10.42.0.0/16`. Empty (the default) = off; an invalid entry stops the server at startup. The addresses are deployment specific
  and belong in the deployment configuration, not in pAthena.
- A connection **from a trusted address must start with a valid v2 header**; the real client address replaces the peer address
  for authentication, login logs, IP bans (including dynamic bans), DNSBL checks and the address stored for the session. A
  missing, malformed, truncated or oversized header (or none within 5 s) closes the connection with a warning: guessing would
  attribute the proxy's address to a player. `LOCAL` headers (proxy health checks) and unspecified families keep the peer address.
- A connection from **any other address is direct and nothing is parsed**, so a client cannot claim another address. This is
  what the CharServer (it connects to the Service, not through the proxy), kubelet TCP probes and direct connections use, on the
  same listener, with or without the feature enabled.
- Rollout: the proxy and the server must be switched together. If Traefik sends headers while the variable is empty, logins
  fail (the header is not a login packet); if the variable is set while Traefik sends none, trusted-source connections are
  rejected. Everything that connects through the proxy needs the header, so enable both in one release.
