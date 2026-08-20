using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Domain.Common;
using Plans.Domain;

namespace Plans;

/// <summary>
/// Plan and price administration.
///
/// Repricing a feature opens a new row and closes the old one from the same
/// moment, rather than editing the amount in place. An invoice issued last month
/// has to remain explicable from the prices that were in force last month; an
/// in-place edit would quietly rewrite history.
/// </summary>
internal sealed class PlanService : IPlanService
{
    private readonly IPlanRepository _plans;
    private readonly IFeaturePriceRepository _prices;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;

    public PlanService(
        IPlanRepository plans,
        IFeaturePriceRepository prices,
        IAuditTrail audit,
        IDateTimeProvider clock)
    {
        _plans = plans;
        _prices = prices;
        _audit = audit;
        _clock = clock;
    }

    public Task<IReadOnlyCollection<Plan>> ListAsync(bool includeInactive, CancellationToken cancellationToken) =>
        _plans.ListAsync(includeInactive, cancellationToken);

    public Task<Plan?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _plans.GetByIdAsync(id, cancellationToken);

    public async Task<Plan> CreateAsync(CreatePlanInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (await _plans.GetByKeyAsync(input.Key.Trim().ToLowerInvariant(), cancellationToken) is not null)
        {
            throw new ConflictException($"A plan with key '{input.Key}' already exists.");
        }

        var plan = Plan.Create(
            Guid.NewGuid(),
            now,
            input.Key,
            input.Name,
            Money.Of(input.BasePrice, input.Currency),
            input.SortOrder);

        plan.UpdateMetadata(input.Name, input.Description, input.SortOrder, now);

        await _plans.AddAsync(plan, cancellationToken);
        await _plans.SaveChangesAsync(cancellationToken);

        await AuditAsync("plan.created", plan, cancellationToken);
        return plan;
    }

    public async Task<Plan> UpdateAsync(Guid id, UpdatePlanInput input, CancellationToken cancellationToken)
    {
        var plan = await RequireAsync(id, cancellationToken);
        var before = Snapshot(plan);
        var now = _clock.UtcNow;

        plan.UpdateMetadata(input.Name, input.Description, input.SortOrder, now);
        plan.Reprice(Money.Of(input.BasePrice, input.Currency), now);
        plan.SetDataRetention(input.DataRetentionDays, now);
        await _plans.SaveChangesAsync(cancellationToken);

        await AuditAsync("plan.updated", plan, cancellationToken, before);
        return plan;
    }

    public Task<Plan> ActivateAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "plan.activated", (plan, now) => plan.Activate(now), cancellationToken);

    public Task<Plan> DeactivateAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "plan.deactivated", (plan, now) => plan.Deactivate(now), cancellationToken);

    public async Task<Plan> SetFeatureAsync(Guid id, SetPlanFeatureInput input, CancellationToken cancellationToken)
    {
        var plan = await RequireAsync(id, cancellationToken);
        var before = Snapshot(plan);
        var now = _clock.UtcNow;

        var existed = plan.Find(input.FeatureId) is not null;
        var entry = plan.SetFeature(
            input.FeatureId,
            input.IsIncluded,
            input.IsCustomerToggleable,
            input.PinnedVersionRange,
            now);

        if (!existed)
        {
            _plans.RegisterNewFeature(entry);
        }

        await _plans.SaveChangesAsync(cancellationToken);

        await AuditAsync("plan.feature_set", plan, cancellationToken, before);
        return plan;
    }

    public async Task<Plan> RemoveFeatureAsync(Guid id, Guid featureId, CancellationToken cancellationToken)
    {
        var plan = await RequireAsync(id, cancellationToken);
        var before = Snapshot(plan);

        var entry = plan.Find(featureId)
            ?? throw new NotFoundException("The plan does not list this feature.");

        plan.RemoveFeature(featureId, _clock.UtcNow);
        _plans.RemoveFeature(entry);
        await _plans.SaveChangesAsync(cancellationToken);

        await AuditAsync("plan.feature_removed", plan, cancellationToken, before);
        return plan;
    }

    public async Task<FeaturePrice> SetPriceAsync(SetFeaturePriceInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // Close whatever this replaces at the same instant the new price opens,
        // so there is never a gap and never an overlap.
        var existing = await _prices.ListForFeatureAsync(input.FeatureId, cancellationToken);
        foreach (var superseded in existing.Where(price => price.PlanId == input.PlanId && price.AppliesAt(now)))
        {
            superseded.Close(now);
        }

        var price = FeaturePrice.Create(
            Guid.NewGuid(),
            input.FeatureId,
            input.PlanId,
            Money.Of(input.Amount, input.Currency),
            input.BillingPeriod,
            now);

        await _prices.AddAsync(price, cancellationToken);
        await _prices.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "plan.price_set",
            nameof(FeaturePrice),
            price.Id.ToString(),
            customerId: null,
            cancellationToken,
            newValue: new
            {
                price.FeatureId,
                price.PlanId,
                price.Amount,
                price.Currency,
                BillingPeriod = price.BillingPeriod.ToString(),
                price.ValidFrom,
            });

        return price;
    }

    public Task<IReadOnlyCollection<FeaturePrice>> ListPricesAsync(Guid featureId, CancellationToken cancellationToken) =>
        _prices.ListForFeatureAsync(featureId, cancellationToken);

    private async Task<Plan> TransitionAsync(
        Guid id,
        string action,
        Action<Plan, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        var plan = await RequireAsync(id, cancellationToken);
        var before = Snapshot(plan);

        transition(plan, _clock.UtcNow);
        await _plans.SaveChangesAsync(cancellationToken);

        await AuditAsync(action, plan, cancellationToken, before);
        return plan;
    }

    private async Task<Plan> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await _plans.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Plan '{id}' was not found.");

    private Task AuditAsync(string action, Plan plan, CancellationToken cancellationToken, object? before = null) =>
        _audit.RecordAsync(
            action,
            nameof(Plan),
            plan.Id.ToString(),

            // Plans are platform-wide, so an audit entry about one belongs to no
            // customer and is visible to platform principals only.
            customerId: null,
            cancellationToken,
            before,
            Snapshot(plan));

    private static object Snapshot(Plan plan) => new
    {
        plan.Key,
        plan.Name,
        plan.Description,
        plan.BasePriceAmount,
        plan.Currency,
        plan.IsActive,
        plan.SortOrder,
        Features = plan.Features
            .Select(feature => new
            {
                feature.FeatureId,
                feature.IsIncluded,
                feature.IsCustomerToggleable,
                feature.PinnedVersionRange,
            })
            .ToArray(),
    };
}
