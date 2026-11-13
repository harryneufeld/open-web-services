#!/usr/bin/env bash
# Installs (or updates) Open Web Services on a 64-bit Raspberry Pi OS / Debian host.
# Usage: sudo bash install-on-pi.sh /path/to/ows-linux-arm64.tar.gz
set -euo pipefail

ARCHIVE="${1:?usage: install-on-pi.sh <archive.tar.gz>}"
APP_DIR=/opt/open-web-services
CONF_DIR=/etc/open-web-services
DATA_DIR=/var/lib/open-web-services
PORT=5000

[ "$(id -u)" -eq 0 ] || { echo "Run as root (sudo)." >&2; exit 1; }
[ "$(uname -m)" = "aarch64" ] || { echo "A 64-bit OS (aarch64) is required, found $(uname -m)." >&2; exit 1; }
[ -f "$ARCHIVE" ] || { echo "Archive not found: $ARCHIVE" >&2; exit 1; }

echo "==> Installing PostgreSQL and runtime libraries"
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y postgresql libicu-dev openssl curl

echo "==> Creating service user and directories"
id -u ows >/dev/null 2>&1 || useradd --system --home "$DATA_DIR" --create-home --shell /usr/sbin/nologin ows
mkdir -p "$APP_DIR" "$CONF_DIR" "$DATA_DIR/keys"
chown -R ows:ows "$DATA_DIR"
chmod 700 "$DATA_DIR/keys"

if [ ! -f "$CONF_DIR/env" ]; then
  echo "==> Creating database and configuration"
  DB_PASSWORD="$(openssl rand -hex 24)"
  sudo -u postgres psql -v ON_ERROR_STOP=1 \
    -c "CREATE ROLE ows LOGIN PASSWORD '${DB_PASSWORD}'" \
    -c "CREATE DATABASE ows OWNER ows"
  cat > "$CONF_DIR/env" <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:${PORT}
ConnectionStrings__Default=Host=localhost;Database=ows;Username=ows;Password=${DB_PASSWORD}
DataProtection__KeysPath=${DATA_DIR}/keys
Authentication__AllowInsecureCookie=true
EOF
  chown root:ows "$CONF_DIR/env"
  chmod 640 "$CONF_DIR/env"
else
  echo "==> Keeping existing configuration in $CONF_DIR/env"
fi

echo "==> Installing application files"
systemctl stop open-web-services 2>/dev/null || true
find "$APP_DIR" -mindepth 1 -delete
tar -xzf "$ARCHIVE" -C "$APP_DIR"
chmod +x "$APP_DIR/OpenWebServices.Web"
chown -R root:root "$APP_DIR"

echo "==> Installing systemd service"
cat > /etc/systemd/system/open-web-services.service <<EOF
[Unit]
Description=Open Web Services
After=network.target postgresql.service

[Service]
User=ows
WorkingDirectory=${APP_DIR}
EnvironmentFile=${CONF_DIR}/env
ExecStart=${APP_DIR}/OpenWebServices.Web
Restart=always
RestartSec=5
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now open-web-services

echo "==> Waiting for the app to answer"
for _ in $(seq 1 30); do
  code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:${PORT}/login" || true)"
  if [ "$code" = "200" ] || [ "$code" = "302" ]; then
    echo "OK: http://$(hostname -I | awk '{print $1}'):${PORT}  (HTTP ${code})"
    exit 0
  fi
  sleep 2
done

echo "The app did not answer in time. Recent log:" >&2
journalctl -u open-web-services -n 40 --no-pager >&2
exit 1
