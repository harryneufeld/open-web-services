using OpenWebServices.Application.Abstractions;
using static OpenWebServices.Infrastructure.Hetzner.HetznerServerProvider;

namespace OpenWebServices.Infrastructure.Hetzner;

/// <summary>Hetzner Cloud server labels. Updating a server with a labels object replaces all of its labels.</summary>
internal sealed class HetznerLabelProvider(HetznerApiClient api) : IServerLabelProvider
{
    public async Task<IReadOnlyDictionary<string, string>> GetLabelsAsync(string externalServerId, CancellationToken ct)
    {
        var server = (await api.GetAsync<HetznerServerEnvelope>($"servers/{ParseId(externalServerId)}", ct))?.Server
                     ?? throw new NotFoundException("Server at Hetzner");
        return server.Labels ?? [];
    }

    public Task SetLabelsAsync(string externalServerId, IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
        api.PutAsync($"servers/{ParseId(externalServerId)}", new HetznerLabelsBody(labels.ToDictionary()), ct);
}
