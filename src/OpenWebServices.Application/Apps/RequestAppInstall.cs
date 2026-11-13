using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Apps;

public sealed class RequestAppInstall(
    AccessContext access,
    IServerRepository servers,
    IAppInstallRepository installs,
    IAppTemplateCatalog templates,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Guid> ExecuteAsync(Guid serverId, string templateId, CancellationToken ct)
    {
        access.Require(TenantRole.Member);
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (templates.Find(templateId) is null)
            throw new NotFoundException("App template");
        if (server.EncryptedSshPrivateKey is null)
            throw new DomainException("Apps can only be installed on servers created here; imported servers have no SSH key.");
        if (!server.IsReachable)
            throw new DomainException("The server must be running before apps can be installed.");

        var install = AppInstall.Request(server.TenantId, serverId, templateId, clock.UtcNow);
        installs.Add(install);
        await unitOfWork.SaveChangesAsync(ct);
        return install.Id;
    }
}
