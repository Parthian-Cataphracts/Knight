using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent;

/// <summary>
/// The credential this store connects to KNIGHT with, wherever it came from.
///
/// <c>Enabled</c> is here rather than only in configuration because connecting a
/// store is an act somebody performs, and until now it was a deploy: the
/// credential went into an environment variable and the shop was restarted. A
/// merchant with a panel in front of them cannot restart a container, and asking
/// them to send the client secret to whoever can is worse than either.
/// </summary>
public sealed record KnightCredential
{
    public string BaseUrl { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    /// <summary>Never logged, never returned by any endpoint, never in an error message.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    public string Environment { get; init; } = "Production";

    public bool Enabled { get; init; }

    /// <summary>Whether there is enough here to try at all.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// Where a credential entered through a panel is kept.
///
/// An interface because a store that already has a settings table, encrypted at
/// rest, should use it. The default writes a file beside the feature registry,
/// so the library works in a store that has neither.
/// </summary>
public interface IKnightCredentialStore
{
    Task<KnightCredential?> ReadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(KnightCredential credential, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The default store: one file, beside the registry, readable only by this user.
///
/// The same place the delivered configuration secrets live, and for the same
/// reason — it must survive a restart, and a container that mounts nothing there
/// loses everything KNIGHT ever delivered.
/// </summary>
public sealed class FileKnightCredentialStore(IOptions<KnightOptions> options) : IKnightCredentialStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path = Path.Combine(options.Value.FeatureRoot, "knight-credential.json");

    public async Task<KnightCredential?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<KnightCredential>(
                await File.ReadAllTextAsync(_path, cancellationToken), Json);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // Unreadable is treated as absent rather than fatal. A store that
            // refused to start because a credential file was truncated would be
            // a shop that is down for a reason unrelated to selling anything.
            return null;
        }
    }

    public async Task SaveAsync(KnightCredential credential, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(credential, Json), cancellationToken);
            File.Move(temporary, _path, overwrite: true);

            Restrict(_path);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows inherits the directory's ACL, and narrowing it here would
            // be a per-file exception nobody maintains.
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Some mounts do not support it. The file still holds a secret, and
            // saying so is the host's business rather than a reason to refuse.
        }
    }
}

/// <summary>
/// The credential in force, and the one place anything asks for it.
///
/// Configuration is the floor and the stored credential is the answer: an
/// operator who connected a store through its panel has said something more
/// recent than whatever was in the environment when the container started. The
/// exception is <c>Knight:Enabled</c> — configuration may turn the agent on, and
/// so may the panel, because a store deployed with a credential should not need
/// somebody to press a button as well.
///
/// Read per use, never cached for the life of the process. Connecting a store is
/// meant to take effect now, and a value captured at start-up would mean a
/// restart — which is the thing this exists to remove.
/// </summary>
public sealed class KnightConnection(IOptions<KnightOptions> options, IKnightCredentialStore store)
{
    private readonly KnightOptions _options = options.Value;

    public async Task<KnightCredential> CurrentAsync(CancellationToken cancellationToken = default)
    {
        var stored = await store.ReadAsync(cancellationToken);

        if (stored is null || !stored.IsComplete)
        {
            return new KnightCredential
            {
                BaseUrl = _options.BaseUrl,
                ClientId = _options.ClientId,
                ClientSecret = _options.ClientSecret,
                Environment = _options.Environment,
                Enabled = _options.Enabled,
            };
        }

        return stored with
        {
            BaseUrl = string.IsNullOrWhiteSpace(stored.BaseUrl) ? _options.BaseUrl : stored.BaseUrl,
            Environment = string.IsNullOrWhiteSpace(stored.Environment) ? _options.Environment : stored.Environment,
            Enabled = stored.Enabled || _options.Enabled,
        };
    }

    /// <summary>
    /// Records a credential an operator entered, and turns the agent on.
    ///
    /// The secret is taken as given and never checked here. Whether it works is
    /// answered by the handshake, which is the only thing that can answer it,
    /// and a panel that pretended to validate a credential locally would be
    /// telling a merchant something it cannot know.
    /// </summary>
    public Task ConnectAsync(KnightCredential credential, CancellationToken cancellationToken = default) =>
        store.SaveAsync(credential with { Enabled = true }, cancellationToken);

    /// <summary>
    /// Stops this store talking to KNIGHT, and forgets the credential.
    ///
    /// The Features it has already taken delivery of stay installed and stay
    /// serving. Disconnecting is not uninstalling: what a merchant means by it
    /// is "stop talking to them", and deleting a shop's Features because
    /// somebody pressed the wrong button would be unrecoverable from here.
    /// </summary>
    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        store.ClearAsync(cancellationToken);
}
