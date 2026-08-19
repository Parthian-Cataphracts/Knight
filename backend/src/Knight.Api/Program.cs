using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using AccessControl;
using Knight.Api.Authorization;
using Knight.Api.Composition;
using Knight.Api.BackgroundServices;
using Knight.Api.ControlPlane;
using Knight.Api.Endpoints;
using Knight.Api.Ingest;
using Knight.Api.Middleware;
using Knight.Application;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Tenancy;
using Knight.Application.Abstractions.Time;
using Knight.Infrastructure;
using Knight.Infrastructure.ControlPlane;
using Knight.Infrastructure.ControlPlane.Security;
using Knight.Infrastructure.HealthChecks;
using Knight.Infrastructure.Security;
using Scalar.AspNetCore;
using Serilog;
using Stores;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Product identity for the generated OpenAPI document. Route paths are part of the
// published API contract and are deliberately untouched by the Knight rename — the
// "platform" segment in /api/platform/* denotes the control plane, not the product.
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Knight API";
        document.Info.Description = "Knight multi-tenant SaaS platform API.";
        return Task.CompletedTask;
    });
});

builder.Services.AddPlatformApplication();
builder.Services.AddPlatformInfrastructure(builder.Configuration);
builder.Services.AddPlatformModules(builder.Configuration);
builder.Services.AddControlPlaneInfrastructure(builder.Configuration);
builder.Services.AddControlPlaneModules(builder.Configuration);
builder.Services.AddPlatformHealthChecks(builder.Configuration);
builder.Services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

// Production must fail fast rather than silently accept a development
// signing-key placeholder — see docs/security/README.md.
builder.Services.AddSingleton<IValidateOptions<JwtOptions>, JwtProductionSigningKeyValidator>();

// The same rule for the key stores verify signed payloads with: a laptop may
// derive it from the token key, a deployment may not.
builder.Services.AddSingleton<IValidateOptions<StoreOptions>, StoreSigningKeyValidator>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        // Without this, ASP.NET Core's default inbound claim mapping silently
        // renames well-known JWT claims (e.g. "sub" -> the long legacy
        // ClaimTypes.NameIdentifier URI), which would break every claim lookup
        // in this codebase that reads JwtRegisteredClaimNames.Sub directly
        // (see HttpContextCurrentUser) — claims in context.User must match
        // exactly what JwtAccessTokenGenerator issued.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions?.Issuer,
            ValidAudience = jwtOptions?.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions?.SigningKey ?? string.Empty)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // A browser cannot set an Authorization header on a WebSocket or
                // an EventSource, so SignalR puts the token in the query string
                // instead. It is read here for the hub path and nowhere else:
                // accepting query-string tokens on ordinary endpoints would put
                // credentials into every proxy log and browser history entry
                // that ever saw the URL (docs/security-threat-model.md).
                if (context.Request.Path.StartsWithSegments(ControlPlaneHub.Path) &&
                    context.Request.Query.TryGetValue("access_token", out var token))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

// Platform and tenant callers are distinguished solely by the "principal_type"
// claim minted at token issuance (see JwtAccessTokenGenerator) — never by the
// mere presence or absence of a tenant claim. See docs/architecture/authorization.md.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("PlatformAdminOnly", policy => policy.RequireClaim("principal_type", "platform_admin"))
    .AddPolicy("TenantUserOnly", policy => policy.RequireClaim("principal_type", "tenant_user"))

    // Dashboard endpoints. A store or agent token carries a different
    // principal_type and is rejected here, before any handler runs
    // (docs/authentication.md section 4).
    .AddPolicy(ControlPlaneAuthorizationExtensions.UserPolicy, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(PrincipalTypes.ClaimType, PrincipalTypes.User))

    // The mirror image for ingestion: a dashboard token is refused here just as
    // firmly as a store token is refused above.
    .AddPolicy(StoreAuthorization.Policy, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(PrincipalTypes.ClaimType, PrincipalTypes.Store));

builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, ControlPlanePermissionHandler>();

