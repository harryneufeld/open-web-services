# Open Web Services

Provider-independent cloud platform. MVP: create and manage Hetzner servers, install Docker-based apps.

## Architecture (dependencies point inward)

```
Web (Blazor Server, composition root)
  -> Infrastructure (EF Core/PostgreSQL, Hetzner API client, SSH.NET, background workers)
       -> Application (use cases + ports: IHostingProvider, IRemoteShell, repositories)
            -> Domain (Server, AppInstall, ProviderAccount; no dependencies)
```

Adding a provider = implement `IHostingProvider` in Infrastructure and register it in `HostingProviderFactory`.

## Run locally

```bash
docker compose up -d db
dotnet run --project src/OpenWebServices.Web
```

Open the app, add a Hetzner API token under Settings, then create a server. Migrations are applied at startup.

## Tests

```bash
dotnet test
```

## Security

- Sign-in is a cookie login with PBKDF2-hashed passwords. The first visit shows `/setup` to create the admin account; after that setup is closed. Login attempts are limited to 10 per minute per IP. Behind a reverse proxy the limiter sees the proxy's IP until forwarded headers are configured, and the auth cookie is HTTPS-only outside Development, so terminate TLS at the proxy.
- Secrets (API tokens, server SSH keys) are encrypted with ASP.NET Data Protection. Back up `DataProtection:KeysPath` (default `data-protection-keys/`); without it stored secrets are unreadable.
- SSH host keys are trusted on first contact.
