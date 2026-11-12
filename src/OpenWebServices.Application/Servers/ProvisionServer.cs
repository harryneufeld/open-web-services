using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Application.Servers;

public sealed record ProvisionServerCommand(
    string Name,
    string ProviderId,
    string Location,
    string TypeName,
    string Image);

/// <summary>Creates a server at a provider and starts tracking it.</summary>
public sealed class ProvisionServer(
    IHostingProviderFactory providers,
    IServerRepository servers,
    ISshKeyGenerator keyGenerator,
    ISecretProtector secrets,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Guid> ExecuteAsync(ProvisionServerCommand command, CancellationToken ct)
    {
        var provider = await providers.GetAsync(command.ProviderId, ct);
        var offer = await FindAvailableOfferAsync(provider, command, ct);

        var keyPair = keyGenerator.Generate($"ows-{command.Name}");
        var server = Server.Provision(
            command.Name, command.ProviderId, command.Location, command.TypeName, command.Image,
            offer.MonthlyNet, offer.Currency, secrets.Protect(keyPair.PrivateKeyPem), clock.UtcNow);

        // Persist first so a crash after the provider call never leaves an untracked, billed server.
        servers.Add(server);
        await unitOfWork.SaveChangesAsync(ct);

        await CreateAtProviderAsync(provider, server, command, keyPair, ct);
        return server.Id;
    }

    private static async Task<ServerOffer> FindAvailableOfferAsync(
        IHostingProvider provider, ProvisionServerCommand command, CancellationToken ct)
    {
        var offers = await provider.GetOffersAsync(ct);
        return offers.FirstOrDefault(o =>
                   o.Location == command.Location && o.TypeName == command.TypeName && o.Available)
               ?? throw new OfferUnavailableException($"{command.ProviderId}/{command.Location}/{command.TypeName}");
    }

    private async Task CreateAtProviderAsync(
        IHostingProvider provider, Server server, ProvisionServerCommand command, SshKeyPair keyPair, CancellationToken ct)
    {
        long? sshKeyId = null;
        try
        {
            sshKeyId = await provider.RegisterSshKeyAsync($"ows-{server.Id:N}", keyPair.PublicKeyOpenSsh, ct);
            var created = await provider.CreateServerAsync(
                new CreateServerRequest(command.Name, command.TypeName, command.Location, command.Image,
                    sshKeyId.Value, ServerBootstrapScript.Content), ct);
            server.AttachProviderResources(created.ProviderServerId, sshKeyId.Value, created.Ipv4);
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException)
        {
            server.MarkFailed(ex.Message);
            if (sshKeyId is not null)
                await TryDeleteKeyAsync(provider, sshKeyId.Value);
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    private static async Task TryDeleteKeyAsync(IHostingProvider provider, long sshKeyId)
    {
        try { await provider.DeleteSshKeyAsync(sshKeyId, CancellationToken.None); }
        catch (ProviderException) { /* best effort cleanup */ }
    }
}
