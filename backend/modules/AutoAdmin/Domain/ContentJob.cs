using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace AutoAdmin.Domain;

public enum ContentJobStatus
{
    /// <summary>Generated and waiting for the merchant to approve it.</summary>
    Draft = 0,

    /// <summary>Approved (or auto) and the publish pass has run; per-channel results are on the publications.</summary>
    Published = 1,

    /// <summary>The publish pass ran against at least one channel and every one of them failed.</summary>
    Failed = 2,
}

/// <summary>What one channel's publish attempt was, recorded so a run is explicable after the fact.</summary>
public readonly record struct PublicationRecord(
    string ChannelKey,
    bool Succeeded,
    string Detail,
    string? ExternalReference,
    string PublisherName);

/// <summary>A single generated piece of content on a job.</summary>
public sealed class ContentDraft : Entity
{
    public Guid ContentJobId { get; private set; }

    public ContentKind Kind { get; private set; }

    public string Body { get; private set; } = string.Empty;

    /// <summary>Which generator produced it — "simulated" until a real model is wired in.</summary>
    public string GeneratorName { get; private set; } = string.Empty;

    private ContentDraft()
    {
    }

    internal ContentDraft(Guid id, Guid contentJobId, ContentKind kind, string body, string generatorName)
        : base(id)
    {
        ContentJobId = contentJobId;
        Kind = kind;
        Body = string.IsNullOrWhiteSpace(body)
            ? throw DomainException.Validation("Generated content cannot be empty.")
            : body.Trim();
        GeneratorName = generatorName.Trim();
    }
}

/// <summary>The result of publishing a job's content to one channel.</summary>
public sealed class Publication : Entity
{
    public Guid ContentJobId { get; private set; }

    public string ChannelKey { get; private set; } = string.Empty;

    public bool Succeeded { get; private set; }

    public string Detail { get; private set; } = string.Empty;

    public string? ExternalReference { get; private set; }

    public string PublisherName { get; private set; } = string.Empty;

    public DateTimeOffset PublishedAt { get; private set; }

    private Publication()
    {
    }

    internal Publication(Guid id, Guid contentJobId, PublicationRecord record, DateTimeOffset publishedAt)
        : base(id)
    {
        ContentJobId = contentJobId;
        ChannelKey = record.ChannelKey;
        Succeeded = record.Succeeded;
        Detail = record.Detail;
        ExternalReference = record.ExternalReference;
        PublisherName = record.PublisherName;
        PublishedAt = publishedAt;
    }
}

/// <summary>
/// One run of the Automatic Admin: a topic, the content it generated for the
/// customer's entitled kinds, and — once approved or run automatically — where it
/// went. This is the "report" the admin gives back (docs/adr/0038).
/// </summary>
public sealed class ContentJob : AuditableEntity
{
    private readonly List<ContentDraft> _drafts = [];
    private readonly List<Publication> _publications = [];

    public Guid CustomerId { get; private set; }

    public string Topic { get; private set; } = string.Empty;

    /// <summary>The autonomy in force when the run started, snapshotted so history is explicable.</summary>
    public AutonomyMode Autonomy { get; private set; }

    public ContentJobStatus Status { get; private set; }

    public IReadOnlyCollection<ContentDraft> Drafts => _drafts;

    public IReadOnlyCollection<Publication> Publications => _publications;

    /// <summary>True while the run is still a draft waiting for approval.</summary>
    public bool AwaitingApproval => Status is ContentJobStatus.Draft;

    /// <summary>True when at least one channel's publish attempt failed.</summary>
    public bool HasPublicationErrors => _publications.Any(p => !p.Succeeded);

    private ContentJob()
    {
    }

    private ContentJob(Guid id, DateTimeOffset createdAt, Guid customerId, string topic, AutonomyMode autonomy)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        Topic = ValidateTopic(topic);
        Autonomy = autonomy;
        Status = ContentJobStatus.Draft;
    }

    public static ContentJob Create(Guid id, DateTimeOffset createdAt, Guid customerId, string topic, AutonomyMode autonomy)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("A content job must name a customer.");
        }

        return new ContentJob(id, createdAt, customerId, topic, autonomy);
    }

    /// <summary>Adds a generated piece. Only while the job is still a draft.</summary>
    public ContentDraft AddDraft(Guid draftId, ContentKind kind, string body, string generatorName)
    {
        if (Status is not ContentJobStatus.Draft)
        {
            throw DomainException.Conflict("Content can only be added while the job is a draft.");
        }

        var draft = new ContentDraft(draftId, Id, kind, body, generatorName);
        _drafts.Add(draft);
        return draft;
    }

    /// <summary>
    /// Records the outcome of the publish pass and closes the job. Only from
    /// Draft, so a run is published exactly once; approving an already-published
    /// job is refused rather than silently re-sending. With no channels the job
    /// is Published with no publications — the content was generated and there was
    /// nowhere entitled to send it, which is a finished run, not a failure.
    /// </summary>
    public void RecordPublications(IReadOnlyCollection<PublicationRecord> records, Func<Guid> newId, DateTimeOffset now)
    {
        if (Status is not ContentJobStatus.Draft)
        {
            throw DomainException.Conflict("This run has already been published.");
        }

        foreach (var record in records)
        {
            _publications.Add(new Publication(newId(), Id, record, now));
        }

        Status = records.Count > 0 && records.All(r => !r.Succeeded)
            ? ContentJobStatus.Failed
            : ContentJobStatus.Published;

        MarkUpdated(now);
    }

    private static string ValidateTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw DomainException.Validation("A topic is required.");
        }

        var trimmed = topic.Trim();
        if (trimmed.Length > 500)
        {
            throw DomainException.Validation("A topic must be 500 characters or fewer.");
        }

        return trimmed;
    }
}
