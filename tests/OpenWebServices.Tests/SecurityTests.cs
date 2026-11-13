using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Infrastructure.Identity;
using OpenWebServices.Infrastructure.Persistence;
using OpenWebServices.Infrastructure.Security;

namespace OpenWebServices.Tests;

public class SecretProtectorTests
{
    private readonly DataProtectionSecretProtector _sut = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Round_trips_within_the_same_tenant_and_purpose()
    {
        var tenant = Guid.NewGuid();
        var cipher = _sut.Protect("token", tenant, SecretPurposes.ProviderCredentials);

        Assert.StartsWith(DataProtectionSecretProtector.Prefix, cipher, StringComparison.Ordinal);
        Assert.Equal("token", _sut.Unprotect(cipher, tenant, SecretPurposes.ProviderCredentials));
    }

    [Fact]
    public void A_secret_copied_to_another_tenant_or_field_does_not_decrypt()
    {
        var tenant = Guid.NewGuid();
        var cipher = _sut.Protect("token", tenant, SecretPurposes.ProviderCredentials);

        Assert.ThrowsAny<CryptographicException>(() => _sut.Unprotect(cipher, Guid.NewGuid(), SecretPurposes.ProviderCredentials));
        Assert.ThrowsAny<CryptographicException>(() => _sut.Unprotect(cipher, tenant, SecretPurposes.SshPrivateKey));
    }
}

public class PasswordHasherTests
{
    private readonly OwsPasswordHasher _sut = new(Options.Create(new PasswordHasherOptions { IterationCount = 1000 }));

    [Fact]
    public void Hashes_from_before_identity_still_verify_and_ask_for_an_upgrade()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2("correct horse battery", salt, 100_000, HashAlgorithmName.SHA256, 32);
        var legacy = $"v1.100000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, _sut.VerifyHashedPassword(new OwsUser(), legacy, "correct horse battery"));
        Assert.Equal(PasswordVerificationResult.Failed, _sut.VerifyHashedPassword(new OwsUser(), legacy, "wrong"));
    }

    [Fact]
    public void New_hashes_use_identity_format()
    {
        var hash = _sut.HashPassword(new OwsUser(), "correct horse battery");

        Assert.DoesNotContain("v1.", hash, StringComparison.Ordinal);
        Assert.Equal(PasswordVerificationResult.Success, _sut.VerifyHashedPassword(new OwsUser(), hash, "correct horse battery"));
    }
}

public class TenantIsolationTests
{
    private readonly string _database = Guid.NewGuid().ToString();

    // Identity reads its schema version (passkeys need version 3) from the application's services.
    private readonly IServiceProvider _appServices = new ServiceCollection()
        .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .BuildServiceProvider();

    private AppDbContext Context(AccessContext access) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_database)
            .UseApplicationServiceProvider(_appServices)
            .Options, access);

    private static AccessContext For(Guid tenantId)
    {
        var access = new AccessContext();
        access.SetUser(Guid.NewGuid(), "user", strongSession: false, isOwsAdmin: false);
        access.SetTenant(tenantId, "t", Domain.Tenants.TenantRole.Owner);
        return access;
    }

    [Fact]
    public async Task Queries_only_see_the_current_tenants_rows()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var system = new AccessContext();
        system.BecomeSystem();
        await using (var db = Context(system))
        {
            db.ProviderGroups.Add(ProviderGroup.Create(a, "A", "fake", "x", ResourceKind.Servers, DateTime.UtcNow));
            db.ProviderGroups.Add(ProviderGroup.Create(b, "B", "fake", "x", ResourceKind.Servers, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        await using var asA = Context(For(a));
        await using var nobody = Context(new AccessContext());
        await using var everyone = Context(system);

        Assert.Equal(["A"], await asA.ProviderGroups.Select(g => g.Name).ToListAsync());
        Assert.Empty(await nobody.ProviderGroups.ToListAsync());
        Assert.Equal(2, await everyone.ProviderGroups.CountAsync());
    }
}
