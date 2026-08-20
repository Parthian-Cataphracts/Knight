namespace Stores.Domain;

/// <summary>
/// Rule keys the store side raises alerts under.
///
/// Constants rather than literals because the key is what deduplicates an alert,
/// what an operator filters on, and what a notification rule routes by. Two
/// spellings of the same condition would show up as two unrelated problems, and
/// the second one would be the one nobody has a rule for.
/// </summary>
public static class StoreAlertRules
{
    /// <summary>A store reported that a backup failed.</summary>
    public const string BackupFailed = "backup.failed";

    /// <summary>
    /// No successful backup has been reported for longer than the configured
    /// window. Separate from <see cref="BackupFailed"/> on purpose: a failing
    /// backup job is loud, and a backup job that quietly stopped running is the
    /// one that costs somebody their data.
    /// </summary>
    public const string BackupOverdue = "backup.overdue";
}
