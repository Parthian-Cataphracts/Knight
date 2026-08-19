namespace Knight.Api.Ingest;

/// <summary>
/// The policy every authenticated ingestion endpoint sits behind.
///
/// It requires the <c>store</c> principal type and nothing else. That is the
/// whole authorization model for a store: there is no permission a store token
/// can carry, and no dashboard endpoint it can reach, because the dashboard
/// policies require the <c>user</c> type and reject it before any handler runs
/// (docs/authentication.md §4).
/// </summary>
public static class StoreAuthorization
{
    public const string Policy = "StorePrincipal";
}
