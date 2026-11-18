using System.Text;
using OpenWebServices.Application.Access;

namespace OpenWebServices.Application.Servers;

/// <summary>The cloud-init user data every new server receives: SSH access, Docker and a basic firewall.</summary>
public static class ServerBootstrapScript
{
    /// <param name="authorizedKeys">
    /// authorized_keys lines for root: the ows key and the keys chosen when the server was ordered. They are written
    /// as the ows block, so later changes from ows replace them cleanly.
    /// </param>
    public static string Build(IReadOnlyList<string> authorizedKeys)
    {
        var script = new StringBuilder();
        script.Append("""
            #cloud-config
            # Keys only: no password logins, and root keeps its keys (some images redirect root to another user).
            disable_root: false
            ssh_pwauth: false
            package_update: true
            packages:
              - ufw
              - curl

            """);
        script.Append("""
            write_files:
              - path: /root/.ssh/authorized_keys
                owner: root:root
                permissions: '0600'
                append: true
                content: |

            """);
        foreach (var line in AuthorizedKeysFile.Rewrite("", authorizedKeys, []).Content.TrimEnd('\n').Split('\n'))
            script.Append("      ").Append(line).Append('\n');
        script.Append("""
            runcmd:
              - chmod 700 /root/.ssh
              - curl -fsSL https://get.docker.com | sh
              - systemctl enable --now docker
              - ufw allow 22/tcp
              - ufw allow 80/tcp
              - ufw allow 443/tcp
              - ufw --force enable
              - mkdir -p /opt/apps

            """);
        return script.ToString();
    }
}
