using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenWebServices.Domain.Apps;
using OpenWebServices.Domain.Servers;

namespace OpenWebServices.Infrastructure.Persistence.Configurations;

internal sealed class AppInstallConfiguration : IEntityTypeConfiguration<AppInstall>
{
    public void Configure(EntityTypeBuilder<AppInstall> builder)
    {
        builder.ToTable("app_installs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.TemplateId).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.Log).IsRequired();
        builder.HasOne<Server>().WithMany().HasForeignKey(a => a.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.ServerId, a.Status });
        builder.HasIndex(a => a.TenantId);
    }
}
