using System.Text.Json;
using Knight.Domain.Versioning;

namespace FeatureRegistry.Domain;

/// <summary>
/// Turns manifest JSON into a <see cref="FeatureManifest"/>, collecting every
/// problem rather than throwing on the first.
///
/// Collecting is the point. A publish is a batch operation run from a pipeline,
/// and an author who has to fix one field per failed run learns to hate the
/// registry. Every error carries the JSON path of the field it is about, so the
/// dashboard's manifest validator can point at the line.
///
/// The reader is intentionally hand-written rather than driven by a serializer.
/// Manifest fields are not symmetrical with the .NET type — version ranges are
/// strings that must parse, enums are kebab-case, defaults are an arbitrary
/// document — and every one of those conversions is a place a validation message
/// is owed to the author.
/// </summary>
internal sealed class ManifestReader
{
    private readonly List<ManifestError> _errors = [];

    public IReadOnlyList<ManifestError> Errors => _errors;

    public FeatureManifest? Read(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            Fail("$", "The manifest must be a JSON object.");
            return null;
        }

        var apiVersion = RequireString(root, "apiVersion");
        if (apiVersion is not null && apiVersion != FeatureManifest.SupportedApiVersion)
        {
            Fail(
                "$.apiVersion",
                $"Unsupported manifest API version '{apiVersion}'. This registry understands '{FeatureManifest.SupportedApiVersion}'.");
        }

        var slug = RequireString(root, "slug");
        if (slug is not null && !FeatureSlug.IsValid(slug))
        {
            Fail("$.slug", $"'{slug}' is not a valid feature slug.");
            slug = null;
        }

        var version = RequireVersion(root, "version");
        var name = RequireString(root, "name");
        var description = OptionalString(root, "description");

        // Before the runtime, because it decides whether there is one. An
        // external service is code the store never loads, so it has no runtime
        // and no migrations and no install strategy, and asking it for them
        // would be asking about a package that does not exist (adr/0033).
        var architecture = ReadArchitecture(root);
        var external = architecture is FeatureArchitecture.ExternalService ? ReadExternalService(root) : null;

        if (architecture is FeatureArchitecture.ExternalService)
        {
            RefuseInProcessBlocks(root);
        }

        // First among the in-process readers, because three of them validate a
        // string differently depending on it: what counts as a callable is a
        // Python dotted path in a Django Feature and a module-and-export in a
        // node one, and validating the wrong one at publish is how an author is
        // told their correct manifest is wrong (adr/0032).
        var runtime = architecture is FeatureArchitecture.InProcess ? ReadRuntimeName(root) : FeatureRuntime.Django;
        var integration = architecture is FeatureArchitecture.InProcess ? ReadRuntimeIntegration(root, runtime) : null;
        var compatibility = ReadCompatibility(root);
        var dependencies = architecture is FeatureArchitecture.InProcess ? ReadDependencies(root) : ManifestDependencies.None;
        var migrations = architecture is FeatureArchitecture.InProcess ? ReadMigrations(root, compatibility) : MigrationPolicy.None;
        var configuration = ReadConfiguration(root);
        var install = architecture is FeatureArchitecture.InProcess ? ReadInstall(root, runtime) : InstallPolicy.External;
        var uninstall = ReadUninstall(root);
        var workers = architecture is FeatureArchitecture.InProcess ? ReadWorkers(root, runtime) : [];

        if (_errors.Count > 0)
        {
            return null;
        }

