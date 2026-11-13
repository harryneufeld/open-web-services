using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Tenants;

namespace OpenWebServices.Infrastructure.Persistence.Configurations;

internal sealed class ProviderGroupConfiguration : IEntityTypeConfiguration<ProviderGroup>
{
    public void Configure(EntityTypeBuilder<ProviderGroup> builder)
    {
        builder.ToTable("provider_groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).HasMaxLength(ProviderGroup.MaxNameLength).IsRequired();
        builder.Property(g => g.ProviderType).HasMaxLength(32).IsRequired();
        builder.Property(g => g.EncryptedCredentials).IsRequired();
        builder.Property(g => g.EnabledResources).HasConversion<int>();
        builder.Property(g => g.LastSyncError).HasMaxLength(ProviderGroup.MaxSyncErrorLength);
        builder.Ignore(g => g.HasCredentials);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(g => g.TenantId);
    }
}
