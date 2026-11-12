namespace OpenWebServices.Application.Servers;

/// <summary>The cloud-init user data every new server receives: Docker plus a basic firewall.</summary>
public static class ServerBootstrapScript
{
    public const string Content = """
        #cloud-config
        package_update: true
        packages:
          - ufw
          - curl
        runcmd:
          - curl -fsSL https://get.docker.com | sh
          - systemctl enable --now docker
          - ufw allow 22/tcp
          - ufw allow 80/tcp
          - ufw allow 443/tcp
          - ufw --force enable
          - mkdir -p /opt/apps
        """;
}
