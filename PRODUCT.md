# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Developers and operators who run servers and DNS at more than one hosting provider and want to manage them in one
place. Self-hosted today; a hosted offering is an open question.

## Product Purpose

ows ("Open Web Services") is a control plane for infrastructure spread across hosting providers: connect provider
accounts, see and manage servers and DNS in one place, create new servers from offers across providers, and install
Docker-based apps on servers it created.

## Positioning

One console, many providers: Hetzner today, AWS, Azure, OVH and others next, without provider lock-in. Resources keep
living at the provider; ows imports and manages them through each provider's API.

## Operating Context

- Credentials are organized as **provider groups**: one credential set at one provider (e.g. one Hetzner Cloud
  project token). Each group chooses which resource kinds it syncs (servers, DNS).
- Adding a group imports existing resources; a background sync keeps them current and pages update live.
- Single admin sign-in today; no tenants, roles or billing yet.

## Capabilities and Constraints

- Servers: list, import, create from offers, power on/off/reboot, delete, app install (on servers ows has access to).
- SSH keys: members add public keys (pasted, or generated in the browser with the private key downloaded) and give
  them access per server; ows writes authorized_keys over SSH, flags unknown keys, pins host keys and can rotate or
  revoke its own key.
- DNS: zones (import, create, delete) and record sets (read live, create, edit, delete).
- Server networking and firewall (read and written live): public addresses, reverse DNS, private networks, and one
  inbound firewall per server managed by ows; firewalls made elsewhere are shown, never changed. Networking and
  firewalls for Hetzner and OVHcloud (OVHcloud's Edge Network Firewall: IPv4 only). Server labels for Hetzner.
- Offers carry a provider-neutral category (shared budget, dedicated, compute-optimized, ...) and the availability
  SLA the provider publishes; unknown SLAs stay empty instead of guessed.
- Domains: search availability and price across registrar groups, quote and place orders (Admin role, audited),
  list registered domains. OVHcloud today.
- Built with ASP.NET Core Blazor Server (interactive server rendering), plain CSS, no JS framework.
- Runs on low-power hardware (e.g. a Raspberry Pi 4), possibly without access to CDNs: no external font or asset hosts.
- Prices are shown net in the provider's currency.

## Brand Commitments

- Name: ows, short for "Open Web Services", written lowercase.
- Logo, colours, service names and voice are defined in `docs/brand.md` and are binding.
- Humour: ows winks at the cloud-console genre. Parody service names are always paired with a plain label. Jokes live
  in names, taglines, empty states and fine print; buttons, errors, confirmations and anything about money stay plain.
- Never use other companies' brand assets (logos, colour schemes, service names, icons), and never name or allude to
  other companies in jokes.
- Visual quality bar: the best modern developer consoles.

## Evidence on Hand

No screenshots, testimonials, customers or pricing exist. Do not invent customer claims, uptime numbers or prices.

## Product Principles

1. Provider-neutral: every screen works the same whichever provider a resource comes from; the provider is a label,
   not a different UI.
2. Truth from the provider: what is shown reflects the provider's real state, and pending actions are visible.
3. Never surprise destructive actions: deletes at the provider are explicit and confirmed.
4. Calm density: an operator scans many resources quickly; information first, decoration never.
