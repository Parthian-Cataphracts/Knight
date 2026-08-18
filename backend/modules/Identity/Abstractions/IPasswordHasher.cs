namespace Identity.Abstractions;

/// <summary>
/// Hashes and verifies passwords. Implemented in Infrastructure using a modern,
/// salted key-derivation function. Plaintext passwords must never be persisted or logged.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string plaintextPassword);

    bool Verify(string plaintextPassword, string passwordHash);

    /// <summary>
    /// True when <paramref name="passwordHash"/> was produced with a weaker work
    /// factor than the hasher's current target — callers should transparently
    /// rehash on the next successful verification.
    /// </summary>
    bool NeedsRehash(string passwordHash);
}
