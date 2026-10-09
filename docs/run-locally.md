# Run Locally

From repo root:
```
dotnet run --project src/LoginServer
```

From `src/LoginServer`:
```
dotnet run -- --secrets ../../solutionfiles/secrets/secret.json
```

Auto-migrate schema at startup (dev only):
```
dotnet run --project src/LoginServer -- --auto-migrate
```

Or via env var:
```
export ATHENA_NET_LOGIN_DB_AUTOMIGRATE=true
dotnet run --project src/LoginServer
```

Migrate and exit:
```
dotnet run --project src/LoginServer -- --migrate-only
```
Applies pending migrations for both `LoginDbContext` and `AthenaIdentityDbContext`, then exits `0`; exits nonzero if the database is not configured or a migration fails. It starts no listeners and runs no self-test or account creation, and it takes precedence over `--self-test` / `--create-account-username`. Safe to re-run. See `docs/migrations.md`.

Note: .NET Aspire is the preferred local runtime (`docs/aspire.md`). This page is for running LoginServer directly.
