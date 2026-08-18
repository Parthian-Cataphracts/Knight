using Knight.Application.Exceptions;
using Knight.Domain.Common;

namespace Promotions.Domain;

public sealed class Promotion : AuditableEntity, ITenantScoped
{
    public const int MaxNameLength = 128;
    public const int MaxDescriptionLength = 500;

    public Guid TenantId { get; private set; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public PromotionStatus Status { get; private set; }
    public PromotionDiscountType DiscountType { get; private set; }
    public decimal DiscountValue { get; private set; }
    public decimal? MinimumSubtotal { get; private set; }
    public decimal? MaximumDiscountAmount { get; private set; }
    public DateTimeOffset? StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public bool RequiresCoupon { get; private set; }
    public int Priority { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    private Promotion()
    {
        Name = string.Empty;
    }

    private Promotion(
        Guid id,
        Guid tenantId,
        string name,
        string? description,
        PromotionStatus status,
        PromotionDiscountType discountType,
        decimal discountValue,
        decimal? minimumSubtotal,
        decimal? maximumDiscountAmount,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        bool requiresCoupon,
        int priority,
        DateTimeOffset now)
        : base(id, now)
    {
        TenantId = tenantId;
        Name = name;
        Description = description;
        Status = status;
        DiscountType = discountType;
        DiscountValue = discountValue;
        MinimumSubtotal = minimumSubtotal;
        MaximumDiscountAmount = maximumDiscountAmount;
        StartsAt = startsAt;
        EndsAt = endsAt;
        RequiresCoupon = requiresCoupon;
        Priority = priority;
    }

    public static Promotion Create(
        Guid id,
        Guid tenantId,
        string name,
        string? description,
        PromotionDiscountType discountType,
        decimal discountValue,
        decimal? minimumSubtotal,
        decimal? maximumDiscountAmount,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        bool requiresCoupon,
        int priority,
        DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Promotion ID cannot be empty.", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));

        ValidateParameters(name, description, discountType, discountValue, minimumSubtotal, maximumDiscountAmount, startsAt, endsAt);

        return new Promotion(
            id,
            tenantId,
            name.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            PromotionStatus.Draft,
            discountType,
            discountValue,
            minimumSubtotal,
            maximumDiscountAmount,
            startsAt,
            endsAt,
            requiresCoupon,
            priority,
            now);
    }

    public void Update(
        string name,
        string? description,
        PromotionDiscountType discountType,
        decimal discountValue,
        decimal? minimumSubtotal,
        decimal? maximumDiscountAmount,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        bool requiresCoupon,
        int priority,
        DateTimeOffset now)
    {
        if (Status == PromotionStatus.Archived)
        {
            throw new ConflictException("Cannot modify an archived promotion.");
        }

        ValidateParameters(name, description, discountType, discountValue, minimumSubtotal, maximumDiscountAmount, startsAt, endsAt);

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        DiscountType = discountType;
        DiscountValue = discountValue;
        MinimumSubtotal = minimumSubtotal;
        MaximumDiscountAmount = maximumDiscountAmount;
        StartsAt = startsAt;
        EndsAt = endsAt;
        RequiresCoupon = requiresCoupon;
        Priority = priority;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        if (Status == PromotionStatus.Archived)
        {
            throw new ConflictException("Cannot activate an archived promotion.");
        }

        if (Status == PromotionStatus.Active)
        {
            return;
        }

        Status = PromotionStatus.Active;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        if (Status == PromotionStatus.Archived)
        {
            return;
        }

        Status = PromotionStatus.Archived;
        ArchivedAt = now;
        UpdatedAt = now;
    }

    public bool IsEligible(decimal subtotal, DateTimeOffset now)
    {
        if (Status != PromotionStatus.Active)
        {
            return false;
        }

        if (StartsAt.HasValue && now < StartsAt.Value)
        {
            return false;
        }

        if (EndsAt.HasValue && now >= EndsAt.Value)
        {
            return false;
        }

        if (MinimumSubtotal.HasValue && subtotal < MinimumSubtotal.Value)
        {
            return false;
        }

        return true;
    }

    public decimal CalculateDiscount(decimal subtotal, DateTimeOffset now)
    {
        if (!IsEligible(subtotal, now) || subtotal <= 0m)
        {
            return 0m;
        }

        decimal discount;
        if (DiscountType == PromotionDiscountType.Percentage)
        {
            discount = Math.Round(subtotal * (DiscountValue / 100m), 2, MidpointRounding.AwayFromZero);
            if (MaximumDiscountAmount.HasValue && discount > MaximumDiscountAmount.Value)
            {
                discount = MaximumDiscountAmount.Value;
            }
        }
        else if (DiscountType == PromotionDiscountType.FixedAmount)
        {
            discount = DiscountValue;
        }
        else
        {
            return 0m;
        }

        discount = Math.Min(discount, subtotal);
        return Math.Max(0m, discount);
    }

    private static void ValidateParameters(
        string name,
        string? description,
        PromotionDiscountType discountType,
        decimal discountValue,
        decimal? minimumSubtotal,
        decimal? maximumDiscountAmount,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Promotion name is required."];
        }
        else if (name.Trim().Length > MaxNameLength)
        {
            errors["name"] = [$"Promotion name cannot exceed {MaxNameLength} characters."];
        }

        if (description is not null && description.Trim().Length > MaxDescriptionLength)
        {
            errors["description"] = [$"Promotion description cannot exceed {MaxDescriptionLength} characters."];
        }

        if (discountType == PromotionDiscountType.Percentage)
        {
            if (discountValue <= 0m || discountValue > 100m)
            {
                errors["discountValue"] = ["Percentage discount value must be greater than 0 and at most 100."];
            }
        }
        else if (discountType == PromotionDiscountType.FixedAmount)
        {
            if (discountValue <= 0m)
            {
                errors["discountValue"] = ["Fixed amount discount value must be greater than 0."];
            }
        }
        else
        {
            errors["discountType"] = ["Invalid discount type."];
        }

        if (minimumSubtotal.HasValue && minimumSubtotal.Value < 0m)
        {
            errors["minimumSubtotal"] = ["Minimum subtotal cannot be negative."];
        }

        if (maximumDiscountAmount.HasValue && maximumDiscountAmount.Value <= 0m)
        {
            errors["maximumDiscountAmount"] = ["Maximum discount amount must be greater than 0."];
        }

        if (startsAt.HasValue && endsAt.HasValue && startsAt.Value >= endsAt.Value)
        {
            errors["timeWindow"] = ["Promotion StartsAt must be strictly before EndsAt."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
