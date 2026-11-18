using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Access;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.SshKeys;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Servers;

/// <param name="SshKeyIds">Keys that can sign in to the new server, besides ows itself.</param>
public sealed record ProvisionServerCommand(
    Guid GroupId,
    string Name,
    string Location,
    string TypeName,
    string Image,
    string? BillingId = null,
    IReadOnlyList<Guid>? SshKeyIds = null);

/// <summary>Creates a server at a provider and starts tracking it.</summary>
public sealed class ProvisionServer(
    AccessContext access,
    IProviderGroupRepository groups,
    IProviderConnectionFactory connections,
    IServerRepository servers,
    SshKeys sshKeys,
    IServerKeyGrantRepository grants,
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
        if (command.BillingId is not null && !group.ShowCommitmentOffers)
            throw new OfferUnavailableException($"{command.Location}/{command.TypeName}/{command.BillingId}");
        var userKeys = await ChosenKeysAsync(command.SshKeyIds ?? [], ct);
        var provider = (await connections.ConnectAsync(group.Id, ct)).RequireServers();
        var offer = await FindAvailableOfferAsync(provider, command, ct);

        var keyPair = keyGenerator.Generate($"ows-{command.Name}");
        var automationLine = ServerAccessWriter.AutomationLine(keyPair.PublicKeyOpenSsh);
        var server = Server.Provision(
            group.TenantId, group.Id, command.Name, command.Location, command.TypeName, command.Image,
            offer.MonthlyNet, offer.Currency,
            secrets.Protect(keyPair.PrivateKeyPem, group.TenantId, SecretPurposes.SshPrivateKey), automationLine, clock.UtcNow);

        // Persist first so a crash after the provider call never leaves an untracked, billed server.
        servers.Add(server);
        foreach (var key in userKeys)
            grants.Add(ServerKeyGrant.Grant(group.TenantId, server.Id, key.Id, access.UserId, clock.UtcNow));
        await unitOfWork.SaveChangesAsync(ct);

        var userData = ServerBootstrapScript.Build([automationLine, .. userKeys.Select(k => k.ToAuthorizedKeysLine())]);
        await CreateAtProviderAsync(provider, server, command, keyPair, userData, ct);
        syncScheduler.RequestSync(command.GroupId);
        return server.Id;
    }

    private async Task<IReadOnlyList<SshKey>> ChosenKeysAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];
        var usable = (await sshKeys.ListUsableAsync(ct)).ToDictionary(k => k.Id);
        return [.. ids.Distinct().Select(id => usable.GetValueOrDefault(id)
                                              ?? throw new DomainException("One of the chosen SSH keys is not available to you any more."))];
    }

    private static async Task<ServerOffer> FindAvailableOfferAsync(
        IServerProvider provider, ProvisionServerCommand command, CancellationToken ct)
    {
        var offers = await provider.GetOffersAsync(ct);
        return offers.FirstOrDefault(o =>
                   o.Location == command.Location && o.TypeName == command.TypeName && o.Billing?.Id == command.BillingId
                   && o.Available)
               ?? throw new OfferUnavailableException($"{command.Location}/{command.TypeName}");
    }

    private async Task CreateAtProviderAsync(
        IServerProvider provider, Server server, ProvisionServerCommand command, SshKeyPair keyPair, string userData, CancellationToken ct)
    {
        string? sshKeyId = null;
        try
        {
            sshKeyId = await provider.RegisterSshKeyAsync($"ows-{server.Id:N}", keyPair.PublicKeyOpenSsh, ct);
            var created = await provider.CreateServerAsync(
                new CreateServerRequest(command.Name, command.TypeName, command.Location, command.Image,
                    sshKeyId, userData, command.BillingId), ct);
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
