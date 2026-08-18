namespace Knight.Infrastructure.Storage;

/// <summary>
/// Bound from configuration (section "Storage"). <see cref="LocalRootPath"/> is used
/// only by the development-time <see cref="LocalFileObjectStorage"/>; production
/// configuration is expected to target an S3-compatible provider.
/// </summary>
public sealed class ObjectStorageOptions
{
    public const string SectionName = "Storage";

    public string LocalRootPath { get; init; } = "storage-data";
}
