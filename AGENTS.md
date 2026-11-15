# Notes for coding agents

## Project

ows (Open Web Services): one console for servers and DNS across hosting providers. ASP.NET Core 10, Blazor Server
(interactive server rendering), EF Core with PostgreSQL, plain CSS. Architecture and the provider plug-in model are
described in `README.md`; product context in `PRODUCT.md`; visual identity in `docs/brand.md`.

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
- Versions come from git via Nerdbank.GitVersioning: `version.json` holds major.minor, the patch is the number of
  commits since that file last changed (every commit bumps it), e.g. `0.1.12`; the full version carries the commit
  id (`0.1.12+1a2b3c4d5e`). The height counts commits on the current branch, so merge to `main` before deploying a
  release to keep numbers unique. Raise `version` in `version.json` for a new minor/major (the patch restarts at 0). Builds need
  the full git history (no shallow clones). The version shows in the sidebar and in the startup log.

## Conventions

- Dependencies point inward: Web → Infrastructure → Application → Domain. Domain has no dependencies.
- Warnings are errors (`Directory.Build.props`). Keep the build clean.
- Provider specifics live only in Infrastructure behind `IProviderConnector`, `IServerProvider` and `IDnsProvider`.
- UI: reuse the components in `Components/Shared` and the tokens in `wwwroot/app.css`; follow `docs/brand.md` and the interface rules in `docs/design.md`.
- Never commit secrets, hostnames or personal details. Machine-specific notes belong in `CLAUDE.local.md`
  (git-ignored).
