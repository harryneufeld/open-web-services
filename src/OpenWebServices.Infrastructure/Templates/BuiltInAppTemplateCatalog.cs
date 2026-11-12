using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Templates;

internal sealed class BuiltInAppTemplateCatalog : IAppTemplateCatalog
{
    public IReadOnlyList<AppTemplate> All { get; } =
    [
        new AppTemplate(
            "nginx", "Nginx", "Web server on port 80.",
            """
            services:
              web:
                image: nginx:stable
                restart: unless-stopped
                ports:
                  - "80:80"
            """,
            []),
        new AppTemplate(
            "postgres", "PostgreSQL 17", "Database, reachable on the server only (127.0.0.1:5432).",
            """
            services:
              db:
                image: postgres:17
                restart: unless-stopped
                environment:
                  POSTGRES_USER: app
                  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
                ports:
                  - "127.0.0.1:5432:5432"
                volumes:
                  - pgdata:/var/lib/postgresql/data
            volumes:
              pgdata:
            """,
            ["POSTGRES_PASSWORD"]),
        new AppTemplate(
            "uptime-kuma", "Uptime Kuma", "Self-hosted monitoring on port 3001.",
            """
            services:
              kuma:
                image: louislam/uptime-kuma:1
                restart: unless-stopped
                ports:
                  - "3001:3001"
                volumes:
                  - kuma:/app/data
            volumes:
              kuma:
            """,
            []),
    ];

    public AppTemplate? Find(string templateId) => All.FirstOrDefault(t => t.Id == templateId);
}
