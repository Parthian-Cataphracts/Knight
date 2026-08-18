namespace Knight.Domain.Common;

/// <summary>
/// Base abstraction for entities that track creation and modification timestamps.
/// </summary>
public abstract class AuditableEntity : Entity
{
    public DateTimeOffset CreatedAt { get; protected set; }

    public DateTimeOffset? UpdatedAt { get; protected set; }

    protected AuditableEntity()
    {
    }

    protected AuditableEntity(Guid id, DateTimeOffset createdAt)
        : base(id)
    {
        CreatedAt = createdAt;
    }

    protected void MarkUpdated(DateTimeOffset updatedAt)
    {
        UpdatedAt = updatedAt;
    }
}