        return new FeatureManifest(
            apiVersion!,
            FeatureSlug.Normalize(slug!),
            version!,
            name!,
            description,
            integration!,
            compatibility,
            dependencies,
            migrations,
            configuration,
            install!,
            uninstall,
            workers,
            architecture,
            external);
    }

    /// <summary>
    /// Which runtime this Feature is built for.
    ///
    /// Absent means Django. Thirteen manifests were written before this field
    /// existed and every one of them is a Django Feature; refusing them would be
    /// breaking a published contract in order to add a field whose value they all
    /// imply (adr/0032 §1).
    /// </summary>
    /// <summary>
    /// Whether this Feature is code the store runs or a service it talks to.
    ///
    /// Absent means <c>in_process</c>, because every manifest written before
    /// this field existed means that, and re-issuing sixteen of them to say so
    /// would be churn that proves nothing.
    /// </summary>
    private FeatureArchitecture ReadArchitecture(JsonElement root)
    {
        var declared = OptionalString(root, "architecture");

        if (declared is null)
        {
            return FeatureArchitecture.InProcess;
        }

        return declared switch
        {
            "in_process" => FeatureArchitecture.InProcess,
            "external_service" => FeatureArchitecture.ExternalService,
            _ => FailArchitecture(declared),
        };
    }

    private FeatureArchitecture FailArchitecture(string declared)
    {
        Fail(
            "$.architecture",
            $"'{declared}' is not an architecture KNIGHT can deliver. Use one of: in_process, external_service.");

        return FeatureArchitecture.InProcess;
    }

    /// <summary>
    /// Refuses the blocks that only mean something for code the store runs.
    ///
    /// The same rule, and the same reason, as a Django Feature carrying a
    /// <c>node:</c> block: it is an author who has copied a manifest, and it is
    /// cheaper to say so at publish than to deliver something the store will
    /// half-read. A <c>migrations:</c> block on a Feature with no database
    /// access is the most dangerous of these, because it reads like a promise
    /// that something will be migrated.
    /// </summary>
    private void RefuseInProcessBlocks(JsonElement root)
    {
        foreach (var name in new[] { "django", "node", "dotnet", "runtime", "migrations", "install", "dependencies", "workers" })
        {
            if (root.TryGetProperty(name, out _))
            {
                Fail(
                    $"$.{name}",
                    $"This Feature is an external service, so it must not carry a '{name}' block: the store runs none of its code.");
            }
        }
    }

    /// <summary>
    /// The service, its events, its routes and its screens.
    ///
    /// Validated as hard as the in-process blocks are, and for the same reason:
    /// every one of these ends up as a route a shopper can reach or a request a
    /// store makes on its own behalf. A prefix that collides with the store's
    /// own API, a method list containing DELETE by accident, an http origin
    /// carrying customer data - all of them are cheaper to refuse here than to
    /// find in production.
    /// </summary>
    private ExternalServiceContract? ReadExternalService(JsonElement root)
    {
        if (!TryGetObject(root, "service", out var service))
        {
            Fail("$.service", "An external Feature must say where its service is.");
            return null;
        }

        var endpoint = ReadServiceEndpoint(service);
        var webhooks = ReadWebhooks(root);
        var proxies = ReadApiProxies(root);
        var mounts = ReadUiMounts(root);

        if (webhooks.Count == 0 && proxies.Count == 0 && mounts.Count == 0)
        {
            // A Feature that subscribes to nothing, serves nothing and shows
            // nothing is a Feature a store would install and never notice. It is
            // almost certainly a manifest somebody has not finished.
            Fail(
                "$",
                "An external Feature must declare at least one of 'webhooks', 'api_proxies' or 'ui_mounts'; " +
                "otherwise installing it does nothing at all.");
        }

        return endpoint is null ? null : new ExternalServiceContract(endpoint, webhooks, proxies, mounts);
    }

    private ServiceEndpoint? ReadServiceEndpoint(JsonElement service)
    {
        var baseUrl = RequireString(service, "base_url", "$.service.base_url");
        Uri? parsed = null;

        if (baseUrl is not null && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out parsed)
            || parsed.Scheme is not ("http" or "https")))
        {
            Fail("$.service.base_url", $"'{baseUrl}' is not an absolute http or https URL.");
            parsed = null;
        }

        var authentication = ServiceAuthentication.HmacSha256;
        var declaredAuth = OptionalString(service, "auth");

        if (declaredAuth is not null)
        {
            authentication = declaredAuth switch
            {
                "hmac-sha256" => ServiceAuthentication.HmacSha256,
                "bearer-token" => ServiceAuthentication.BearerToken,
                _ => FailAuthentication(declaredAuth),
            };
        }

        var health = OptionalString(service, "health") ?? "/health";

        if (!health.StartsWith('/'))
        {
            Fail("$.service.health", $"'{health}' must be a path beginning with '/'.");
        }

        // A name, never a value. This manifest is public, signed and kept in a
        // catalogue; a secret in it is a secret in every copy of it for ever.
        var secretName = OptionalString(service, "secret") ?? "KNIGHT_SERVICE_SECRET";

        if (!IsPythonIdentifier(secretName.Replace("-", "_")))
        {
            Fail("$.service.secret", $"'{secretName}' is not a valid secret name.");
        }

        return parsed is null ? null : new ServiceEndpoint(parsed, authentication, health, secretName);
    }

    private ServiceAuthentication FailAuthentication(string declared)
    {
        Fail("$.service.auth", $"'{declared}' is not an authentication KNIGHT knows. Use one of: hmac-sha256, bearer-token.");
        return ServiceAuthentication.HmacSha256;
    }

    private IReadOnlyList<WebhookSubscription> ReadWebhooks(JsonElement root)
    {
        if (!root.TryGetProperty("webhooks", out var list))
        {
            return [];
        }

        if (list.ValueKind is not JsonValueKind.Array)
        {
            Fail("$.webhooks", "'webhooks' must be a list.");
            return [];
        }

        var subscriptions = new List<WebhookSubscription>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var entry in list.EnumerateArray())
        {
            var path = $"$.webhooks[{index++}]";

            if (entry.ValueKind is not JsonValueKind.Object)
            {
                Fail(path, "Each webhook must be an object.");
                continue;
            }

            var name = RequireString(entry, "event", $"{path}.event");
            var target = RequireString(entry, "path", $"{path}.path");

            if (name is not null && !IsEventName(name))
            {
                Fail($"{path}.event", $"'{name}' is not a valid event name. Use dotted lower-case names such as 'order.placed'.");
            }

            // Two subscriptions to one event would have the store deliver it
            // twice to the same service, which looks to the service exactly like
            // a retry and is not one.
            if (name is not null && !seen.Add(name))
            {
                Fail($"{path}.event", $"'{name}' is subscribed to twice.");
            }

            if (target is not null && !target.StartsWith('/'))
            {
                Fail($"{path}.path", $"'{target}' must be a path beginning with '/'.");
            }

            var delivery = WebhookDelivery.AtLeastOnce;
            var declared = OptionalString(entry, "delivery");

            if (declared is not null)
            {
                delivery = declared switch
                {
                    "at-least-once" => WebhookDelivery.AtLeastOnce,
                    "at-most-once" => WebhookDelivery.AtMostOnce,
                    _ => FailDelivery($"{path}.delivery", declared),
                };
            }

            if (name is not null && target is not null)
            {
                subscriptions.Add(new WebhookSubscription(name, target, delivery));
            }
        }

        return subscriptions;
    }

    private WebhookDelivery FailDelivery(string path, string declared)
    {
        Fail(path, $"'{declared}' is not a delivery KNIGHT knows. Use one of: at-least-once, at-most-once.");
        return WebhookDelivery.AtLeastOnce;
    }

    /// <summary>Methods a store will forward. Closed, and deliberately not including TRACE or CONNECT.</summary>
    private static readonly string[] ProxyableMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    private IReadOnlyList<ApiProxyRoute> ReadApiProxies(JsonElement root)
    {
        if (!root.TryGetProperty("api_proxies", out var list))
        {
            return [];
        }

        if (list.ValueKind is not JsonValueKind.Array)
        {
            Fail("$.api_proxies", "'api_proxies' must be a list.");
            return [];
        }

        var routes = new List<ApiProxyRoute>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var entry in list.EnumerateArray())
        {
            var path = $"$.api_proxies[{index++}]";

            if (entry.ValueKind is not JsonValueKind.Object)
            {
                Fail(path, "Each proxy route must be an object.");
                continue;
            }

            var prefix = RequireString(entry, "prefix", $"{path}.prefix");
            var upstream = OptionalString(entry, "upstream") ?? "/";

            if (prefix is not null && !IsRoutePrefix(prefix))
            {
                Fail(
                    $"{path}.prefix",
                    $"'{prefix}' is not a valid route prefix. Use lower-case segments ending in '/', such as 'subscriptions/'.");
            }

            if (prefix is not null && !seen.Add(prefix))
            {
                Fail($"{path}.prefix", $"'{prefix}' is claimed twice.");
            }

            if (!upstream.StartsWith('/'))
            {
                Fail($"{path}.upstream", $"'{upstream}' must be a path beginning with '/'.");
            }

            var methods = ReadMethods(entry, path);
            var identity = ProxyIdentity.Anonymous;
            var declared = OptionalString(entry, "identity");

            if (declared is not null)
            {
                identity = declared switch
                {
                    "anonymous" => ProxyIdentity.Anonymous,
                    "customer" => ProxyIdentity.Customer,
                    "staff" => ProxyIdentity.Staff,
                    _ => FailIdentity($"{path}.identity", declared),
                };
            }

            if (prefix is not null)
            {
                routes.Add(new ApiProxyRoute(prefix, upstream, methods, identity));
            }
        }

        return routes;
    }

    private IReadOnlyList<string> ReadMethods(JsonElement entry, string path)
    {
        if (!entry.TryGetProperty("methods", out var list))
        {
            // Read-only by default. A route that acquires a DELETE because
            // nobody wrote a list is the failure this default exists to avoid.
            return ["GET"];
        }

        if (list.ValueKind is not JsonValueKind.Array)
        {
            Fail($"{path}.methods", "'methods' must be a list.");
            return ["GET"];
        }

        var methods = new List<string>();

        foreach (var value in list.EnumerateArray())
        {
            if (value.ValueKind is not JsonValueKind.String)
            {
                Fail($"{path}.methods", "Each method must be a string.");
                continue;
            }

            var method = value.GetString()!.ToUpperInvariant();

            if (!ProxyableMethods.Contains(method))
            {
                Fail($"{path}.methods", $"'{method}' is not a method a store will forward. Use one of: {string.Join(", ", ProxyableMethods)}.");
                continue;
            }

            methods.Add(method);
        }

        return methods.Count == 0 ? ["GET"] : methods;
    }

    private ProxyIdentity FailIdentity(string path, string declared)
    {
        Fail(path, $"'{declared}' is not an identity KNIGHT knows. Use one of: anonymous, customer, staff.");
        return ProxyIdentity.Anonymous;
    }

    private IReadOnlyList<UiMount> ReadUiMounts(JsonElement root)
    {
        if (!root.TryGetProperty("ui_mounts", out var list))
        {
            return [];
        }

        if (list.ValueKind is not JsonValueKind.Array)
        {
            Fail("$.ui_mounts", "'ui_mounts' must be a list.");
            return [];
        }

        var mounts = new List<UiMount>();
        var index = 0;

        foreach (var entry in list.EnumerateArray())
        {
            var path = $"$.ui_mounts[{index++}]";

            if (entry.ValueKind is not JsonValueKind.Object)
            {
                Fail(path, "Each UI mount must be an object.");
                continue;
            }

            var slot = RequireString(entry, "slot", $"{path}.slot");
            var label = RequireString(entry, "label", $"{path}.label");
            var target = RequireString(entry, "path", $"{path}.path");

            if (slot is not null && !IsEventName(slot))
            {
                Fail($"{path}.slot", $"'{slot}' is not a valid slot. Use dotted lower-case names such as 'admin.sidebar'.");
            }

            if (target is not null && !target.StartsWith('/'))
            {
                Fail($"{path}.path", $"'{target}' must be a path beginning with '/'.");
            }

            var kind = UiMountKind.Iframe;
            var declared = OptionalString(entry, "kind");

            if (declared is not null)
            {
                kind = declared switch
                {
                    "iframe" => UiMountKind.Iframe,
                    "redirect" => UiMountKind.Redirect,
                    _ => FailMountKind($"{path}.kind", declared),
                };
            }

            if (slot is not null && label is not null && target is not null)
            {
                mounts.Add(new UiMount(slot, label, target, kind));
            }
        }

        return mounts;
    }

    private UiMountKind FailMountKind(string path, string declared)
    {
        Fail(path, $"'{declared}' is not a mount kind KNIGHT knows. Use one of: iframe, redirect.");
        return UiMountKind.Iframe;
    }

    /// <summary>A dotted lower-case name: <c>order.placed</c>, <c>admin.sidebar</c>.</summary>
    private static bool IsEventName(string value) =>
        value.Length is > 0 and <= 100
        && value == value.ToLowerInvariant()
        && value.Split('.').All(segment =>
            segment.Length > 0
            && char.IsAsciiLetterLower(segment[0])
            && segment.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '_'));

    /// <summary>
    /// A route prefix: lower-case segments, a trailing slash, no traversal.
    ///
    /// It becomes a route in the store's own URL space, so a prefix containing
    /// <c>..</c> or a leading slash would be a Feature claiming somewhere it was
    /// not given.
    /// </summary>
    private static bool IsRoutePrefix(string value) =>
        value.Length is > 1 and <= 200
        && value.EndsWith('/')
        && !value.StartsWith('/')
        && !value.Contains("..", StringComparison.Ordinal)
        && value.TrimEnd('/').Split('/').All(segment =>
            segment.Length > 0
            && char.IsAsciiLetterLower(segment[0])
            && segment.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_'));

    private FeatureRuntime ReadRuntimeName(JsonElement root)
    {
        var declared = OptionalString(root, "runtime");

        if (declared is null)
        {
            return FeatureRuntime.Django;
        }

        if (!Enum.TryParse<FeatureRuntime>(declared, ignoreCase: true, out var runtime))
        {
            Fail(
                "$.runtime",
                $"'{declared}' is not a runtime KNIGHT can deliver to. Use one of: {string.Join(", ", Enum.GetNames<FeatureRuntime>()).ToLowerInvariant()}.");

            return FeatureRuntime.Django;
        }

        return runtime;
    }

    /// <summary>
    /// The block named by the runtime, read into the three names that are the
    /// same whatever the runtime is: namespace, module and mount (adr/0032 §3).
    /// </summary>
    private RuntimeIntegration? ReadRuntimeIntegration(JsonElement root, FeatureRuntime runtime)
    {
        var name = runtime.ToString().ToLowerInvariant();

        // Wiring for a runtime this Feature is not built for is an author who has
        // copied a manifest, and it is cheaper to say so at publish than to
        // deliver a package the store cannot load.
        foreach (var other in Enum.GetNames<FeatureRuntime>())
        {
            var key = other.ToLowerInvariant();

            if (key != name && root.TryGetProperty(key, out _))
            {
                Fail($"$.{key}", $"This Feature declares runtime '{name}', so it must not also carry a '{key}' block.");
            }
        }

        return runtime switch
        {
            FeatureRuntime.Node => ReadNode(root),
            FeatureRuntime.Dotnet => ReadDotnet(root),
            _ => ReadDjango(root),
        };
    }

    private RuntimeIntegration? ReadDjango(JsonElement root)
    {
        if (!TryGetObject(root, "django", out var django))
        {
            Fail("$.django", "A Feature must say how it attaches to the store's Django project.");
            return null;
        }

        var appLabel = RequireString(django, "app_label", "$.django.app_label");
        var installedApp = RequireString(django, "installed_app", "$.django.installed_app");

        // Both are Python identifiers that end up in INSTALLED_APPS and in a
        // migration table. A malformed one would not fail until the installer is
        // already halfway through a store's database.
        if (appLabel is not null && !IsPythonIdentifier(appLabel))
        {
            Fail("$.django.app_label", $"'{appLabel}' is not a valid Django app label.");
        }

        if (installedApp is not null && !IsDottedPythonPath(installedApp))
        {
            Fail("$.django.installed_app", $"'{installedApp}' is not a valid Python module path.");
        }

        string? include = null;
        string? prefix = null;

        if (TryGetObject(django, "urls", out var urls))
        {
            include = RequireString(urls, "include", "$.django.urls.include");
            prefix = OptionalString(urls, "prefix");

            if (include is not null && !IsDottedPythonPath(include))
            {
                Fail("$.django.urls.include", $"'{include}' is not a valid Python module path.");
            }
        }

        return _errors.Count > 0
            ? null
            : new RuntimeIntegration(FeatureRuntime.Django, appLabel!, installedApp!, include, prefix);
    }

    private RuntimeIntegration? ReadNode(JsonElement root)
    {
        if (!TryGetObject(root, "node", out var node))
        {
            Fail("$.node", "A Feature must say how it attaches to the store's node application.");
            return null;
        }

        var ns = RequireString(node, "namespace", "$.node.namespace");
        var module = RequireString(node, "module", "$.node.module");

        // The namespace ends up in the store's migration ledger under this exact
        // string, so it is held to the same shape as a Django app label: a name a
        // store can key a table on and a person can read in a log line.
        if (ns is not null && !IsPythonIdentifier(ns))
        {
            Fail("$.node.namespace", $"'{ns}' is not a valid namespace. Use letters, digits and underscores.");
        }

        if (module is not null && !IsNodeSpecifier(module))
        {
            Fail("$.node.module", $"'{module}' is not a valid node module specifier.");
        }

        string? export = null;
        string? prefix = null;

        if (TryGetObject(node, "mount", out var mount))
        {
            export = RequireString(mount, "export", "$.node.mount.export");
            prefix = OptionalString(mount, "prefix");

            if (export is not null && !IsJavaScriptIdentifier(export))
            {
                Fail("$.node.mount.export", $"'{export}' is not a valid exported name.");
            }
        }

        return _errors.Count > 0
            ? null
            : new RuntimeIntegration(FeatureRuntime.Node, ns!, module!, export, prefix);
    }

    /// <summary>
    /// An ASP.NET Core Feature: the same three names, wearing .NET clothes.
    ///
    /// A <c>namespace</c> is what the schema is recorded under, exactly as it is
    /// for the other two. A <c>assembly</c> is what the store loads — the file
    /// name of the delivered assembly, without its extension, because the store
    /// loads it from the directory it was delivered into rather than resolving
    /// it from NuGet. A <c>mount</c> is the type that registers the Feature's
    /// endpoints and the prefix they serve at.
    /// </summary>
    private RuntimeIntegration? ReadDotnet(JsonElement root)
    {
        if (!TryGetObject(root, "dotnet", out var dotnet))
        {
            Fail("$.dotnet", "A Feature must say how it attaches to the store's ASP.NET Core application.");
            return null;
        }

        var ns = RequireString(dotnet, "namespace", "$.dotnet.namespace");
        var assembly = RequireString(dotnet, "assembly", "$.dotnet.assembly");

        // Held to the same shape as the other two namespaces: it ends up in the
        // store's migration ledger under this exact string.
        if (ns is not null && !IsPythonIdentifier(ns))
        {
            Fail("$.dotnet.namespace", $"'{ns}' is not a valid namespace. Use letters, digits and underscores.");
        }

        if (assembly is not null && !IsDotnetAssemblyName(assembly))
        {
            Fail("$.dotnet.assembly", $"'{assembly}' is not a valid assembly name.");
        }

        string? entryPoint = null;
        string? prefix = null;

        if (TryGetObject(dotnet, "mount", out var mount))
        {
            entryPoint = RequireString(mount, "type", "$.dotnet.mount.type");
            prefix = OptionalString(mount, "prefix");

            if (entryPoint is not null && !IsDotnetTypeName(entryPoint))
            {
                Fail("$.dotnet.mount.type", $"'{entryPoint}' is not a valid .NET type name.");
            }
        }

        return _errors.Count > 0
            ? null
            : new RuntimeIntegration(FeatureRuntime.Dotnet, ns!, assembly!, entryPoint, prefix);
    }

    /// <summary>
    /// An assembly name: dot-separated segments, each starting with a letter.
    ///
    /// Validated because it becomes a file name the store opens inside the
    /// directory the artifact was unpacked into. A segment containing a
    /// separator or a dot-dot would be a delivered artifact reaching out of it.
    /// </summary>
    private static bool IsDotnetAssemblyName(string value) =>
        value.Length is > 0 and <= 256
        && value.Split('.').All(segment =>
            segment.Length > 0
            && char.IsAsciiLetter(segment[0])
            && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '_'));

    /// <summary>A fully qualified type name: an assembly-name shape with at least two segments.</summary>
    private static bool IsDotnetTypeName(string value) =>
        IsDotnetAssemblyName(value) && value.Contains('.');

    /// <summary>
    /// Database engines a Feature may require, spelled the way a store reports
    /// them. Closed on purpose: see the failure message in ReadCompatibility.
    /// </summary>
    private static readonly string[] SupportedDatabases = ["postgresql", "mysql", "sqlite"];

    /// <summary>
    /// Scheduled jobs, validated hard.
    ///
    /// Hard because a worker is code KNIGHT causes a store to run on a timer with
    /// nobody watching. A malformed entrypoint is a job that fails silently every
    /// hour for as long as the Feature is installed, so it is refused at publish
    /// where the author is present to fix it.
    /// </summary>
    private IReadOnlyList<WorkerDeclaration> ReadWorkers(JsonElement root, FeatureRuntime runtime)
    {
        if (!root.TryGetProperty("workers", out var workers) || workers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var declared = new List<WorkerDeclaration>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var element in workers.EnumerateArray())
        {
            var path = $"$.workers[{index}]";
            index++;

            if (element.ValueKind != JsonValueKind.Object)
            {
                Fail(path, "A worker must be an object.");
                continue;
            }

            var name = RequireString(element, "name", $"{path}.name");
            var entrypoint = RequireString(element, "entrypoint", $"{path}.entrypoint");
            var schedule = OptionalString(element, "schedule") ?? "daily";

            if (name is not null && !names.Add(name))
            {
                // Two workers of one name cannot both be tracked: a store records
                // the last run per name, so the second would overwrite the first.
                Fail($"{path}.name", $"'{name}' is declared more than once.");
            }

            if (entrypoint is not null && !IsCallable(entrypoint, runtime))
            {
                Fail($"{path}.entrypoint", CallableFailure(entrypoint, runtime));
            }

            if (!Enum.TryParse<WorkerSchedule>(schedule, ignoreCase: true, out var parsed))
            {
                Fail(
                    $"{path}.schedule",
                    $"'{schedule}' is not a schedule KNIGHT knows. Use one of: {string.Join(", ", Enum.GetNames<WorkerSchedule>()).ToLowerInvariant()}.");
                continue;
            }

            if (name is not null && entrypoint is not null)
            {
                declared.Add(new WorkerDeclaration(name, entrypoint, parsed));
            }
        }

        return declared;
    }

    private CompatibilityConstraints ReadCompatibility(JsonElement root)
    {
        if (!TryGetObject(root, "compatibility", out var compatibility))
        {
            // Absent compatibility is not an error but it is a strong claim, and
            // the resolver treats an unbounded range as "the author asserts this
            // runs anywhere". Recording it as Any keeps that claim explicit.
            return new CompatibilityConstraints(VersionRange.Any, VersionRange.Any, VersionRange.Any);
        }

        var database = OptionalString(compatibility, "database");

        if (database is not null && !SupportedDatabases.Contains(database))
        {
            // A closed list rather than free text. A Feature declaring
            // "postgres" against a store reporting "postgresql" would be
            // refused for a spelling, which is the kind of failure nobody can
            // read - so the spelling is fixed here, at publish, where the author
            // is present to fix it.
            Fail(
                "$.compatibility.database",
                $"'{database}' is not a database KNIGHT knows. Use one of: {string.Join(", ", SupportedDatabases)}.");
        }

        return new CompatibilityConstraints(
            OptionalRange(compatibility, "storeVersion", "$.compatibility.storeVersion"),
            OptionalRange(compatibility, "python", "$.compatibility.python"),
            OptionalRange(compatibility, "django", "$.compatibility.django"),
            database,
            OptionalRange(compatibility, "node", "$.compatibility.node"),
            OptionalRange(compatibility, "dotnet", "$.compatibility.dotnet"));
    }

    private ManifestDependencies ReadDependencies(JsonElement root)
    {
        if (!TryGetObject(root, "dependencies", out var dependencies))
        {
            return ManifestDependencies.None;
        }

        var features = new List<FeatureDependencyDeclaration>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (dependencies.TryGetProperty("features", out var featureList))
        {
            if (featureList.ValueKind is not JsonValueKind.Array)
            {
                Fail("$.dependencies.features", "Feature dependencies must be an array.");
            }
            else
            {
                var index = 0;
                foreach (var element in featureList.EnumerateArray())
                {
                    var path = $"$.dependencies.features[{index}]";
                    index++;

                    if (element.ValueKind is not JsonValueKind.Object)
                    {
                        Fail(path, "A feature dependency must be an object with a slug and a version range.");
                        continue;
                    }

                    var slug = RequireString(element, "slug", $"{path}.slug");
                    if (slug is null)
                    {
                        continue;
                    }

                    if (!FeatureSlug.IsValid(slug))
                    {
                        Fail($"{path}.slug", $"'{slug}' is not a valid feature slug.");
                        continue;
                    }

                    var normalized = FeatureSlug.Normalize(slug);

                    // Two entries for the same slug are a contradiction the
                    // resolver cannot arbitrate, so they are refused here rather
                    // than silently last-one-wins.
                    if (!seen.Add(normalized))
                    {
                        Fail($"{path}.slug", $"'{normalized}' is declared as a dependency more than once.");
                        continue;
                    }

                    features.Add(new FeatureDependencyDeclaration(
                        normalized,
                        OptionalRange(element, "version", $"{path}.version")));
                }
            }
        }

        var pythonPackages = ReadStringArray(dependencies, "python", "$.dependencies.python");

        return new ManifestDependencies(features, pythonPackages);
    }

    /// <summary>
    /// Database extensions a Feature may ask for, and the complete list of them.
    ///
    /// Closed, and closed for a security reason rather than a tidiness one. A
    /// PostgreSQL extension can be a procedural language or a foreign-data
    /// wrapper — <c>plpython3u</c>, <c>plperlu</c>, <c>file_fdw</c>,
    /// <c>dblink</c> — and creating one of those is arbitrary code execution
    /// against the database owner on every store that installs the Feature.
    /// Adding to this list is a KNIGHT release, which is the point
    /// (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
    ///
    /// Everything here is additive, indexing or text handling, and is a trusted
    /// extension on PostgreSQL 13 and later — so a store's own database owner can
    /// create it without a superuser.
    /// </summary>
    private static readonly string[] SupportedExtensions =
    [
        "pg_trgm",     // trigram similarity: fuzzy search, typo tolerance
        "btree_gin",   // plain-scalar columns inside a GIN index
        "btree_gist",  // plain-scalar columns inside a GiST index, and exclusion constraints
        "unaccent",    // accent-insensitive text matching
        "citext",      // case-insensitive text
        "pgcrypto",    // digests and random bytes
    ];

    private MigrationPolicy ReadMigrations(JsonElement root, CompatibilityConstraints compatibility)
    {
        if (!TryGetObject(root, "migrations", out var migrations))
        {
            return MigrationPolicy.None;
        }

        var required = OptionalBool(migrations, "required", "$.migrations.required") ?? false;

        // Reversible defaults to false when migrations are required. The safe
        // default is the pessimistic one: assuming an undeclared migration can
        // be undone is how a rollback corrupts a customer's data
        // (docs/adr/0016).
        var reversible = OptionalBool(migrations, "reversible", "$.migrations.reversible") ?? !required;

        var duration = OptionalInt(migrations, "estimatedDurationSeconds", "$.migrations.estimatedDurationSeconds") ?? 0;
        if (duration < 0)
        {
            Fail("$.migrations.estimatedDurationSeconds", "An estimated duration cannot be negative.");
        }

        var maintenance = OptionalBool(migrations, "requiresMaintenanceWindow", "$.migrations.requiresMaintenanceWindow") ?? false;

        return new MigrationPolicy(
            required,
            reversible,
            Math.Max(duration, 0),
            maintenance,
            ReadExtensions(migrations, compatibility));
    }

    /// <summary>
    /// The declared database extensions, validated against the closed list.
    ///
    /// Three separate refusals, and each one is a failure somebody would
    /// otherwise meet on a customer's store: a name nobody vetted, a name
    /// declared twice, and an extension asked for by a Feature that never said it
    /// needs PostgreSQL — which would install onto SQLite and fail in the middle
    /// of a migration rather than before one
    /// (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
    /// </summary>
    private IReadOnlyList<string> ReadExtensions(JsonElement migrations, CompatibilityConstraints compatibility)
    {
        var declared = ReadStringArray(migrations, "extensions", "$.migrations.extensions");

        if (declared.Count == 0)
        {
            return [];
        }

        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var extension in declared)
        {
            var path = $"$.migrations.extensions[{index}]";
            index++;

            if (!SupportedExtensions.Contains(extension))
            {
                Fail(
                    path,
                    $"'{extension}' is not an extension KNIGHT allows a Feature to create. " +
                    $"Use one of: {string.Join(", ", SupportedExtensions)}. " +
                    "The list is closed because an extension can be a procedural language or a foreign-data " +
                    "wrapper, and creating one of those runs arbitrary code on every store that installs this Feature.");
                continue;
            }

            if (!seen.Add(extension))
            {
                Fail(path, $"'{extension}' is declared more than once.");
                continue;
            }

            kept.Add(extension);
        }

        // An extension is a PostgreSQL concept, so a Feature that needs one runs
        // nowhere else and must say so. Checked here rather than left to the
        // installer, because the installer learns it from a failed migration.
        if (kept.Count > 0 && compatibility.Database != "postgresql")
        {
            Fail(
                "$.compatibility.database",
                "A Feature that declares database extensions must also declare 'database: postgresql'. " +
                $"Extensions are a PostgreSQL concept, and this manifest asks for {string.Join(", ", kept)} " +
                $"while claiming to run on {compatibility.Database ?? "any engine"}.");
        }

        return kept;
    }

    private ConfigurationContract ReadConfiguration(JsonElement root)
    {
        if (!TryGetObject(root, "configuration", out var configuration))
        {
            return ConfigurationContract.None;
        }

        var schemaPath = OptionalString(configuration, "schema");

        JsonElement? defaults = null;
        if (configuration.TryGetProperty("defaults", out var defaultsElement))
        {
            if (defaultsElement.ValueKind is not JsonValueKind.Object)
            {
                Fail("$.configuration.defaults", "Configuration defaults must be an object.");
            }
            else
            {
                defaults = defaultsElement.Clone();
            }
        }

        var secrets = ReadStringArray(configuration, "secrets", "$.configuration.secrets");

        // A secret that is also a default would put its value in the package.
        // The manifest is refused rather than the value quietly dropped.
        if (defaults is { } document)
        {
            foreach (var secret in secrets)
            {
                if (document.TryGetProperty(secret, out _))
                {
                    Fail(
                        "$.configuration.defaults",
                        $"'{secret}' is declared as a secret and cannot have a default value in the package.");
                }
            }
        }

        return new ConfigurationContract(schemaPath, defaults, secrets);
    }

    private InstallPolicy? ReadInstall(JsonElement root, FeatureRuntime runtime)
    {
        if (!TryGetObject(root, "install", out var install))
        {
            Fail("$.install", "A Feature must say how it is installed.");
            return null;
        }

        var strategyText = RequireString(install, "strategy", "$.install.strategy");
        var strategy = strategyText switch
        {
            "package-install" => InstallStrategy.PackageInstall,
            "vendored" => InstallStrategy.Vendored,
            "no-op" => InstallStrategy.NoOp,
            null => (InstallStrategy?)null,
            _ => null,
        };

        if (strategyText is not null && strategy is null)
        {
            Fail(
                "$.install.strategy",
                $"'{strategyText}' is not a known install strategy. Expected 'package-install', 'vendored' or 'no-op'.");
        }

        var requiresRestart = OptionalBool(install, "requiresRestart", "$.install.requiresRestart") ?? false;
        var healthCheck = OptionalString(install, "healthCheck");

        if (healthCheck is not null && !IsCallable(healthCheck, runtime))
        {
            Fail("$.install.healthCheck", CallableFailure(healthCheck, runtime));
        }

        return strategy is null ? null : new InstallPolicy(strategy.Value, requiresRestart, healthCheck);
    }

    private UninstallPolicy ReadUninstall(JsonElement root)
    {
        if (!TryGetObject(root, "uninstall", out var uninstall))
        {
            // The default is the conservative one described in
            // docs/feature-delivery.md §11: disable first, and keep the data
            // long enough that a customer who renews loses nothing.
            return new UninstallPolicy(UninstallStrategy.DisableThenRemove, 30);
        }

        var strategyText = OptionalString(uninstall, "strategy");
        var strategy = strategyText switch
        {
            null or "disable-then-remove" => UninstallStrategy.DisableThenRemove,
            "remove-immediately" => UninstallStrategy.RemoveImmediately,
            _ => (UninstallStrategy?)null,
        };

        if (strategy is null)
        {
            Fail(
                "$.uninstall.strategy",
                $"'{strategyText}' is not a known uninstall strategy. Expected 'disable-then-remove' or 'remove-immediately'.");
            strategy = UninstallStrategy.DisableThenRemove;
        }

        var retention = OptionalInt(uninstall, "dataRetentionDays", "$.uninstall.dataRetentionDays") ?? 30;
        if (retention < 0)
        {
            Fail("$.uninstall.dataRetentionDays", "A retention window cannot be negative.");
            retention = 30;
        }

        return new UninstallPolicy(strategy.Value, retention);
    }

    // --- Primitive readers -------------------------------------------------

    private bool TryGetObject(JsonElement parent, string property, out JsonElement value)
    {
        if (!parent.TryGetProperty(property, out value) || value.ValueKind is JsonValueKind.Null)
        {
            return false;
        }

        if (value.ValueKind is not JsonValueKind.Object)
        {
            Fail($"$.{property}", $"'{property}' must be an object.");
            return false;
        }

        return true;
    }

    private string? RequireString(JsonElement parent, string property, string? path = null)
    {
        path ??= $"$.{property}";

        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            Fail(path, $"'{property}' is required.");
            return null;
        }

        if (value.ValueKind is not JsonValueKind.String)
        {
            Fail(path, $"'{property}' must be a string.");
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            Fail(path, $"'{property}' cannot be empty.");
            return null;
        }

        return text.Trim();
    }

    private string? OptionalString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private SemanticVersion? RequireVersion(JsonElement parent, string property)
    {
        var text = RequireString(parent, property);
        if (text is null)
        {
            return null;
        }

        if (!SemanticVersion.TryParse(text, out var version))
        {
            Fail($"$.{property}", $"'{text}' is not a valid semantic version.");
            return null;
        }

        return version;
    }

    private VersionRange OptionalRange(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return VersionRange.Any;
        }

        if (value.ValueKind is not JsonValueKind.String)
        {
            Fail(path, $"'{property}' must be a version range written as a string.");
            return VersionRange.Any;
        }

        if (!VersionRange.TryParse(value.GetString(), out var range))
        {
            Fail(path, $"'{value.GetString()}' is not a valid version range.");
            return VersionRange.Any;
        }

        return range;
    }

    private bool? OptionalBool(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind is JsonValueKind.False)
        {
            return false;
        }

        Fail(path, $"'{property}' must be true or false.");
        return null;
    }

    private int? OptionalInt(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            Fail(path, $"'{property}' must be a whole number.");
            return null;
        }

        return number;
    }

    private IReadOnlyList<string> ReadStringArray(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind is not JsonValueKind.Array)
        {
            Fail(path, $"'{property}' must be an array of strings.");
            return [];
        }

        var items = new List<string>();
        var index = 0;

        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                Fail($"{path}[{index}]", "Every entry must be a non-empty string.");
            }
            else
            {
                items.Add(element.GetString()!.Trim());
            }

            index++;
        }

        return items;
    }

    private void Fail(string path, string message) => _errors.Add(new ManifestError(path, message));

    private static bool IsPythonIdentifier(string value)
    {
        if (value.Length == 0 || (!char.IsAsciiLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a string names something the store can call, in the spelling this
    /// runtime uses.
    ///
    /// A Django Feature writes a dotted import path; a node one writes a module
    /// and an exported name separated by a hash, which is the shape a store can
    /// pass straight to a dynamic import. Validating the wrong spelling would
    /// tell an author with a correct manifest that it is wrong, which is the
    /// worst kind of validation error (adr/0032 §3).
    /// </summary>
    private static bool IsCallable(string value, FeatureRuntime runtime)
    {
        // A .NET callable is a type and a method on it, which is the same shape
        // as node's `module#export` and reads the same way in a log line.
        if (runtime is FeatureRuntime.Dotnet)
        {
            var separator = value.IndexOf('#');

            return separator > 0
                && separator != value.Length - 1
                && IsDotnetTypeName(value[..separator])
                && IsDotnetAssemblyName(value[(separator + 1)..]);
        }

        if (runtime is not FeatureRuntime.Node)
        {
            return IsDottedPythonPath(value);
        }

        var hash = value.IndexOf('#');

        if (hash <= 0 || hash == value.Length - 1)
        {
            return false;
        }

        return IsNodeSpecifier(value[..hash]) && IsJavaScriptIdentifier(value[(hash + 1)..]);
    }

    private static string CallableFailure(string value, FeatureRuntime runtime) => runtime switch
    {
        FeatureRuntime.Node => $"'{value}' is not a valid node callable. Write it as 'module#exportedName'.",
        FeatureRuntime.Dotnet => $"'{value}' is not a valid .NET callable. Write it as 'Namespace.Type#Method'.",
        _ => $"'{value}' is not a valid Python callable path.",
    };

    /// <summary>
    /// A node module specifier: a package name, optionally scoped, optionally
    /// with a subpath.
    ///
    /// Validated because it ends up in an <c>import</c> on a store's server. The
    /// rules are npm's own, narrowed: lower case, no leading dot or underscore,
    /// and no path traversal - a specifier that climbed out of the package
    /// directory would be a delivered artifact reaching into the store.
    /// </summary>
    private static bool IsNodeSpecifier(string value)
    {
        if (value.Length is 0 or > 214 || value != value.ToLowerInvariant())
        {
            return false;
        }

        if (value.Contains("..", StringComparison.Ordinal) || value.StartsWith('.') || value.StartsWith('_'))
        {
            return false;
        }

        var name = value;

        if (name.StartsWith('@'))
        {
            var slash = name.IndexOf('/');

            if (slash <= 1 || slash == name.Length - 1)
            {
                return false;
            }

            // The scope, then everything after it, are each held to the same
            // rules as an unscoped name.
            return IsNodeNameSegment(name[1..slash]) && IsNodeSubpath(name[(slash + 1)..]);
        }

        return IsNodeSubpath(name);
    }

    private static bool IsNodeSubpath(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var segment in value.Split('/'))
        {
            if (!IsNodeNameSegment(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNodeNameSegment(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// An exported name a store can address: a plain JavaScript identifier.
    ///
    /// Narrower than the language allows - no unicode, no <c>default</c> - because
    /// this is a name written in a manifest by a person and read out of a module
    /// by a store, and the exotic cases buy nothing but ways to be wrong.
    /// </summary>
    private static bool IsJavaScriptIdentifier(string value)
    {
        if (value.Length == 0 || (!char.IsAsciiLetter(value[0]) && value[0] is not ('_' or '$')))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '$'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDottedPythonPath(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var segment in value.Split('.'))
        {
            if (!IsPythonIdentifier(segment))
            {
                return false;
            }
        }

        return true;
    }
}
