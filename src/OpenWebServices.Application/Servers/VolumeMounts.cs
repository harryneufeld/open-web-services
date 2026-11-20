using System.Globalization;
using System.Text.RegularExpressions;
using OpenWebServices.Domain;

namespace OpenWebServices.Application.Servers;

/// <summary>How an attached volume looks on the server.</summary>
/// <param name="DevicePresent">The server sees the volume's block device.</param>
/// <param name="Filesystem">e.g. "ext4"; null when the volume is empty.</param>
/// <param name="MountPoint">Where it is mounted; null when not mounted.</param>
public sealed record VolumeMount(bool DevicePresent, string? Filesystem, string? MountPoint, long? SizeBytes, long? UsedBytes);

/// <summary>
/// Shell scripts that mount, unmount and grow volumes on a server, run as root over SSH. They never format a volume
/// that holds data: only a device on which blkid finds nothing at all gets a new ext4 filesystem.
/// </summary>
public static partial class VolumeMounts
{
    /// <summary>Directories nothing may be mounted on or below.</summary>
    private static readonly string[] SystemTrees = ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/proc", "/run", "/sbin", "/sys", "/tmp", "/usr"];

    /// <summary>Directories that hold other things; a volume goes below them, e.g. /mnt/data or /var/lib/docker.</summary>
    private static readonly string[] Parents = ["/home", "/mnt", "/opt", "/root", "/srv", "/var", "/var/lib"];

    /// <summary>The usual place for a volume: /mnt/ plus its name.</summary>
    public static string DefaultMountPoint(string volumeName)
    {
        var slug = InvalidPathChars().Replace(volumeName.Trim().ToLowerInvariant(), "-").Trim('-', '.');
        return "/mnt/" + (slug.Length == 0 ? "volume" : slug);
    }

    /// <summary>An absolute directory outside the system directories, e.g. /mnt/data or /srv/backups.</summary>
    public static string NormalizeMountPoint(string? value)
    {
        var path = (value ?? "").Trim();
        if (path.Length > 1)
            path = path.TrimEnd('/');
        if (!ValidMountPoint().IsMatch(path) || path.Split('/').Any(part => part is "." or ".."))
            throw new DomainException("Mount point must be an absolute path such as /mnt/data: letters, digits, '.', '_', '-' and '/'.");
        if (path == "/" || Parents.Contains(path)
                        || SystemTrees.Any(system => path == system || path.StartsWith(system + "/", StringComparison.Ordinal)))
            throw new DomainException($"{path} is a system directory. Use a directory such as /mnt/data or /srv/data.");

        return path;
    }

    /// <summary>One line per device: path|present|filesystem|size used target.</summary>
    public static string StatusScript(IEnumerable<string> devicePaths)
    {
        var devices = string.Join(" ", devicePaths.Select(Quote));
        return $$"""
            for p in {{devices}}; do
              r=$(readlink -f "$p" 2>/dev/null || true)
              if [ -n "$r" ] && [ -b "$r" ]; then
                t=$(blkid -p -o value -s TYPE "$r" 2>/dev/null || true)
                m=$(findmnt -n -b -o SIZE,USED,TARGET -S "$r" 2>/dev/null | head -n 1)
                printf '%s|1|%s|%s\n' "$p" "$t" "$m"
              else
                printf '%s|0||\n' "$p"
              fi
            done
            """;
    }

