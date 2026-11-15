# syntax=docker/dockerfile:1
#
# Multi-arch image (linux/amd64 and linux/arm64, e.g. Raspberry Pi 64-bit).
# The SDK stage runs on the build machine's native architecture and cross-compiles for the target, so
# arm64 images build without emulation.
#
# Build locally:  docker build -t open-web-services .
# The build needs the git history: Nerdbank.GitVersioning derives the version from it (see version.json).

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY . .
# Docker names the architectures amd64/arm64, .NET names them x64/arm64.
RUN --mount=type=cache,target=/root/.nuget/packages \
    case "$TARGETARCH" in amd64) RID=x64 ;; arm64) RID=arm64 ;; *) echo "unsupported: $TARGETARCH" >&2; exit 1 ;; esac \
    && dotnet publish src/OpenWebServices.Web/OpenWebServices.Web.csproj \
        -c Release \
        -a "$RID" \
        -o /app/publish \
        -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DataProtection__KeysPath=/var/lib/open-web-services/keys

# The aspnet image ships a non-root "app" user (uid 1654). The keys directory must be writable by it, and it
# must be a volume (see docker-compose.yml): without these keys, stored secrets cannot be decrypted.
RUN mkdir -p /var/lib/open-web-services/keys \
    && chown -R app:app /var/lib/open-web-services \
    && chmod 700 /var/lib/open-web-services/keys

COPY --from=build /app/publish .

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenWebServices.Web.dll"]
