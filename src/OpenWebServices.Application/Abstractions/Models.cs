namespace OpenWebServices.Application.Abstractions;

public enum PowerAction { PowerOn, Shutdown, Reboot }

public enum ProviderServerStatus { Starting, Running, Stopped, Unknown }

public sealed record SshKeyPair(string PrivateKeyPem, string PublicKeyOpenSsh);

/// <summary>A purchasable server size at one location, normalized across providers.</summary>
public sealed record ServerOffer(
    string ProviderId,
    string Location,
    string LocationCity,
    string Country,
    string TypeName,
    int VCpus,
    string CpuKind,
    string Architecture,
    decimal MemoryGb,
    int DiskGb,
    decimal MonthlyNet,
    decimal MonthlyGross,
    string Currency,
    bool Available)
{
    public string Key => $"{ProviderId}/{Location}/{TypeName}";
}

public sealed record CreateServerRequest(
    string Name,
    string TypeName,
    string Location,
    string Image,
    long SshKeyId,
    string UserData);

public sealed record CreatedServer(long ProviderServerId, string? Ipv4);

public sealed record ProviderServerState(ProviderServerStatus Status, string? Ipv4);

public sealed record RemoteTarget(string Host, string PrivateKeyPem, string User = "root");

public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed record AppTemplate(
    string Id,
    string Name,
    string Description,
    string ComposeYaml,
    IReadOnlyList<string> GeneratedSecrets);
