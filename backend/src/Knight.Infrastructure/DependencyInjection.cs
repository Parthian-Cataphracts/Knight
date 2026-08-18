using Catalog.Domain;
using FeatureManagement.Domain;
using Identity.Abstractions;
using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Knight.Application.Abstractions.Auditing;
using Knight.Infrastructure.Auditing;
using Knight.Infrastructure.Caching;
using Knight.Infrastructure.Persistence;
using Knight.Infrastructure.Persistence.Repositories;
using Knight.Infrastructure.Security;
using Knight.Infrastructure.Storage;
using Ordering.Domain;
using Knight.Infrastructure.Adapters;
using Tenancy.Domain;

namespace Knight.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("Missing required connection string 'Platform'. Set it via configuration or the PLATFORM_DB_CONNECTION_STRING environment variable.");

        services.AddDbContext<PlatformDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform")));

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = configuration.GetConnectionString("Redis");
        });
        services.AddSingleton<ICacheService, RedisCacheService>();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.Configure<ObjectStorageOptions>(configuration.GetSection(ObjectStorageOptions.SectionName));

        services.AddSingleton<IObjectStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
            return new LocalFileObjectStorage(options.LocalRootPath);
        });

        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<IAccessTokenGenerator, JwtAccessTokenGenerator>();
        services.AddSingleton<IRefreshTokenGenerator, RefreshTokenGenerator>();

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IPlatformAdminRepository, PlatformAdminRepository>();
        services.AddScoped<ITenantUserRepository, TenantUserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<ITenantUserRoleRepository, TenantUserRoleRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IFeatureStore, FeatureStore>();

        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IProductVariantRepository, ProductVariantRepository>();
        services.AddScoped<IModifierGroupRepository, ModifierGroupRepository>();
        services.AddScoped<IModifierRepository, ModifierRepository>();
        services.AddScoped<IProductModifierGroupRepository, ProductModifierGroupRepository>();
        services.AddScoped<IProductMediaRepository, ProductMediaRepository>();

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<ITenantOrderCounterRepository, TenantOrderCounterRepository>();
        services.AddScoped<ICatalogOrderingReader, CatalogOrderingReader>();
        services.AddScoped<IOrderingTenantReader, OrderingTenantReader>();
        services.AddScoped<ICustomerOrderingReader, CustomerOrderingReader>();
        services.AddScoped<IOrderFulfillmentResolver, OrderFulfillmentResolver>();
        services.AddScoped<IOrderPromotionEvaluator, OrderPromotionEvaluator>();
        services.AddScoped<IOrderPromotionRedeemer, OrderPromotionRedeemer>();

        services.AddScoped<Customer.Domain.ICustomerRepository, CustomerRepository>();
        services.AddScoped<Fulfillment.Domain.ITenantFulfillmentSettingsRepository, TenantFulfillmentSettingsRepository>();
        services.AddScoped<Delivery.Domain.ITenantDeliverySettingsRepository, TenantDeliverySettingsRepository>();
        services.AddScoped<Delivery.Domain.IDeliveryZoneRepository, DeliveryZoneRepository>();
        services.AddScoped<Checkout.Domain.ICheckoutIdempotencyRepository, CheckoutIdempotencyRepository>();
        services.AddScoped<Checkout.Domain.ICheckoutOrderingGateway, CheckoutOrderingGateway>();
        services.AddScoped<Payment.Domain.IPaymentRepository, PaymentRepository>();
        services.AddScoped<Payment.Domain.IPaymentOrderReader, PaymentOrderReader>();

        services.AddScoped<Promotions.Domain.IPromotionRepository, PromotionRepository>();

        services.AddScoped<IAuditLogger, EfAuditLogger>();

        return services;
    }
}
