using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Observability.Domain;

namespace Knight.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Mapping for error grouping, incidents and notifications.
///
/// Error groups and incidents are customer-scoped and therefore pick up the
/// isolation filter automatically. Notification channels are scoped too, but
/// nullably: a platform channel — the operators' own on-call webhook — belongs
/// to nobody and must stay invisible to every customer, which is exactly what
/// the filter does with a null customer.
/// </summary>
internal sealed class ErrorGroupConfiguration : IEntityTypeConfiguration<ErrorGroup>
{
    public void Configure(EntityTypeBuilder<ErrorGroup> builder)
    {
        builder.ToTable("error_groups");

        builder.HasKey(group => group.Id);

        builder.Property(group => group.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(group => group.Environment).HasMaxLength(20).IsRequired();
        builder.Property(group => group.ExceptionType).HasMaxLength(200).IsRequired();
        builder.Property(group => group.Title).HasMaxLength(ErrorGroup.MaxTitleLength).IsRequired();
        builder.Property(group => group.Endpoint).HasMaxLength(500);
        builder.Property(group => group.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(group => group.FirstSeenVersion).HasMaxLength(50);
        builder.Property(group => group.LastSeenVersion).HasMaxLength(50);
        builder.Property(group => group.ResolvedInVersion).HasMaxLength(50);

        builder.Ignore(group => group.IsAlertable);
        builder.Ignore(group => group.IsRegression);

        // The identity of a problem, and the constraint that makes upserting one
        // safe: two events of the same problem arriving concurrently cannot
        // create two groups. The algorithm version is part of the key so that
        // changing the algorithm starts new groups instead of colliding with old
        // ones (adr/0013).
        builder.HasIndex(group => new { group.StoreId, group.Fingerprint, group.FingerprintVersion })
            .IsUnique();

        // The errors screen: this customer's groups, worst-recent first.
        builder.HasIndex(group => new { group.CustomerId, group.Status, group.LastSeenAt })
            .IsDescending(false, false, true);

        // The spike sweep asks for everything seen since a cutoff, across stores.
        builder.HasIndex(group => group.LastSeenAt);

        builder.HasIndex(group => group.IncidentId);
    }
}

internal sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> builder)
    {
        builder.ToTable("incidents");

        builder.HasKey(incident => incident.Id);

        builder.Property(incident => incident.Reference).HasMaxLength(20).IsRequired();
        builder.Property(incident => incident.Title).HasMaxLength(Incident.MaxTitleLength).IsRequired();
        builder.Property(incident => incident.Summary).HasMaxLength(Incident.MaxSummaryLength);
        builder.Property(incident => incident.RootCause).HasMaxLength(Incident.MaxSummaryLength);
        builder.Property(incident => incident.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(incident => incident.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(incident => incident.RuleKey).HasMaxLength(100);

        builder.Ignore(incident => incident.IsOpen);

        // A reference is what people type into a chat window during an outage;
        // two incidents sharing one would be worse than useless.
        builder.HasIndex(incident => incident.Reference).IsUnique();

        // Rule deduplication: is there already an open incident for this rule and
        // subject? Asked on every rule pass, for every discrepancy found.
        builder.HasIndex(incident => new { incident.RuleKey, incident.Status });

        builder.HasIndex(incident => new { incident.Status, incident.OpenedAt }).IsDescending(false, true);
        builder.HasIndex(incident => incident.CustomerId);
        builder.HasIndex(incident => incident.StoreId);

        // The timeline is part of the aggregate and has no life without it.
        builder.HasMany(incident => incident.Timeline)
            .WithOne()
            .HasForeignKey(entry => entry.IncidentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(incident => incident.Timeline)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude(false);
    }
}

internal sealed class IncidentEventConfiguration : IEntityTypeConfiguration<IncidentEvent>
{
    public void Configure(EntityTypeBuilder<IncidentEvent> builder)
    {
        builder.ToTable("incident_events");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(entry => entry.Message).HasMaxLength(IncidentEvent.MaxMessageLength).IsRequired();

        builder.HasIndex(entry => new { entry.IncidentId, entry.OccurredAt });
    }
}

internal sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannel>
{
    public void Configure(EntityTypeBuilder<NotificationChannel> builder)
    {
        builder.ToTable("notification_channels");

        builder.HasKey(channel => channel.Id);

        builder.Property(channel => channel.Name).HasMaxLength(NotificationChannel.MaxNameLength).IsRequired();
        builder.Property(channel => channel.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(channel => channel.Endpoint).HasMaxLength(NotificationChannel.MaxEndpointLength);
        builder.Property(channel => channel.MinimumSeverity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(channel => channel.RuleFilter).HasMaxLength(1000);
        builder.Property(channel => channel.DisabledReason).HasMaxLength(500);

        // Ciphertext, never the secret. Long because the envelope carries its own
        // nonce and authentication tag alongside the payload.
        builder.Property(channel => channel.SecretCipher).HasMaxLength(2000);

        builder.HasIndex(channel => new { channel.CustomerId, channel.IsEnabled });
    }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries");

        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(delivery => delivery.RuleKey).HasMaxLength(100).IsRequired();
        builder.Property(delivery => delivery.Subject).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(delivery => delivery.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(delivery => delivery.Title).HasMaxLength(NotificationDelivery.MaxSubjectLength).IsRequired();
        builder.Property(delivery => delivery.Body).HasMaxLength(NotificationDelivery.MaxBodyLength).IsRequired();
        builder.Property(delivery => delivery.LastError).HasMaxLength(1000);

        // The dispatcher's only query: what is pending and due, oldest first.
        builder.HasIndex(delivery => new { delivery.Status, delivery.NextAttemptAt });

        // The per-channel cooldown check, run once per candidate channel every
        // time anything is notified.
        builder.HasIndex(delivery => new { delivery.ChannelId, delivery.RuleKey, delivery.SubjectId, delivery.CreatedAt });

        // The notification centre: this customer's unread in-app messages.
        builder.HasIndex(delivery => new { delivery.CustomerId, delivery.ReadAt });

        builder.HasOne<NotificationChannel>()
            .WithMany()
            .HasForeignKey(delivery => delivery.ChannelId)
            // Deliveries outlive the channel being reconfigured but not deleted:
            // "was anyone told?" must stay answerable, and a cascade would erase
            // the answer.
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// The per-year counter behind incident references.
///
/// A table rather than a sequence because the count restarts each year, and
/// because a row can be locked and incremented inside the same transaction as
/// the insert — which is what stops two incidents opened in the same second from
/// sharing <c>INC-2026-0042</c>.
/// </summary>
internal sealed class IncidentReferenceSequenceConfiguration : IEntityTypeConfiguration<IncidentReferenceSequence>
{
    public void Configure(EntityTypeBuilder<IncidentReferenceSequence> builder)
    {
        builder.ToTable("incident_reference_sequences");

        builder.HasKey(sequence => sequence.Year);

        builder.Property(sequence => sequence.Year).ValueGeneratedNever();
        builder.Property(sequence => sequence.LastValue).IsRequired();
    }
}
