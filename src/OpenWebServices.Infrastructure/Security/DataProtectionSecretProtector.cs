using Microsoft.AspNetCore.DataProtection;
using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Security;

internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("OpenWebServices.Secrets.v1");

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}
