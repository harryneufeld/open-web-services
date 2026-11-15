# Open Web Services

Provider-independent cloud platform. MVP: manage Hetzner Cloud servers and DNS zones, install Docker-based apps.

## Architecture (dependencies point inward)

```
Web (Blazor Server, composition root)
  -> Infrastructure (EF Core/PostgreSQL, Hetzner API client, SSH.NET, background workers)
       -> Application (use cases + ports: IProviderConnector, IServerProvider, IDnsProvider, IRemoteShell, repositories)
            -> Domain (ProviderGroup, Server, DnsZone, AppInstall; no dependencies)
```

### Provider groups

A **group** is one set of credentials at one provider (a Hetzner Cloud project token, later e.g. an AWS account or an
OVH application). Every resource belongs to a group. A provider type declares which resource kinds it supports
(`ResourceKind`: servers, DNS) and which credential fields it needs; each group chooses which kinds to sync.
Adding a group verifies the credentials and imports existing resources. A background worker re-syncs every group
every 60 s, every 3 s while a server changes state, and immediately after actions. Pages refresh as soon as data
changes (in-process notifications over the Blazor circuit).

Adding a provider = implement `IProviderConnector` (returns an `IProviderConnection` exposing `IServerProvider` and/or
`IDnsProvider`) in Infrastructure and register it in `DependencyInjection`. The UI builds its forms from the
connector's `ProviderTypeInfo`. A new resource kind = a `ResourceKind` flag, a capability interface, and an
`IResourceSync` implementation.

Server networking (`IServerNetworkProvider`), firewalls (`IFirewallProvider`) and domain registration
(`IDomainRegistrar`) are optional capabilities of a connection; the UI explains when a provider has none. They are
read and written live at the provider, nothing is stored. Offers carry an `OfferTier` (provider-neutral category and
published availability SLA) for filtering.

Provider setup notes: [OVHcloud setup](docs/ovh.md) (one group per Public Cloud project; consumer key with GET, POST, PUT and DELETE on `/*`).

## Run locally

```bash
docker compose up -d db
dotnet run --project src/OpenWebServices.Web
```

Open the app, add a provider group under IOU (Provider groups) (existing servers and DNS zones are imported), then create servers or zones. Migrations are applied at startup.

## Tests

```bash
dotnet test
```

## Security

- Sign-in is a cookie login with PBKDF2-hashed passwords. The first visit shows `/setup` to create the admin account; after that setup is closed. Login attempts are limited to 10 per minute per IP. Behind a reverse proxy the limiter sees the proxy's IP until forwarded headers are configured, and the auth cookie is HTTPS-only outside Development, so terminate TLS at the proxy.
- Secrets (API tokens, server SSH keys) are encrypted with ASP.NET Data Protection. Back up `DataProtection:KeysPath` (default `data-protection-keys/`); without it stored secrets are unreadable.
- SSH host keys are trusted on first contact.