// The realtime channel the dashboard listens on. Registered before CORS because
// the hub is served from the same origin policy as the API — a browser opening a
// websocket honours it exactly as it does a fetch.
builder.Services.AddSignalR();
builder.Services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()

            // The dashboard sends its refresh-token cookie, so the browser
            // requires this header on the preflight response; without it every
            // credentialed call fails in the browser while succeeding from a
            // test client, which is exactly the class of bug a server-side test
            // cannot see. Safe because the origins are an explicit list — the
            // browser itself forbids pairing credentials with a wildcard.
            .AllowCredentials();
    });
});

// Extensible rate-limiting foundation: distinct named policies so platform,
// tenant-public, and authentication traffic can be tuned independently.
// Login/refresh policies partition by client IP so one abusive caller cannot
// exhaust the limit for everyone else — see docs/architecture/authorization.md.
// Rate limiting is a defense-in-depth measure alongside account lockout, not a
// substitute for it.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    var rateLimitOptions = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
    var window = TimeSpan.FromSeconds(rateLimitOptions.WindowSeconds);

    options.AddFixedWindowLimiter("platform", limiterOptions =>
    {
        limiterOptions.PermitLimit = rateLimitOptions.PlatformPermitLimit;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("tenant-public", limiterOptions =>
    {
        limiterOptions.PermitLimit = rateLimitOptions.TenantPublicPermitLimit;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });

    options.AddPolicy("control-plane", PartitionByClientIp(rateLimitOptions.ControlPlanePermitLimit, window));
    options.AddPolicy("auth-control-plane", PartitionByClientIp(rateLimitOptions.ControlPlaneLoginPermitLimit, window));
    options.AddPolicy("auth-platform-login", PartitionByClientIp(rateLimitOptions.PlatformLoginPermitLimit, window));
    options.AddPolicy("auth-tenant-login", PartitionByClientIp(rateLimitOptions.TenantLoginPermitLimit, window));
    options.AddPolicy("auth-refresh", PartitionByClientIp(rateLimitOptions.RefreshPermitLimit, window));
    options.AddPolicy("checkout_quote", PartitionByTenantAndClientIp(rateLimitOptions.CheckoutQuotePermitLimit, window));
    options.AddPolicy("checkout_submit", PartitionByTenantAndClientIp(rateLimitOptions.CheckoutSubmitPermitLimit, window));

    // The handshake is unauthenticated, so there is no store to partition by
    // yet: an address it is. This is the credential-guessing surface, and the
    // limit is deliberately tighter than ingestion's.
    options.AddPolicy(StoreIngestEndpoints.HandshakePolicy, PartitionByClientIp(rateLimitOptions.IngestHandshakePermitLimit, window));

    // Authenticated ingestion is partitioned by store id. Per-address would be
    // wrong in both directions: stores on one shared host would share a budget,
    // and one store behind several addresses would multiply its own.
    options.AddPolicy(StoreIngestEndpoints.IngestPolicy, PartitionByStore(rateLimitOptions.IngestPermitLimit, window));
});

var app = builder.Build();

// Pipeline order, outside-in: correlation and exception handling wrap everything
// so every response (including ones the pipeline short-circuits below) carries a
// correlation id and a consistent Problem Details shape. Authentication must run
// before tenant resolution because resolution reads validated claims off
// context.User (a tenant-token's tenant_id claim, a platform-admin's principal
// type). Tenant resolution runs before authorization so authorization policies
// and endpoint handlers can rely on ITenantContext already being populated (or
// deliberately empty) for the request. See docs/architecture/multi-tenancy.md.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors("Default");

app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();

// Runs after authentication, before authorization: the customer boundary must
// be established from validated claims before any policy or handler queries
// anything (docs/authorization.md section 3).
app.UseMiddleware<ControlPlaneScopeMiddleware>();

app.UseAuthorization();

