using AccessControl.Abstractions;
using AccessControl.Domain;
using Customers.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Domain.Exceptions;
using Microsoft.Extensions.Options;

namespace Onboarding;

/// <summary>
/// Self-service registration and email verification.
///
/// The invariants live here rather than in the endpoint. Registering never
/// tells the caller whether an address is already taken: a taken address returns
/// the same "check your email" as a fresh one, and no account is created or
/// mail sent for it. A new account is created unverified — it holds its password
/// but cannot sign in — until its holder follows the emailed link, so an address
/// nobody controls can never become a usable account.
///
/// The customer is created in <see cref="CustomerStatus.Prospect"/> and stays
/// there: verifying an email lets the owner sign in and choose a plan; it is
/// paying (a later phase) that makes the customer operable.
/// </summary>
internal sealed class OnboardingService : IOnboardingService
{
    private readonly ICustomerRepository _customers;
    private readonly IControlPlaneUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IControlPlanePasswordHasher _passwords;
    private readonly ISecureTokenFactory _tokens;
    private readonly IVerificationEmailSender _verification;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly OnboardingOptions _options;

    public OnboardingService(
        ICustomerRepository customers,
        IControlPlaneUserRepository users,
        IRoleRepository roles,
        IControlPlanePasswordHasher passwords,
        ISecureTokenFactory tokens,
        IVerificationEmailSender verification,
        IAuditTrail audit,
        IDateTimeProvider clock,
        IOptions<OnboardingOptions> options)
    {
        _customers = customers;
        _users = users;
        _roles = roles;
        _passwords = passwords;
        _tokens = tokens;
        _verification = verification;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
    }

    public async Task RegisterAsync(string email, string password, string name, string? companyName, CancellationToken cancellationToken)
    {
        var address = (email ?? string.Empty).Trim();
        var displayName = (name ?? string.Empty).Trim();

        var errors = new Dictionary<string, string[]>();
        if (address.Length == 0)
        {
            errors["email"] = ["An email address is required."];
        }

        if (displayName.Length == 0)
        {
            errors["name"] = ["A name is required."];
        }

        if (string.IsNullOrEmpty(password) || password.Length < _options.MinPasswordLength || password.Length > 128)
        {
            errors["password"] = [$"A password must be between {_options.MinPasswordLength} and 128 characters."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        // A malformed address is a validation error, not a lookup: normalising it
        // for the duplicate check would otherwise throw further in with a worse
        // message.
        string normalized;
        try
        {
            normalized = EmailAddress.NormalizeForComparison(address);
        }
        catch (DomainException)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["email"] = ["That is not a valid email address."],
            });
        }

        // The duplicate check is deliberately unfiltered and silent: an account
        // already exists is indistinguishable to the caller from a fresh sign-up.
        // Nothing is created and no mail is sent, so a taken address is not even a
        // timing signal beyond a single lookup.
        if (await _users.FindForAuthenticationAsync(normalized, cancellationToken) is not null)
        {
            return;
        }

        var now = _clock.UtcNow;

        var customer = Customer.Create(
            Guid.NewGuid(),
            now,
            string.IsNullOrWhiteSpace(companyName) ? displayName : companyName.Trim(),
            address);
        await _customers.AddAsync(customer, cancellationToken);

        var user = ControlPlaneUser.CreateCustomerUser(
            Guid.NewGuid(),
            now,
            customer.Id,
            address,
            displayName,
            _passwords.Hash(password));

        var token = _tokens.Generate();
        user.BeginEmailVerification(token.Hash, now.Add(_options.EmailVerificationLifetime), now);
        await _users.AddAsync(user, cancellationToken);

        var role = await _roles.GetByNameAsync(
            SystemRoles.CustomerOwner.ToUpperInvariant(),
            RoleScope.Customer,
            customerId: null,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The CustomerOwner system role is not seeded; the access seeder must run before self-service registration.");

        _users.RegisterNewAssignment(user.AssignRole(Guid.NewGuid(), role, now));

        // One unit of work: the customer, the account and its role are inserted
        // together, so a half-made customer with no way in can never be observed.
        await _users.SaveChangesAsync(cancellationToken);

        // The token's plaintext exists only in the link. When mail cannot leave
        // this deployment the account still exists and can be verified through
        // the API with the token an operator reads from the register response's
        // out-of-band channel — never from a log.
        await _verification.SendAsync(user.Email, user.DisplayName, token.RawValue, cancellationToken);

        await _audit.RecordAsync("auth.register", nameof(ControlPlaneUser), user.Id.ToString(), customer.Id, cancellationToken);
    }

    public async Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var user = await _users.FindByActivationTokenAsync(_tokens.Hash(token), cancellationToken);
        if (user is null)
        {
            return false;
        }

        var now = _clock.UtcNow;
        try
        {
            user.ConfirmEmailVerification(now);
        }
        catch (DomainException)
        {
            // An expired or already-consumed token is simply not a verification.
            return false;
        }

        await _users.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("auth.email_verified", nameof(ControlPlaneUser), user.Id.ToString(), user.CustomerId, cancellationToken);
        return true;
    }

    public async Task ResendVerificationAsync(string email, CancellationToken cancellationToken)
    {
        var address = (email ?? string.Empty).Trim();
        if (address.Length == 0)
        {
            return;
        }

        string normalized;
        try
        {
            normalized = EmailAddress.NormalizeForComparison(address);
        }
        catch (DomainException)
        {
            return;
        }

        var user = await _users.FindForAuthenticationAsync(normalized, cancellationToken);

        // Silent whether or not there is anything to resend: an unknown address, a
        // verified account and a platform account all look the same from outside.
        if (user is null || user.EmailVerified || user.CustomerId is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        var token = _tokens.Generate();
        user.BeginEmailVerification(token.Hash, now.Add(_options.EmailVerificationLifetime), now);
        await _users.SaveChangesAsync(cancellationToken);

        await _verification.SendAsync(user.Email, user.DisplayName, token.RawValue, cancellationToken);
    }
}
