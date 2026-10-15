# Official server images

Every push to `main` is a release: the `release-images` workflow validates it, tags it `vMAJOR.MINOR.PATCH` and publishes four production images to GHCR with that tag (for example `ghcr.io/pallassoftware/pathena/loginserver:v0.0.1`):

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
- If the publish step fails after the tag exists, re-run the workflow: the commit keeps its version and the images are published.
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
