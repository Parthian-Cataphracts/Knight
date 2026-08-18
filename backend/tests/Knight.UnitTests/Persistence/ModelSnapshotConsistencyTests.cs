using Microsoft.EntityFrameworkCore;
using Knight.Infrastructure.Persistence;

namespace Knight.UnitTests.Persistence;

/// <summary>
/// Guards the EF model against drifting away from the migrations that build the
/// PostgreSQL schema.
///
/// Without this, drift only surfaces when an integration test boots the API against
/// a real database: EF raises <c>PendingModelChangesWarning</c> during
/// <c>MigrateAsync</c>, which fails every PostgreSQL-backed test at once and buries
/// the single root cause under hundreds of identical stack traces. Worse, drift can
/// mean the shipped migration created a column the model disagrees with — for
/// example a NOT NULL column that the aggregate never populates, which only fails
/// once someone actually inserts a row.
///
/// This check needs no database or container: it compares the model built from the
/// entity configurations against the committed model snapshot in memory.
/// </summary>
public sealed class ModelSnapshotConsistencyTests
{
    [Fact]
    public void EfModel_HasNoPendingChanges_AgainstCommittedMigrations()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The EF model no longer matches the committed migrations/ModelSnapshot. " +
            "Add a corrective migration (do not edit historical migrations) so the " +
            "PostgreSQL schema and the model agree.");
    }
}
