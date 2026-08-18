namespace Knight.Infrastructure.Storage;

/// <summary>
/// Development-only <see cref="IObjectStorage"/> backed by the local filesystem.
/// Production deployments must configure an S3-compatible implementation instead —
/// see docs/architecture/platform-overview.md.
/// </summary>
public sealed class LocalFileObjectStorage : IObjectStorage
{
    private readonly string _rootPath;

    public LocalFileObjectStorage(string rootPath)
    {
        _rootPath = rootPath;
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<StoredObjectReference> PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var fileStream = File.Create(path);
        await content.CopyToAsync(fileStream, cancellationToken);

        return new StoredObjectReference(key, new Uri(path).AbsoluteUri);
    }

    public Task<Stream?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        Stream? stream = File.Exists(path) ? File.OpenRead(path) : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string ResolvePath(string key)
    {
        var normalized = key.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, normalized));

        if (!fullPath.StartsWith(_rootPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved storage path escapes the configured storage root.");
        }

        return fullPath;
    }
}
