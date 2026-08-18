using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.SubjectId).IsRequired();
        builder.Property(t => t.SubjectType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.FamilyId).IsRequired();
        builder.Property(t => t.TokenHash).HasMaxLength(100).IsRequired();
        builder.Property(t => t.RevokedReason).HasMaxLength(100);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);

        // Supports both active-session lookup and bulk revoke-all-for-subject.
        builder.HasIndex(t => new { t.SubjectType, t.SubjectId, t.RevokedAt });
    }
}
