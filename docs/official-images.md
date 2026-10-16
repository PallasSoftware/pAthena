# Official server images

Every push to `main` that passes validation is a complete release, in ONE workflow (`.github/workflows/release.yml`, no manual step, nothing
deployed): `Validate` → `Tag the release` → `Publish the images` → `GitHub Release`. It publishes four production images to GHCR with the
version as tag (for example `ghcr.io/pallassoftware/pathena/loginserver:v0.0.1`):

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
- One workflow, sequential dependent jobs, all visible in the same run: each job `needs` the previous one (no `always()`), so a failing
  validation creates no tag, no tag publishes nothing, and the GitHub Release is created only after all four image tags exist.
  To finish a failed release use **Re-run failed jobs** of that run: the commit keeps its tag, an image tag that already exists is never
  overwritten or rebuilt (only the missing ones are added), and an existing GitHub Release is left alone.
- Nothing here deploys: `pathena-server` selects a release through its submodule and deploys on its own.
- Tests: `pwsh .github/scripts/Test-ReleaseVersion.ps1`, `Test-ReleaseImages.ps1`, `Test-ReleaseWorkflowRules.ps1` (also the real-registry digest check in CI).

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

Ragnarok is raw TCP, so a load balancer such as Traefik reports the client address with the **PROXY protocol v2** (a binary header
before the first byte), not with HTTP headers. It is opt-in and generic (`src/Shared/Net/ProxyProtocolV2.cs`) and uses a
**separate listener**:

- `ATHENA_NET_LOGIN_PROXY_PORT` (unset/0 = off; an invalid value stops the server at startup) opens a second LoginServer listener on
  that port. **Every connection to it must start with a valid v2 header**; the client address it carries replaces the peer
  address for authentication, login logs, IP bans (including dynamic bans), DNSBL checks and the address stored for the session.
  A missing, malformed, truncated or oversized header (or none within 5 s) closes the connection with a warning, because guessing
  would attribute the proxy's address to a player. `LOCAL` headers (proxy health checks) and unspecified families keep the peer address.
- The ordinary `LoginPort` (6900) never parses a header. The CharServer (it connects to the Service directly), kubelet probes and
  direct connections keep using it, with or without the feature.
- The proxy listener does not authenticate its peer: **whoever can reach it can claim any client address.** Restrict it in the
  network (in Kubernetes a NetworkPolicy that only admits the load balancer's pods by label) and do not route clients to it before
  that restriction is active. No addresses are configured in pAthena.
