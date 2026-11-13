using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenWebServices.Domain.Dns;
using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Infrastructure.Persistence.Configurations;

internal sealed class DnsZoneConfiguration : IEntityTypeConfiguration<DnsZone>
{
    public void Configure(EntityTypeBuilder<DnsZone> builder)
    {
        builder.ToTable("dns_zones");
        builder.HasKey(z => z.Id);
        builder.Property(z => z.ExternalId).HasMaxLength(128).IsRequired();
        builder.Property(z => z.Name).HasMaxLength(253).IsRequired();
        builder.Property(z => z.Status).HasMaxLength(32).IsRequired();
        builder.Property(z => z.Nameservers).HasMaxLength(2000).IsRequired();
        builder.Property(z => z.DelegationStatus).HasMaxLength(32);
        builder.Ignore(z => z.NameserverList);
        builder.HasOne<ProviderGroup>().WithMany().HasForeignKey(z => z.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(z => new { z.GroupId, z.ExternalId }).IsUnique();
        builder.HasIndex(z => z.TenantId);
    }
}
