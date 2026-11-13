using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Servers;

public sealed record ProvisionServerCommand(
    Guid GroupId,
    string Name,
    string Location,
    string TypeName,
    string Image);

/// <summary>Creates a server at a provider and starts tracking it.</summary>
public sealed class ProvisionServer(
    AccessContext access,
    IProviderGroupRepository groups,
    IProviderConnectionFactory connections,
    IServerRepository servers,
    ISshKeyGenerator keyGenerator,
    ISecretProtector secrets,
    ISyncScheduler syncScheduler,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Guid> ExecuteAsync(ProvisionServerCommand command, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var group = await groups.FindAsync(command.GroupId, ct) ?? throw new NotFoundException("Provider group");
        var provider = (await connections.ConnectAsync(group.Id, ct)).RequireServers();
        var offer = await FindAvailableOfferAsync(provider, command, ct);

        var keyPair = keyGenerator.Generate($"ows-{command.Name}");
        var server = Server.Provision(
            group.TenantId, group.Id, command.Name, command.Location, command.TypeName, command.Image,
            offer.MonthlyNet, offer.Currency,
            secrets.Protect(keyPair.PrivateKeyPem, group.TenantId, SecretPurposes.SshPrivateKey), clock.UtcNow);

        // Persist first so a crash after the provider call never leaves an untracked, billed server.
        servers.Add(server);
        await unitOfWork.SaveChangesAsync(ct);

        await CreateAtProviderAsync(provider, server, command, keyPair, ct);
        syncScheduler.RequestSync(command.GroupId);
        return server.Id;
    }

    private static async Task<ServerOffer> FindAvailableOfferAsync(
        IServerProvider provider, ProvisionServerCommand command, CancellationToken ct)
    {
        var offers = await provider.GetOffersAsync(ct);
        return offers.FirstOrDefault(o =>
                   o.Location == command.Location && o.TypeName == command.TypeName && o.Available)
               ?? throw new OfferUnavailableException($"{command.Location}/{command.TypeName}");
    }

    private async Task CreateAtProviderAsync(
        IServerProvider provider, Server server, ProvisionServerCommand command, SshKeyPair keyPair, CancellationToken ct)
    {
        string? sshKeyId = null;
        try
        {
            sshKeyId = await provider.RegisterSshKeyAsync($"ows-{server.Id:N}", keyPair.PublicKeyOpenSsh, ct);
            var created = await provider.CreateServerAsync(
                new CreateServerRequest(command.Name, command.TypeName, command.Location, command.Image,
                    sshKeyId, ServerBootstrapScript.Content), ct);
            server.AttachProviderResources(created.ExternalId, sshKeyId, created.Ipv4);
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException)
        {
            server.MarkFailed(ex.Message);
            if (sshKeyId is not null)
                await TryDeleteKeyAsync(provider, sshKeyId);
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    private static async Task TryDeleteKeyAsync(IServerProvider provider, string sshKeyId)
    {
        try { await provider.DeleteSshKeyAsync(sshKeyId, CancellationToken.None); }
        catch (ProviderException) { /* best effort cleanup */ }
    }
}
