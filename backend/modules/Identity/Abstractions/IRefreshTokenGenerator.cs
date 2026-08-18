namespace Identity.Abstractions;

public sealed record GeneratedRefreshToken(string RawToken, string TokenHash);

/// <summary>
/// Generates opaque, cryptographically random refresh tokens and hashes them for
/// storage. The raw value is only ever available at issuance time — the database
/// stores <see cref="GeneratedRefreshToken.TokenHash"/> exclusively.
/// </summary>
public interface IRefreshTokenGenerator
{
    GeneratedRefreshToken Generate();

    string Hash(string rawToken);
}
