# CI: required checks, test isolation, vulnerabilities

## What must pass before a merge

One status check, **`Validate`** (workflow `release`, job `Validate`), runs for **every pull request and every push to `main`
with exactly the same steps**:

1. the release script tests (`Test-ReleaseVersion.ps1`, `Test-ReleaseImages.ps1`) and the workflow rules (`Test-ReleaseWorkflowRules.ps1`);
2. `Test-NoVulnerablePackages.ps1`: no direct or transitive package with a known vulnerability (nothing is suppressed);
3. the complete test suites of LoginServer, CharServer, MapServer and World (`dotnet test`, Release).

The per-server workflows (`login-server`, `char-server`, `map-server`, `world-data-importer`) are path filtered and stay as extra feedback;
they can not be *required* (a required check that is skipped by a path filter blocks the merge forever). `Validate` has no path filter.

A pull request also runs `Re-tag keeps the digest` (a real throw-away registry). Because the same jobs validate the pull request and `main`, a commit that
was merged green is validated again before it is released; a failure on `main` stops the chain inside the same run:

- `Tag the release` `needs: [Validate, Re-tag keeps the digest]` and uses no `always()`/`!cancelled()`: **a failed validation never creates a version tag**;
- `Publish the images` needs the tag job and `GitHub Release` needs the publish job: **nothing is published without a tag, no release without all four images**;
- pull requests never tag, publish or release, and nothing deploys (`Test-ReleaseWorkflowRules.ps1` asserts all of this and fails the build if a refactoring weakens it).

## Making it enforced (repository setting, needs an administrator)

The `Protect main` ruleset currently has no required status check, so a red `Validate` does not by itself block a merge. Add it in
*Settings -> Rules -> Rulesets -> Protect main -> Require status checks to pass*: add **`Validate`** and **`Re-tag keeps the digest`** (GitHub Actions), and tick
*Require branches to be up to date before merging*. With the CLI (replace the ruleset id, review the JSON first: it replaces the rule list):

```sh
gh api repos/PallasSoftware/pAthena/rulesets/<id> --method PUT --input updated-ruleset.json
# updated-ruleset.json = the current ruleset (gh api repos/PallasSoftware/pAthena/rulesets/<id>) plus the rule
# {"type":"required_status_checks","parameters":{"strict_required_status_checks_policy":true,"required_status_checks":[{"context":"Validate"},{"context":"Re-tag keeps the digest"}]}}
```

## Test isolation rules

xUnit runs test classes in parallel; process-wide state is shared between them. Rules for tests in this repository:

- **Logging**: never `Console.SetOut`, never `LoginLogger.Configure` in a test. Use `LoginLogger.UseScope(writer, config)`: it redirects the output
  *and* the settings of the logging done in the current async flow only, so parallel tests can neither pollute the captured text nor be
  affected by its configuration.
- **Environment variables**: configuration objects read the environment when they are created (`new LoginConfig()`), so changing a variable
  in one test breaks unrelated tests at random. Test parsing through the pure functions (`LoginConfig.ParseProxyPort`,
  `AdvertisedIpv4.FromValue`). If a test really must change the process environment, put its class in `[Collection(ProcessEnvironmentCollection.Name)]`:
  xUnit then runs that collection alone, after the parallel tests; everything else stays parallel.
- Never `Assert.True(true)`: a test asserts the behavior it names.

How it was found: 25 repeated runs of the LoginServer suite on `main` failed 4 times with four different tests
(`LoginLoggerTimestampTests`, `LoginConfigStoreTests`, `ServiceCompositionTests`, `ServiceAuthenticationServiceTests`): the logger tests switched the
global `Console.Out`/logger configuration while other tests logged, and the environment-variable tests for the advertised address and the
proxy port made `new LoginConfig()` throw in tests running at the same time.

## Vulnerable packages

`System.Security.Cryptography.Pkcs` 9.0.11 (GHSA-846f-f764-76mx high, GHSA-2fmv-rv2c-59x7 moderate) arrived transitively through
`Microsoft.Data.SqlClient` 6.1.x (LoginServer, CharServer) and 10.0.11 through `Aspire.Hosting.SqlServer` (AppHost). It is pinned to the patched
10.0.13 as a direct reference in those three projects (comment next to each) and the lock files are updated. Remove the pins when
`Microsoft.Data.SqlClient` / Aspire depend on a patched version themselves; `Test-NoVulnerablePackages.ps1` keeps this from regressing.
