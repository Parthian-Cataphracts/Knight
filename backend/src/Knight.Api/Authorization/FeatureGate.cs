using Knight.Application.Abstractions.Features;
using Knight.Application.Abstractions.Tenancy;
using Knight.Application.Exceptions;

namespace Knight.Api.Authorization;

/// <summary>
/// Declarative, module-agnostic tenant feature gate. Mirrors the ergonomics of
/// <see cref="AuthorizationPolicyBuilderExtensions.RequirePermission"/> — a route or
/// route group states the capability it needs and the plumbing enforces it — but
/// runs as an <see cref="IEndpointFilter"/> rather than an authorization
/// requirement, for three reasons:
///
/// <list type="bullet">
/// <item>Anonymous storefront routes (see <c>PublicCatalogEndpoints</c>) carry no
/// authorization policy at all, and attaching one purely to smuggle in a feature
/// check would put those routes through the authorization middleware for no other
/// purpose.</item>
/// <item>Platform control-plane routes resolve their target tenant from a route
/// value rather than from <see cref="ITenantContext"/>; a filter sees
/// <see cref="HttpContext.Request"/> route values directly, whereas an
/// authorization handler would need the endpoint's route data threaded to it.</item>
/// <item>Failing through <see cref="ForbiddenException"/> reuses the exact
/// Problem Details response the manual per-handler check produced, so the wire
/// contract is unchanged (see <c>ExceptionHandlingMiddleware</c>). Filters execute
/// inside the endpoint pipeline, which the exception-handling middleware wraps.</item>
/// </list>
///
/// The gate knows nothing about any particular module: it takes a feature key
/// string and asks <see cref="IFeatureAccessService"/>. It fails closed on every
/// path — no resolvable tenant, an unparseable route tenant, an unknown feature
/// key, or a tenant that simply has the feature off all deny identically.
/// </summary>
public sealed class FeatureGateEndpointFilter : IEndpointFilter
{
    private readonly string _featureKey;
    private readonly string? _tenantRouteValueName;

    /// <param name="featureKey">
    /// The <c>FeatureDefinition</c> key to require. A key that is not registered
    /// in the platform's feature catalog is never implicitly enabled: no
    /// <c>TenantFeature</c> row can exist for it, so <see cref="IFeatureAccessService.IsEnabledAsync"/>
    /// reports false and this filter denies.
    /// </param>
    /// <param name="tenantRouteValueName">
    /// When set, the target tenant is read from this route value instead of from
    /// <see cref="ITenantContext"/>. Used by the platform control-plane routes,
    /// which act on a named tenant rather than on the caller's own tenant.
    /// </param>
    public FeatureGateEndpointFilter(string featureKey, string? tenantRouteValueName = null)
    {
        _featureKey = featureKey;
        _tenantRouteValueName = tenantRouteValueName;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var tenantId = ResolveTenantId(httpContext);

        var featureAccessService = httpContext.RequestServices.GetRequiredService<IFeatureAccessService>();
        var isEnabled = await featureAccessService.IsEnabledAsync(tenantId, _featureKey, httpContext.RequestAborted);

        if (!isEnabled)
        {
            throw new ForbiddenException($"The {_featureKey} feature is not enabled for this tenant.");
        }

        return await next(context);
    }

    private Guid ResolveTenantId(HttpContext httpContext)
    {
        if (_tenantRouteValueName is null)
        {
            var tenantContext = httpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenantContext.HasTenant
                ? tenantContext.TenantId!.Value
                : throw new ForbiddenException("A tenant context is required.");
        }

        // A missing or malformed route tenant must never fall back to the caller's
        // own tenant — there is nothing to check against, so the request is denied.
        return httpContext.Request.RouteValues.TryGetValue(_tenantRouteValueName, out var raw)
            && Guid.TryParse(raw?.ToString(), out var routeTenantId)
                ? routeTenantId
                : throw new ForbiddenException("A tenant context is required.");
    }
}

/// <summary>
/// <c>.RequireFeature(...)</c> for minimal-API routes and route groups, the feature
/// counterpart of <c>.RequireAuthorization(p =&gt; p.RequirePermission(...))</c>.
/// The two are entirely independent: a route may require a feature, a permission,
/// both, or neither, and either one denying is sufficient to deny the request.
/// </summary>
public static class FeatureGateEndpointExtensions
{
    /// <summary>
    /// Requires <paramref name="featureKey"/> for the tenant resolved into
    /// <see cref="ITenantContext"/> — tenant self-administration routes and
    /// host-resolved anonymous public routes.
    /// </summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string featureKey)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new FeatureGateEndpointFilter(featureKey));

    /// <summary>
    /// Requires <paramref name="featureKey"/> for the tenant named by a route
    /// value — the platform control-plane routes, which address a tenant
    /// explicitly rather than through <see cref="ITenantContext"/>.
    /// </summary>
    public static TBuilder RequireFeatureForRouteTenant<TBuilder>(
        this TBuilder builder,
        string featureKey,
        string tenantRouteValueName = "tenantId")
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new FeatureGateEndpointFilter(featureKey, tenantRouteValueName));
}
