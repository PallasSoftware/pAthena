# Migrations

LoginServer has two separate EF Core `DbContext`s with their own migration
histories, and both must be applied to a fresh database:

- `LoginDbContext` — the legacy Ragnarok wire-protocol tables (`login`,
  `ipbanlist`, `loginlog`, account registry, etc.). Migrations live in
  `src/LoginServer/Migrations/`.
- `AthenaIdentityDbContext` — ASP.NET Core Identity's own tables plus
  `AthenaGameAccounts` (see `ai/login-server.md` for the schema). Migrations
  live in `src/LoginServer/Migrations/Identity/`.

Both contexts are configured for SQL Server only (MySQL/Pomelo support was
removed from LoginServer).

## Apply migrations to an existing/fresh database

```bash
dotnet tool install --global dotnet-ef --version 10.0.12  # once, if not already installed
export PATH="$PATH:$HOME/.dotnet/tools"

dotnet ef database update --project src/LoginServer/LoginServer.csproj --context LoginDbContext
dotnet ef database update --project src/LoginServer/LoginServer.csproj --context AthenaIdentityDbContext
```

`./src/LoginServer/scripts/migrations-update.sh` runs both of the above in
sequence against the connection string LoginServer itself resolves
(`secrets.json` / `conf/inter_athena.conf` / `ATHENA_NET_LOGIN_DB_CONNECTION`).
LoginServer also applies any pending migrations for both contexts
automatically on startup (with retry), so this script is only needed to
apply migrations ahead of time or outside of running the server.

## Automatic migrations and migration-only mode

LoginServer and CharServer (the servers that own an EF Core schema) share the same options:

| Mode | LoginServer | CharServer | Default |
| --- | --- | --- | --- |
| Migrate at startup, then run | `--auto-migrate` or `ATHENA_NET_LOGIN_DB_AUTOMIGRATE=true` | `--auto-migrate` or `ATHENA_NET_CHAR_DB_AUTOMIGRATE=true` | off |
| Migrate and exit | `--migrate-only` | `--migrate-only` | off |

Nothing migrates implicitly: with neither option the server starts against whatever schema exists.

`--migrate-only` applies pending EF Core migrations (LoginServer: `LoginDbContext` then `AthenaIdentityDbContext`; CharServer: `CharDbContext`) and exits. It starts no listeners, background services, LoginServer connection, self-test or account provisioning, and takes precedence over `--self-test` and `--create-account-username`. Unlike `--auto-migrate` it never falls back to `EnsureCreated`: a context with no registered migrations is an error.

Connection string: resolved exactly as for normal startup (`ConnectionStrings__LoginDb` / `ATHENA_NET_LOGIN_DB_CONNECTION` for LoginServer, `ConnectionStrings__CharDb` / `ATHENA_NET_CHAR_DB_CONNECTION` for CharServer, then `secret.json`, then `conf/inter_athena.conf`). SQL Server only. The login must be allowed to alter the schema. Connection strings and passwords are not logged. The database is retried for about two minutes (60 x 2s) while it starts. CharServer expects the database itself to exist already; LoginServer creates it if the login may.

Exit codes: `0` all migrations applied (or nothing pending); `1` database not configured or unsupported provider, no migrations registered, database unreachable after retries, or a migration failed.

Re-running is safe: with nothing pending it changes nothing. LoginServer uses EF Core 10, which holds a database migration lock, so overlapping runs are serialized. CharServer uses EF Core 9, which does not lock: do not run two CharServer migrators against the same database at once (for example, use a single replica or a deployment strategy that never overlaps).

```bash
# Local, Docker Compose or CI: run once, check the exit code
dotnet run --project src/LoginServer -- --migrate-only
dotnet run --project src/CharServer  -- --migrate-only
```

In a container platform, run the same image with `--migrate-only` as a one-shot step (for example a Docker Compose service with `restart: "no"` that the server `depends_on` with `service_completed_successfully`, or a Kubernetes initContainer), and start the server without `--auto-migrate`.

## Adding a new migration

```bash
dotnet ef migrations add <Name> --project src/LoginServer/LoginServer.csproj --context LoginDbContext -o Migrations
dotnet ef migrations add <Name> --project src/LoginServer/LoginServer.csproj --context AthenaIdentityDbContext -o Migrations/Identity
```

`./src/LoginServer/scripts/migrations-init.sh` creates the very first
`LoginDbContext` migration; the `AthenaIdentityDbContext` migrations were
generated the same way (see the `dotnet ef migrations add ... --context
AthenaIdentityDbContext -o Migrations/Identity` command above) and already
exist in the repository, so `migrations-init.sh` does not need to be run
again for Identity unless the schema needs a brand-new baseline.
