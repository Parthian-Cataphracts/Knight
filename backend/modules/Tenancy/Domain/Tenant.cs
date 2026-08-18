using System.Text.RegularExpressions;
using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Tenancy.Domain;

/// <summary>
/// A tenant onboarded onto the platform. Business-type and feature-specific
/// behavior must never live on this entity — see FeatureManagement for
/// capability toggles. Owns its <see cref="TenantDomain"/> collection so
/// domain invariants (uniqueness of purpose, controlled primary-domain changes)
/// are enforced in one place rather than by ad hoc database mutation.
/// </summary>
public sealed partial class Tenant : AuditableEntity
{
    public string Name { get; private set; }

    public string Slug { get; private set; }

    public TenantStatus Status { get; private set; }

    public string TimeZone { get; private set; }

    public string DefaultCurrency { get; private set; }

    private readonly List<TenantDomain> _domains = [];

    public IReadOnlyCollection<TenantDomain> Domains => _domains.AsReadOnly();

    private Tenant()
    {
        Name = string.Empty;
        Slug = string.Empty;
        TimeZone = "UTC";
        DefaultCurrency = "USD";
    }

    private Tenant(Guid id, DateTimeOffset createdAt, string name, string slug, string timeZone, string defaultCurrency)
        : base(id, createdAt)
    {
        Name = name;
        Slug = slug;
        TimeZone = timeZone;
        DefaultCurrency = defaultCurrency;
        Status = TenantStatus.Pending;
    }

    public static Tenant Create(
        Guid id,
        DateTimeOffset createdAt,
        string name,
        string slug,
        string timeZone,
        string defaultCurrency)
    {
        var normalizedName = ValidateName(name);
        var normalizedSlug = ValidateSlug(slug);
        var normalizedTimeZone = ValidateTimeZone(timeZone);
        var normalizedCurrency = ValidateCurrency(defaultCurrency);

        return new Tenant(id, createdAt, normalizedName, normalizedSlug, normalizedTimeZone, normalizedCurrency);
    }

    public void UpdateProfile(string name, string timeZone, string defaultCurrency, DateTimeOffset now)
    {
        Name = ValidateName(name);
        TimeZone = ValidateTimeZone(timeZone);
        DefaultCurrency = ValidateCurrency(defaultCurrency);
        MarkUpdated(now);
    }

    // --- Lifecycle -----------------------------------------------------

    public void Activate(DateTimeOffset now)
    {
        if (Status is not (TenantStatus.Pending or TenantStatus.Suspended))
        {
            throw DomainException.Conflict($"A tenant in status '{Status}' cannot be activated.");
        }

        Status = TenantStatus.Active;
        MarkUpdated(now);
    }

    public void Suspend(DateTimeOffset now)
    {
        if (Status != TenantStatus.Active)
        {
            throw DomainException.Conflict($"A tenant in status '{Status}' cannot be suspended; only an active tenant can be.");
        }

        Status = TenantStatus.Suspended;
        MarkUpdated(now);
    }

    public void Archive(DateTimeOffset now)
    {
        if (Status is not (TenantStatus.Active or TenantStatus.Suspended))
        {
            throw DomainException.Conflict($"A tenant in status '{Status}' cannot be archived.");
        }

        Status = TenantStatus.Archived;
        MarkUpdated(now);
    }

    // --- Domains ---------------------------------------------------------

    public TenantDomain AddDomain(Guid domainId, string rawHost, TenantDomainType type, bool makePrimary, DateTimeOffset now)
    {
        if (Status == TenantStatus.Archived)
        {
            throw DomainException.Conflict("Cannot add a domain to an archived tenant.");
        }

        var normalizedHost = NormalizeHostOrThrow(rawHost);

        if (_domains.Any(d => d.Host == normalizedHost))
        {
            throw DomainException.Conflict($"Host '{normalizedHost}' is already mapped to this tenant.");
        }

        var domain = new TenantDomain(domainId, Id, normalizedHost, type, isPrimary: false, now);
        _domains.Add(domain);

        if (makePrimary)
        {
            SetPrimaryDomain(domainId, now);
        }

        MarkUpdated(now);
        return domain;
    }

    public void RemoveDomain(Guid domainId, DateTimeOffset now)
    {
        var domain = GetDomainOrThrow(domainId);
        _domains.Remove(domain);
        MarkUpdated(now);
    }

    /// <summary>
    /// Promotes the given domain to primary for its <see cref="TenantDomainType"/>,
    /// demoting any previous primary domain of the same type. This is the only
    /// sanctioned way to change a tenant's primary domain.
    /// </summary>
    public void SetPrimaryDomain(Guid domainId, DateTimeOffset now)
    {
        var domain = GetDomainOrThrow(domainId);

        foreach (var other in _domains.Where(d => d.Type == domain.Type && d.Id != domain.Id && d.IsPrimary))
        {
            other.ClearPrimary();
        }

        domain.MarkPrimary();
        MarkUpdated(now);
    }

    public TenantDomain? GetPrimaryDomain(TenantDomainType type = TenantDomainType.Primary) =>
        _domains.FirstOrDefault(d => d.Type == type && d.IsPrimary);

    private TenantDomain GetDomainOrThrow(Guid domainId) =>
        _domains.FirstOrDefault(d => d.Id == domainId)
            ?? throw DomainException.Conflict($"Domain '{domainId}' does not belong to this tenant.");

    private static string NormalizeHostOrThrow(string rawHost)
    {
        try
        {
            return DomainHostFormat.Normalize(rawHost);
        }
        catch (FormatException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
    }

    // --- Validation --------------------------------------------------------

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Tenant name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 200)
        {
            throw DomainException.Validation("Tenant name cannot exceed 200 characters.");
        }

        return trimmed;
    }

    private static string ValidateSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw DomainException.Validation("Tenant slug is required.");
        }

        var normalized = SlugFormat.Normalize(slug);
        if (!SlugFormat.IsValid(normalized))
        {
            throw DomainException.Validation(
                "Tenant slug must be lowercase alphanumeric segments separated by single hyphens (e.g. 'acme-co').");
        }

        return normalized;
    }

    private static string ValidateTimeZone(string timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            throw DomainException.Validation("Tenant time zone is required.");
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw DomainException.Validation($"'{timeZone}' is not a recognized time zone identifier.");
        }

        return timeZone;
    }

    private static string ValidateCurrency(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw DomainException.Validation("Tenant default currency is required.");
        }

        var normalized = currencyCode.Trim().ToUpperInvariant();
        if (!CurrencyCodePattern().IsMatch(normalized))
        {
            throw DomainException.Validation("Tenant default currency must be a 3-letter ISO 4217 code (e.g. 'USD').");
        }

        return normalized;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCodePattern();
}
