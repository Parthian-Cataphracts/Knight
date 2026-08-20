using Knight.Domain.Exceptions;
using Stores.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Backup reports.
///
/// The invariant worth having a test for is the one that refuses a "successful"
/// backup of nothing. A backup job whose output is zero bytes is the classic
/// silent failure, and a control plane that records it as green is worse than one
/// that records nothing at all.
/// </summary>
public sealed class StoreBackupTests
{
    private static readonly DateTimeOffset Started = new(2026, 8, 20, 2, 0, 0, TimeSpan.Zero);

    private static StoreBackup Backup(
        BackupStatus status = BackupStatus.Succeeded,
        long? sizeBytes = 1_048_576,
        DateTimeOffset? completedAt = null) =>
        StoreBackup.Record(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            status,
            BackupKind.Scheduled,
            Started,
            completedAt ?? Started.AddMinutes(4),
            Started.AddMinutes(5),
            sizeBytes,
            "s3://knight-backups/acme/2026-08-20.dump");

    [Fact]
    public void ASuccessfulBackup_RecordsItsDuration()
    {
        var backup = Backup();

        Assert.Equal(TimeSpan.FromMinutes(4), backup.Duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void ASuccessfulBackupThatProducedNothing_IsRefused(long? sizeBytes)
    {
        var refusal = Assert.Throws<DomainException>(() => Backup(sizeBytes: sizeBytes));

        Assert.Contains("must report the size", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedBackup_NeedsNoSize()
    {
        var backup = StoreBackup.Record(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            BackupStatus.Failed,
            BackupKind.Scheduled,
            Started,
            Started.AddSeconds(30),
            Started.AddMinutes(1),
            detail: "pg_dump exited 1: no space left on device");

        Assert.Equal(BackupStatus.Failed, backup.Status);
        Assert.Null(backup.SizeBytes);
    }

    [Fact]
    public void ABackupCannotFinishBeforeItStarted()
    {
        Assert.Throws<DomainException>(() => Backup(completedAt: Started.AddMinutes(-1)));
    }

    [Fact]
    public void ARunningBackup_HasNoDurationYet()
    {
        var backup = StoreBackup.Record(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            BackupStatus.Running,
            BackupKind.PreDeployment,
            Started,
            null,
            Started);

        Assert.Null(backup.Duration);
        Assert.Null(backup.CompletedAt);
    }
}