app.MapControlPlaneAuthEndpoints();
app.MapStoreIngestEndpoints();
app.MapStoreJobEndpoints();
app.MapArtifactEndpoints();
app.MapAgentEndpoints();
app.MapControlPlaneCustomerEndpoints();
app.MapControlPlaneStoreEndpoints();
app.MapControlPlaneAuditLogEndpoints();
app.MapControlPlaneLogEndpoints();
app.MapControlPlanePlanEndpoints();
app.MapControlPlaneDeliveryEndpoints();
app.MapControlPlaneServerEndpoints();
app.MapControlPlaneObservabilityEndpoints();
app.MapControlPlaneSubscriptionEndpoints();
app.MapControlPlaneBillingEndpoints();
app.MapControlPlaneAccessEndpoints();

// The connection is placed into its groups from its own claims on connect; there
// is deliberately no hub method a client can call to choose what it receives.
app.MapHub<ControlPlaneHub>(ControlPlaneHub.Path).RequireCors("Default");

app.MapPlatformHealthEndpoints();
app.MapPlatformTenantEndpoints();
app.MapTenantRuntimeEndpoints();
app.MapPlatformAuthEndpoints();
app.MapTenantAuthEndpoints();
app.MapTenantStaffEndpoints();
app.MapTenantRoleEndpoints();
app.MapTenantPermissionEndpoints();
app.MapPlatformStaffEndpoints();
app.MapPlatformRoleEndpoints();
app.MapTenantCatalogCategoryEndpoints();
app.MapTenantCatalogProductEndpoints();
app.MapTenantCatalogVariantEndpoints();
app.MapTenantCatalogModifierEndpoints();
app.MapTenantCatalogMediaEndpoints();
app.MapPlatformCatalogEndpoints();
app.MapPublicCatalogEndpoints();
app.MapPublicCheckoutEndpoints();
app.MapTenantOrderEndpoints();
app.MapPlatformOrderEndpoints();
app.MapTenantCustomerEndpoints();
app.MapPlatformCustomerEndpoints();
app.MapTenantFulfillmentEndpoints();
app.MapPlatformFulfillmentEndpoints();
app.MapTenantDeliveryEndpoints();
app.MapPlatformDeliveryEndpoints();
app.MapTenantPaymentEndpoints();
app.MapPlatformPaymentEndpoints();
app.MapTenantPromotionEndpoints();
app.MapTenantCouponEndpoints();
app.MapPlatformPromotionEndpoints();
app.MapPlatformCouponEndpoints();

app.Services.InitializePermissionCatalog();

app.Run();

static Func<HttpContext, RateLimitPartition<string>> PartitionByClientIp(int permitLimit, TimeSpan window) => httpContext =>
{
    var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueLimit = 0
    });
};

static Func<HttpContext, RateLimitPartition<string>> PartitionByStore(int permitLimit, TimeSpan window) => httpContext =>
{
    // Read straight off the validated token: the rate limiter runs after
    // authentication, and the store id in the payload is never trusted for
    // anything, least of all for deciding whose budget to spend.
    var storeId = httpContext.User.FindFirst(StoreClaims.StoreId)?.Value;

    // A request with no store claim cannot reach an ingestion handler — the
    // policy rejects it — but it still passes through the limiter, so it gets a
    // shared bucket rather than a free pass.
    var key = string.IsNullOrWhiteSpace(storeId) ? "unauthenticated" : storeId;

    return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueLimit = 0
    });
};

static Func<HttpContext, RateLimitPartition<string>> PartitionByTenantAndClientIp(int permitLimit, TimeSpan window) => httpContext =>
{
    // The rate limiter deliberately runs before TenantResolutionMiddleware so that a
    // flood is rejected before it can cost a tenant-resolution database lookup. That
    // means ITenantContext is still empty here, so the tenant half of the partition
    // key must come from the same request signal the resolver itself keys off: the
    // host. Distinct tenants necessarily own distinct hosts, so this keeps one
    // storefront's traffic from consuming another's budget.
    var host = httpContext.Request.Host.Host;
    var tenantKey = string.IsNullOrWhiteSpace(host) ? "no-host" : host.ToLowerInvariant();
    var ipKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var partitionKey = $"{tenantKey}:{ipKey}";
    return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueLimit = 0
    });
};

// Enables WebApplicationFactory<Program> in Knight.IntegrationTests.
public partial class Program;
