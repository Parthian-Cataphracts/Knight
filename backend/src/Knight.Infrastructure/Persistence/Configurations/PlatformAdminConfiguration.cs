using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class PlatformAdminConfiguration : IEntityTypeConfiguration<PlatformAdmin>
{
    public void Configure(EntityTypeBuilder<PlatformAdmin> builder)
    {
        builder.ToTable("platform_admins");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Email).HasMaxLength(320).IsRequired();
        builder.Property(a => a.NormalizedEmail).HasMaxLength(320).IsRequired();
        builder.Property(a => a.PasswordHash).IsRequired();
        builder.Property(a => a.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(a => a.NormalizedEmail).IsUnique();
    }
}
