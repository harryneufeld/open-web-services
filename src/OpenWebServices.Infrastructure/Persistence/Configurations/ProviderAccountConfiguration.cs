using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Infrastructure.Persistence.Configurations;

internal sealed class ProviderAccountConfiguration : IEntityTypeConfiguration<ProviderAccount>
{
    public void Configure(EntityTypeBuilder<ProviderAccount> builder)
    {
        builder.ToTable("provider_accounts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ProviderId).HasMaxLength(32).IsRequired();
        builder.Property(a => a.EncryptedApiToken).IsRequired();
        builder.HasIndex(a => a.ProviderId).IsUnique();
    }
}
