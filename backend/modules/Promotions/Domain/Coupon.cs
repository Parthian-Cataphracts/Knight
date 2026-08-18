using Knight.Application.Exceptions;
using Knight.Domain.Common;

namespace Promotions.Domain;

public sealed class Coupon : AuditableEntity, ITenantScoped
{
    public const int MaxCodeLength = 64;

    public Guid TenantId { get; private set; }
    public Guid PromotionId { get; private set; }
    public string Code { get; private set; }
    public string NormalizedCode { get; private set; }
    public CouponStatus Status { get; private set; }
    public int? UsageLimitTotal { get; private set; }
    public DateTimeOffset? StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    private Coupon()
    {
        Code = string.Empty;
        NormalizedCode = string.Empty;
    }

    private Coupon(
        Guid id,
        Guid tenantId,
        Guid promotionId,
        string code,
        string normalizedCode,
        CouponStatus status,
        int? usageLimitTotal,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        DateTimeOffset now)
        : base(id, now)
    {
        TenantId = tenantId;
        PromotionId = promotionId;
        Code = code;
        NormalizedCode = normalizedCode;
        Status = status;
        UsageLimitTotal = usageLimitTotal;
        StartsAt = startsAt;
        EndsAt = endsAt;
    }

    public static Coupon Create(
        Guid id,
        Guid tenantId,
        Guid promotionId,
        string code,
        int? usageLimitTotal,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Coupon ID cannot be empty.", nameof(id));
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));
        if (promotionId == Guid.Empty) throw new ArgumentException("Promotion ID cannot be empty.", nameof(promotionId));

        ValidateParameters(code, usageLimitTotal, startsAt, endsAt);

        var trimmedCode = code.Trim();
        var normalizedCode = NormalizeCode(trimmedCode);

        return new Coupon(
            id,
            tenantId,
            promotionId,
            trimmedCode,
            normalizedCode,
            CouponStatus.Active,
            usageLimitTotal,
            startsAt,
            endsAt,
            now);
    }

    public void Update(
        int? usageLimitTotal,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        DateTimeOffset now)
    {
        if (Status == CouponStatus.Archived)
        {
            throw new ConflictException("Cannot modify an archived coupon.");
        }

        ValidateParameters(Code, usageLimitTotal, startsAt, endsAt);

        UsageLimitTotal = usageLimitTotal;
        StartsAt = startsAt;
        EndsAt = endsAt;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        if (Status == CouponStatus.Archived)
        {
            return;
        }

        Status = CouponStatus.Archived;
        ArchivedAt = now;
        UpdatedAt = now;
    }

    public bool IsActiveAt(DateTimeOffset now)
    {
        if (Status != CouponStatus.Active)
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

        return true;
    }

    public static string NormalizeCode(string rawCode)
    {
        return string.IsNullOrWhiteSpace(rawCode) ? string.Empty : rawCode.Trim().ToUpperInvariant();
    }

    private static void ValidateParameters(
        string code,
        int? usageLimitTotal,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(code))
        {
            errors["code"] = ["Coupon code is required."];
        }
        else if (code.Trim().Length > MaxCodeLength)
        {
            errors["code"] = [$"Coupon code cannot exceed {MaxCodeLength} characters."];
        }

        if (usageLimitTotal.HasValue && usageLimitTotal.Value <= 0)
        {
            errors["usageLimitTotal"] = ["Coupon usage limit must be greater than 0."];
        }

        if (startsAt.HasValue && endsAt.HasValue && startsAt.Value >= endsAt.Value)
        {
            errors["timeWindow"] = ["Coupon StartsAt must be strictly before EndsAt."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
