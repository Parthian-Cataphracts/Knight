using AccessControl.Domain;
using Customers.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stores.Domain;
using ControlPlaneCustomer = Customers.Domain.Customer;

namespace Knight.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Mapping for the control-plane schema. Enums are stored as strings so a
/// database dump stays readable and reordering an enum member cannot silently
/// change the meaning of existing rows.
/// </summary>
internal sealed class CustomerConfiguration : IEntityTypeConfiguration<ControlPlaneCustomer>
{
    public void Configure(EntityTypeBuilder<ControlPlaneCustomer> builder)
    {
        builder.ToTable("customers");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.LegalName).HasMaxLength(200);
        builder.Property(c => c.ContactEmail).HasMaxLength(320).IsRequired();
        builder.Property(c => c.Phone).HasMaxLength(32);
        builder.Property(c => c.Notes).HasMaxLength(4000);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Contact email identifies the customer commercially, so it is unique
        // platform-wide and the database enforces it rather than trusting a
        // check-then-insert in the service.
        builder.HasIndex(c => c.ContactEmail).IsUnique();
        builder.HasIndex(c => c.Status);
    }
}

internal sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> builder)
    {
        builder.ToTable("stores");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Slug).HasMaxLength(100).IsRequired();
        builder.Property(s => s.PrimaryDomain).HasMaxLength(253).IsRequired();
        builder.Property(s => s.ApplicationVersion).HasMaxLength(50);
        builder.Property(s => s.Environment).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.HostingModel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.IntegrationStatus).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(s => s.DomainVerificationToken).HasMaxLength(100);
        builder.Property(s => s.DomainVerificationMethod).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(s => s.Slug).IsUnique();
        builder.HasIndex(s => s.PrimaryDomain).IsUnique();
        builder.HasIndex(s => s.CustomerId);

        // The "no cross-database foreign keys" rule concerns references to
        // store-side data. A customer and its stores both live in this schema,
        // so this relationship is a real one and the database enforces it.
        builder.HasOne<ControlPlaneCustomer>()
            .WithMany()
            .HasForeignKey(s => s.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(s => s.Credentials)
            .WithOne()
            .HasForeignKey(c => c.StoreId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Store.Credentials))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class StoreCredentialConfiguration : IEntityTypeConfiguration<StoreCredential>
{
    public void Configure(EntityTypeBuilder<StoreCredential> builder)
    {
        builder.ToTable("store_credentials");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.ClientId).HasMaxLength(100).IsRequired();

        // Only the hash is ever stored; the plaintext secret is shown once at
        // issue time (docs/authentication.md section 2).
        builder.Property(c => c.SecretHash).HasMaxLength(200).IsRequired();

        builder.HasIndex(c => c.ClientId).IsUnique();
        builder.HasIndex(c => c.StoreId);
    }
}

internal sealed class ControlPlaneUserConfiguration : IEntityTypeConfiguration<ControlPlaneUser>
{
    public void Configure(EntityTypeBuilder<ControlPlaneUser> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email).HasMaxLength(320).IsRequired();
        builder.Property(u => u.NormalizedEmail).HasMaxLength(320).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(400).IsRequired();
        builder.Property(u => u.MfaSecret).HasMaxLength(200);
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(u => u.NormalizedEmail).IsUnique();
        builder.HasIndex(u => u.CustomerId);

        builder.HasOne<ControlPlaneCustomer>()
            .WithMany()
            .HasForeignKey(u => u.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(u => u.Roles)
            .WithOne()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(ControlPlaneUser.Roles))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class UserRoleAssignmentConfiguration : IEntityTypeConfiguration<UserRoleAssignment>
{
    public void Configure(EntityTypeBuilder<UserRoleAssignment> builder)
    {
        builder.ToTable("user_roles");

        builder.HasKey(r => r.Id);

        // One account holds a given role at most once; the database says so
        // rather than relying on the aggregate having been loaded first.
        builder.HasIndex(r => new { r.UserId, r.RoleId }).IsUnique();

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(r => r.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.NormalizedName).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(500);
        builder.Property(r => r.Scope).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Names are unique within a scope and owner: two different customers may
        // each define a role called "Analyst" without colliding.
        builder.HasIndex(r => new { r.NormalizedName, r.Scope, r.CustomerId }).IsUnique();

        builder.HasMany(r => r.Permissions)
            .WithOne()
            .HasForeignKey(p => p.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Role.Permissions))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");

        builder.HasKey(p => new { p.RoleId, p.PermissionKey });

        builder.Property(p => p.PermissionKey).HasMaxLength(100).IsRequired();
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.RefreshTokenHash).HasMaxLength(200).IsRequired();
        builder.Property(s => s.RevokedReason).HasMaxLength(100);
        builder.Property(s => s.IpAddress).HasMaxLength(64);
        builder.Property(s => s.UserAgent).HasMaxLength(512);

        builder.HasIndex(s => s.RefreshTokenHash).IsUnique();
        builder.HasIndex(s => s.FamilyId);
        builder.HasIndex(s => s.UserId);

        builder.HasOne<ControlPlaneUser>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.ActorType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.ActorDisplay).HasMaxLength(200);
        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.Property(a => a.TargetType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.TargetId).HasMaxLength(100);
        builder.Property(a => a.CorrelationId).HasMaxLength(100);
        builder.Property(a => a.IpAddress).HasMaxLength(64);
        builder.Property(a => a.PreviousValue).HasColumnType("jsonb");
        builder.Property(a => a.NewValue).HasColumnType("jsonb");

        builder.HasIndex(a => a.OccurredAt);
        builder.HasIndex(a => a.CustomerId);
        builder.HasIndex(a => new { a.TargetType, a.TargetId });
        builder.HasIndex(a => a.ActorUserId);

        // Audit rows outlive the accounts they mention: the actor is recorded by
        // id and display name, without a foreign key that would block deleting a
        // user or, worse, cascade their history away.
    }
}

/// <summary>
/// Notes a person wrote about a customer. Customer-scoped, so the isolation
/// filter applies: a customer's own staff see their notes, platform staff see
/// all of them, and nobody sees a neighbour's.
/// </summary>
internal sealed class CustomerNoteConfiguration : IEntityTypeConfiguration<CustomerNote>
{
    public void Configure(EntityTypeBuilder<CustomerNote> builder)
    {
        builder.ToTable("customer_notes");

        builder.HasKey(note => note.Id);

        builder.Property(note => note.AuthorName).HasMaxLength(200).IsRequired();
        builder.Property(note => note.Body).HasMaxLength(CustomerNote.MaxBodyLength).IsRequired();

        // The only read: one customer's notes, newest first.
        builder.HasIndex(note => new { note.CustomerId, note.CreatedAt }).IsDescending(false, true);
    }
}
