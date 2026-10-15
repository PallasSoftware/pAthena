# Official server images

Release tags `vMAJOR.MINOR.PATCH` publish four production images to GHCR by the `release-images` workflow:

| Image | Contents |
|---|---|
| `ghcr.io/pallassoftware/pathena/loginserver` | LoginServer |
| `ghcr.io/pallassoftware/pathena/charserver` | CharServer, `char_athena.conf`/`inter_athena.conf` templates, `conf/import` |
| `ghcr.io/pallassoftware/pathena/mapserver` | MapServer, Athena Map Pack (`MapData/AthenaMaps.bin`), `map_athena.conf` template, `world_partitions.json` |
| `ghcr.io/pallassoftware/pathena/worldserver` | WorldServer (Orleans silo), `world_partitions.json`, rAthena `map_cache.dat` |

The images contain no environment-specific configuration and no secrets. They run as UID 1654 on `dotnet/runtime`, with base
images pinned by digest. Deployments mount their own configuration (for example a ConfigMap at `/app/conf/k8s`) and supply
secrets at runtime. `docker-compose.yml` builds the same Dockerfiles.

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
