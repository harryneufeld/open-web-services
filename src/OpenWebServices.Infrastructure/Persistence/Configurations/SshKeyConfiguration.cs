using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenWebServices.Domain.Servers;
using OpenWebServices.Domain.SshKeys;

namespace OpenWebServices.Infrastructure.Persistence.Configurations;

internal sealed class SshKeyConfiguration : IEntityTypeConfiguration<SshKey>
{
    public void Configure(EntityTypeBuilder<SshKey> builder)
    {
        builder.ToTable("ssh_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Name).HasMaxLength(SshKey.MaxNameLength).IsRequired();
        builder.Property(k => k.PublicKey).HasMaxLength(16384).IsRequired();
        builder.Property(k => k.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(k => k.Algorithm).HasMaxLength(16).IsRequired();
        builder.Property(k => k.Origin).HasConversion<string>().HasMaxLength(24);
        builder.Ignore(k => k.Key);
        builder.Ignore(k => k.IsShared);
        builder.Ignore(k => k.IsRevoked);
        builder.HasIndex(k => k.TenantId);
        builder.HasIndex(k => new { k.TenantId, k.OwnerUserId });

        // A key can be in a tenant once; a revoked copy may stay on record next to a re-added one.
        builder.HasIndex(k => new { k.TenantId, k.Fingerprint }).IsUnique().HasFilter("\"RevokedAtUtc\" IS NULL");
    }
}

internal sealed class ServerKeyGrantConfiguration : IEntityTypeConfiguration<ServerKeyGrant>
{
    public void Configure(EntityTypeBuilder<ServerKeyGrant> builder)
    {
        builder.ToTable("server_key_grants");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.State).HasConversion<string>().HasMaxLength(16);
        builder.Ignore(g => g.IsPending);
        builder.HasOne<Server>().WithMany().HasForeignKey(g => g.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<SshKey>().WithMany().HasForeignKey(g => g.SshKeyId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(g => new { g.ServerId, g.SshKeyId }).IsUnique();
        builder.HasIndex(g => g.SshKeyId);
        builder.HasIndex(g => g.TenantId);
    }
}