    public static IReadOnlyDictionary<string, VolumeMount> ParseStatus(string output)
    {
        var result = new Dictionary<string, VolumeMount>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 4);
            if (parts.Length < 4)
                continue;
            var mount = parts[3].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var mounted = mount.Length == 3;
            result[parts[0]] = new VolumeMount(
                parts[1] == "1",
                parts[2].Length > 0 ? parts[2] : null,
                mounted ? mount[2] : null,
                mounted ? ParseLong(mount[0]) : null,
                mounted ? ParseLong(mount[1]) : null);
        }

        return result;
    }

    /// <summary>
    /// Waits for the device, formats it as ext4 when it is empty, adds it to /etc/fstab (with nofail, so a missing
    /// volume never blocks booting) and mounts it.
    /// </summary>
    public static string MountScript(string devicePath, string mountPoint) => $$"""
        set -e
        dev={{Quote(devicePath)}}; mnt={{Quote(mountPoint)}}
        i=0; while [ ! -b "$dev" ] && [ $i -lt 60 ]; do sleep 1; i=$((i+1)); done
        [ -b "$dev" ] || { echo "The server does not see the volume at $dev yet. Try again in a minute."; exit 3; }
        real=$(readlink -f "$dev")
        rc=0; blkid -p "$real" >/dev/null 2>&1 || rc=$?
        if [ $rc -eq 2 ]; then
          echo "The volume is empty; formatting it as ext4."
          mkfs.ext4 -q "$real"
        elif [ $rc -ne 0 ]; then
          echo "Could not inspect $real (blkid exit $rc)."; exit 4
        fi
        fs=$(blkid -p -o value -s TYPE "$real" || true)
        case "$fs" in ext2|ext3|ext4|xfs|btrfs) ;; *) echo "The volume holds '$fs', which ows does not mount. Mount it yourself."; exit 5;; esac
        uuid=$(blkid -p -o value -s UUID "$real")
        current=$(findmnt -n -o TARGET -S "$real" | head -n 1 || true)
        if [ -n "$current" ] && [ "$current" != "$mnt" ]; then echo "The volume is already mounted at $current."; exit 0; fi
        other=$(findmnt -n -o SOURCE --mountpoint "$mnt" || true)
        if [ -n "$other" ] && [ "$(readlink -f "$other")" != "$real" ]; then echo "$mnt is already used by $other. Choose another mount point."; exit 7; fi
        mkdir -p "$mnt"
        awk -v u="UUID=$uuid" -v d="$dev" '$1 != u && $1 != d' /etc/fstab > /etc/fstab.ows
        echo "UUID=$uuid $mnt $fs defaults,nofail 0 2" >> /etc/fstab.ows
        cat /etc/fstab.ows > /etc/fstab && rm -f /etc/fstab.ows
        systemctl daemon-reload 2>/dev/null || true
        [ -n "$current" ] || mount "$mnt"
        echo "Mounted at $mnt ($fs)."
        """;

    /// <summary>Unmounts every mount of the device and removes it from /etc/fstab. Fails while it is in use.</summary>
    public static string UnmountScript(string devicePath) => $$"""
        set -e
        dev={{Quote(devicePath)}}
        real=$(readlink -f "$dev" 2>/dev/null || true)
        uuid=
        if [ -n "$real" ] && [ -b "$real" ]; then
          uuid=$(blkid -p -o value -s UUID "$real" 2>/dev/null || true)
          for t in $(findmnt -n -o TARGET -S "$real" 2>/dev/null || true); do
            umount "$t" 2>/dev/null || { echo "$t is still in use. Stop the programs that use it, then try again."; exit 6; }
          done
        fi
        awk -v u="UUID=$uuid" -v d="$dev" '($1 != u || u == "UUID=") && $1 != d' /etc/fstab > /etc/fstab.ows
        cat /etc/fstab.ows > /etc/fstab && rm -f /etc/fstab.ows
        systemctl daemon-reload 2>/dev/null || true
        echo "Unmounted."
        """;

    /// <summary>Waits until the kernel sees the new size, then grows the mounted filesystem to fill the volume.</summary>
    public static string GrowScript(string devicePath, int sizeGb) => $$"""
        set -e
        real=$(readlink -f {{Quote(devicePath)}})
        want={{(long)sizeGb * 1024 * 1024 * 1024}}
        rescan=/sys/class/block/$(basename "$real")/device/rescan
        i=0
        while [ "$(blockdev --getsize64 "$real")" -lt "$want" ] && [ $i -lt 30 ]; do
          [ -w "$rescan" ] && echo 1 > "$rescan" || true
          sleep 1; i=$((i+1))
        done
        fs=$(blkid -p -o value -s TYPE "$real" || true)
        target=$(findmnt -n -o TARGET -S "$real" | head -n 1 || true)
        case "$fs" in
          ext2|ext3|ext4) resize2fs "$real" >/dev/null 2>&1 ;;
          xfs) [ -n "$target" ] && xfs_growfs "$target" >/dev/null ;;
          btrfs) [ -n "$target" ] && btrfs filesystem resize max "$target" >/dev/null ;;
          "") echo "The volume is empty; nothing to grow."; exit 0 ;;
          *) echo "ows cannot grow '$fs'. Grow it on the server."; exit 5 ;;
        esac
        echo "Filesystem grown to fill the volume."
        """;

    /// <summary>The last line a script printed, which is the reason when it failed.</summary>
    public static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    /// <summary>Device paths come from the provider; they are checked before they go into a script.</summary>
    private static string Quote(string value)
    {
        if (!SafeShellValue().IsMatch(value))
            throw new DomainException($"'{value}' cannot be used on the server.");
        return "'" + value + "'";
    }

    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    [GeneratedRegex(@"^/[A-Za-z0-9._/-]{0,200}$")]
    private static partial Regex ValidMountPoint();

    [GeneratedRegex(@"^/[A-Za-z0-9._/:-]{1,250}$")]
    private static partial Regex SafeShellValue();

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex InvalidPathChars();
}
