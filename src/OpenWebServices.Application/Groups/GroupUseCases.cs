using OpenWebServices.Application.Abstractions;
using OpenWebServices.Application.Sync;
using OpenWebServices.Domain;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Application.Groups;

public sealed record AddProviderGroupCommand(
    string Name,
    string ProviderType,
    IReadOnlyDictionary<string, string> Credentials,
    ResourceKind EnabledResources);

/// <param name="NewCredentials">Blank or missing fields keep their stored values.</param>
public sealed record UpdateProviderGroupCommand(
    Guid GroupId,
    string Name,
    ResourceKind EnabledResources,
    IReadOnlyDictionary<string, string>? NewCredentials);

/// <param name="Credentials">Masked stored credentials; only filled for a single group viewed by an admin.</param>
public sealed record GroupView(
    ProviderGroup Group, ProviderTypeInfo? Type, int ServerCount, int ZoneCount, IReadOnlyList<MaskedCredential>? Credentials = null)
{
    public string ProviderName => Type?.DisplayName ?? Group.ProviderType;
}

/// <summary>Verifies the credentials, stores the group and immediately imports its existing resources.</summary>
public sealed class AddProviderGroup(
    AccessContext access,
    IAuditLog audit,
    IProviderCatalog catalog,
    IProviderConnectionFactory connections,
    IProviderGroupRepository groups,
    ISecretProtector secrets,
    SyncGroup syncGroup,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<(Guid GroupId, SyncReport Import)> ExecuteAsync(AddProviderGroupCommand command, CancellationToken ct)
    {
        var tenantId = access.Require(TenantRole.Admin);
        var type = catalog.Find(command.ProviderType) ?? throw new NotFoundException($"Provider type '{command.ProviderType}'");
        var credentials = CredentialInput.Validate(type, command.Credentials);
        await connections.Connect(type.Id, credentials).VerifyAsync(ct);

        var group = ProviderGroup.Create(
            tenantId, command.Name, type.Id,
            secrets.Protect(GroupCredentials.Serialize(credentials), tenantId, SecretPurposes.ProviderCredentials),
            command.EnabledResources & type.SupportedResources, clock.UtcNow);
        groups.Add(group);
        audit.Add(new AuditEntry(AuditTypes.ProviderCredentialsChanged, $"Added group '{group.Name}' ({type.DisplayName})", access.UserId, tenantId));
        await unitOfWork.SaveChangesAsync(ct);

        return (group.Id, await syncGroup.ExecuteAsync(group.Id, ct));
    }
}

public sealed class UpdateProviderGroup(
    AccessContext access,
    IAuditLog audit,
    IProviderCatalog catalog,
    IProviderConnectionFactory connections,
    IProviderGroupRepository groups,
    ISecretProtector secrets,
    SyncGroup syncGroup,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    /// <returns>The sync run after saving, so newly enabled resources are imported right away.</returns>
    public async Task<SyncReport> ExecuteAsync(UpdateProviderGroupCommand command, CancellationToken ct)
    {
        access.Require(TenantRole.Admin);
        var group = await groups.FindAsync(command.GroupId, ct) ?? throw new NotFoundException("Provider group");
        var type = catalog.Find(group.ProviderType) ?? throw new NotFoundException($"Provider type '{group.ProviderType}'");

        if (command.NewCredentials is { } input && input.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            // Fields left blank keep their stored value, so one rotated key can be replaced on its own.
            var stored = GroupCredentials.Deserialize(
                secrets.Unprotect(group.EncryptedCredentials, group.TenantId, SecretPurposes.ProviderCredentials));
            var merged = type.CredentialFields.ToDictionary(
                field => field.Key,
                field => input.GetValueOrDefault(field.Key) is { } value && !string.IsNullOrWhiteSpace(value)
                    ? value
                    : stored.GetValueOrDefault(field.Key) ?? "");
            var credentials = CredentialInput.Validate(type, merged);
            await connections.Connect(type.Id, credentials).VerifyAsync(ct);
            group.ReplaceCredentials(
                secrets.Protect(GroupCredentials.Serialize(credentials), group.TenantId, SecretPurposes.ProviderCredentials),
                clock.UtcNow);
            audit.Add(new AuditEntry(AuditTypes.ProviderCredentialsChanged, $"Replaced credentials of '{group.Name}'", access.UserId, group.TenantId));
        }

        group.Rename(command.Name, clock.UtcNow);
        group.SetEnabledResources(command.EnabledResources & type.SupportedResources, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(ct);

        return await syncGroup.ExecuteAsync(group.Id, ct);
    }
}

/// <summary>
/// Stops managing a group. Its tracked servers and zones are removed from the platform; nothing is deleted at the
/// provider. SSH keys of servers created here are lost with them.
/// </summary>
public sealed class RemoveProviderGroup(
    AccessContext access, IAuditLog audit, IProviderGroupRepository groups, IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(Guid groupId, CancellationToken ct)
    {
        access.Require(TenantRole.Admin);
        var group = await groups.FindAsync(groupId, ct) ?? throw new NotFoundException("Provider group");
        groups.Remove(group);
        audit.Add(new AuditEntry(AuditTypes.ProviderGroupRemoved, $"Removed group '{group.Name}'", access.UserId, group.TenantId));
        await unitOfWork.SaveChangesAsync(ct);
    }
}

public sealed class GroupQueries(
    AccessContext access,
    IProviderGroupRepository groups,
    IServerRepository servers,
    IDnsZoneRepository zones,
    IProviderCatalog catalog,
    ISecretProtector secrets)
{
    public async Task<IReadOnlyList<GroupView>> ListAsync(CancellationToken ct)
    {
        var all = await groups.ListAsync(ct);
        var serverCounts = (await servers.ListAsync(ct)).GroupBy(s => s.GroupId).ToDictionary(g => g.Key, g => g.Count());
        var zoneCounts = (await zones.ListAsync(ct)).GroupBy(z => z.GroupId).ToDictionary(g => g.Key, g => g.Count());

        return [.. all.Select(g => new GroupView(
            g, catalog.Find(g.ProviderType), serverCounts.GetValueOrDefault(g.Id), zoneCounts.GetValueOrDefault(g.Id)))];
    }

    public async Task<GroupView> GetAsync(Guid groupId, CancellationToken ct)
    {
        var view = (await ListAsync(ct)).FirstOrDefault(v => v.Group.Id == groupId) ?? throw new NotFoundException("Provider group");
        return view.Type is { } type && access.Can(TenantRole.Admin) && view.Group.HasCredentials
            ? view with { Credentials = MaskStored(view.Group, type) }
            : view;
    }

    private IReadOnlyList<MaskedCredential> MaskStored(ProviderGroup group, ProviderTypeInfo type)
    {
        try
        {
            var stored = secrets.Unprotect(group.EncryptedCredentials, group.TenantId, SecretPurposes.ProviderCredentials);
            return GroupCredentials.Mask(type, GroupCredentials.Deserialize(stored));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return []; // unreadable with the current keys; the page then only offers to replace them
        }
    }
}

internal static class CredentialInput
{
    /// <summary>Keeps only the fields the provider type defines, trimmed, and requires all of them.</summary>
    public static Dictionary<string, string> Validate(ProviderTypeInfo type, IReadOnlyDictionary<string, string> input)
    {
        var result = new Dictionary<string, string>();
        foreach (var field in type.CredentialFields)
        {
            var value = input.GetValueOrDefault(field.Key)?.Trim();
            if (string.IsNullOrEmpty(value))
                throw new DomainException($"{field.Label} is required.");
            result[field.Key] = value;
        }

        return result;
    }
}
