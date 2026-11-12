using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Apps;

namespace OpenWebServices.Application.Apps;

public sealed class RequestAppInstall(
    IServerRepository servers,
    IAppInstallRepository installs,
    IAppTemplateCatalog templates,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Guid> ExecuteAsync(Guid serverId, string templateId, CancellationToken ct)
    {
        var server = await servers.FindAsync(serverId, ct) ?? throw new NotFoundException("Server");
        if (templates.Find(templateId) is null)
            throw new NotFoundException("App template");
        if (!server.IsReachable)
            throw new DomainException("The server must be running before apps can be installed.");

        var install = AppInstall.Request(serverId, templateId, clock.UtcNow);
        installs.Add(install);
        await unitOfWork.SaveChangesAsync(ct);
        return install.Id;
    }
}
