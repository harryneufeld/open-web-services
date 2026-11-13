using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenWebServices.Domain.Providers;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Infrastructure.Persistence.Configurations;

internal sealed class ServerConfiguration : IEntityTypeConfiguration<Server>
{
    public void Configure(EntityTypeBuilder<Server> builder)
    {
        builder.ToTable("servers");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(Server.MaxNameLength).IsRequired();
        builder.Property(s => s.Origin).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.ExternalId).HasMaxLength(128);
        builder.Property(s => s.ExternalSshKeyId).HasMaxLength(128);
        builder.Property(s => s.Location).HasMaxLength(64).IsRequired();
        builder.Property(s => s.ServerType).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Image).HasMaxLength(128).IsRequired();
        builder.Property(s => s.Currency).HasMaxLength(3).IsRequired();
        builder.Property(s => s.MonthlyPriceNet).HasPrecision(12, 4);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.PublicIpv4).HasMaxLength(45);
        builder.Property(s => s.FailureReason).HasMaxLength(2000);
        builder.Ignore(s => s.IsReachable);
        builder.Ignore(s => s.CanInstallApps);
        builder.Ignore(s => s.IsTransitioning);
        builder.HasOne<ProviderGroup>().WithMany().HasForeignKey(s => s.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => s.Status);
        builder.HasIndex(s => s.TenantId);
        builder.HasIndex(s => new { s.GroupId, s.ExternalId });
    }
}
