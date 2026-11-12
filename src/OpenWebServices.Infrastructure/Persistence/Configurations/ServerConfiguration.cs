using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Infrastructure.Persistence.Configurations;

internal sealed class ServerConfiguration : IEntityTypeConfiguration<Server>
{
    public void Configure(EntityTypeBuilder<Server> builder)
    {
        builder.ToTable("servers");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(63).IsRequired();
        builder.Property(s => s.ProviderId).HasMaxLength(32).IsRequired();
        builder.Property(s => s.Location).HasMaxLength(32).IsRequired();
        builder.Property(s => s.ServerType).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Image).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Currency).HasMaxLength(3).IsRequired();
        builder.Property(s => s.MonthlyPriceNet).HasPrecision(12, 4);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.PublicIpv4).HasMaxLength(45);
        builder.Property(s => s.EncryptedSshPrivateKey).IsRequired();
        builder.Property(s => s.FailureReason).HasMaxLength(2000);
        builder.HasIndex(s => s.Status);
    }
}
