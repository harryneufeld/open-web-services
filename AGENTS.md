# Notes for coding agents

## Project

ows (Open Web Services): one console for servers and DNS across hosting providers. ASP.NET Core 10, Blazor Server
(interactive server rendering), EF Core with PostgreSQL, plain CSS. Architecture and the provider plug-in model are
described in `README.md`.

## Commands

- Build: `dotnet build`
- Test: `dotnet test` (unit tests plus an in-memory integration test of the web app)
- Run: `docker compose up -d db`, then `dotnet run --project src/OpenWebServices.Web`
- New migration: `dotnet ef migrations add <Name> --project src/OpenWebServices.Infrastructure --startup-project src/OpenWebServices.Web --output-dir Persistence/Migrations`
  (`dotnet tool restore` first). Migrations run automatically at startup; data-changing migrations must preserve
  existing rows.
- Release for a 64-bit Raspberry Pi OS / Debian host:
  `dotnet publish src/OpenWebServices.Web -c Release -r linux-arm64 --self-contained true -o artifacts/publish`,
  pack the folder as `ows-linux-arm64.tar.gz` and install it with `sudo bash deploy/install-on-pi.sh <archive>`.
  Back up the database before installing a release that contains a migration.

## Conventions

- Dependencies point inward: Web → Infrastructure → Application → Domain. Domain has no dependencies.
- Warnings are errors (`Directory.Build.props`). Keep the build clean.
- Provider specifics live only in Infrastructure behind `IProviderConnector`, `IServerProvider` and `IDnsProvider`.
- Never commit secrets, hostnames or personal details. Machine-specific notes belong in `CLAUDE.local.md`
  (git-ignored).
