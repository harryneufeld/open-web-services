using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Application.Providers;

public sealed class SaveProviderToken(
    IProviderAccountRepository accounts,
    ISecretProtector secrets,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(string providerId, string apiToken, CancellationToken ct)
    {
        var encrypted = secrets.Protect(apiToken.Trim());
        var account = await accounts.FindByProviderAsync(providerId, ct);
        if (account is null)
            accounts.Add(ProviderAccount.Create(providerId, encrypted, clock.UtcNow));
        else
            account.ReplaceToken(encrypted, clock.UtcNow);

        await unitOfWork.SaveChangesAsync(ct);
    }
}
